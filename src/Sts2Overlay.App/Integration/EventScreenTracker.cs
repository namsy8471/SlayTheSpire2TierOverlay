using Sts2Overlay.Core.Events;
using Sts2Overlay.Vision.Events;

namespace Sts2Overlay.App.Integration;

/// <summary>The buttons on screen lined up with option keys: what the "추천!" markers are drawn from.</summary>
/// <param name="Page">The event page the buttons belong to.</param>
/// <param name="Keys">Option text key per button, top to bottom.</param>
/// <param name="Buttons">Button boxes, frame pixels (same order).</param>
/// <param name="Exact">True when the keys came from the co-op log; false when lined up from the page data.</param>
public sealed record EventButtonMatch(string Page, IReadOnlyList<string> Keys, IReadOnlyList<Vision.PixelRect> Buttons, bool Exact);

/// <summary>
/// Follows an event room from screen to screen and says which option is on which button.
/// <para>
/// Single player has no log of the options, so the page comes from the event data: the first screen is the
/// event's first page, and when its buttons go away (or change text) the button that was under the mouse is the
/// one that was clicked, whose <c>next</c> page comes up. If the click can't be told (no hovered button seen), the
/// tracker stops placing markers for the rest of the room instead of guessing. Co-op logs name the options and
/// the choices, which override all of that.
/// </para>
/// Not thread-safe: the app feeds it on the UI thread.
/// </summary>
public sealed class EventScreenTracker(EventCatalog catalog)
{
    /// <summary>Text-signature distance above which an idle button shows different text (same text: below 0.005).</summary>
    public const float TextChanged = 0.02f;

    /// <summary>Frames in a row without the matched buttons before the page counts as left (a passing tooltip is one frame).</summary>
    public const int LeaveFrames = 2;

    private string? _eventId;
    private string? _page;                 // null: unknown, no markers until the room ends
    private EventScreen? _matchedScreen;
    private IReadOnlyList<string>? _matchedKeys;
    private int _lastHover = -1;
    private int _away;
    private int _logChosen;

    public EventButtonMatch? Current { get; private set; }

    /// <summary>The event the tracker follows; a new id starts over at its first page.</summary>
    public void SetEvent(string? eventId)
    {
        if (eventId == _eventId) return;
        _eventId = eventId;
        _page = eventId is null ? null : catalog.Find(eventId)?.InitialPage?.Page;
        _logChosen = 0;
        Unmatch();
    }

    /// <summary>
    /// Co-op log facts: the offered keys of the first page and the keys chosen so far. A new choice moves to its
    /// option's next page (the log is right even when the mouse was not seen).
    /// </summary>
    public void SetLog(IReadOnlyList<string>? offered, IReadOnlyList<string> chosen)
    {
        _offered = offered;
        if (chosen.Count == _logChosen) return;
        _logChosen = chosen.Count;
        _page = catalog.FindOption(chosen[^1]) is { } hit ? hit.Option.Next : null;
        Unmatch();
    }

    private IReadOnlyList<string>? _offered;

    /// <summary>One captured frame's buttons (null: no event buttons on screen). Returns true when <see cref="Current"/> changed.</summary>
    public bool OnScreen(EventScreen? screen)
    {
        var before = Current;
        if (_matchedScreen is not null)
        {
            if (screen is not null && SameButtons(_matchedScreen, screen))
            {
                _away = 0;
                if (screen.HoveredIndex >= 0) _lastHover = screen.HoveredIndex;
                Current = Current! with { Buttons = screen.Buttons.Select(b => b.Bounds).ToArray() };
                _matchedScreen = screen;
                return !SameBoxes(before, Current);
            }
            if (++_away < LeaveFrames) return false;
            LeavePage();
        }
        if (screen is not null && _page is not null) TryMatch(screen);
        return !SameBoxes(before, Current) && !(before is null && Current is null);
    }

    private void TryMatch(EventScreen screen)
    {
        var def = _eventId is null ? null : catalog.Find(_eventId);
        var page = def?.FindPage(_page);
        IReadOnlyList<string>? keys = null;
        var exact = false;
        if (_logChosen == 0 && _offered is { Count: > 0 } offered && page is not null && page.Page == def!.InitialPage?.Page)
        {
            if (offered.Count == screen.Buttons.Count)
            {
                keys = offered;
                exact = true;
            }
        }
        else if (page is not null)
        {
            keys = EventSlots.Keys(page, screen.Buttons.Count, screen.Buttons.Select(b => b.State == EventButtonState.Locked).ToArray());
        }
        if (keys is null) return;
        _matchedScreen = screen;
        _matchedKeys = keys;
        _lastHover = screen.HoveredIndex;
        _away = 0;
        Current = new EventButtonMatch(page!.Page, keys, screen.Buttons.Select(b => b.Bounds).ToArray(), exact);
    }

    /// <summary>The matched buttons went away: the hovered one was clicked; its next page comes up (or the room ends).</summary>
    private void LeavePage()
    {
        // A co-op log line naming the choice may come before or after this; SetLog overrides either way.
        _page = _lastHover >= 0 && _matchedKeys is { } keys && _lastHover < keys.Count && catalog.FindOption(keys[_lastHover]) is { } hit
            ? hit.Option.Next
            : null;
        Unmatch();
    }

    private void Unmatch()
    {
        _matchedScreen = null;
        _matchedKeys = null;
        _lastHover = -1;
        _away = 0;
        Current = null;
    }

    /// <summary>Same buttons: same count and states apart from hover, and the text of every button idle in both frames agrees.</summary>
    private static bool SameButtons(EventScreen a, EventScreen b)
    {
        if (a.Buttons.Count != b.Buttons.Count) return false;
        for (int i = 0; i < a.Buttons.Count; i++)
        {
            EventButton x = a.Buttons[i], y = b.Buttons[i];
            if ((x.State == EventButtonState.Locked) != (y.State == EventButtonState.Locked)) return false;
            // A hovered button is drawn a little larger, which moves its text: compare idle ones only.
            if (x.State == EventButtonState.Idle && y.State == EventButtonState.Idle
                && EventButtonDetector.SignatureDistance(x.TextSignature, y.TextSignature) > TextChanged) return false;
        }
        return true;
    }

    private static bool SameBoxes(EventButtonMatch? a, EventButtonMatch? b)
        => a is not null && b is not null && a.Buttons.SequenceEqual(b.Buttons) && a.Keys.SequenceEqual(b.Keys) && a.Page == b.Page;
}
