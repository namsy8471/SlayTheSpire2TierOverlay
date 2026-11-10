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

    // ---- why: the game enlarges the card under the mouse ----

    [Fact]
    public void The_edge_of_a_hovered_card_counts_although_the_recogniser_saw_it_resting()
    {
        // Reward card at rest (scale 0.8) as last recognised; the mouse touches it and it snaps to 1.0 at once.
        var resting = Card(960, 616, 0.8);
        var hovered = Card(960, 616, 1.0);
        var zone = HoverGeometry.Zoom(resting, HoverGeometry.CardHoverZoom);

        // A point just inside the hovered card's right edge: the game shows the tip, so the overlay must step aside.
        var x = hovered.Right - 4;
        var y = hovered.Y + hovered.Height / 2;
        Assert.False(HoverGeometry.Contains([resting], x, y)); // the old zone (resting size + 6 %) missed it
        Assert.True(HoverGeometry.Contains([zone], x, y));

        // ... without reaching the neighbour 350 px away (its badge must come back once the mouse moves there).
        var neighbour = Card(1310, 616, 0.8);
        Assert.False(HoverGeometry.Contains([zone], neighbour.X + 2, y));
    }

    [Fact]
    public void Zoom_scales_about_the_centre()
    {
        Assert.Equal(new PxBox(75, 75, 250, 250), HoverGeometry.Zoom(new PxBox(100, 100, 200, 200), 1.25));
        Assert.Equal(new PxBox(0, 0, 0, 0), HoverGeometry.Zoom(new PxBox(0, 0, 0, 0), 1.25)); // empty stays empty
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
    public void Zones_outlive_a_recognition_that_comes_back_empty_for_a_moment()
    {
        var m = new HoverZoneMemory(holdMs: 1500);
        PxBox[] cards = [new(100, 100, 300, 400)];
        Assert.Equal(cards, m.Zones(cards, 0));
        // Hovering the card hid the badges' recognition for a beat: the zones stay, so the overlay stays aside.
        Assert.Equal(cards, m.Zones([], 1000));
        Assert.Equal(cards, m.Zones([], 1500));
        // Long gone (the screen really closed): nothing left to test against.
        Assert.Empty(m.Zones([], 1501));
        // New marks replace the memory.
        PxBox[] next = [new(500, 100, 300, 400)];
        Assert.Equal(next, m.Zones(next, 2000));
        m.Clear();
        Assert.Empty(m.Zones([], 2001));
    }

    [Fact]
    public void Reading_a_card_while_its_badge_flickers_keeps_the_overlay_aside()
    {
        // The full loop at 50 ms polls: the mouse never leaves the card, the badges vanish for 600 ms in between.
        var state = new HoverYieldState(showDelayMs: 300);
        var memory = new HoverZoneMemory();
        PxBox[] card = [new(100, 100, 300, 400)];
        var changes = 0;
        for (long t = 0; t <= 2000; t += 50)
        {
            var current = t is >= 500 and < 1100 ? [] : card;
            if (state.Update(HoverGeometry.Contains(memory.Zones(current, t), 250, 300), t)) changes++;
            Assert.True(t == 0 || state.Hidden, $"overlay came back at {t} ms");
        }
        Assert.Equal(1, changes); // hidden once, never shown again while the card is read
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

    // ---- a resting cursor and our own panel ----

    [Fact]
    public void A_cursor_parked_on_a_card_brings_the_badges_back_after_a_while()
    {
        // The player clicked a reward where the next screen's card appears, or plays on a gamepad: the cursor never moves.
        var s = new HoverYieldState(showDelayMs: 300, idleShowMs: 2500);
        var still = (X: 960, Y: 600);
        Assert.False(s.Update(false, 0, still));
        Assert.True(s.Update(true, 5000, still)); // a card appeared under it: step aside (its tip may be up)
        Assert.False(s.Update(true, 7450, still)); // the resting time counts from the hide, not from the last move
        Assert.True(s.Update(true, 7500, still)); // 2.5 s without a move: back
        Assert.False(s.Hidden);
        Assert.False(s.Update(true, 20000, still)); // and it stays back while the cursor rests

        Assert.True(s.Update(true, 20050, (961, 600))); // any movement: normal rules again, on a zone → aside
        Assert.True(s.Hidden);
        Assert.False(s.Update(true, 40000, (961, 600))); // and that hide came from a movement: it never times out
        Assert.True(s.Hidden);
    }

    [Fact]
    public void Reading_a_long_tip_with_the_mouse_held_still_keeps_the_overlay_aside()
    {
        // The player moves onto a card and holds still to read its tip: the badges must not come back over it.
        var s = new HoverYieldState(showDelayMs: 300, idleShowMs: 2500);
        s.Update(false, 0, (200, 900));
        s.Update(false, 50, (600, 700));
        Assert.True(s.Update(true, 100, (900, 600))); // moved onto the card on this sample
        for (long t = 150; t <= 10100; t += 50)
        {
            Assert.False(s.Update(true, t, (900, 600)), $"overlay came back at {t} ms");
        }
        Assert.True(s.Hidden);

        // Stopping just short of the card and the card's zone growing under it a moment later is still deliberate.
        var s2 = new HoverYieldState(showDelayMs: 300, idleShowMs: 2500);
        s2.Update(false, 0, (10, 10));
        s2.Update(false, 1000, (900, 600)); // last movement
        Assert.True(s2.Update(true, 1000 + HoverYieldState.SettleMs - 50, (900, 600)));
        Assert.False(s2.Update(true, 10000, (900, 600)));
        Assert.True(s2.Hidden);
    }

    [Fact]
    public void After_a_deliberate_hover_a_zone_appearing_under_a_resting_cursor_still_comes_back()
    {
        var s = new HoverYieldState(showDelayMs: 300, idleShowMs: 2500);
        s.Update(false, 0, (100, 100));
        Assert.True(s.Update(true, 50, (300, 300))); // moved onto a card: deliberate
        Assert.False(s.Update(false, 100, (700, 300))); // moved off it ...
        Assert.True(s.Update(false, 400, (700, 300))); // ... back after 300 ms
        // Rests there; a second later the next screen puts a card under it.
        Assert.True(s.Update(true, 1400, (700, 300)));
        Assert.False(s.Update(true, 3850, (700, 300)));
        Assert.True(s.Update(true, 3900, (700, 300))); // 2.5 s after the hide: back
        Assert.False(s.Hidden);
    }

    [Fact]
    public void A_moving_cursor_never_counts_as_resting()
    {
        var s = new HoverYieldState(showDelayMs: 300, idleShowMs: 2500);
        s.Update(true, 0, (100, 100));
        for (var t = 50; t <= 5000; t += 50)
        {
            Assert.False(s.Update(true, t, (100 + t / 50 % 2, 100))); // reading a tip, the hand trembles a pixel
        }
        Assert.True(s.Hidden);
        // Without a position (the old overload) it never comes back by itself either.
        var noPos = new HoverYieldState(showDelayMs: 300, idleShowMs: 2500);
        noPos.Update(true, 0);
        Assert.False(noPos.Update(true, 10000));
        Assert.True(noPos.Hidden);
    }

    [Fact]
    public void Reset_ends_a_parked_show()
    {
        var s = new HoverYieldState(showDelayMs: 300, idleShowMs: 2500);
        s.Update(false, 0, (5, 5));
        s.Update(true, 1000, (5, 5)); // a zone appeared under the resting cursor
        s.Update(true, 3500, (5, 5));
        Assert.False(s.Hidden);
        s.Reset(); // overlay hidden / game left the front
        Assert.True(s.Update(true, 3600, (5, 5))); // shown again later over a zone: steps aside again first
    }

    [Fact]
    public void Our_own_panel_never_makes_the_overlay_step_aside()
    {
        // 4K at 150 %: the top strip reaches y 340 px; the panel's default header sits at y 210 px inside it.
        var game = new PxBox(0, 0, 3840, 2160);
        var strip = HoverGeometry.TopStrip(game);
        var panel = new PxBox(3204, 210, 600, 900);
        PxBox[] zones = [new(3300, 400, 300, 400)];

        Assert.True(HoverGeometry.ShouldYield(3000, 250, strip, zones, panel)); // the top strip next to the panel
        Assert.False(HoverGeometry.ShouldYield(3300, 250, strip, zones, panel)); // the panel's tabs, inside the strip
        Assert.False(HoverGeometry.ShouldYield(3400, 500, strip, zones, panel)); // a card zone under the panel
        Assert.True(HoverGeometry.ShouldYield(3400, 500, strip, zones, null)); // panel hidden: the zone counts
        Assert.False(HoverGeometry.ShouldYield(1000, 250, null, zones, panel)); // no strip (no marks shown)
    }
}
