using System.IO;
using Sts2Overlay.App.Services;

namespace Sts2Overlay.App.Tests;

public class CardArtProviderTests
{
    // 1x1 PNG; Skia decodes by content, so it can stand in for a .webp portrait.
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    [Fact]
    public void Hover_images_are_bounded_and_recent_ones_reused()
    {
        var root = Path.Combine(Path.GetTempPath(), "card-art-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pool = Path.Combine(root, "colorless");
            Directory.CreateDirectory(pool);
            const int cards = CardArtProvider.LargeCacheSize + 4;
            for (int i = 0; i < cards; i++)
                File.WriteAllBytes(Path.Combine(pool, $"card_{i}.webp"), Convert.FromBase64String(OnePixelPng));
            var p = new CardArtProvider(root, catalogPath: null);

            var first = p.Large("CARD.CARD_0");
            Assert.NotNull(first);
            Assert.Same(first, p.Large("CARD.CARD_0")); // cache hit
            for (int i = 1; i < cards; i++) Assert.NotNull(p.Large($"CARD.CARD_{i}"));

            Assert.Equal(CardArtProvider.LargeCacheSize, p.LargeCached);
            var last = p.Large($"CARD.CARD_{cards - 1}");
            Assert.Same(last, p.Large($"CARD.CARD_{cards - 1}"));
            var again = p.Large("CARD.CARD_0"); // evicted long ago: decoded again
            Assert.NotNull(again);
            Assert.NotSame(first, again);
            Assert.Equal(CardArtProvider.LargeCacheSize, p.LargeCached);

            // Thumbnails are not part of the bound.
            var thumb = p.Thumb("CARD.CARD_0");
            Assert.NotNull(thumb);
            Assert.Same(thumb, p.Thumb("CARD.CARD_0"));
            Assert.Null(p.Large("CARD.MISSING"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
