namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// Runs against the real portraits and captures on the developer's machine; skipped elsewhere.
/// Nothing from the game is committed: the files live under data/local (gitignored) and %LOCALAPPDATA%.
/// </summary>
public class RealDataTests
{
    private static readonly Lazy<CardArtDatabase> Db = new(() =>
        CardArtDatabase.LoadOrBuild(TestPaths.ArtDir, TestPaths.Catalog, TestPaths.DbCache));

    [LocalDataFact]
    public void Database_covers_the_offerable_catalog_without_identical_art()
    {
        CardArtDatabase db = Db.Value;
        VisionCatalog catalog = VisionCatalog.Load(TestPaths.Catalog);
        int offerable = catalog.Cards.Count(c => VisionCatalog.OfferablePools.Contains(c.Pool));
        var covered = db.Entries.Select(e => e.CardId).ToHashSet(StringComparer.Ordinal);
        Assert.True(catalog.Cards.Where(c => VisionCatalog.OfferablePools.Contains(c.Pool)).All(c => covered.Contains(c.Id)),
            $"some of the {offerable} offerable cards have no portrait");
        // v0.107.1: the only identical pair among all portraits (BLACKENED_BLADE / BLADE_OF_INK) involves a card
        // that is no longer in the catalog, so the filtered database has none.
        Assert.Empty(db.Twins);
    }

    [LocalDataFact]
    public void Every_portrait_identifies_itself()
    {
        CardArtDatabase db = Db.Value;
        foreach (ArtEntry e in db.Entries)
            Assert.Equal(e.CardId, db.TopK(db.FullVector(e.Index), 1)[0].CardId);
    }

    [LocalDataFact("reward_20261108_201101_497.png")]
    public void Real_reward_capture_at_1080p_and_simulated_4K()
    {
        var rec = new CardRecognizer(Db.Value);
        ScreenFrame frame = ImageLoader.Load(Path.Combine(TestPaths.CapturesDir, "reward_20261108_201101_497.png"));
        var hints = new RecognitionHints("CHARACTER.IRONCLAD");
        string[] expected = ["CARD.ANGER", "CARD.THUNDERCLAP", "CARD.FORGOTTEN_RITUAL"];

        foreach (ScreenFrame f in new[] { frame, Upscale2(frame) })
        {
            ScreenRecognition r = rec.Recognize(f, hints);
            Assert.Equal(ScreenKind.RewardCardSelect, r.Kind);
            Assert.Equal(expected, r.Cards.Select(c => c.CardId));
            Assert.All(r.Cards, c => Assert.False(c.Upgraded));
        }
    }

    /// <summary>Nearest-neighbour x2, standing in for a native 4K capture.</summary>
    private static ScreenFrame Upscale2(ScreenFrame f)
    {
        int w = f.Width * 2, h = f.Height * 2;
        byte[] dst = new byte[w * h * 4];
        ReadOnlySpan<byte> src = f.Bgra.Span;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                src.Slice((y / 2) * f.Stride + (x / 2) * 4, 4).CopyTo(dst.AsSpan((y * w + x) * 4, 4));
        return ScreenFrame.FromPacked(dst, w, h);
    }
}
