using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Tests.Learning;

public class CharacterCardPolicyTests
{
    private static readonly Lazy<DataPack> Pack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.111.0")));

    /// <summary>The characters with their own card model use it; Ironclad keeps the shared model.</summary>
    [Theory]
    [InlineData("CHARACTER.DEFECT")]
    [InlineData("CHARACTER.SILENT")]
    [InlineData("CHARACTER.NECROBINDER")]
    [InlineData("CHARACTER.REGENT")]
    [InlineData("CHARACTER.FOOL")]
    public void Character_uses_its_own_card_model(string character)
    {
        var pack = Pack.Value;
        Assert.NotNull(pack.CardPolicy);
        var own = pack.CardPolicyFor(character);
        Assert.NotNull(own);
        Assert.NotSame(pack.CardPolicy, own);
        Assert.Same(pack.CardPolicy, pack.CardPolicyFor("CHARACTER.IRONCLAD"));
        Assert.Same(pack.CardPolicy, pack.CardPolicyFor(null));
    }

    /// <summary>The Pool model is the simulator's card rule: per-card value plus the upgraded weight, skip constant, no
    /// engine component (its total must not move with the engine's tier points).</summary>
    [Fact]
    public void Pool_model_ranks_by_its_card_values_only()
    {
        var model = Pack.Value.CardPolicyFor("CHARACTER.FOOL")!;
        Assert.Equal(0.005, model.PointUtility);
        Assert.Equal(0.0, model.RelativeWeight("base"));
        var engine = new Dictionary<string, double> { ["base"] = 80 };
        var u = model.Utilities(2, ["CARD.STRIKE_FOOL"],
            [new("CARD.REPEAT_SHOT", 0, 0, engine, 0), new("CARD.NOCK", 1, 0, new Dictionary<string, double>(), 0)],
            new Dictionary<string, double>());
        Assert.True(u[1] > u[0] && u[0] > u[2], $"skip {u[0]}, Repeat Shot {u[1]}, Nock+ {u[2]}");
        Assert.True(model.Knows("CARD.REPEAT_SHOT"));
    }
}
