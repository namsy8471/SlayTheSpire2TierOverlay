using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// An empty map / event layer gives its full-screen surface back by shrinking to 1x1 px (Views/LayerTrimmer.cs): when
/// it is trimmed, where the window then sits, and that marks always find it full size again.
/// </summary>
public class LayerTrimTests
{
    private const long Delay = 5000;

    // ---- EmptyLayerTrim: when ----

    [Fact]
    public void Trims_only_after_the_layer_was_empty_for_the_whole_delay()
    {
        var trim = new EmptyLayerTrim(Delay);
        Assert.False(trim.OnContent(hasMarks: false, trimmed: false, nowMs: 1000));
        Assert.True(trim.Waiting(trimmed: false));
        Assert.False(trim.ShouldTrim(trimmed: false, nowMs: 1000 + Delay - 1));
        Assert.True(trim.ShouldTrim(trimmed: false, nowMs: 1000 + Delay));
    }

    [Fact]
    public void A_layer_placed_before_its_first_marks_counts_as_empty_from_then()
    {
        var trim = new EmptyLayerTrim(Delay);
        Assert.False(trim.Waiting(trimmed: false)); // nothing known yet: no timer
        trim.OnContent(hasMarks: false, trimmed: false, nowMs: 0); // SetLayerRect
        Assert.True(trim.ShouldTrim(trimmed: false, nowMs: Delay));
    }

    [Fact]
    public void Repeated_empty_updates_do_not_restart_the_count()
    {
        var trim = new EmptyLayerTrim(Delay);
        trim.OnContent(false, false, 0);
        trim.OnContent(false, false, 3000); // a placement or another Clear while still empty
        Assert.True(trim.ShouldTrim(false, Delay));
    }

    [Fact]
    public void Short_gaps_between_marks_never_trim()
    {
        // The map clears its marks while it scrolls and draws them again once it stands still.
        var trim = new EmptyLayerTrim(Delay);
        long t = 0;
        for (var i = 0; i < 20; i++)
        {
            trim.OnContent(hasMarks: true, trimmed: false, t);
            t += 900;
            trim.OnContent(hasMarks: false, trimmed: false, t);
            t += 300;
            Assert.False(trim.ShouldTrim(false, t));
        }
    }

    [Fact]
    public void Marks_bring_a_trimmed_layer_back_at_once_and_stop_the_count()
    {
        var trim = new EmptyLayerTrim(Delay);
        trim.OnContent(false, false, 0);
        Assert.True(trim.ShouldTrim(false, Delay));
        Assert.False(trim.Waiting(trimmed: true)); // trimmed: nothing more to wait for
        Assert.True(trim.OnContent(hasMarks: true, trimmed: true, nowMs: Delay + 10)); // untrim before drawing
        Assert.False(trim.Waiting(trimmed: false));
        Assert.False(trim.ShouldTrim(false, Delay * 10));
        Assert.False(trim.OnContent(hasMarks: true, trimmed: false, nowMs: Delay * 10)); // already full size
    }

    [Fact]
    public void A_held_layer_is_never_trimmed_and_a_trimmed_one_comes_back_when_held()
    {
        // The map is about to open / an event room is open: marks are coming, keep the layer full size.
        var trim = new EmptyLayerTrim(Delay);
        trim.OnContent(false, false, 0);
        Assert.False(trim.SetHold(hold: true, trimmed: false, nowMs: 100)); // full size already: nothing to do
        Assert.False(trim.ShouldTrim(false, Delay * 3));
        Assert.False(trim.SetHold(true, false, 200)); // unchanged
        // Trimmed when the hold starts (left the map long ago, now it is about to open again): back at once.
        var trimmed = new EmptyLayerTrim(Delay);
        trimmed.OnContent(false, false, 0);
        Assert.True(trimmed.ShouldTrim(false, Delay));
        Assert.True(trimmed.SetHold(hold: true, trimmed: true, nowMs: Delay + 10));
    }

