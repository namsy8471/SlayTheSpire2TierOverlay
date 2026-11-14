using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Text;

namespace Sts2Overlay.Core.Tests.Text;

/// <summary>Uses the same version of localization and numeric facts for the coverage test and the manual dump.
/// Set CARD_TEXT_LOCAL_DIR to a prepared local data directory to override data/local. For a manual extract
/// without a manifest, CARD_TEXT_VERSION selects the matching game version.</summary>
internal static class LocalCardTextData
{
    private static string LocalDir => Environment.GetEnvironmentVariable("CARD_TEXT_LOCAL_DIR")
        ?? Path.Combine(TestPaths.RepoRoot, "data", "local");

    public static (CardCatalog Catalog, Dictionary<string, CardVars> Vars) LoadNumbers()
    {
        var version = Environment.GetEnvironmentVariable("CARD_TEXT_VERSION");
        if (string.IsNullOrEmpty(version))
        {
            var path = Path.Combine(LocalDir, "manifest.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                version = doc.RootElement.GetProperty("gameVersion").GetString();
            }
            // Before resource preparation wrote a manifest, the repository's local extract was v0.107.1.
            version ??= "v0.107.1";
        }
        var dir = Path.Combine(TestPaths.RepoRoot, "data", "catalog");
        var catalog = DataPackLoader.LoadCatalogFile(Path.Combine(dir, $"cards.{version}.json"));
        var vars = CardVarsFile.Parse(File.ReadAllText(Path.Combine(dir, $"card-vars.{version}.json")));
        Assert.Equal(version, catalog.GameVersion);
        Assert.Equal(version, vars.GameVersion);
        return (catalog, vars.Cards.ToDictionary(c => c.Id, StringComparer.Ordinal));
    }

    public static CardLocalization Localization(string lang)
    {
        var dir = Path.Combine(LocalDir, "pck", "localization", lang);
        return new CardLocalization(lang, Table(Path.Combine(dir, "cards.json")), Table(Path.Combine(dir, "card_keywords.json")));
    }

    public static CardTextFacts Facts(CatalogCard card) =>
        new(card.Cost ?? -1, card.CostX, card.StarCost, card.Keywords, card.MaxUpgrade ?? 0)
        {
            StarCostX = card.StarCostX,
            GainsBlock = card.GainsBlock,
            Target = card.Target,
        };

    private static Dictionary<string, string> Table(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }
}
