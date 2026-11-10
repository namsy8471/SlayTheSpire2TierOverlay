using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Views;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Events;
using PixelRect = Sts2Overlay.Vision.PixelRect;

namespace Sts2Overlay.App.Tests;

/// <summary><see cref="EventScreenTracker"/>: which option is on which button, page after page.</summary>
public class EventScreenTrackerTests
{
    private const string Json = """
    {
      "events": [
        {
          "id": "EVENT.TEST_DOOR",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "TEST_DOOR.pages.INITIAL.options.OPEN", "next": "INSIDE" },
              { "key": "TEST_DOOR.pages.INITIAL.options.KNOCK", "next": "ANSWER" },
              { "key": "TEST_DOOR.pages.INITIAL.options.KNOCK_LOCKED" }
            ]},
            { "page": "INSIDE", "options": [
              { "key": "TEST_DOOR.pages.INSIDE.options.TAKE", "next": "DONE" },
              { "key": "TEST_DOOR.pages.INSIDE.options.LEAVE", "leave": true }
            ]},
            { "page": "ANSWER", "options": [ { "key": "TEST_DOOR.pages.ANSWER.options.PROCEED", "leave": true } ] },
            { "page": "DONE", "options": [ { "key": "TEST_DOOR.pages.DONE.options.PROCEED", "leave": true } ] }
          ]
        }
      ]
    }
    """;

    private static readonly EventCatalog Catalog = EventCatalog.Parse(Json);

    /// <summary>A screen of buttons; <paramref name="text"/> picks the button texts (same number = same text).</summary>
    private static EventScreen Screen(int count, int hover = -1, int text = 0, int locked = -1)
        => new(Enumerable.Range(0, count).Select(i =>
        {
            var sig = new float[96];
            sig[(text * 7 + i * 13) % 96] = 1; // a distinct "text" per page and button
            var state = i == locked ? EventButtonState.Locked : i == hover ? EventButtonState.Hovered : EventButtonState.Idle;
            return new EventButton(i, new PixelRect(930, 604 + 104 * i, 784, 84), state, sig);
        }).ToArray());

    private static string[] Short(EventButtonMatch? m) => m!.Keys.Select(k => k[(k.LastIndexOf('.') + 1)..]).ToArray();

    private static EventScreenTracker Start()
    {
        var t = new EventScreenTracker(Catalog);
        t.SetEvent("EVENT.TEST_DOOR");
        return t;
    }

    [Fact]
    public void First_screen_is_the_first_page_and_a_locked_button_takes_its_locked_key()
    {
        var t = Start();
        Assert.True(t.OnScreen(Screen(2, locked: 1)));
        Assert.Equal(["OPEN", "KNOCK_LOCKED"], Short(t.Current));
        Assert.False(t.Current!.Exact);
    }

    [Fact]
    public void The_hovered_button_when_the_buttons_go_away_is_the_click()
    {
        var t = Start();
        t.OnScreen(Screen(2));
        t.OnScreen(Screen(2, hover: 0)); // mouse on OPEN
        Assert.False(t.OnScreen(null));  // one frame without buttons: a tooltip or the click animation
        Assert.NotNull(t.Current);
        t.OnScreen(null);                // gone: OPEN was clicked
        Assert.Null(t.Current);
        Assert.True(t.OnScreen(Screen(2, text: 1)));
        Assert.Equal(["TAKE", "LEAVE"], Short(t.Current));
    }

    [Fact]
    public void New_text_on_the_same_number_of_buttons_is_a_new_page()
    {
        var t = Start();
        t.OnScreen(Screen(2, hover: 0));
        // OPEN clicked; INSIDE also has two buttons, and the mouse now rests on its first one.
        t.OnScreen(Screen(2, hover: 0, text: 1));
        t.OnScreen(Screen(2, hover: 0, text: 1));
        Assert.Equal(["TAKE", "LEAVE"], Short(t.Current));
    }

