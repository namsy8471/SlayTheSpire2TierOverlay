namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// The full-screen inspect view (right-click on a card): the game dims the screen it opened from and shows one big card
/// with its tips. The dimmed cards still match, so before this test the overlay kept its badges over the big card.
/// </summary>
public class InspectViewTests
{
    // Measured on the four inspect captures (FHD, Korean UI); the fading-in frame is not required to match.
    private static readonly string[] InspectCaptures =
    [
        "reward_20261109_003515_138.png", // over an event card grid: recognised as CardGrid before
        "shop_20261108_210133_209.png", // over the deck view
        "shop_20261108_210137_545.png",
        "reward_20261108_213229_066.png", // over the card reward screen
    ];

    /// <summary>A dimmed frame (backstop over everything, top bar included) with a bright big card in the middle.</summary>
    private static Canvas InspectView(int width, int height)
    {
        var c = new Canvas(width, height);
        c.FillRef(new RectF(0, 0, 1920, 1080), 8, 8, 8);
        c.FillRef(new RectF(760, 180, 400, 700), 140, 120, 90);
        return c;
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1080)] // 21:9: the game keeps its 16:9 UI centred
    public void Dimmed_screen_with_a_big_card_is_the_inspect_view(int width, int height)
    {
        ScreenGate gate = ScreenClassifier.Evaluate(InspectView(width, height).Frame);
        Assert.True(gate.InspectLikely, gate.Detail);
        Assert.False(gate.RewardLikely);
        Assert.False(gate.ShopLikely);
    }

    [Fact]
    public void The_lit_top_bar_rules_it_out()
    {
        // The card reward screen darkens the sides too, but never the game's top bar.
        Canvas c = InspectView(1920, 1080);
        c.FillRef(new RectF(0, 0, 1920, 76), 70, 66, 60);
        Assert.False(ScreenClassifier.Evaluate(c.Frame).InspectLikely);
    }

    [Fact]
    public void A_black_frame_is_not_the_inspect_view()
    {
        // Loading screens and fades: dark everywhere, nothing in the middle.
        var c = new Canvas(1920, 1080);
        Assert.False(ScreenClassifier.Evaluate(c.Frame).InspectLikely);
    }

    [LocalDataFact("reward_20261109_003515_138.png", "shop_20261108_210133_209.png", "shop_20261108_210137_545.png", "reward_20261108_213229_066.png")]
    public void Real_inspect_captures_are_recognised_as_such_and_get_no_cards()
    {
        var rec = new CardRecognizer(RealDb.Value);
        foreach (string name in InspectCaptures)
        {
            ScreenFrame f = ImageLoader.Load(Path.Combine(TestPaths.CapturesDir, name));
            ScreenRecognition r = rec.Recognize(f);
            Assert.True(r.Gate.InspectLikely, $"{name}: {r.Gate.Detail}");
            Assert.Equal(ScreenKind.Other, r.Kind);
            Assert.Empty(r.Cards);
        }
    }

    [LocalDataFact("reward_20261109_003510_891.png", "reward_20261108_213226_864.png", "reward_20261108_201012_394.png", "shop_20261108_232348_110.png")]
    public void The_screens_they_opened_from_are_not()
    {
        // The grid and reward screen just before the inspect view, and an ordinary reward and shop screen.
        foreach (string name in new[] { "reward_20261109_003510_891.png", "reward_20261108_213226_864.png", "reward_20261108_201012_394.png", "shop_20261108_232348_110.png" })
        {
            ScreenGate gate = ScreenClassifier.Evaluate(ImageLoader.Load(Path.Combine(TestPaths.CapturesDir, name)));
            Assert.False(gate.InspectLikely, $"{name}: {gate.Detail}");
        }
    }

    private static readonly Lazy<CardArtDatabase> RealDb = new(() =>
        CardArtDatabase.LoadOrBuild(TestPaths.ArtDir, TestPaths.Catalog, TestPaths.DbCache));
}
