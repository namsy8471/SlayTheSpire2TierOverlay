namespace Sts2Overlay.Core.Scoring;

/// <summary>One signed, labelled part of a score, e.g. <c>{ key: "need", label: "광역 부족", value: +24 }</c>.</summary>
public sealed record ScoreComponent(string Key, string Label, double Value, string? Detail = null);

/// <summary>
/// A score as a list of components. <see cref="Total"/> is always exactly the sum of the components (PLAN §5.3),
/// so explanations can never disagree with the number shown.
/// </summary>
public sealed class ScoreBreakdown
{
    public ScoreBreakdown(IEnumerable<ScoreComponent> components)
    {
        Components = components.ToArray();
    }

    public IReadOnlyList<ScoreComponent> Components { get; }

    public double Total => Components.Sum(c => c.Value);

    public double ValueOf(string key) => Components.Where(c => c.Key == key).Sum(c => c.Value);

    /// <summary>Non-zero components other than the base, largest magnitude first.</summary>
    public IReadOnlyList<ScoreComponent> TopReasons(int count = 3)
        => Components.Where(c => c.Key != ComponentKeys.Base && Math.Abs(c.Value) >= 0.05)
            .OrderByDescending(c => Math.Abs(c.Value))
            .Take(count)
            .ToArray();

    public override string ToString()
        => $"{Total:0.#} = " + string.Join(" ", Components.Select(c => $"{c.Key}:{c.Value:+0.#;-0.#;0}"));
}

/// <summary>Stable component keys (UI and tests rely on them).</summary>
public static class ComponentKeys
{
    public const string Base = "base";
    public const string Build = "build";
    public const string OffBuild = "off_build";
    public const string Combo = "combo";
    public const string Need = "need";
    public const string Situation = "situation";
    public const string Dilution = "dilution";
    public const string Duplicate = "duplicate";
    public const string EngineMissing = "engine_missing";
    public const string Saturation = "saturation";
    public const string PowerSaturation = "power_saturation";

    public const string SkipBase = "skip_base";
    public const string SkipDeckSize = "skip_deck_size";
    public const string SkipCommit = "skip_commit";

    public const string RemovalValue = "removal_value";

    /// <summary>Shop card items: minus the skip score, so the item value is <c>card − skip</c> and still sums exactly.</summary>
    public const string SkipRelative = "skip";
}
