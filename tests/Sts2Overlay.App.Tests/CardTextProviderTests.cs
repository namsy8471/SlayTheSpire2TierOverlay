using System.IO;
using Sts2Overlay.App.Services;

namespace Sts2Overlay.App.Tests;

public class CardTextProviderTests
{
    [Fact]
    public void Card_vars_file_sits_next_to_the_pack_catalog()
    {
        var pack = Path.Combine("root", "data", "packs", "v0.107.1");
        Assert.Equal(Path.GetFullPath(Path.Combine("root", "data", "catalog", "card-vars.v0.107.1.json")), CardTextProvider.CardVarsPath(pack));
    }

    [Fact]
    public void Empty_provider_has_no_text() => Assert.Null(CardTextProvider.Empty.For("CARD.BASH"));
}
