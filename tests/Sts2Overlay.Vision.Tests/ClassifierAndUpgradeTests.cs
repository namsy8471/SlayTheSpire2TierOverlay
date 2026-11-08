namespace Sts2Overlay.Vision.Tests;

public class ClassifierAndUpgradeTests
{
    [Theory]
    [InlineData(127, 255, 0, true)]   // StsColors.green #7FFF00
    [InlineData(38, 77, 0, true)]     // the same green at 30 % while the card fades in from black
    [InlineData(255, 246, 226, false)] // normal cream title #FFF6E2
    [InlineData(120, 165, 28, false)] // brightest shading of the rare (gold) banner
    [InlineData(27, 97, 49, false)]   // upgraded title outline #1B6131 (too much blue)
    [InlineData(0, 40, 0, false)]     // too dark to judge
    public void Upgrade_green_rule(float r, float g, float b, bool expected) =>
        Assert.Equal(expected, UpgradeDetector.IsUpgradeGreen(r, g, b));

    /// <summary>Paints the reward screen's probe areas: parchment ribbon ends/top, black backstop beside it.</summary>
    internal static void PaintRewardBanner(Canvas c)
    {
        c.FillRef(new RectF(700, 235, 520, 70), 162, 134, 104);
        c.FillRef(new RectF(530, 240, 80, 70), 10, 10, 12);
        c.FillRef(new RectF(1310, 240, 80, 70), 10, 10, 12);
    }

    internal static void PaintRug(Canvas c, int patches = 3)
    {
        if (patches > 0) c.FillRef(new RectF(240, 240, 90, 270), 75, 108, 98);
        if (patches > 1) c.FillRef(new RectF(290, 930, 1020, 70), 77, 112, 99);
        if (patches > 2) c.FillRef(new RectF(1590, 290, 120, 220), 80, 114, 97);
    }

    [Fact]
    public void Black_frame_is_neither()
    {
        ScreenGate g = ScreenClassifier.Evaluate(new Canvas(1920, 1080).Frame);
        Assert.False(g.RewardLikely);
        Assert.False(g.ShopLikely);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3842, 2160)]
    [InlineData(1920, 1200)] // letterboxed: probes must follow the 16:9 canvas, not the frame edges
    public void Reward_banner_is_detected_at_any_resolution(int w, int h)
    {
        var c = new Canvas(w, h);
        PaintRewardBanner(c);
        Assert.True(ScreenClassifier.Evaluate(c.Frame).RewardLikely);
    }

    [Fact]
    public void Banner_without_dark_backstop_is_not_a_reward_screen()
    {
        var c = new Canvas(1920, 1080);
        PaintRewardBanner(c);
        c.FillRef(new RectF(530, 240, 80, 70), 120, 120, 120); // e.g. the map or a bright event scene
        Assert.False(ScreenClassifier.Evaluate(c.Frame).RewardLikely);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(2, true)]  // one patch hidden by a tooltip or the merchant's speech bubble
    [InlineData(1, false)]
    public void Shop_needs_two_of_three_rug_patches(int patches, bool expected)
    {
        var c = new Canvas(1920, 1080);
        PaintRug(c, patches);
        Assert.Equal(expected, ScreenClassifier.Evaluate(c.Frame).ShopLikely);
    }
}
