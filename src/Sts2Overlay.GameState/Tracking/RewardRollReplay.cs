using System.Text.Json;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Log;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// The overlay's screen recognizer reports the cards it read on a card-reward screen (id + upgrade, from the title
/// colour). GameState stays UI-free: the App only hands over Core <see cref="CardRef"/>s.
/// </summary>
public interface ICardOfferSink
{
    void OnCardOfferSeen(IReadOnlyList<CardRef> offered);
}

/// <summary>
/// One reward card slot found in my Rewards stream: the rarity it rolled, the value that picked the card
/// (<c>NextItem</c>), and the upgrade roll that follows.
/// </summary>
internal sealed record OfferSlot(int Counter, string Rarity, double Pick, float UpgradeRoll);

/// <summary>
/// Replays one player's Rewards stream (<c>PlayerRngSet.Rewards</c>) from the run seed, to learn whether a reward
/// card rolled its free upgrade — the log only says "obtained CARD.X".
///
/// The stream: player seed = hash(run seed) + player slot, stream seed = player seed + hash("rewards")
/// (Player.InitializeSeed, PlayerRngSet.CreateRng; <see cref="RngSeeds.PlayerStream"/>). Each reward card draws three values in a row
/// (CardFactory.CreateForReward): the rarity roll (logged as <c>Card rarity: Rolled X</c>), the card pick (NextItem),
/// then the upgrade roll (RollForUpgrade, NextFloat). So finding X in the stream gives the other two. Everything else
/// that uses the stream (potion drops, shop rolls ...) only moves the position forward; the next logged roll is
/// searched for from there.
/// </summary>
internal sealed class RewardRollReplay
{
    /// <summary>Most values the stream may advance between two of my logged rolls (v0.107.1 runs: at most 22; same stream use in v0.111.0).</summary>
    private const int MaxGap = 400;
    /// <summary>Wider search used only when the next roll confirms the hit (a gap we didn't foresee, a run joined late).</summary>
    private const int WideWindow = 20_000;
    private const int ScarcityAscension = 7;

    private readonly RngTape _tape;
    private int _last = -1;

    public RewardRollReplay(string runSeed, int slot, RngScheme scheme)
    {
        Slot = slot;
        Scheme = scheme;
        _tape = new RngTape(new GameRng(RngSeeds.PlayerStream(scheme, runSeed, slot, "rewards")));
    }

    public int Slot { get; }

    /// <summary>The seed arithmetic the stream was built with (a change rebuilds the replay).</summary>
    public RngScheme Scheme { get; }

    /// <summary>
    /// Finds a burst of my rarity rolls (one reward screen) in the stream. A roll is accepted within
    /// <see cref="MaxGap"/> of the previous one; further away only if the next roll sits exactly 3 values later
    /// (a second 24-bit coincidence is out of the question). Rolls not found are skipped (null).
    /// </summary>
    public List<OfferSlot?> Locate(IReadOnlyList<CardRarityRolledEvent> rolls)
    {
        var found = new List<OfferSlot?>();
        for (int i = 0; i < rolls.Count; i++)
        {
            float roll = rolls[i].RollFloat;
            int c = _tape.FindFloat(roll, _last + 1, _last + 1 + MaxGap);
            if (c < 0 && i + 1 < rolls.Count)
            {
                int from = _last + 1;
                while ((c = _tape.FindFloat(roll, from, _last + 1 + WideWindow)) >= 0
                       && GameRng.ToFloat(_tape.At(c + 3)) != rolls[i + 1].RollFloat)
                    from = c + 1;
            }
            if (c < 0) { found.Add(null); continue; }
            _last = c;
            found.Add(new OfferSlot(c, RarityOf(rolls[i]), GameRng.ToDouble(_tape.At(c + 1)), GameRng.ToFloat(_tape.At(c + 2))));
        }
        return found;
    }

    /// <summary>The rarity a roll gives (CardRarityOdds.RollWithoutChangingFutureOdds), in float like the game.</summary>
    public static string RarityOf(CardRarityRolledEvent r)
    {
        float roll = r.RollFloat, need = (float)r.Need;
        if (roll < need) return "Rare";
        float uncommon = r.Source switch { RaritySource.Elite => 0.4f, RaritySource.Boss => 0f, _ => 0.37f };
        return roll < uncommon + need ? "Uncommon" : "Common";
    }

    /// <summary>
    /// CardFactory.RollForUpgrade for a reward card: upgraded when roll &lt;= act index x 25% (12.5% from ascension 7),
    /// compared as decimal like the game. Rares (base chance 0) never are.
    /// </summary>
    public static bool Upgrades(float roll, string rarity, int actIndex, int ascension)
    {
        if (rarity == "Rare") return false;
        decimal odds = actIndex * (ascension >= ScarcityAscension ? 0.125m : 0.25m);
        return (decimal)roll <= odds;
    }
}

