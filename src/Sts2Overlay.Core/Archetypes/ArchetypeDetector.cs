using System.Collections.Frozen;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Archetypes;

/// <summary>Commit, completion %, missing cards and offer probability per archetype (PLAN §7.5).</summary>
/// <remarks>
/// <list type="bullet">
/// <item>All card and relic ids (deck, relics and archetype lists) go through <see cref="DataPack.Canonical"/>.</item>
/// <item>A card listed under several roles counts once, with the first role in the order core, payoff, enabler, support
/// (same order as <see cref="Archetype.RoleOf"/>).</item>
/// <item><b>Commit</b>: the first copy of an archetype card adds its full role weight, the second copy adds
/// <see cref="SecondCopyFactor"/> × role weight, further copies add nothing. Each owned key relic adds its <c>w</c> once.</item>
/// <item><b>Completion %</b> counts distinct cards only (copies do not raise it).</item>
/// <item><b>Stage</b> = <c>ceil(pct / 20)</c> clamped to 1..5 when pct &gt; 0, and 0 when nothing is owned.</item>
/// </list>
/// </remarks>
public static class ArchetypeDetector
{
    /// <summary>Commit contribution of the second copy of a card, relative to the first. Third and later copies add 0.</summary>
    public const double SecondCopyFactor = 0.5;

    /// <summary>Rarity assumed for missing cards whose rarity is unknown (no catalog and no tier-entry rarity).</summary>
    public const string FallbackRarity = "uncommon";

    private const double Epsilon = 1e-9;

    private static readonly string[] RoleOrder = [ArchetypeRoles.Core, ArchetypeRoles.Payoff, ArchetypeRoles.Enabler, ArchetypeRoles.Support];