    [Fact]
    public void Hover_alone_does_not_change_the_page()
    {
        var t = Start();
        t.OnScreen(Screen(2));
        for (int i = 0; i < 5; i++) t.OnScreen(Screen(2, hover: i % 2));
        Assert.Equal(["OPEN", "KNOCK"], Short(t.Current));
    }

    [Fact]
    public void An_unseen_click_stops_the_markers_for_the_room()
    {
        var t = Start();
        t.OnScreen(Screen(2));  // never hovered (keyboard, or between captures)
        t.OnScreen(null);
        t.OnScreen(null);
        t.OnScreen(Screen(2, text: 1));
        Assert.Null(t.Current);
        t.SetEvent("EVENT.TEST_DOOR"); // same room: still nothing
        t.OnScreen(Screen(2, text: 1));
        Assert.Null(t.Current);
    }

    [Fact]
    public void A_count_that_does_not_fit_the_page_gives_no_match()
    {
        var t = Start();
        t.OnScreen(Screen(3));
        Assert.Null(t.Current);
    }

    [Fact]
    public void Coop_log_keys_are_exact_and_log_choices_move_the_page()
    {
        var t = Start();
        string[] offered = ["TEST_DOOR.pages.INITIAL.options.KNOCK", "TEST_DOOR.pages.INITIAL.options.OPEN"];
        t.SetLog(offered, []);
        t.OnScreen(Screen(2));
        Assert.True(t.Current!.Exact);
        Assert.Equal(["KNOCK", "OPEN"], Short(t.Current));

        t.SetLog(offered, ["TEST_DOOR.pages.INITIAL.options.KNOCK"]);
        Assert.Null(t.Current);
        t.OnScreen(Screen(1, text: 2));
        Assert.Equal(["PROCEED"], Short(t.Current));
    }

    [Fact]
    public void Offered_keys_belong_to_their_room_only()
    {
        // A seed replay (single-player ancient) or a log names one room's options; the next room must not reuse them.
        var t = Start();
        t.SetLog(["TEST_DOOR.pages.INITIAL.options.KNOCK", "TEST_DOOR.pages.INITIAL.options.OPEN"], []);
        t.SetEvent(null);
        t.SetEvent("EVENT.TEST_DOOR");
        t.OnScreen(Screen(2));
        Assert.False(t.Current!.Exact);
        Assert.Equal(["OPEN", "KNOCK"], Short(t.Current));
    }

    [Fact]
    public void Markers_sit_right_of_the_button_at_half_its_height()
    {
        var layer = new PxBox(0, 0, 3840, 2160);
        var (left, top, scale) = EventMarkerLayout.Place(new PxBox(1860, 1208, 1568, 168), layer, 1.5, 1.0, 170);
        Assert.True(left > (1860 + 1568) / 1.5);
        Assert.Equal(168 / 1.5 * 0.5 / EventMarkerLayout.PillHeight, scale, 3);
        Assert.Equal(1208 / 1.5 + (168 / 1.5 - EventMarkerLayout.PillHeight * scale) / 2, top, 3);
    }

    // ---- Text ink (what the app's detector gives): every button's text is compared, hovered or not. ----

    /// <summary>
    /// A button's text ink: 2 px dots on a 4 px grid, half of them set by the text's name, so other text is another
    /// dot set. <paramref name="every"/> keeps one dot in that many (a fading text).
    /// <para>
    /// The dots come from <see cref="StableHash"/>, not <see cref="HashCode"/>: HashCode is seeded anew in every process,
    /// so the dots were another set on every test run, and about 1 set in 120 made a fading text line up better one
    /// grid step aside, which <see cref="TextInk.Judge"/> calls "changed" (a release build failed on that).
    /// </para>
    /// </summary>
    private static TextInk Ink(string text, int every = 1)
    {
        const int w = 160, h = 24;
        var ink = new bool[w * h];
        int n = 0;
        for (int gy = 0; gy < h / 4; gy++)
            for (int gx = 0; gx < w / 4; gx++)
            {
                uint k = StableHash(text, gx, gy) * 2654435761u;
                if (((k >> 11) & 1) == 0 || n++ % every != 0) continue;
                for (int y = 0; y < 2; y++)
                    for (int x = 0; x < 2; x++)
                        ink[(gy * 4 + y) * w + gx * 4 + x] = true;
            }
        return new TextInk(ink, w, h, w / 2f, h / 2f);
    }

