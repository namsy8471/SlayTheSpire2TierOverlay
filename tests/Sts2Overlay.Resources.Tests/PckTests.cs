using Sts2Overlay.Resources.Pck;

namespace Sts2Overlay.Resources.Tests;

public sealed class PckTests
{
    [Theory]
    [InlineData("localization/kor/*.json", "localization/kor/cards.json", true)]
    [InlineData("localization/kor/*.json", "localization/kor/sub/cards.json", false)]
    [InlineData("images/packed/card_portraits/**/*.png", "images/packed/card_portraits/ironclad/beta/anger.png", true)]
    [InlineData("images/packed/card_portraits/**/*.png", "images/ui/x.png", false)]
    [InlineData("portrait", "images/packed/card_portraits/a.png", true)] // no wildcard = substring
    public void Glob_Matches(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, new Glob(pattern).IsMatch(path));
    }

    [Fact]
    public void Glob_LiteralPrefix_IsStrippedFromOutputPaths()
    {
        Assert.Equal("images/packed/card_portraits/", new Glob("images/packed/card_portraits/**/*.png").LiteralDirectoryPrefix);
        var outDir = Path.Combine(Path.GetTempPath(), "out");
        Assert.Equal(Path.Combine(outDir, "kor", "cards.json"), PckExtractor.OutputPath(outDir, "localization/kor/cards.json", "localization/"));
    }

    [Fact]
    public void OutputPath_RefusesToEscapeTheOutputFolder()
    {
        Assert.Throws<InvalidDataException>(() => PckExtractor.OutputPath(Path.GetTempPath(), "../../evil.json", ""));
    }

    [Theory]
    [InlineData("path=\"res://.godot/imported/a.png-1.ctex\"", ".godot/imported/a.png-1.ctex")]
    [InlineData("[remap]\npath.bptc=\"res://.godot/imported/b.png-2.bptc.ctex\"\npath.s3tc=\"x\"", ".godot/imported/b.png-2.bptc.ctex")]
    [InlineData("importer=\"texture\"", null)]
    public void ImportFile_PointsToTheCtex(string text, string? expected)
    {
        Assert.Equal(expected, PckExtractor.FindCtexPath(text));
    }

    [Fact]
    public void Reader_ReadsTheSyntheticPack()
    {
        using var game = new FakeGame();
        using var pck = new PckReader(game.Install.PckPath);

        Assert.StartsWith("4.5.1", pck.GodotVersion, StringComparison.Ordinal);
        Assert.Contains(pck.Entries, e => e.Path == "localization/kor/cards.json");
        Assert.Equal(9, pck.Entries.Count); // 3 json + 3 × (.import + .ctex)
    }
}
