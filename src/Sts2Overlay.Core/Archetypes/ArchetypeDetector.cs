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
    /// v1 approximation: P(at least one missing core/payoff card appears in the next <paramref name="remainingRewards"/> card rewards),
    /// using per-slot rarity odds and pool sizes (catalog when available, else <see cref="CompletionWeights.PoolSize"/>).
    /// </summary>
    /// <remarks>
    /// Per slot, a specific card <c>c</c> appears with <c>rarityOdds[rarity(c)] / poolSize(rarity(c))</c>; slots are treated as independent,
    /// so the result is <c>1 − (1 − Σ p(c))^(cardsPerReward × N)</c>. Unknown rarity counts as <see cref="FallbackRarity"/>; a rarity absent
    /// from <c>rarityOdds</c> (e.g. basic, ancient) never appears in rewards.
    /// Returns 1.0 when no core/payoff card is missing (already complete), otherwise 0 for N ≤ 0. [추정]
    /// </remarks>
    public static double CoreOfferProbability(ArchetypeStatus status, RunState state, DataPack pack, int remainingRewards)
    {
        if (status.MissingCards.Count == 0) return 1.0;
        var completion = pack.Weights.Completion;
        var slots = (long)completion.CardsPerReward * remainingRewards;
        if (slots <= 0) return 0.0;

        var poolSizes = CatalogPoolSizes(state, pack);
        var perSlot = 0.0;
        foreach (var missing in status.MissingCards)
        {
            var rarity = missing.Rarity?.ToLowerInvariant() ?? FallbackRarity;
            if (!completion.RarityOdds.TryGetValue(rarity, out var odds) || odds <= 0) continue;
            var poolSize = poolSizes is not null && poolSizes.TryGetValue(rarity, out var n) && n > 0
                ? n
                : completion.PoolSize.TryGetValue(rarity, out var fallback) ? fallback : 0;
            if (poolSize <= 0) continue;
            perSlot += odds / poolSize;
        }

        var none = Math.Clamp(1.0 - perSlot, 0.0, 1.0);
        return Math.Clamp(1.0 - Math.Pow(none, slots), 0.0, 1.0);
    }

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

    /// <summary>Rarity → number of reward-eligible cards in the character's pool, or null without a catalog.</summary>
    private static Dictionary<string, int>? CatalogPoolSizes(RunState state, DataPack pack)
    {
        if (pack.Catalog is null) return null;
        return pack.Catalog.Cards
            .Where(c => string.Equals(c.Pool, state.Character, StringComparison.Ordinal))
            .Where(c => state.IsMultiplayer || !c.MultiplayerOnly)
            .Where(c => c.Rarity is not null)
            .GroupBy(c => c.Rarity!.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }
}
