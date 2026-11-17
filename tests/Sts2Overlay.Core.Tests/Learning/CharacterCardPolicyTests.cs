using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Tests.Learning;

public class CharacterCardPolicyTests
{
    private static readonly Lazy<DataPack> Pack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.111.0")));

    /// <summary>The four characters with their own A0 win-only card model use it; Ironclad keeps the shared model.</summary>
    [Theory]
    [InlineData("CHARACTER.DEFECT")]
    [InlineData("CHARACTER.SILENT")]
    [InlineData("CHARACTER.NECROBINDER")]
    [InlineData("CHARACTER.REGENT")]
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
}