    private static readonly IReadOnlyDictionary<string, double> DefaultRoleWeight = new Archetype().RoleWeight.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Statuses for every archetype of the run's character, highest commit first.
    /// <paramref name="roleSupply"/> and <paramref name="provides"/> are used to evaluate <see cref="ArchetypeNeed"/>s; pass null to skip need checks.
    /// </summary>
    /// <remarks>Ties are broken by completion % (desc), then archetype id (ordinal).</remarks>
    public static IReadOnlyList<ArchetypeStatus> Detect(
        RunState state,
        DataPack pack,
        IReadOnlyDictionary<string, double>? roleSupply = null,
        IReadOnlyDictionary<string, int>? provides = null)
    {
        var copies = state.Deck
            .GroupBy(c => pack.Canonical(c.Id), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var relics = state.Relics.Select(pack.Canonical).ToHashSet(StringComparer.Ordinal);

        return pack.ArchetypesFor(state.Character)
            .Select(a => Evaluate(a, state.Character, pack, copies, relics, roleSupply, provides))
            .OrderByDescending(s => s.Commit)
            .ThenByDescending(s => s.CompletionPercent)
            .ThenBy(s => s.Archetype.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// P(at least one missing core/payoff card appears in the next <paramref name="remainingRewards"/> normal combat card rewards),
    /// using the code-confirmed card reward rarity odds for the run's ascension (<see cref="CardOddsWeights.CardReward"/>) without a pity offset,
    /// and pool sizes from the catalog (else <see cref="CompletionWeights.PoolSize"/>).
    /// Same as <see cref="CoreOfferProbability(ArchetypeStatus, RunState, DataPack, int, OfferContext, double?)"/> with
    /// <see cref="OfferContext.CardReward"/> and no pity offset.
    /// </summary>
    public static double CoreOfferProbability(ArchetypeStatus status, RunState state, DataPack pack, int remainingRewards)
        => CoreOfferProbability(status, state, pack, remainingRewards, OfferContext.CardReward, pityOffset: null);

    /// <summary>
    /// P(at least one missing core/payoff card appears in the next <paramref name="remainingRewards"/> offers of <paramref name="context"/>),
    /// each offer being <see cref="CompletionWeights.CardsPerReward"/> independently rolled cards.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Per card, rarity chances come from <see cref="CardOdds.Rarity"/> (source, ascension, pity offset). A specific missing card of rarity
    /// <c>r</c> then appears with <c>1 / poolSize(r)</c>; pool sizes count the run character's catalog cards of that rarity that exist in this
    /// game mode (<see cref="CardCatalog.PoolSizes"/>: multiplayer-only cards are left out of single player, single-player-only cards out of
    /// multiplayer, cards hidden from the library are left out), else <see cref="CompletionWeights.PoolSize"/>.</item>
    /// <item>A missing card that the catalog places outside the character's pool (colorless, event, ...) or excludes from this game mode
    /// is never offered and counts 0. Unknown rarity counts as <see cref="FallbackRarity"/>; rarities a source never rolls (basic, ancient)
    /// count 0.</item>
    /// <item><paramref name="pityOffset"/> null: the plain table odds for every card, so the result is <c>1 − (1 − Σ p(c))^(cardsPerReward × N)</c>.
    /// Otherwise the offset is tracked card by card as a distribution (it resets on a rare and rises by the pity step on any other card,
    /// capped), starting from the given value.</item>
    /// <item>Duplicates inside one offer, rerolls and the shop's fixed type slots are ignored. [추정: 근사]</item>
    /// </list>
    /// Returns 1.0 when no core/payoff card is missing (already complete), otherwise 0 for N ≤ 0.
    /// </remarks>
    public static double CoreOfferProbability(ArchetypeStatus status, RunState state, DataPack pack, int remainingRewards,
        OfferContext context, double? pityOffset)
    {
        if (status.MissingCards.Count == 0) return 1.0;
        var completion = pack.Weights.Completion;
        var odds = pack.Weights.CardOdds;
        var slots = (long)completion.CardsPerReward * remainingRewards;
        if (slots <= 0) return 0.0;

        var poolSizes = pack.Catalog?.PoolSizes(state.Character, state.IsMultiplayer);
        var missingByRarity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var missing in status.MissingCards)
        {
            var known = pack.FindCatalog(missing.CardId);
            if (known is not null
                && (!string.Equals(known.PoolOwner, state.Character, StringComparison.Ordinal) || !known.IsAvailable(state.IsMultiplayer)))
            {
                continue;
            }
            var rarity = missing.Rarity?.ToLowerInvariant() ?? FallbackRarity;
            missingByRarity[rarity] = missingByRarity.GetValueOrDefault(rarity) + 1;
        }

        // Fraction of each rarity's pool that is a wanted card.
        var share = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (rarity, count) in missingByRarity)
        {
            var poolSize = poolSizes is not null && poolSizes.TryGetValue(rarity, out var n) && n > 0
                ? n
                : completion.PoolSize.TryGetValue(rarity, out var fallback) ? fallback : 0;
            if (poolSize > 0) share[rarity] = Math.Min(1.0, (double)count / poolSize);
        }
        if (share.Count == 0) return 0.0;

        double HitChance(RarityChances ch) => share.Sum(kv => ch.For(kv.Key) * kv.Value);

        if (pityOffset is null)
        {
            var perSlot = Math.Clamp(HitChance(CardOdds.Rarity(odds, context, state.Ascension)), 0.0, 1.0);
            return Math.Clamp(1.0 - Math.Pow(1.0 - perSlot, slots), 0.0, 1.0);
        }

        // Distribution over pity offsets, conditioned on "no wanted card seen yet" (mass = P(no hit so far)).
        var rareShare = share.GetValueOrDefault("rare");
        var dist = new Dictionary<long, (double Offset, double Mass)> { [OffsetKey(pityOffset.Value)] = (pityOffset.Value, 1.0) };
        for (long i = 0; i < slots; i++)
        {
            var next = new Dictionary<long, (double Offset, double Mass)>();
            void Add(double offset, double mass)
            {
                if (mass <= 0) return;
                var key = OffsetKey(offset);
                next[key] = next.TryGetValue(key, out var cur) ? (cur.Offset, cur.Mass + mass) : (offset, mass);
            }
            foreach (var (offset, mass) in dist.Values)
            {
                var ch = CardOdds.Rarity(odds, context, state.Ascension, offset);
                var hit = Math.Clamp(HitChance(ch), 0.0, 1.0);
                var rareMiss = Math.Clamp(ch.Rare * (1.0 - rareShare), 0.0, 1.0 - hit);
                var otherMiss = Math.Max(0.0, 1.0 - hit - rareMiss);
                Add(CardOdds.NextPityOffset(odds, offset, rolledRare: true, state.Ascension), mass * rareMiss);
                Add(CardOdds.NextPityOffset(odds, offset, rolledRare: false, state.Ascension), mass * otherMiss);
            }
            dist = next;
            if (dist.Count == 0) break;
        }
        var noHit = dist.Values.Sum(v => v.Mass);
        return Math.Clamp(1.0 - noHit, 0.0, 1.0);
    }

    private static long OffsetKey(double offset) => (long)Math.Round(offset * 1_000_000);

    public static ArchetypeReport Report(RunState state, DataPack pack, int remainingRewards,
        IReadOnlyDictionary<string, double>? roleSupply = null, IReadOnlyDictionary<string, int>? provides = null)
    {
        var statuses = Detect(state, pack, roleSupply, provides);
        var rewards = Math.Max(0, remainingRewards);
        var isHybrid = statuses.Count >= 2
            && statuses[0].Commit > 0
            && statuses[1].Commit > 0
            && Math.Abs(statuses[0].Commit - statuses[1].Commit) <= pack.Weights.HybridMargin + Epsilon;
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in statuses)
        {
            probabilities[s.Archetype.Id] = CoreOfferProbability(s, state, pack, rewards);
        }

        return new ArchetypeReport
        {
            Statuses = statuses,
            IsHybrid = isHybrid,
            CoreOfferProbability = probabilities,
            RemainingRewards = rewards,
        };
    }

