using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Replay.Validation;

/// <summary>Knobs of <see cref="TierShiftCalibrator"/>. The defaults are deliberately conservative (shrinkage).</summary>
public sealed record TierShiftOptions
{
    /// <summary>A card moves only when (times under-rated − times over-rated) reaches this.</summary>
    public int MinNet { get; init; } = 3;

    /// <summary>...and the evidence comes from at least this many different games (one odd run cannot move a card).</summary>
    public int MinGames { get; init; } = 2;

    /// <summary>Highest tier the calibrator may raise to. S is left to human review (README §2.1).</summary>
    public Tier MaxTier { get; init; } = Tier.A;

    public Tier MinTier { get; init; } = Tier.D;
}

/// <summary>One proposed tier move.</summary>
/// <param name="PackKey">Tier pack that owns the entry (<c>CHARACTER.X</c> or <c>COLORLESS</c>).</param>
/// <param name="CardId">Canonical card id.</param>
/// <param name="Steps">+1 raises one letter (C → B), −1 lowers one.</param>
/// <param name="Up">Picks of this card that the engine ranked below another offered card.</param>
/// <param name="Down">Times the engine's rank-1 card was passed over for another offered card.</param>
/// <param name="Games">Different games behind the winning side of the evidence.</param>
public sealed record TierShift(string PackKey, string CardId, int Steps, int Up, int Down, int Games);

/// <summary>
/// The only automated tier "training" in the project: proposes one-letter tier moves for cards the player's picks
/// disagree with often and across several games. It learns <b>only</b> from the decisions passed to <see cref="Fit"/>,
/// so cross-validation can call it once per fold without the test fold leaking into the pack.
/// </summary>
/// <remarks>
/// Evidence is card-vs-card only. Skip disagreements are ignored because they mostly reflect the skip threshold
/// (<c>skip.t0</c>, a weight), not a single card's tier.
/// </remarks>
public sealed class TierShiftCalibrator
{
    private readonly IRecommender _base;

    public TierShiftCalibrator(DataPack basePack, TierShiftOptions? options = null, IRecommender? baseRecommender = null)
    {
        ArgumentNullException.ThrowIfNull(basePack);
        BasePack = basePack;
        Options = options ?? new TierShiftOptions();
        _base = baseRecommender ?? new CachedRecommender(new EngineRecommender(basePack));
    }

    public DataPack BasePack { get; }

    public TierShiftOptions Options { get; }

    /// <summary>Proposes shifts from <paramref name="train"/> only. Ordered by evidence strength.</summary>
    public IReadOnlyList<TierShift> Fit(IEnumerable<LabeledDecision> train)
    {
        ArgumentNullException.ThrowIfNull(train);
        var up = new Dictionary<(string, string), List<string>>();
        var down = new Dictionary<(string, string), List<string>>();

        foreach (var d in train)
        {
            if (d.PickedIndex is not int picked || d.OfferedCount < 2) continue;
            var p = _base.Predict(d);
            if (p.Standing(picked).Above == 0) continue; // already (tied) first
            var character = d.Decision.StateBefore.Character;
            var max = p.Scores.Max();
            Note(up, character, d.Decision.Offered[picked].Id, d.Stream.GameKey);
            for (var i = 0; i < p.Scores.Count; i++)
            {
                if (p.Scores[i] == max) Note(down, character, d.Decision.Offered[i].Id, d.Stream.GameKey);
            }
        }

        var shifts = new List<TierShift>();
        foreach (var key in up.Keys.Union(down.Keys))
        {
            var u = up.GetValueOrDefault(key) ?? [];
            var dn = down.GetValueOrDefault(key) ?? [];
            var net = u.Count - dn.Count;
            if (net >= Options.MinNet && u.Distinct(StringComparer.Ordinal).Count() >= Options.MinGames)
            {
                shifts.Add(new TierShift(key.Item1, key.Item2, +1, u.Count, dn.Count, u.Distinct(StringComparer.Ordinal).Count()));
            }
            else if (-net >= Options.MinNet && dn.Distinct(StringComparer.Ordinal).Count() >= Options.MinGames)
            {
                shifts.Add(new TierShift(key.Item1, key.Item2, -1, u.Count, dn.Count, dn.Distinct(StringComparer.Ordinal).Count()));
            }
        }

        return shifts
            .OrderByDescending(s => Math.Abs(s.Up - s.Down))
            .ThenBy(s => s.PackKey, StringComparer.Ordinal)
            .ThenBy(s => s.CardId, StringComparer.Ordinal)
            .ToArray();

        void Note(Dictionary<(string, string), List<string>> map, string character, string cardId, string game)
        {
            var owner = PackEditing.OwnerOf(BasePack, character, cardId);
            if (owner is null) return; // unrated card: nothing to move
            var k = (owner, BasePack.Canonical(cardId));
            if (!map.TryGetValue(k, out var list)) map[k] = list = [];
            list.Add(game);
        }
    }

