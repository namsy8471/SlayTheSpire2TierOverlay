using System.Text;
using System.Text.Json;
using Sts2Overlay.GameState.Tracking;
using Xunit.Abstractions;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// Opt-in measurement over other players' run-history files (<c>STS2_RUN_CORPUS</c>, see <see cref="RunCorpusTests"/>):
/// replays each solo vanilla run's Rewards stream (<c>PlayerRngSet.Rewards</c>) from the seed, floor by floor, and checks
/// the cards every combat card reward offered (<c>card_choices</c>). History files keep no rolled values, so the replay
/// has to know exactly how many values every room draws from the stream; it stops at the first room it can't count.
/// Prints counts only (the files hold Steam ids and per-run data).
/// <para>
/// What draws from the stream (decompiled v0.111.0; everything else uses other streams):
/// <list type="bullet">
/// <item>Combat end (<c>RewardsSet.GenerateRewardsFor</c>, then <c>GenerateWithoutOffering</c> populates in list order):
/// potion chance (<c>PotionRewardOdds.Roll</c>: one float, odds start 0.4, ∓0.1, elite +0.25×0.5), gold
/// (<c>GoldReward.Populate</c>: one int), the potion if one was rolled (<c>PotionFactory.CreateRandomPotions</c>: rarity float
/// + item), three cards (<c>CardFactory.CreateForReward</c>: rarity float with <c>CardRarityOdds.Roll</c>, item,
/// <c>RollForUpgrade</c> float), an elite's relic (<c>RelicFactory.RollRarity</c>: one float). The last act's boss gives
/// nothing.</item>
/// <item>A merchant (<c>MerchantInventory.CreateForNormalMerchant</c>): five character cards × (rarity roll
/// <c>RollWithoutChangingFutureOdds</c> + <c>RollForUpgrade</c>), two colourless cards × <c>RollForUpgrade</c>, two relic
/// rarities: 14 values. Card items and prices use the Shops stream.</item>
/// <item>A treasure room's gold (<c>OneOffSynchronizer.DoTreasureRoomRewards</c>: one int); the relic uses the run's
/// treasure stream. A rest-site DIG (<c>DigRestSiteOption</c>: <c>PullNextRelicFromFront</c> → one rarity float).</item>
/// <item>Large Capsule's two relics (<c>LargeCapsule.AfterObtained</c> → <c>PullNextRelicFromFront</c>: two rarity floats).</item>
/// </list>
/// Relics, events and cards that draw from the stream in ways the history can't count (or change the rewards through
/// hooks) end the replay of that run.
/// </para>
/// </summary>
public class RewardCorpusTests(ITestOutputHelper output)
{
    /// <summary>Relic classes that draw from the Rewards stream or hook reward generation (grep of Models.Relics).</summary>
    private static readonly HashSet<string> s_stopRelics = Ids(
        "AmethystAubergine", "ArcaneScroll", "BlackStar", "CallingBell", "Cauldron", "DingyRug", "DreamCatcher", "Driftwood",
        "DustyTome", "GlassEye", "HeftyTablet", "Kaleidoscope", "LastingCandy", "LavaRock", "LeadPaperweight", "LostCoffer",
        "MassiveScroll", "NeowsBones", "Orrery", "PaelsTooth", "PaelsWing", "PrayerWheel", "PrismaticGem", "ScrollBoxes",
        "SeaGlass", "SilverCrucible", "SmallCapsule", "TheCourier", "TinyMailbox", "ToyBox", "WhiteBeastStatue", "WhiteStar",
        "WongosMysteryTicket", "BigGameHunter");

    /// <summary>Event classes that open rewards or draw from the stream (grep of Models.Events, plus the Crystal Sphere rewards).</summary>
    private static readonly HashSet<string> s_stopEvents = Ids(
        "BattlewornDummy", "BrainLeech", "ColorfulPhilosophers", "DrowningBeacon", "EndlessConveyor", "FakeMerchant",
        "InfestedAutomaton", "LuminousChoir", "PotionCourier", "PunchOff", "RanwidTheElder", "RelicTrader", "RoomFullOfCheese",
        "RoundTeaParty", "TheFutureOfPotions", "TheLegendsWereTrue", "ThisOrThat", "Trial", "UnrestSite", "WarHistorianRepy",
        "Wellspring", "WhisperingHollow", "CrystalSphere");

