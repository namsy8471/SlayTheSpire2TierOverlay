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
    public void Markers_sit_right_of_the_button_at_half_its_height()
    {
        var layer = new PxBox(0, 0, 3840, 2160);
        var (left, top, scale) = EventMarkerLayout.Place(new PxBox(1860, 1208, 1568, 168), layer, 1.5, 1.0, 170);
        Assert.True(left > (1860 + 1568) / 1.5);
        Assert.Equal(168 / 1.5 * 0.5 / EventMarkerLayout.PillHeight, scale, 3);
        Assert.Equal(1208 / 1.5 + (168 / 1.5 - EventMarkerLayout.PillHeight * scale) / 2, top, 3);
    }
}
