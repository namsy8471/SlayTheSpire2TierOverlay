namespace Sts2Overlay.Core.Data;

/// <summary>
/// Loads a data pack directory (see docs/DATA_SCHEMA.md):
/// <code>
/// weights.json            (optional, partial overrides of Weights defaults)
/// tiers/*.json            TierPack per character (+ colorless.json)
/// archetypes/*.json       array of Archetype
/// combos/*.json           array of Combo
/// aliases.json            (optional) { "OLD_ID": "NEW_ID" }
/// catalog.json            (optional) CardCatalog, usually generated locally
/// </code>
/// JSON uses camelCase, allows comments and trailing commas, and tier letters as strings.
/// </summary>
public static class DataPackLoader
{
    public static DataPack LoadDirectory(string directory, CardCatalog? catalog = null) => throw new NotImplementedException();

    public static Weights LoadWeights(string json) => throw new NotImplementedException();

    public static TierPack LoadTierPack(string json) => throw new NotImplementedException();

    public static IReadOnlyList<Archetype> LoadArchetypes(string json) => throw new NotImplementedException();

    public static IReadOnlyList<Combo> LoadCombos(string json) => throw new NotImplementedException();

    public static CardCatalog LoadCatalog(string json) => throw new NotImplementedException();

    /// <summary>Shared serializer options (camelCase, comments, trailing commas, string enums).</summary>
    public static System.Text.Json.JsonSerializerOptions JsonOptions => throw new NotImplementedException();
}

/// <summary>Finds data problems before they become silent bad advice.</summary>
public static class DataPackValidator
{
    /// <summary>
    /// Errors and warnings: malformed ids (must look like <c>CARD.X</c>/<c>RELIC.X</c>), role strengths outside 0..1,
    /// archetype cards without tier entries, duplicate archetype ids, combos with fewer than 2 cards,
    /// and (when a catalog is present) ids missing from the catalog.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> Validate(DataPack pack) => throw new NotImplementedException();
}

public enum ValidationSeverity
{
    Warning,
    Error,
}

public sealed record ValidationIssue(ValidationSeverity Severity, string Where, string Message);
