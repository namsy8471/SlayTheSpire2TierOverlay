using Sts2Overlay.Core.Events;

namespace Sts2Overlay.Core.Tests.Events;

/// <summary><see cref="EventSlots"/>: lining up a page's options with the buttons on screen.</summary>
public class EventSlotsTests
{
    private static readonly Lazy<EventCatalog> Real = new(() => EventCatalog.LoadDirectory(PackDir()));

    private static string PackDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SlayTheSpire2TierOverlay.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data", "packs", "v0.107.1");
    }

    private static EventPage Initial(string id) => Real.Value.Find(id)!.InitialPage!;

    private static string[] Short(IReadOnlyList<string>? keys) => keys!.Select(k => k[(k.LastIndexOf('.') + 1)..]).ToArray();

    [Fact]
    public void Self_help_book_has_three_buttons_and_a_locked_one_takes_its_locked_key()
    {
        var page = Initial("EVENT.SELF_HELP_BOOK");
        Assert.Equal(["READ_THE_BACK", "READ_PASSAGE", "READ_ENTIRE_BOOK"], Short(EventSlots.Keys(page, 3)));
        Assert.Equal(["READ_THE_BACK", "READ_PASSAGE", "READ_ENTIRE_BOOK_LOCKED"], Short(EventSlots.Keys(page, 3, [false, false, true])));
        // None of the three enchants possible: the lone "move on" button.
        Assert.Equal(["NO_OPTIONS"], Short(EventSlots.Keys(page, 1)));
        Assert.Equal(["READ_THE_BACK", "READ_PASSAGE", "READ_ENTIRE_BOOK"], EventSlots.Displayed(page).Select(o => o.ShortName));
    }

    [Fact]
    public void Ranwid_keeps_its_button_order_with_locked_faces()
    {
        var page = Initial("EVENT.RANWID_THE_ELDER");
        Assert.Equal(["POTION", "GOLD", "RELIC"], Short(EventSlots.Keys(page, 3)));
        Assert.Equal(["POTION_LOCKED", "GOLD", "RELIC"], Short(EventSlots.Keys(page, 3, [true, false, false])));
    }

    [Fact]
    public void A_count_that_does_not_fit_gives_nothing()
    {
        Assert.Null(EventSlots.Keys(Initial("EVENT.THIS_OR_THAT"), 3));
        Assert.Null(EventSlots.Keys(Initial("EVENT.THIS_OR_THAT"), 1)); // the "continue" screen after a choice
        Assert.Null(EventSlots.Keys(Initial("EVENT.ENDLESS_CONVEYOR"), 2)); // a random dish: can't tell which
        Assert.Equal(["PLAIN", "ORNATE"], Short(EventSlots.Keys(Initial("EVENT.THIS_OR_THAT"), 2)));
    }

    [Fact]
    public void Every_event_page_lines_up_with_its_displayed_count()
    {
        foreach (var e in Real.Value.Events)
        {
            foreach (var p in e.Pages)
            {
                var shown = EventSlots.Displayed(p);
                if (shown.Count == 0) continue;
                var keys = EventSlots.Keys(p, shown.Count);
                Assert.True(keys is not null, $"{e.Id} {p.Page}");
                Assert.Equal(shown.Select(o => o.Key), keys);
            }
        }
    }
}
