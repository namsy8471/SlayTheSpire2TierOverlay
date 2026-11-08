namespace Sts2Overlay.Core.Data;

/// <summary>
/// <c>relic-fit.json</c> (optional): how a relic fits each build, measured on top players' runs. For every archetype, how often
/// top players took the relic when it was offered (shop buy / ancient pick) in runs of that build compared with the same
/// character's other runs. Where the decision-time deck could be rebuilt, the rate is also split by whether the deck already
/// held a core card of the build.
/// <code>
/// { "version": 1, "source": "opgg-top-a10-2026-11",
///   "archetypes": { "silent.poison": { "RELIC.SNECKO_SKULL": { "use": true, "fitRatio": 2.17, "pickRateIn": 0.72,
///                                                           "pickRateOut": 0.31, "nIn": 38, "pickRateCoreHeld": 0.8, "nCoreHeld": 30 } } } }
/// </code>
/// Every other field (signal, basis, eff_n, players, bootstrap shares, presence lift) is evidence only.
/// </summary>
public sealed class RelicFit
{
    public int Version { get; set; } = 1;

    public string? Source { get; set; }

    /// <summary>Archetype id → <c>RELIC.X</c> → its measured fit.</summary>
    public Dictionary<string, Dictionary<string, RelicFitStat>> Archetypes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The entry for a build and relic, or null.</summary>
    public RelicFitStat? Get(string archetypeId, string relicId)
        => Archetypes.TryGetValue(archetypeId, out var relics) && relics.TryGetValue(relicId, out var s) ? s : null;
}

/// <summary>One relic's measured fit to one build.</summary>
public sealed class RelicFitStat
{
    /// <summary>The runtime gate: true = the difference between in-build and out-of-build rates is stable. Required.</summary>
    public bool? Use { get; set; }

    /// <summary>Take rate when offered, in runs of this build.</summary>
    public double PickRateIn { get; set; }

    /// <summary>Take rate when offered, in the same character's other runs.</summary>
    public double PickRateOut { get; set; }

    /// <summary>
    /// (<see cref="PickRateIn"/> ÷ <see cref="PickRateOut"/>) ÷ the same ratio over all relics of the same signal: the relic's own
    /// in-build lift with the build's general buying tendency taken out. Null = use the plain rate ratio.
    /// </summary>
    public double? FitRatio { get; set; }

    /// <summary>In-build decisions behind <see cref="PickRateIn"/>.</summary>
    public int? NIn { get; set; }

    /// <summary>Decisions behind <see cref="PickRateCoreHeld"/> / <see cref="PickRateNoCore"/>.</summary>
    public int? NCoreHeld { get; set; }
    public int? NNoCore { get; set; }

    /// <summary>Take rate when the deck already held a core card of this build (null = not measured).</summary>
    public double? PickRateCoreHeld { get; set; }

    /// <summary>Take rate when the deck held no core card of this build yet (null = not measured).</summary>
    public double? PickRateNoCore { get; set; }

    /// <summary>
    /// The in-build lift: <see cref="FitRatio"/> (else <see cref="PickRateIn"/> ÷ <see cref="PickRateOut"/>), and, when the
    /// deck's core situation was measured on at least <paramref name="minSplit"/> decisions, scaled by that situation's rate ÷
    /// <see cref="PickRateIn"/>. Null when there is no usable ratio.
    /// </summary>
    public double? Lift(bool coreHeld, int minSplit, out bool split)
    {
        split = false;
        double? lift = FitRatio ?? (PickRateOut > 0 ? PickRateIn / PickRateOut : null);
        if (lift is not { } l || !double.IsFinite(l) || l <= 0) return null;
        var (rate, n) = coreHeld ? (PickRateCoreHeld, NCoreHeld) : (PickRateNoCore, NNoCore);
        if (rate is { } r && n >= minSplit && PickRateIn > 0)
        {
            split = true;
            l *= r / PickRateIn;
        }
        return l > 0 ? l : null;
    }
}