    /// <summary>Cards whose play adds a combat reward (TheHunt: a card reward; Royalties: gold).</summary>
    private static readonly HashSet<string> s_stopCards = ["CARD.THE_HUNT", "CARD.ROYALTIES"];

    private static HashSet<string> Ids(params string[] classes) => classes.Select(Snake).ToHashSet(StringComparer.Ordinal);

    /// <summary>ModelDb id entry of a class name: <c>PaelsTooth</c> → <c>PAELS_TOOTH</c>.</summary>
    private static string Snake(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(name[i]));
        }
        return sb.ToString();
    }

    private const int ShopDraws = 14;

    /// <summary>Slot indices in the order a history file lists an offer: the taken card first, the rest as shown.</summary>
    private static int[] HistoryOrder(string[] shown, string? taken)
    {
        int t = taken is null ? -1 : Array.IndexOf(shown, taken);
        return t < 0 ? [.. Enumerable.Range(0, shown.Length)] : [t, .. Enumerable.Range(0, shown.Length).Where(k => k != t)];
    }

    [RunCorpusFact]
    public void Card_rewards_replayed_from_history()
    {
        var files = Directory.EnumerateFiles(Environment.GetEnvironmentVariable(RunCorpusFactAttribute.Variable)!, "*.run", SearchOption.AllDirectories).ToList();
        foreach (string version in new[] { "v0.111.0", "v0.107.1" })
        {
            var tried = new SortedDictionary<int, int>();
            var matched = new SortedDictionary<int, int>();
            var setOnly = new SortedDictionary<int, int>();
            var upgradeOk = new SortedDictionary<int, int>();
            var ends = new Dictionary<string, int>(StringComparer.Ordinal);
            int runs = 0, skipped = 0;
            foreach (string file in files)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;
                if (root.GetProperty("build_id").GetString() != version) continue;
                if (root.GetProperty("modifiers").GetArrayLength() > 0 || root.GetProperty("players").GetArrayLength() != 1
                    || root.GetProperty("game_mode").GetString() != "standard") { skipped++; continue; }
                runs++;
                string end = Replay(root, (i, ok, set, up) =>
                {
                    tried[i] = tried.GetValueOrDefault(i) + 1;
                    if (ok) matched[i] = matched.GetValueOrDefault(i) + 1;
                    else if (set) setOnly[i] = setOnly.GetValueOrDefault(i) + 1;
                    if (ok && up) upgradeOk[i] = upgradeOk.GetValueOrDefault(i) + 1;
                });
                ends[end] = ends.GetValueOrDefault(end) + 1;
            }
            output.WriteLine($"== {version}: {runs} solo vanilla runs ({skipped} skipped: co-op / modifiers / other modes)");
            output.WriteLine("reward#  matched/tried  (same cards other order)  upgrades also right");
            foreach (int i in tried.Keys.Where(k => k <= 30))
                output.WriteLine($"{i,7}  {matched.GetValueOrDefault(i),5}/{tried[i],-5}  {setOnly.GetValueOrDefault(i),5}  {upgradeOk.GetValueOrDefault(i),5}");
            output.WriteLine($"total    {matched.Values.Sum()}/{tried.Values.Sum()}");
            output.WriteLine("where each run's replay ended:");
            foreach (var (k, v) in ends.OrderByDescending(kv => kv.Value).Take(40)) output.WriteLine($"  {v,5}  {k}");
        }
    }

    /// <summary>
    /// Walks one run; calls <paramref name="report"/>(reward index, ids match in order, ids match as a set, upgrades match)
    /// for each combat card reward; returns why the walk ended.
    /// </summary>
    private static string Replay(JsonElement root, Action<int, bool, bool, bool> report)
    {
        string build = root.GetProperty("build_id").GetString()!;
        RngScheme scheme = RngSeeds.ForGameVersion(build);
        string seed = root.GetProperty("seed").GetString()!;
        int ascension = root.GetProperty("ascension").GetInt32();
        JsonElement player = root.GetProperty("players")[0];
        string character = player.GetProperty("character").GetString()!;
        if (RewardCardPool.For(character, multiplayer: false, scheme) is not { } pool) return "no pool for character";
        bool scarcity = ascension >= 7; // AscensionLevel.Scarcity
        int acts = root.GetProperty("map_point_history").GetArrayLength();

        var rng = new GameRng(RngSeeds.PlayerStream(scheme, seed, 0, "rewards"));
        float cardOffset = -0.05f, potionOdds = 0.4f;
        ulong live = pool.Hypotheses >= 64 ? ulong.MaxValue : (1UL << pool.Hypotheses) - 1;
        var owned = new HashSet<string>(StringComparer.Ordinal);
        int reward = 0;

        int actIndex = -1;
        foreach (JsonElement act in root.GetProperty("map_point_history").EnumerateArray())
        {
            actIndex++;
            foreach (JsonElement point in act.EnumerateArray())
            {
                JsonElement stats = point.GetProperty("player_stats")[0];
                JsonElement rooms = point.GetProperty("rooms");
                string roomType = rooms.GetArrayLength() > 0 ? rooms[0].GetProperty("room_type").GetString() ?? "" : "";
                string model = rooms.GetArrayLength() > 0 && rooms[0].TryGetProperty("model_id", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";

                if (owned.FirstOrDefault(s_stopRelics.Contains) is { } stopRelic) return $"relic {stopRelic}";
                if (Strings(stats, "cards_gained", "id").FirstOrDefault(s_stopCards.Contains) is { } stopCard) return $"card {stopCard}";

                switch (roomType)
                {
                    case "event" when point.GetProperty("map_point_type").GetString() == "ancient":
                    {
                        string? chosen = stats.TryGetProperty("ancient_choice", out var ac)
                            ? ac.EnumerateArray().Where(c => c.GetProperty("was_chosen").GetBoolean()).Select(c => c.GetProperty("TextKey").GetString()).FirstOrDefault()
                            : null;
                        if (chosen is null || chosen.Contains('.')) return $"ancient option {chosen ?? "none"}";
                        if (s_stopRelics.Contains(chosen)) return $"relic {chosen}";
                        if (chosen == "LARGE_CAPSULE") { rng.NextULong(); rng.NextULong(); } // two relics from the bag, each with a rarity roll
                        owned.Add(chosen);
                        break;
                    }
                    case "event":
                        if (rooms.GetArrayLength() > 1) return "event with a fight";
                        string entry = model.StartsWith("EVENT.", StringComparison.Ordinal) ? model[6..] : model;
                        if (s_stopEvents.Contains(entry)) return $"event {entry}";
                        break;
                    case "monster" or "elite" or "boss":
                    {
                        if (rooms.GetArrayLength() > 1) return "fight with two rooms";
                        if (roomType == "boss" && actIndex >= acts - 1) break; // RewardsSet.WithRewardsFromRoom: no rewards after the last boss
                        if (model == "ENCOUNTER.GREMLIN_MERC_NORMAL") return "gremlin merc (gold proportion)";
                        if (stats.TryGetProperty("gold_stolen", out var gs) && gs.GetInt32() > 0) return "gold stolen (extra gold reward)";
                        // PotionRewardOdds.Roll
                        float chance = potionOdds + (roomType == "elite" ? 0.25f * 0.5f : 0f);
                        bool potion = rng.NextFloat() < chance;
                        potionOdds += potion ? -0.1f : 0.1f;
                        bool historyPotion = stats.TryGetProperty("potion_choices", out var pc) && pc.GetArrayLength() > 0;
                        rng.NextULong(); // GoldReward.Populate
                        if (potion) { rng.NextULong(); rng.NextULong(); } // PotionReward.Populate: rarity + item
                        var slots = new List<OfferSlot>();
                        for (int s = 0; s < 3; s++)
                        {
                            int counter = rng.Counter;
                            string rarity = RollRarity(rng.NextFloat(), roomType, ref cardOffset, scarcity);
                            double pick = rng.NextDouble();
                            float up = rng.NextFloat();
                            slots.Add(new OfferSlot(counter, rarity, pick, up));
                        }
                        if (roomType == "elite") rng.NextULong(); // RelicReward.Populate → RelicFactory.RollRarity

                        var truth = stats.TryGetProperty("card_choices", out var cc)
                            ? cc.EnumerateArray().Select(c => c.GetProperty("card")).ToList() : [];
                        if (truth.Count == 0) return "fight without a card reward (death, skip-all, or a hook)";
                        if (truth.Count != 3) return $"card reward with {truth.Count} cards";
                        reward++;
                        var ids = truth.Select(c => c.GetProperty("id").GetString()!).ToArray();
                        var ups = truth.Select(c => c.TryGetProperty("current_upgrade_level", out var u) ? u.GetInt32() : 0).ToArray();
                        string? taken = cc.EnumerateArray().Where(c => c.TryGetProperty("was_picked", out var wp) && wp.GetBoolean())
                                          .Select(c => c.GetProperty("card").GetProperty("id").GetString()).FirstOrDefault();
                        bool ok = false, set = false, upOk = false;
                        ulong keep = 0;
                        for (int h = 0; h < pool.Hypotheses && h < 64; h++)
                        {
                            if ((live >> h & 1) == 0 || pool.Predict(slots, h) is not { } shown) continue;
                            // The history lists the taken card first, then the others in screen order (measured: every
                            // reproduced offer whose taken card wasn't in the first slot).
                            int[] order = HistoryOrder(shown, taken);
                            if (order.Select(k => shown[k]).SequenceEqual(ids))
                            {
                                keep |= 1UL << h;
                                ok = true;
                                upOk |= order.Select(k => MayUpgrade(shown[k], scheme) && RewardRollReplay.Upgrades(slots[k].UpgradeRoll, CardRarities.Of(shown[k], scheme)!, actIndex, ascension) ? 1 : 0)
                                             .SequenceEqual(ups.Select(u => Math.Min(u, 1)));
                            }
                            else if (shown.Order().SequenceEqual(ids.Order())) set = true;
                        }
                        report(reward, ok, set, upOk);
                        if (!ok)
                            return $"reward {(reward <= 3 ? reward.ToString() : "4+")} mismatch ({roomType}{(potion != historyPotion ? ", potion roll disagrees" : "")})";
                        live = keep;
                        if (potion != historyPotion) return "potion roll disagrees after a matched reward";
                        break;
                    }
                    case "shop":
                        if (owned.Contains("THE_COURIER")) return "relic THE_COURIER";
                        for (int k = 0; k < ShopDraws; k++) rng.NextULong();
                        break;
                    case "treasure":
                        rng.NextULong(); // DoTreasureRoomRewards: gold
                        break;
                    case "rest_site":
                        foreach (string c in Strings(stats, "rest_site_choices", null)) if (c == "DIG") rng.NextULong();
                        break;
                    default:
                        return $"room {roomType}";
                }

                // Relics gained here count from the next room on.
                foreach (string r in Strings(stats, "relic_choices", "choice", picked: true)) owned.Add(r.StartsWith("RELIC.", StringComparison.Ordinal) ? r[6..] : r);
                foreach (string r in Strings(stats, "bought_relics", null)) owned.Add(r.StartsWith("RELIC.", StringComparison.Ordinal) ? r[6..] : r);
            }
        }
        return "end of run";
    }

    private static bool MayUpgrade(string id, RngScheme scheme) => CardRarities.MaxUpgrade(id, scheme) > 0;

    /// <summary>CardRarityOdds.Roll for an encounter reward (float arithmetic like the game); moves the pity offset.</summary>
    private static string RollRarity(float roll, string roomType, ref float offset, bool scarcity)
    {
        float rare, uncommon;
        switch (roomType)
        {
            case "elite": rare = scarcity ? 0.05f : 0.1f; uncommon = 0.4f; break;
            case "boss": rare = 1f; uncommon = 0f; break;
            default: rare = scarcity ? 0.0149f : 0.03f; uncommon = 0.37f; break;
        }
        float need = rare + (roomType == "boss" ? 0f : offset);
        string rarity = roll < need ? "Rare" : roll < uncommon + need ? "Uncommon" : "Common";
        offset = rarity == "Rare" ? -0.05f : Math.Min(offset + (scarcity ? 0.005f : 0.01f), 0.4f);
        return rarity;
    }

    private static IEnumerable<string> Strings(JsonElement stats, string name, string? field, bool picked = false)
    {
        if (!stats.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (picked && !(e.TryGetProperty("was_picked", out var p) && p.GetBoolean())) continue;
            JsonElement v = field is null ? e : e.TryGetProperty(field, out var f) ? f : default;
            if (v.ValueKind == JsonValueKind.String) yield return v.GetString()!;
        }
    }
}
