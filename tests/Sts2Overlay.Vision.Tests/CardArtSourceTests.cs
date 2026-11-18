namespace Sts2Overlay.Vision.Tests;

public class CardArtSourceTests
{
    /// <summary>CardModel portraits: own art, else beta art, else the shared placeholder (CardModel.MissingPortraitPath).</summary>
    [Fact]
    public void A_card_without_art_gets_the_game_placeholder()
    {
        string root = Path.Combine(Path.GetTempPath(), "art-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "fool", "beta"));
            File.WriteAllBytes(Path.Combine(root, "fool", "strike_fool.webp"), [1]);
            File.WriteAllBytes(Path.Combine(root, "fool", "beta", "cho_firm.webp"), [1]);
            File.WriteAllBytes(Path.Combine(root, CardArtSource.MissingPortrait), [1]);
            const string pool = "CARD_POOL.FOOL_CARD_POOL";
            var catalog = new VisionCatalog([new("CARD.STRIKE_FOOL", pool, "CHARACTER.FOOL", "Basic", "Attack"),
                new("CARD.CHO_FIRM", pool, "CHARACTER.FOOL", "Common", "Skill"), new("CARD.NAIL", pool, "CHARACTER.FOOL", "Common", "Attack")]);

            var files = CardArtSource.Enumerate(root, catalog);
            Assert.Equal(["fool/strike_fool.webp", "fool/beta/cho_firm.webp", CardArtSource.MissingPortrait], files.Select(f => f.RelativePath));
            Assert.Equal("CARD.NAIL", files[2].CardId);
            Assert.True(new ArtEntry(0, "CARD.NAIL", pool, null, files[2].RelativePath, true).IsMissingPortrait);
        }
        finally { Directory.Delete(root, true); }
    }
}