    [Fact]
    public void After_a_hold_an_empty_layer_waits_a_whole_delay_again()
    {
        var trim = new EmptyLayerTrim(Delay);
        trim.OnContent(false, false, 0);
        trim.SetHold(true, false, 0);
        Assert.False(trim.SetHold(hold: false, trimmed: false, nowMs: 20_000)); // releasing never untrims
        Assert.False(trim.ShouldTrim(false, 20_000 + Delay - 1));
        Assert.True(trim.ShouldTrim(false, 20_000 + Delay));
    }

    // ---- LayerPlacement: where ----

    private sealed class FakeWindow
    {
        public PixelRect Bounds = new(0, 0, 100, 100);
        public int Sets, InPlaceSets;
        public readonly List<string> Log = [];

        public LayerPlacement Placement() => new("map", _ => Bounds, (_, r) =>
        {
            Sets++;
            Bounds = r;
        }, Log.Add, (_, r) =>
        {
            InPlaceSets++;
            Bounds = r;
        });
    }

    [Fact]
    public void Trimming_keeps_the_layers_place_in_the_z_order()
    {
        // A placement lifts the layer to the top of the topmost band (the controller then puts the panel back above
        // it); a trim or untrim must not, or marks could cover the panel until the next re-order.
        var w = new FakeWindow();
        var p = w.Placement();
        p.Place(Hwnd, new PixelRect(0, 0, 1920, 1080));
        Assert.Equal((1, 0), (w.Sets, w.InPlaceSets));
        p.SetTrimmed(Hwnd, true);
        p.SetTrimmed(Hwnd, false);
        Assert.Equal((1, 2), (w.Sets, w.InPlaceSets));
    }

    private static readonly IntPtr Hwnd = new(1);

    [Fact]
    public void Trimmed_layer_is_one_pixel_at_the_games_corner_and_comes_back_full_size()
    {
        var w = new FakeWindow();
        var p = w.Placement();
        var game = new PixelRect(1920, 100, 3840, 2160);
        p.Place(Hwnd, game);
        Assert.True(p.SetTrimmed(Hwnd, true));
        Assert.Equal(new PixelRect(1920, 100, 1, 1), w.Bounds);
        Assert.False(p.SetTrimmed(Hwnd, true)); // no change, no SetWindowPos
        Assert.True(p.SetTrimmed(Hwnd, false));
        Assert.Equal(game, w.Bounds);
        Assert.Empty(w.Log);
    }

    [Fact]
    public void Trimmed_layer_follows_the_game_and_untrims_onto_its_newest_rect()
    {
        var w = new FakeWindow();
        var p = w.Placement();
        p.Place(Hwnd, new PixelRect(0, 0, 1920, 1080));
        p.SetTrimmed(Hwnd, true);
        // The game moved to the second monitor while the layer was empty: the corner moves with it.
        var moved = new PixelRect(2560, 0, 3840, 2160);
        Assert.True(p.Place(Hwnd, moved));
        Assert.Equal(new PixelRect(2560, 0, 1, 1), w.Bounds);
        p.SetTrimmed(Hwnd, false);
        Assert.Equal(moved, w.Bounds);
    }

    [Fact]
    public void A_dpi_change_while_trimmed_puts_the_corner_back_not_the_full_rect()
    {
        var w = new FakeWindow();
        var p = w.Placement();
        p.Place(Hwnd, new PixelRect(0, 0, 1920, 1080));
        p.SetTrimmed(Hwnd, true);
        w.Bounds = new PixelRect(0, 0, 2, 2); // WPF applied Windows' suggested rect
        p.Reapply(Hwnd);
        Assert.Equal(new PixelRect(0, 0, 1, 1), w.Bounds);
    }

    [Fact]
    public void Trimming_before_the_first_placement_takes_effect_when_the_layer_is_placed()
    {
        var w = new FakeWindow();
        var p = w.Placement();
        p.SetTrimmed(Hwnd, true);
        Assert.Equal(0, w.Sets); // nowhere to go yet
        p.Place(Hwnd, new PixelRect(0, 0, 1920, 1080));
        Assert.Equal(new PixelRect(0, 0, 1, 1), w.Bounds);
    }
}