/// <summary>
/// A character's reward card pool in game order (CardPoolModel.GenerateAllCards), to replay which card each reward
/// slot shows: <c>items = pool.Where(rarity == rolled).Except(cards already in this reward)</c>, card =
/// <c>items[(int)(pick * items.Count)]</c> (CardFactory.CreateForReward / Rng.NextItem).
///
/// Some cards stay out of the pool until the player has revealed their timeline epoch (FilterThroughEpochs), and the
/// overlay doesn't know a guest's unlocks. Each combination of locked epochs is a hypothesis; a pick that a hypothesis
/// can't produce rules it out. Checked against 51 runs' offered cards (<c>card_choices</c> in the .run files): every
/// three-card offer reproduced.
/// <para>
/// The pool and the rarities come from the embedded catalogs of the run's <see cref="GameDataVersion"/>: v0.111 added 17
/// pool cards (16 co-op only, filtered out by <see cref="CardRarities.AllowedWithPlayers"/> in single player) and moved
/// the rarity of 10. <c>CardFactory.CreateForReward</c> / <c>RollForUpgrade</c> / <c>FilterForPlayerCount</c> and the
/// odds (<c>CardRarityOdds</c>) are the same in the v0.111.0 source as in this port.
/// </para>
/// </summary>
internal sealed class RewardCardPool
{
    private static readonly Lazy<Dictionary<string, (List<string> Cards, List<List<string>> Epochs)>> s_v0107 = new(() => Load(RngScheme.Legacy32));
    private static readonly Lazy<Dictionary<string, (List<string> Cards, List<List<string>> Epochs)>> s_v0111 = new(() => Load(RngScheme.Xx64));

    private readonly List<string> _cards;
    private readonly List<List<string>> _epochs;
    private readonly bool _multiplayer;

    private RewardCardPool(List<string> cards, List<List<string>> epochs, bool multiplayer, RngScheme scheme)
    {
        _cards = cards;
        _epochs = epochs;
        _multiplayer = multiplayer;
        Scheme = scheme;
    }

    /// <summary>The game generation whose pool and rarities this is.</summary>
    public RngScheme Scheme { get; }

    public static RewardCardPool? For(string? character, bool multiplayer, RngScheme scheme = RngSeeds.Default) =>
        character != null && (scheme == RngScheme.Legacy32 ? s_v0107 : s_v0111).Value.TryGetValue(character, out var p)
            ? new RewardCardPool(p.Cards, p.Epochs, multiplayer, scheme)
            : null;

    /// <summary>Number of hypotheses: one per subset of still-locked epochs (bit i set = epoch i locked).</summary>
    public int Hypotheses => 1 << _epochs.Count;

    private static readonly string[] s_order = ["Common", "Uncommon", "Rare"];

    /// <summary>The pool under one hypothesis (CardPoolModel.GetUnlockedCards + FilterForPlayerCount), in game order.</summary>
    private List<string> Unlocked(int lockedMask)
    {
        var locked = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _epochs.Count; i++)
            if ((lockedMask >> i & 1) != 0) locked.UnionWith(_epochs[i]);
        return _cards.Where(c => !locked.Contains(c) && CardRarities.AllowedWithPlayers(c, _multiplayer, Scheme)).ToList();
    }

    private static readonly string[] s_transformable = ["Basic", "Common", "Uncommon", "Rare"];

    /// <summary>
    /// CardFactory.CreateRandomCardForTransform for a card of this pool: <c>rng.NextItem</c> over the pool's common /
    /// uncommon / rare cards other than the original. <paramref name="raw"/> is the stream value that call draws. Null
    /// unless every hypothesis in <paramref name="liveMask"/> gives the same card (or the card isn't from this pool).
    /// </summary>
    public string? TransformTarget(string original, ulong raw, ulong liveMask)
    {
        if (!_cards.Contains(original) || !s_transformable.Contains(CardRarities.Of(original, Scheme))) return null;
        string? result = null;
        for (int h = 0; h < Hypotheses && h < 64; h++)
        {
            if ((liveMask >> h & 1) == 0) continue;
            var options = Unlocked(h).Where(c => c != original && s_order.Contains(CardRarities.Of(c, Scheme))).ToList();
            if (options.Count == 0) return null;
            string card = options[GameRng.ToInt(raw, options.Count)];
            if (result != null && result != card) return null;
            result = card;
        }
        return result;
    }

    /// <summary>The cards an offer shows, slot by slot, under one hypothesis; null if the replay breaks down.</summary>
    public string[]? Predict(IReadOnlyList<OfferSlot> offer, int lockedMask)
    {
        var pool = Unlocked(lockedMask);
        var shown = new string[offer.Count];
        for (int s = 0; s < offer.Count; s++)
        {
            var options = pool.Where(c => !shown.Contains(c)).ToList();
            // CardFactory.GetNextAllowedRarity: a rarity with no card left moves up (Rare wraps to Common).
            string rarity = offer[s].Rarity;
            for (int k = 0; k < 3 && !options.Any(c => CardRarities.Of(c, Scheme) == rarity); k++)
                rarity = s_order[(Array.IndexOf(s_order, rarity) + 1) % 3];
            var items = options.Where(c => CardRarities.Of(c, Scheme) == rarity).ToList();
            if (items.Count == 0) return null;
            shown[s] = items[Math.Min((int)(offer[s].Pick * items.Count), items.Count - 1)];
        }
        return shown;
    }

    private static Dictionary<string, (List<string>, List<List<string>>)> Load(RngScheme scheme)
    {
        var map = new Dictionary<string, (List<string>, List<List<string>>)>(StringComparer.Ordinal);
        using Stream? s = typeof(RewardCardPool).Assembly.GetManifestResourceStream(GameDataVersion.Resource("card-pools", scheme));
        if (s == null) return map;
        using JsonDocument doc = JsonDocument.Parse(s);
        if (!doc.RootElement.TryGetProperty("pools", out JsonElement pools)) return map;
        foreach (JsonProperty p in pools.EnumerateObject())
        {
            var cards = p.Value.GetProperty("cards").EnumerateArray().Select(x => x.GetString()!).ToList();
            var epochs = p.Value.GetProperty("epochs").EnumerateObject()
                          .Select(e => e.Value.EnumerateArray().Select(x => x.GetString()!).ToList()).ToList();
            map[p.Name] = (cards, epochs);
        }
        return map;
    }
}
