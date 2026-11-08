using System.Text.Json;

namespace Sts2Overlay.Resources.Tests;

/// <summary>
/// Sanity checks on the committed data/catalog/card-vars.v0.107.1.json (written by tools/CatalogBuilder --card-vars).
/// Expected numbers were checked by hand against the game's card classes (CanonicalVars / OnUpgrade).
/// </summary>
public class CardVarsFileTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "catalog", "card-vars.v0.107.1.json"))));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    private static JsonElement Card(string id) =>
        Doc.Value.RootElement.GetProperty("cards").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    private static decimal Var(JsonElement card, string name) => card.GetProperty("vars").GetProperty(name).GetDecimal();

    private static decimal UpgradeVar(JsonElement card, string name) =>
        card.GetProperty("upgrade").GetProperty("vars").GetProperty(name).GetDecimal();

    [Fact]
    public void File_CoversEveryCatalogCard()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "catalog", "cards.v0.107.1.json")));
        var catalogIds = catalog.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToHashSet();
        var varIds = Doc.Value.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();

        Assert.Equal("v0.107.1", Doc.Value.RootElement.GetProperty("gameVersion").GetString());
        Assert.Equal(578, varIds.Count);
        Assert.True(catalogIds.SetEquals(varIds));
    }

    [Fact]
    public void BaseValuesAndUpgradeDeltas()
    {
        var anger = Card("CARD.ANGER");
        Assert.Equal(6m, Var(anger, "Damage"));
        Assert.Equal(2m, UpgradeVar(anger, "Damage"));

        var bash = Card("CARD.BASH");
        Assert.Equal(8m, Var(bash, "Damage"));
        Assert.Equal(2m, Var(bash, "VulnerablePower")); // PowerVar<VulnerablePower> is named typeof(T).Name
        Assert.Equal(1m, UpgradeVar(bash, "VulnerablePower"));

        var greed = Card("CARD.HAND_OF_GREED");                  // new DynamicVar("Gold", 20m)
        Assert.Equal(20m, Var(greed, "Gold"));
        Assert.Equal(5m, UpgradeVar(greed, "Gold"));

        var mad = Card("CARD.MAD_SCIENCE");                     // named PowerVar: new PowerVar<WeakPower>("SappingWeak", 2m)
        Assert.Equal(2m, Var(mad, "SappingWeak"));
    }

    [Fact]
    public void CostAndKeywordChanges()
    {
        Assert.Equal(-1, Card("CARD.APOTHEOSIS").GetProperty("upgrade").GetProperty("costDelta").GetInt32());
        Assert.Contains("Innate", Card("CARD.MAD_SCIENCE").GetProperty("upgrade").GetProperty("addKeywords")
            .EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public void CalculatedVarsFollowTheirBase()
    {
        var perfected = Card("CARD.PERFECTED_STRIKE");
        Assert.Equal(6m, Var(perfected, "CalculatedDamage"));   // out of combat = CalculationBase
        var calc = perfected.GetProperty("calculated").GetProperty("CalculatedDamage");
        Assert.Equal("CalculationBase", calc.GetProperty("base").GetString());
        Assert.Equal("ExtraDamage", calc.GetProperty("extra").GetString());
        Assert.Equal(1m, UpgradeVar(perfected, "ExtraDamage"));
    }

    [Fact]
    public void SavedPropertyVarsUseTheFreshCardValue()
    {
        var genetic = Card("CARD.GENETIC_ALGORITHM");           // BlockVar(CurrentBlock), _currentBlock = 1
        Assert.Equal(1m, Var(genetic, "Block"));
        Assert.Equal("CurrentBlock", genetic.GetProperty("savedProperties").GetProperty("Block").GetString());
    }

    [Fact]
    public void OnlyMadScienceHasRuntimeOnlyVars()
    {
        var withUnresolved = Doc.Value.RootElement.GetProperty("cards").EnumerateArray()
            .Where(c => c.GetProperty("unresolved").GetArrayLength() > 0)
            .Select(c => c.GetProperty("id").GetString())
            .ToList();
        Assert.Equal(["CARD.MAD_SCIENCE"], withUnresolved);
    }
}