    /// <summary>FNV-1a over the text and the grid cell: the same value in every process and on every machine.</summary>
    private static uint StableHash(string text, int gx, int gy)
    {
        uint k = 2166136261;
        foreach (char c in text) k = (k ^ c) * 16777619;
        k = (k ^ (uint)gx) * 16777619;
        k = (k ^ (uint)gy) * 16777619;
        return k ^ (k >> 15);
    }

    /// <summary>A screen whose buttons show <paramref name="texts"/> (as ink); the signatures are all the same.</summary>
    private static EventScreen InkScreen(string[] texts, int hover = -1, int every = 1)
        => new(texts.Select((text, i) => new EventButton(i, new PixelRect(930, 604 + 104 * i, 784, 84),
            i == hover ? EventButtonState.Hovered : EventButtonState.Idle, new float[96], Ink(text, every))).ToArray());

    /// <summary>
    /// The click leaves the mouse on the next page's button in the same place, and the other button keeps its text:
    /// no button is idle with new text, which the signature check could not see. Ink sees the hovered one change.
    /// </summary>
    [Fact]
    public void A_page_change_under_the_mouse_is_seen_and_hides_the_old_markers_at_once()
    {
        var t = Start();
        t.OnScreen(InkScreen(["open", "knock"], hover: 0));
        Assert.Equal(["OPEN", "KNOCK"], Short(t.Current));
        Assert.True(t.OnScreen(InkScreen(["take", "knock"], hover: 0)));
        Assert.Null(t.Current); // other text: the old page's markers would be wrong
        t.OnScreen(InkScreen(["take", "knock"], hover: 0));
        Assert.Equal(["TAKE", "LEAVE"], Short(t.Current));
    }

    [Fact]
    public void Without_ink_the_same_change_goes_unseen()
    {
        // the coarse fallback (no ink): a hovered button is not compared, so the page seems unchanged
        var t = Start();
        t.OnScreen(Screen(2, hover: 0, text: 0));
        var next = new EventScreen(Screen(2, hover: 0, text: 1).Buttons.Select((b, i) => i == 1 ? Screen(2, text: 0).Buttons[1] : b).ToArray());
        t.OnScreen(next);
        t.OnScreen(next);
        Assert.Equal(["OPEN", "KNOCK"], Short(t.Current));
    }

    [Fact]
    public void A_page_fading_in_and_out_keeps_its_markers()
    {
        var t = Start();
        t.OnScreen(InkScreen(["open", "knock"], every: 3));  // fading in: a third of the ink
        Assert.NotNull(t.Current);
        t.OnScreen(InkScreen(["open", "knock"]));            // full: becomes the reference
        t.OnScreen(InkScreen(["open", "knock"], hover: 1, every: 2)); // fading out: half
        Assert.Equal(["OPEN", "KNOCK"], Short(t.Current));
    }

    [Fact]
    public void Unsure_text_hides_the_markers_and_stops_them_when_it_lasts()
    {
        Assert.Equal(TextVerdict.Unsure, TextInk.Judge(Ink("open"), Ink("open", every: 12))); // not "changed"
        var t = Start();
        t.OnScreen(InkScreen(["open", "knock"], hover: 1));
        Assert.True(t.OnScreen(InkScreen(["open", "knock"], every: 12))); // almost all ink gone: nothing to tell
        Assert.Null(t.Current);
        Assert.True(t.OnScreen(InkScreen(["open", "knock"])));            // the same again: back
        Assert.Equal(["OPEN", "KNOCK"], Short(t.Current));
        for (int i = 0; i < EventScreenTracker.UnsureFrames; i++) t.OnScreen(InkScreen(["open", "knock"], every: 12));
        Assert.Null(t.Current);
        t.OnScreen(InkScreen(["open", "knock"]));                         // stopped for the room
        Assert.Null(t.Current);
    }
}