    /// <summary>The base pack with <paramref name="shifts"/> applied (a copy; the base pack is not changed).</summary>
    public DataPack Apply(IEnumerable<TierShift> shifts) => PackEditing.WithTierShifts(BasePack, shifts, Options.MaxTier, Options.MinTier);
}

/// <summary>Memoizes another recommender per decision instance (the engine is deterministic for a fixed pack).</summary>
public sealed class CachedRecommender(IRecommender inner) : IRecommender
{
    private readonly Dictionary<LabeledDecision, Prediction> _cache = new(ReferenceEqualityComparer.Instance);

    public string Name => inner.Name;

    public Prediction Predict(LabeledDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!_cache.TryGetValue(decision, out var p))
        {
            _cache[decision] = p = inner.Predict(decision);
        }

        return p;
    }
}

/// <summary>Copy-on-write edits of a <see cref="DataPack"/>'s tier entries (used by validation; never written to disk).</summary>
public static class PackEditing
{
    /// <summary>The tier pack key that <see cref="DataPack.FindTier"/> would read the card from, or null if unrated.</summary>
    public static string? OwnerOf(DataPack pack, string character, string cardId)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var id = pack.Canonical(cardId);
        if (pack.Tiers.TryGetValue(character, out var own) && own.Cards.ContainsKey(id)) return character;
        if (pack.Tiers.TryGetValue(DataPack.Colorless, out var cl) && cl.Cards.ContainsKey(id)) return DataPack.Colorless;
        return pack.Tiers.FirstOrDefault(kv => kv.Value.Cards.ContainsKey(id)).Key;
    }

    /// <summary>Moves each card's tier (and every act tier) by its steps, clamped to <paramref name="maxTier"/>..<paramref name="minTier"/>.</summary>
    public static DataPack WithTierShifts(DataPack pack, IEnumerable<TierShift> shifts, Tier maxTier = Tier.A, Tier minTier = Tier.D)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(shifts);
        var edits = new Dictionary<(string, string), CardTierEntry>();
        foreach (var s in shifts)
        {
            if (s.Steps == 0 || !pack.Tiers.TryGetValue(s.PackKey, out var tp) || !tp.Cards.TryGetValue(s.CardId, out var entry)) continue;
            var copy = Clone(entry);
            copy.Tier = Shift(entry.Tier, s.Steps, maxTier, minTier);
            if (entry.ActTier is not null)
            {
                copy.ActTier = entry.ActTier.ToDictionary(kv => kv.Key, kv => Shift(kv.Value, s.Steps, maxTier, minTier));
            }

            edits[(s.PackKey, s.CardId)] = copy;
        }

        return WithEntries(pack, edits);
    }

    /// <summary>The pack with the given entries replaced (keyed by tier pack key and card id).</summary>
    public static DataPack WithEntries(DataPack pack, IReadOnlyDictionary<(string PackKey, string CardId), CardTierEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return pack;

        var tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal);
        foreach (var (key, tp) in pack.Tiers)
        {
            var mine = entries.Where(e => e.Key.PackKey == key).ToArray();
            if (mine.Length == 0)
            {
                tiers[key] = tp;
                continue;
            }

            var cards = new Dictionary<string, CardTierEntry>(tp.Cards, StringComparer.Ordinal);
            foreach (var e in mine) cards[e.Key.CardId] = e.Value;
            tiers[key] = new TierPack { Character = tp.Character, DataVersion = tp.DataVersion, Cards = cards };
        }

        return new DataPack
        {
            Weights = pack.Weights,
            Tiers = tiers,
            Archetypes = pack.Archetypes,
            Combos = pack.Combos,
            Catalog = pack.Catalog,
            IdAliases = pack.IdAliases,
        };
    }

    /// <summary>One letter up per positive step (D → C → B → A → S), clamped.</summary>
    public static Tier Shift(Tier tier, int steps, Tier maxTier = Tier.A, Tier minTier = Tier.D)
    {
        // The enum runs S=0 … D=4, so "up" is a smaller number. A tier already above the cap is kept, not lowered.
        var hi = Math.Min((int)maxTier, (int)tier);
        var lo = Math.Max((int)minTier, (int)tier);
        return (Tier)Math.Clamp((int)tier - steps, hi, lo);
    }

    public static CardTierEntry Clone(CardTierEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new CardTierEntry
        {
            Tier = e.Tier,
            ActTier = e.ActTier is null ? null : new Dictionary<int, Tier>(e.ActTier),
            UpgradedDelta = e.UpgradedDelta,
            Roles = new Dictionary<string, double>(e.Roles, StringComparer.Ordinal),
            Provides = [.. e.Provides],
            Consumes = [.. e.Consumes],
            Requires = [.. e.Requires],
            DupFactor = e.DupFactor,
            Cycle = e.Cycle,
            Tip = e.Tip,
            TipEng = e.TipEng,
            Confidence = e.Confidence,
            ChangedIn = e.ChangedIn,
            Source = e.Source,
            Type = e.Type,
            Rarity = e.Rarity,
            Cost = e.Cost,
        };
    }
}