    /// <summary>Completion % → flame stage: 0 when nothing is owned, otherwise <c>ceil(pct/20)</c> clamped to 1..5.</summary>
    public static int StageFor(double completionPercent)
    {
        if (completionPercent <= Epsilon) return 0;
        var stage = (int)Math.Ceiling(Math.Round(completionPercent / 20.0, 9));
        return Math.Clamp(stage, 1, 5);
    }

    private static ArchetypeStatus Evaluate(
        Archetype a,
        string character,
        DataPack pack,
        Dictionary<string, int> copies,
        HashSet<string> relics,
        IReadOnlyDictionary<string, double>? roleSupply,
        IReadOnlyDictionary<string, int>? provides)
    {
        var completion = pack.Weights.Completion;

        // Distinct canonical card → role, in role priority order then list order.
        var cards = new List<(string Id, string Role)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in RoleOrder)
        {
            foreach (var raw in ListFor(a.Cards, role))
            {
                var id = pack.Canonical(raw);
                if (seen.Add(id)) cards.Add((id, role));
            }
        }

        var weightSum = 0.0;
        var ownedCompletion = 0.0;
        var totalCompletion = 0.0;
        var owned = new List<string>();
        var missing = new List<MissingCard>();
        foreach (var (id, role) in cards)
        {
            var isCoreLike = role is ArchetypeRoles.Core or ArchetypeRoles.Payoff;
            var completionWeight = isCoreLike ? completion.CoreWeight : completion.SupportWeight;
            totalCompletion += completionWeight;

            var n = copies.TryGetValue(id, out var c) ? c : 0;
            if (n > 0)
            {
                owned.Add(id);
                ownedCompletion += completionWeight;
                weightSum += RoleWeight(a, role) * (n >= 2 ? 1.0 + SecondCopyFactor : 1.0);
            }
            else if (isCoreLike)
            {
                missing.Add(new MissingCard(id, role, pack.CardRarity(character, id)));
            }
        }

        var ownedRelics = new List<string>();
        foreach (var r in a.KeyRelics)
        {
            var id = pack.Canonical(r.Id);
            if (relics.Contains(id) && !ownedRelics.Contains(id, StringComparer.Ordinal))
            {
                ownedRelics.Add(id);
                weightSum += r.W;
            }
        }

        var commit = a.FullWeight > 0 ? Math.Clamp(weightSum / a.FullWeight, 0.0, 1.0) : 0.0;
        var pct = totalCompletion > 0 ? Math.Clamp(ownedCompletion / totalCompletion * 100.0, 0.0, 100.0) : 0.0;

        return new ArchetypeStatus
        {
            Archetype = a,
            Commit = commit,
            CompletionPercent = pct,
            Stage = StageFor(pct),
            OwnedCards = owned,
            OwnedRelics = ownedRelics,
            MissingCards = missing,
            UnmetNeeds = a.Needs.Where(need => !IsMet(need, roleSupply, provides)).ToList(),
        };
    }

    private static bool IsMet(ArchetypeNeed need, IReadOnlyDictionary<string, double>? roleSupply, IReadOnlyDictionary<string, int>? provides)
    {
        if (need.Role is not null && roleSupply is not null)
        {
            var supply = roleSupply.TryGetValue(need.Role, out var s) ? s : 0.0;
            if (supply < need.Min - Epsilon) return false;
        }
        if (need.Tag is not null && provides is not null)
        {
            var count = provides.TryGetValue(need.Tag, out var p) ? p : 0;
            if (count < need.Min - Epsilon) return false;
        }
        return true;
    }

    private static double RoleWeight(Archetype a, string role)
        => a.RoleWeight.TryGetValue(role, out var w) ? w
            : DefaultRoleWeight.TryGetValue(role, out var d) ? d
            : 0.0;

    private static List<string> ListFor(ArchetypeCards cards, string role) => role switch
    {
        ArchetypeRoles.Core => cards.Core,
        ArchetypeRoles.Payoff => cards.Payoff,
        ArchetypeRoles.Enabler => cards.Enabler,
        _ => cards.Support,
    };
}
