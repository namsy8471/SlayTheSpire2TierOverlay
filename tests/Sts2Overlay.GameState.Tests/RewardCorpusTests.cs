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
/// <item>Counted stop rooms (the "after" rule sets): Brain Leech RIP 6 values / SHARE_KNOWLEDGE 10, This or That ORNATE 1
/// / PLAIN 0, Arcane Scroll 1, and one value whenever Darv offers Dusty Tome (see <see cref="Rules"/>).</item>
/// </list>
/// Relics, events and cards that draw from the stream in ways the history can't count (or change the rewards through
/// hooks) end the replay of that run. Every rule set in <see cref="s_rules"/> is measured, so the output shows what each
/// rule adds. With <c>STS2_REWARD_DETAIL</c> set, the last rule set also prints each miss (card and relic ids only).
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

    /// <summary>Set to print each mismatch (card and relic ids only: no run ids, seeds or file names).</summary>
    private const string DetailVariable = "STS2_REWARD_DETAIL";

    /// <summary>What a replay knows beyond the counted rooms (the rule set under test). Each set adds one rule to the previous.</summary>
    /// <param name="CountedStops">Brain Leech, This or That and Arcane Scroll are replayed instead of ending the run.</param>
    /// <param name="DarvTome">An ancient offering Dusty Tome draws its card (Darv.GenerateInitialOptions → DustyTome.SetupForPlayer).</param>
    /// <param name="CombatPotions">A history potion without a predicted potion reward is a potion procured in combat, not a disagreement.</param>
    /// <param name="HookUpgrades">Egg relics and Lava Lamp upgrade offered cards without a draw, also when obtained on the same reward screen.</param>
    private sealed record Rules(string Name, bool CountedStops, bool DarvTome, bool CombatPotions, bool HookUpgrades);

    private static readonly Rules[] s_rules =
    [
        new("before", false, false, false, false),
        new("+ stop rooms counted", true, false, false, false),
        new("+ Darv's Dusty Tome offer", true, true, false, false),
        new("+ combat potions", true, true, true, false),
        new("+ hook upgrades (after)", true, true, true, true),
    ];

    /// <summary>Collects one rule set's counts over one version.</summary>
    private sealed class Tally
    {
        public readonly SortedDictionary<int, int> Tried = new(), Matched = new(), SetOnly = new(), UpgradeOk = new();
        public readonly Dictionary<string, int> Ends = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> UpgradeMisses = new(StringComparer.Ordinal);
        public readonly List<string> Details = [];
        public int EventOffersTried, EventOffersMatched;
    }

    [RunCorpusFact]
    public void Card_rewards_replayed_from_history()
    {
        bool detail = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DetailVariable));
        var files = Directory.EnumerateFiles(Environment.GetEnvironmentVariable(RunCorpusFactAttribute.Variable)!, "*.run", SearchOption.AllDirectories).ToList();
        foreach (string version in new[] { "v0.111.0", "v0.107.1" })
        {
            var tallies = s_rules.Select(_ => new Tally()).ToArray();
            int runs = 0, skipped = 0;
            foreach (string file in files)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;
                if (root.GetProperty("build_id").GetString() != version) continue;
                if (root.GetProperty("modifiers").GetArrayLength() > 0 || root.GetProperty("players").GetArrayLength() != 1
                    || root.GetProperty("game_mode").GetString() != "standard") { skipped++; continue; }
                runs++;
                for (int r = 0; r < s_rules.Length; r++)
                {
                    Tally t = tallies[r];
                    string end = Replay(root, s_rules[r], t, detail);
                    t.Ends[end] = t.Ends.GetValueOrDefault(end) + 1;
                }
            }
            output.WriteLine($"== {version}: {runs} solo vanilla runs ({skipped} skipped: co-op / modifiers / other modes)");
            for (int r = 0; r < s_rules.Length; r++)
            {
                Tally t = tallies[r];
                output.WriteLine($"-- rules {s_rules[r].Name}: total {t.Matched.Values.Sum()}/{t.Tried.Values.Sum()}, upgrades also right {t.UpgradeOk.Values.Sum()}"
                                 + (s_rules[r].CountedStops ? $", Brain Leech grid offers {t.EventOffersMatched}/{t.EventOffersTried}" : ""));
                output.WriteLine("reward#  matched/tried  (same cards other order)  upgrades also right");
                foreach (int i in t.Tried.Keys.Where(k => k <= 30))
                    output.WriteLine($"{i,7}  {t.Matched.GetValueOrDefault(i),5}/{t.Tried[i],-5}  {t.SetOnly.GetValueOrDefault(i),5}  {t.UpgradeOk.GetValueOrDefault(i),5}");
                output.WriteLine("upgrade flags wrong on a matched offer, by cause:");
                foreach (var (k, v) in t.UpgradeMisses.OrderByDescending(kv => kv.Value)) output.WriteLine($"  {v,5}  {k}");
                output.WriteLine("where each run's replay ended:");
                foreach (var (k, v) in t.Ends.OrderByDescending(kv => kv.Value).Take(40)) output.WriteLine($"  {v,5}  {k}");
                if (detail && r == s_rules.Length - 1)
                {
                    output.WriteLine("details:");
                    foreach (string d in t.Details) output.WriteLine("  " + d);
                }
            }
        }
    }

    /// <summary>Relic → card type it upgrades in rewards (EggRelicHelper.UpgradeValidCards via TryModifyCardRewardOptionsLate).</summary>
    private static readonly Dictionary<string, string> s_eggTypes = new(StringComparer.Ordinal)
    {
        ["MOLTEN_EGG"] = "Attack", ["TOXIC_EGG"] = "Skill", ["FROZEN_EGG"] = "Power",
    };

    /// <summary>
    /// Walks one run under <paramref name="rules"/>, counting each combat card reward in <paramref name="t"/>; returns why
    /// the walk ended.
    /// </summary>
    private static string Replay(JsonElement root, Rules rules, Tally t, bool detail)
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
        var since = new List<string>(); // rooms walked since the last checked reward (for the details)
        int reward = 0;
        string Owned() => string.Join(",", owned.Order(StringComparer.Ordinal));

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
                string entry = model.StartsWith("EVENT.", StringComparison.Ordinal) ? model[6..] : model;

                if (owned.FirstOrDefault(r => IsStopRelic(r, rules)) is { } stopRelic) return $"relic {stopRelic}";
                if (Strings(stats, "cards_gained", "id").FirstOrDefault(s_stopCards.Contains) is { } stopCard) return $"card {stopCard}";

                switch (roomType)
                {
                    case "event" when point.GetProperty("map_point_type").GetString() == "ancient":
                    {
                        string? chosen = stats.TryGetProperty("ancient_choice", out var ac)
                            ? ac.EnumerateArray().Where(c => c.GetProperty("was_chosen").GetBoolean()).Select(c => c.GetProperty("TextKey").GetString()).FirstOrDefault()
                            : null;
                        if (chosen is null || chosen.Contains('.')) return $"ancient option {chosen ?? "none"}";
                        // Darv.GenerateInitialOptions: when Dusty Tome is one of the offers, DustyTome.SetupForPlayer picks its
                        // ancient card with PlayerRng.Rewards.NextItem, chosen or not. Obtaining it later draws nothing.
                        if (rules.DarvTome && ac.EnumerateArray().Any(c => c.GetProperty("TextKey").GetString() == "DUSTY_TOME")) rng.NextULong();
                        if (IsStopRelic(chosen, rules)) return $"relic {chosen}";
                        if (chosen == "LARGE_CAPSULE") { rng.NextULong(); rng.NextULong(); } // two relics from the bag, each with a rarity roll
                        // ArcaneScroll.AfterObtained: CreateForReward(1 card, Uniform odds, rares only, NoUpgradeRoll) → one NextItem.
                        if (chosen == "ARCANE_SCROLL") rng.NextULong();
                        owned.Add(chosen);
                        since.Add($"ancient {chosen}");
                        break;
                    }
                    case "event":
                    {
                        if (rooms.GetArrayLength() > 1) return "event with a fight";
                        string option = EventOption(stats);
                        since.Add($"event {entry}.{option}");
                        if (rules.CountedStops && entry == "BRAIN_LEECH")
                        {
                            // BrainLeech (v0.111.0): both options use CardCreationOptions.ForNonCombatWithDefaultOdds (NoUpgradeRoll,
                            // source Other → RollWithBaseOdds): two values per card. RIP offers one CardReward of 3 colourless
                            // cards (RewardsCmd.OfferCustom populates it), SHARE_KNOWLEDGE a grid of 5 character cards.
                            if (option == "RIP") { for (int k = 0; k < 6; k++) rng.NextULong(); }
                            else if (option == "SHARE_KNOWLEDGE")
                            {
                                var grid = new List<OfferSlot>();
                                for (int s = 0; s < 5; s++)
                                {
                                    int counter = rng.Counter;
                                    string rarity = BaseRarity(rng.NextFloat(), scarcity);
                                    grid.Add(new OfferSlot(counter, rarity, rng.NextDouble(), 1f));
                                }
                                // History keeps the four cards left on the grid in card_choices (none picked) and the taken
                                // one in cards_gained.
                                var gridIds = (stats.TryGetProperty("card_choices", out var bc)
                                        ? bc.EnumerateArray().Select(c => c.GetProperty("card").GetProperty("id").GetString()!) : [])
                                    .Concat(Strings(stats, "cards_gained", "id")).Order(StringComparer.Ordinal).ToArray();
                                t.EventOffersTried++;
                                bool any = false;
                                for (int h = 0; h < pool.Hypotheses && h < 64; h++)
                                    if ((live >> h & 1) != 0 && pool.Predict(grid, h) is { } shown && shown.Order(StringComparer.Ordinal).SequenceEqual(gridIds)) any = true;
                                if (any) t.EventOffersMatched++;
                            }
                            else return $"event {entry} option {option}";
                            break;
                        }
                        if (rules.CountedStops && entry == "THIS_OR_THAT")
                        {
                            // ThisOrThat: PLAIN draws nothing here (its gold uses the event stream); ORNATE pulls a relic
                            // (RelicFactory.PullNextRelicFromFront → RollRarity: one float). The relic is in relic_choices.
                            if (option == "ORNATE") rng.NextULong();
                            else if (option != "PLAIN") return $"event {entry} option {option}";
                            break;
                        }
                        if (s_stopEvents.Contains(entry)) return $"event {entry}";
                        break;
                    }
                    case "monster" or "elite" or "boss":
                    {
                        if (rooms.GetArrayLength() > 1) return "fight with two rooms";
                        if (roomType == "boss" && actIndex >= acts - 1) break; // RewardsSet.WithRewardsFromRoom: no rewards after the last boss
                        if (model == "ENCOUNTER.GREMLIN_MERC_NORMAL") return "gremlin merc (gold proportion)";
                        if (stats.TryGetProperty("gold_stolen", out var gs) && gs.GetInt32() > 0) return "gold stolen (extra gold reward)";
                        // PotionRewardOdds.Roll
                        float oddsBefore = potionOdds;
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
                        var ups = truth.Select(c => c.TryGetProperty("current_upgrade_level", out var u) ? Math.Min(u.GetInt32(), 1) : 0).ToArray();
                        string? taken = cc.EnumerateArray().Where(c => c.TryGetProperty("was_picked", out var wp) && wp.GetBoolean())
                                          .Select(c => c.GetProperty("card").GetProperty("id").GetString()).FirstOrDefault();
                        int damage = stats.TryGetProperty("damage_taken", out var dt) && dt.ValueKind == JsonValueKind.Number ? dt.GetInt32() : 0;
                        bool ok = false, set = false, upOk = false;
                        string? upMiss = null;
                        string[]? firstShown = null;
                        ulong keep = 0;
                        for (int h = 0; h < pool.Hypotheses && h < 64; h++)
                        {
                            if ((live >> h & 1) == 0 || pool.Predict(slots, h) is not { } shown) continue;
                            firstShown ??= shown;
                            // The history lists the taken card first, then the others in screen order (measured: every
                            // reproduced offer whose taken card wasn't in the first slot).
                            int[] order = HistoryOrder(shown, taken);
                            if (order.Select(k => shown[k]).SequenceEqual(ids))
                            {
                                keep |= 1UL << h;
                                ok = true;
                                var (predicted, alternatives) = PredictedUpgrades(order.Select(k => (shown[k], slots[k].UpgradeRoll)).ToArray(),
                                                                                  rules, owned, RoomRelics(stats), damage, actIndex, ascension, scheme);
                                bool right = predicted.SequenceEqual(ups) || alternatives.Any(alt => alt.SequenceEqual(ups));
                                upOk |= right;
                                if (!right) upMiss ??= UpgradeMissCause(ids, predicted, ups, owned, RoomRelics(stats), damage, scheme);
                            }
                            else if (shown.Order().SequenceEqual(ids.Order())) set = true;
                        }
                        t.Tried[reward] = t.Tried.GetValueOrDefault(reward) + 1;
                        if (ok) t.Matched[reward] = t.Matched.GetValueOrDefault(reward) + 1;
                        else if (set) t.SetOnly[reward] = t.SetOnly.GetValueOrDefault(reward) + 1;
                        if (ok && upOk) t.UpgradeOk[reward] = t.UpgradeOk.GetValueOrDefault(reward) + 1;
                        if (ok && !upOk && upMiss != null)
                        {
                            string cause = upMiss.Split(':')[0];
                            t.UpgradeMisses[cause] = t.UpgradeMisses.GetValueOrDefault(cause) + 1;
                            if (detail && cause == "no egg / lamp") t.Details.Add($"upgrade {build} act {actIndex} {roomType} #{reward}: {upMiss} relics [{Owned()}]");
                        }
                        if (!ok)
                        {
                            if (detail)
                                t.Details.Add($"mismatch {build} {character} A{ascension} act {actIndex} {roomType} #{reward} set={set} potion pred={potion} hist={historyPotion} "
                                              + $"odds={oddsBefore:0.0}: hist [{string.Join(",", ids)}] pred [{string.Join(",", firstShown ?? [])}] "
                                              + $"rar [{string.Join(",", slots.Select(s => s.Rarity[0]))}] since [{string.Join("; ", since)}] relics [{Owned()}]");
                            return $"reward {(reward <= 3 ? reward.ToString() : "4+")} mismatch ({roomType}{(potion != historyPotion ? ", potion roll disagrees" : "")})";
                        }
                        live = keep;
                        // PotionCmd.TryToProcure logs every procured potion in potion_choices (Petrified Toad, Delicate
                        // Frond, cards), so a history potion without a predicted reward potion isn't a disagreement.
                        if (potion != historyPotion && !(rules.CombatPotions && historyPotion))
                        {
                            if (detail)
                                t.Details.Add($"potion {build} {character} act {actIndex} {roomType} #{reward} pred={potion} hist={historyPotion} odds={oddsBefore:0.0} "
                                              + $"histPotions {PotionChoices(stats)} since [{string.Join("; ", since)}] relics [{Owned()}]");
                            return "potion roll disagrees after a matched reward";
                        }
                        since.Clear();
                        break;
                    }
                    case "shop":
                        if (owned.Contains("THE_COURIER")) return "relic THE_COURIER";
                        for (int k = 0; k < ShopDraws; k++) rng.NextULong();
                        since.Add("shop");
                        break;
                    case "treasure":
                        rng.NextULong(); // DoTreasureRoomRewards: gold
                        since.Add("treasure");
                        break;
                    case "rest_site":
                        foreach (string c in Strings(stats, "rest_site_choices", null))
                        {
                            if (c == "DIG") rng.NextULong();
                            since.Add($"rest {c}");
                        }
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

    private static bool IsStopRelic(string relic, Rules rules) =>
        s_stopRelics.Contains(relic) && !(rules.CountedStops && relic == "ARCANE_SCROLL") && !(rules.DarvTome && relic == "DUSTY_TOME");

    /// <summary>The chosen option's name from an event room's <c>event_choices</c> (title key <c>EVENT.pages.PAGE.options.NAME.title</c>).</summary>
    private static string EventOption(JsonElement stats)
    {
        if (!stats.TryGetProperty("event_choices", out var ec) || ec.GetArrayLength() == 0) return "none";
        string? key = ec[0].TryGetProperty("title", out var title) && title.TryGetProperty("key", out var k) ? k.GetString() : null;
        if (key is null) return "none";
        string[] parts = key.Split('.');
        int i = Array.IndexOf(parts, "options");
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : "none";
    }

    private static string PotionChoices(JsonElement stats) =>
        stats.TryGetProperty("potion_choices", out var pc)
            ? $"{pc.GetArrayLength()} (picked {pc.EnumerateArray().Count(c => c.TryGetProperty("was_picked", out var w) && w.GetBoolean())})"
            : "none";

    /// <summary>
    /// The upgrade flags of an offer in history order: <c>RollForUpgrade</c>, then (with <see cref="Rules.HookUpgrades"/>)
    /// the late reward hooks that upgrade without a draw: an egg upgrades its card type (EggRelicHelper.UpgradeValidCards),
    /// Lava Lamp upgrades every card when the player took no unblocked, blockable damage this combat. The other arrays are
    /// outcomes that are possible as well: history's <c>damage_taken</c> includes unblockable self-damage, so with Lava Lamp
    /// and damage &gt; 0 both can happen; and <c>CardReward.OnRelicObtained</c> re-runs the hooks for a relic obtained while
    /// the reward is still open (an elite's relic taken before the card), which history doesn't order.
    /// </summary>
    private static (int[] Flags, List<int[]> Alternatives) PredictedUpgrades((string Id, float Roll)[] cards, Rules rules, HashSet<string> owned,
                                                                         IReadOnlyCollection<string> roomRelics, int damage, int actIndex,
                                                                         int ascension, RngScheme scheme)
    {
        int[] Flags(HashSet<string> relics, bool lamp) => cards.Select(c =>
        {
            if (!MayUpgrade(c.Id, scheme)) return 0;
            bool up = RewardRollReplay.Upgrades(c.Roll, CardRarities.Of(c.Id, scheme)!, actIndex, ascension);
            if (rules.HookUpgrades)
            {
                string? type = CardRarities.TypeOf(c.Id, scheme);
                up |= s_eggTypes.Any(e => relics.Contains(e.Key) && e.Value == type);
                up |= lamp;
            }
            return up ? 1 : 0;
        }).ToArray();
        var alternatives = new List<int[]>();
        if (!rules.HookUpgrades) return (Flags(owned, false), alternatives);
        int[] main = Flags(owned, owned.Contains("LAVA_LAMP") && damage == 0);
        if (owned.Contains("LAVA_LAMP") && damage > 0) alternatives.Add(Flags(owned, true));
        if (roomRelics.Count > 0)
        {
            var with = new HashSet<string>(owned, StringComparer.Ordinal);
            with.UnionWith(roomRelics);
            alternatives.Add(Flags(with, with.Contains("LAVA_LAMP") && damage == 0));
            if (with.Contains("LAVA_LAMP")) alternatives.Add(Flags(with, true));
        }
        return (main, alternatives);
    }

    /// <summary>Relics picked in this room (relic_choices), without the <c>RELIC.</c> prefix.</summary>
    private static List<string> RoomRelics(JsonElement stats) =>
        Strings(stats, "relic_choices", "choice", picked: true).Select(r => r.StartsWith("RELIC.", StringComparison.Ordinal) ? r[6..] : r).ToList();

    /// <summary>A short cause for an upgrade-flag miss: which egg / Lava Lamp could explain it, else the cards.</summary>
    private static string UpgradeMissCause(string[] ids, int[] predicted, int[] actual, HashSet<string> owned, List<string> roomRelics, int damage, RngScheme scheme)
    {
        var wrong = Enumerable.Range(0, ids.Length).Where(k => predicted[k] != actual[k]).ToList();
        bool allUpHist = wrong.All(k => actual[k] == 1);
        string cards = string.Join(",", wrong.Select(k => $"{ids[k]}({CardRarities.TypeOf(ids[k], scheme)},{CardRarities.Of(ids[k], scheme)}) pred {predicted[k]} hist {actual[k]}"));
        bool Explains(IEnumerable<string> relics) =>
            allUpHist && (relics.Contains("LAVA_LAMP") || wrong.All(k => s_eggTypes.Any(e => relics.Contains(e.Key) && e.Value == CardRarities.TypeOf(ids[k], scheme))));
        if (Explains(owned))
            return (owned.Contains("LAVA_LAMP") ? $"lava lamp (damage {damage})" : "egg of that type") + ": " + cards;
        if (Explains(roomRelics)) return "egg / lamp from the same reward screen: " + cards;
        return "no egg / lamp: " + cards;
    }

    /// <summary>CardRarityOdds.RollWithBaseOdds(RegularEncounter): no pity offset, and the offset is left alone.</summary>
    private static string BaseRarity(float roll, bool scarcity)
    {
        float rare = scarcity ? 0.0149f : 0.03f;
        return roll < rare ? "Rare" : roll < 0.37f + rare ? "Uncommon" : "Common";
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
