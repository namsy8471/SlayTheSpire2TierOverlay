using System.IO;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Text;

namespace Sts2Overlay.App.Tests;

public class RelicInfoProviderTests
{
    [Fact]
    public void Relic_vars_file_sits_next_to_the_pack_catalog()
    {
        var pack = Path.Combine("root", "data", "packs", "v0.107.1");
        Assert.Equal(Path.GetFullPath(Path.Combine("root", "data", "catalog", "relic-vars.v0.107.1.json")), RelicInfoProvider.RelicVarsPath(pack));
    }

    [Fact]
    public void Empty_provider_still_names_the_relic()
    {
        var tip = RelicInfoProvider.Empty.For("RELIC.LEAD_PAPERWEIGHT", "kor");
        Assert.Equal("Lead Paperweight", tip.Title);
        Assert.False(tip.HasBody);
        Assert.Null(tip.Thumb);
        Assert.Equal("Caller Name", RelicInfoProvider.Empty.For("RELIC.X", "kor", "Caller Name").Title);
    }

    [Fact]
    public void Loads_text_and_icon_from_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "relic-info-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalogDir = Path.Combine(root, "data", "catalog");
            var packDir = Path.Combine(root, "data", "packs", "v9.9.9");
            var locDir = Path.Combine(root, "loc");
            var iconDir = Path.Combine(root, "icons");
            Directory.CreateDirectory(catalogDir);
            Directory.CreateDirectory(packDir);
            Directory.CreateDirectory(Path.Combine(locDir, "eng"));
            Directory.CreateDirectory(iconDir);
            File.WriteAllText(Path.Combine(catalogDir, "cards.v9.9.9.json"), "{\"cards\":[]}");
            File.WriteAllText(Path.Combine(catalogDir, "relic-vars.v9.9.9.json"), "{\"relics\":[{\"id\":\"RELIC.TEST_STONE\",\"vars\":{\"Block\":7}}]}");
            // Made-up text in the game's format (the real strings stay in the extracted localization).
            File.WriteAllText(Path.Combine(locDir, "eng", "relics.json"),
                "{\"TEST_STONE.title\":\"Test Stone\",\"TEST_STONE.description\":\"Gain [blue]{Block}[/blue] [gold]Block[/gold].\"}");
            // 1x1 PNG
            File.WriteAllBytes(Path.Combine(iconDir, "test_stone.png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="));

            var p = RelicInfoProvider.Load(packDir, locDir, iconDir);
            var tip = p.For("RELIC.TEST_STONE", "kor"); // no Korean table → English
            Assert.Equal("Test Stone", tip.Title);
            Assert.Equal("Gain 7 Block.", CardTextRenderer.ToPlainText(tip.Body));
            Assert.Contains(tip.Body, r => r.Style == CardTextStyle.Keyword && r.Text == "Block");
            Assert.NotNull(tip.Thumb);
            Assert.Equal(RelicInfoProvider.LargeSize, tip.Large!.Width, 3);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
