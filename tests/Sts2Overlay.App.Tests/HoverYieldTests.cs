using Sts2Overlay.App.Views;
using RectF = Sts2Overlay.Vision.RectF;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The overlay stepping aside for the game's hover tips (<see cref="HoverGeometry"/>, <see cref="HoverYieldState"/>).
/// The game draws its tips inside its own frame, under our windows, so the only fix is to hide our marks while the
/// mouse is on something that has a tip.
/// </summary>
public class HoverYieldTests
{
    private static readonly PxBox LayerFhd = new(0, 0, 1920, 1080);

    // ---- why: a hovered reward card's tip lands on its neighbour's badge ----

    /// <summary>A card of the given node scale centred at a reference point (FHD pixels).</summary>
    private static PxBox Card(double cx, double cy, double nodeScale)
        => new((int)(cx - 150 * nodeScale), (int)(cy - 211 * nodeScale), (int)(300 * nodeScale), (int)(422 * nodeScale));

    [Fact]
    public void A_hovered_cards_tip_overlaps_the_next_cards_badge()
    {
        // Reward row (x 610/960/1310, y 616, scale 0.8), the middle card hovered (scale 1.0).
        PxBox[] cards = [Card(610, 616, 0.8), Card(960, 616, 1.0), Card(1310, 616, 0.8)];
        var f = BadgeLayout.Compute(cards, [], LayerFhd, 1.0, 1, skip: false, outlines: false, [new(2), new(2), new(2)]);

        // NHoverTipSet.SetAlignmentForCardHolder: a 360-wide text box at the card's top-right corner, here two
        // keywords tall. That is exactly where the right neighbour's badge or chips sit.
        var hovered = cards[1];
        var tip = new DipRect(hovered.Right, hovered.Y, 360, 300);
        var neighbour = new List<DipRect> { f.Badges[2].Box };
        if (f.Chips[2] is { } chips) neighbour.Add(chips.Box);
        Assert.Contains(neighbour, tip.Intersects);

        // ... and the mouse that opened it is in a hover zone, so the overlay steps aside.
        var cx = hovered.X + hovered.Width / 2;
        var cy = hovered.Y + hovered.Height / 2;
        Assert.True(HoverGeometry.Contains(cards, cx, cy));
    }

    // ---- geometry ----

    [Fact]
    public void Contains_grows_each_zone_a_little_for_the_hover_zoom()
    {
        PxBox[] zones = [new(100, 100, 200, 300)];
        Assert.True(HoverGeometry.Contains(zones, 150, 150));
        Assert.True(HoverGeometry.Contains(zones, 100, 100)); // corner
        Assert.True(HoverGeometry.Contains(zones, 305, 395)); // 5 px past the right edge: 6 % of 200 = 12 px of grow
        Assert.False(HoverGeometry.Contains(zones, 320, 250)); // 20 px past: outside
        Assert.False(HoverGeometry.Contains(zones, 305, 395, grow: 0));
        Assert.False(HoverGeometry.Contains([], 150, 150));
        Assert.False(HoverGeometry.Contains([new PxBox(0, 0, 0, 0)], 0, 0)); // empty zones never match
    }

    [Fact]
    public void Top_strip_follows_the_games_scale_and_letterbox()
    {
        // FHD: the design space as is.
        Assert.Equal(new PxBox(0, 0, 1920, 170), HoverGeometry.TopStrip(LayerFhd));
        // 4K: twice as big. Window offsets carry over.
        Assert.Equal(new PxBox(10, 20, 3840, 340), HoverGeometry.TopStrip(new PxBox(10, 20, 3840, 2160)));
        // 21:9 (2560x1080): the game keeps 16:9 UI centred, 320 px bars left and right.
        Assert.Equal(new PxBox(320, 0, 1920, 170), HoverGeometry.TopStrip(new PxBox(0, 0, 2560, 1080)));
    }

    [Fact]
    public void Design_px_marks_become_screen_px()
    {
        var layer = new PxBox(100, 50, 3840, 2160);
        Assert.Equal(new PxBox(100 + 500, 50 + 300, 400, 200), HoverGeometry.FromRef(new RectF(250, 150, 200, 100), layer));
    }

    // ---- timing ----

    [Fact]
    public void Hides_at_once_and_comes_back_after_the_delay()
    {
        var s = new HoverYieldState(showDelayMs: 300);
        Assert.False(s.Update(false, 0));
        Assert.False(s.Hidden);

        Assert.True(s.Update(true, 1000)); // on a card: hide right away
        Assert.True(s.Hidden);
        Assert.False(s.Update(true, 1050)); // still there: no change

        Assert.False(s.Update(false, 1100)); // left: wait
        Assert.False(s.Update(false, 1350));
        Assert.True(s.Hidden);
        Assert.True(s.Update(false, 1400)); // 300 ms off every zone: back
        Assert.False(s.Hidden);
    }

    [Fact]
    public void Moving_from_card_to_card_does_not_flash_the_badges()
    {
        var s = new HoverYieldState(showDelayMs: 300);
        s.Update(true, 0);
        // The gap between two reward cards takes ~100 ms to cross.
        Assert.False(s.Update(false, 50));
        Assert.False(s.Update(false, 150));
        Assert.False(s.Update(true, 200));
        // The off-zone time restarts: 250 ms after leaving the second card is still too soon.
        Assert.False(s.Update(false, 400));
        Assert.False(s.Update(false, 650));
        Assert.True(s.Hidden);
        Assert.True(s.Update(false, 700));
    }

    [Fact]
    public void Reset_shows_again_at_once()
    {
        var s = new HoverYieldState();
        Assert.False(s.Reset());
        s.Update(true, 0);
        Assert.True(s.Reset());
        Assert.False(s.Hidden);
        // A stale "left at" time does not survive the reset.
        s.Update(true, 10);
        Assert.False(s.Update(false, 20));
        Assert.True(s.Hidden);
    }
}
