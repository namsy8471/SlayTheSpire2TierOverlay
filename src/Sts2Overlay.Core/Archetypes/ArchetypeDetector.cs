using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Archetypes;

/// <summary>Commit, completion %, missing cards and offer probability per archetype (PLAN §7.5).</summary>
public static class ArchetypeDetector
{
    /// <summary>
    /// Statuses for every archetype of the run's character, highest commit first.
    /// <paramref name="roleSupply"/> and <paramref name="provides"/> are used to evaluate <see cref="ArchetypeNeed"/>s; pass null to skip need checks.
    /// </summary>
    public static IReadOnlyList<ArchetypeStatus> Detect(
        RunState state,
        DataPack pack,
        IReadOnlyDictionary<string, double>? roleSupply = null,
        IReadOnlyDictionary<string, int>? provides = null)
        => throw new NotImplementedException();

    /// <summary>
    /// v1 approximation: P(at least one missing core/payoff card appears in the next <paramref name="remainingRewards"/> card rewards),
    /// using per-slot rarity odds and pool sizes (catalog when available, else <see cref="CompletionWeights.PoolSize"/>).
    /// </summary>
    public static double CoreOfferProbability(ArchetypeStatus status, RunState state, DataPack pack, int remainingRewards)
        => throw new NotImplementedException();

    public static ArchetypeReport Report(RunState state, DataPack pack, int remainingRewards,
        IReadOnlyDictionary<string, double>? roleSupply = null, IReadOnlyDictionary<string, int>? provides = null)
        => throw new NotImplementedException();
}
