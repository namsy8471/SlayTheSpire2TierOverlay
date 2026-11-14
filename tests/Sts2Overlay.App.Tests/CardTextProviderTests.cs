using System.IO;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Text;

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

    [Fact]
    public void Only_equivalent_deck_copies_stack_together()
    {
        DeckCard Copy(int level, int saved, string? enchantment = null) => new(new CardRef("CARD.EXAMPLE", level), EnchantmentId: enchantment)
        {
            SavedProperties = new SavedCardProperties(new Dictionary<string, decimal> { ["CurrentDamage"] = saved }),
        };
        var groups = AppController.StackDeckCopies([Copy(0, 2), Copy(0, 2), Copy(1, 2), Copy(0, 7), Copy(0, 2, "ENCHANTMENT.TEST")]);
        Assert.Equal([2, 1, 1, 1], groups.Select(g => g.Count()));
    }

    [Theory]
    [InlineData("v0.113.0", "v0.113.0", "9", "13")]
    [InlineData("v0.111.0", "v0.111.0", "?", null)]
    [InlineData("v0.113.0", "v0.111.0", "?", null)]
    public void Numbers_follow_the_loaded_card_catalog(string fileVersion, string declaredVersion, string expectedBase, string? expectedUpgrade)
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            var packDir = Path.Combine(temp.FullName, "packs", "v0.111.0");
            var catalogDir = temp.CreateSubdirectory("catalog");
            var localization = temp.CreateSubdirectory("eng");
            File.WriteAllText(Path.Combine(localization.FullName, "cards.json"),
                """{"EXAMPLE.title":"Example","EXAMPLE.description":"{Damage:diff()}"}""");
            File.WriteAllText(Path.Combine(catalogDir.FullName, $"card-vars.{fileVersion}.json"),
                """{"gameVersion":"VERSION","cards":[{"id":"CARD.EXAMPLE","vars":{"Damage":9},"upgrade":{"vars":{"Damage":4}}}]}""".Replace("VERSION", declaredVersion));
            var pack = new DataPack
            {
                Catalog = new CardCatalog { GameVersion = "v0.113.0", Cards = [new CatalogCard { Id = "CARD.EXAMPLE", MaxUpgrade = 1, StarCostX = true }] },
            };
            var text = CardTextProvider.Load(pack, packDir, temp.FullName).For("CARD.EXAMPLE", "eng")!;
            Assert.Equal("X", text.Base.StarCost);
            Assert.Equal(expectedBase, CardTextRenderer.ToPlainText(text.Base.Body));
            Assert.Equal(expectedUpgrade, text.Upgraded is { } up ? CardTextRenderer.ToPlainText(up.Body) : null);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
