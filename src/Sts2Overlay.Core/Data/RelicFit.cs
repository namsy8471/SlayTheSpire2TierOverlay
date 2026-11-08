namespace Sts2Overlay.Core.Data;

/// <summary>
/// <c>relic-fit.json</c> (optional): how a relic fits each build, measured on top players' runs. For every archetype, how often
/// top players took the relic when it was offered (shop buy / ancient pick) in runs of that build compared with the same
/// character's other runs. Where the decision-time deck could be rebuilt, the rate is also split by whether the deck already
/// held a core card of the build.
/// <code>
/// { "version": 1, "source": "opgg-top-a10-2026-11",
///   "archetypes": { "ironclad.strength": { "RELIC.VAJRA": { "use": true, "pickRateIn": 0.62, "pickRateOut": 0.41,
///                                                          "pickRateCoreHeld": 0.70, "pickRateNoCore": 0.55 } } } }
/// </code>
/// Every other field (n, eff_n, players, bootstrap shares, presence lift) is evidence only.
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

    /// <summary>Take rate when the deck already held a core card of this build (null = not measured).</summary>
    public double? PickRateCoreHeld { get; set; }

    /// <summary>Take rate when the deck held no core card of this build yet (null = not measured).</summary>
    public double? PickRateNoCore { get; set; }

    /// <summary>
    /// The in-build rate for the deck's situation: <see cref="PickRateCoreHeld"/> / <see cref="PickRateNoCore"/> when measured,
    /// else <see cref="PickRateIn"/>.
    /// </summary>
    public double RateFor(bool coreHeld) => (coreHeld ? PickRateCoreHeld : PickRateNoCore) ?? PickRateIn;
}
