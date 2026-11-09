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
/// <para>
/// Text changes are read from each button's <see cref="TextInk"/> against the ink it was matched on (every button,
/// hovered or not, since a click usually leaves the mouse on a button of the next page): <see cref="TextVerdict.Changed"/>
/// counts as the buttons going away, <see cref="TextVerdict.Unsure"/> hides the markers until a frame reads the same
/// again, and <see cref="UnsureFrames"/> such frames without one stop the markers for the room (a wrong page's
/// markers are a wrong answer, none is only an unknown). Buttons without ink fall back to the coarse
/// <see cref="EventButton.TextSignature"/> of idle buttons.
/// </para>
/// Not thread-safe: the app feeds it on the UI thread.
/// </summary>
public sealed class EventScreenTracker(EventCatalog catalog)
{
    /// <summary>
    /// Fallback for buttons without ink: text-signature distance above which an idle button shows different text
    /// (same text: below 0.005; but other pages can come as close as 0.007, so ink is what decides when present).
    /// </summary>
    public const float TextChanged = 0.02f;

    /// <summary>Frames in a row without the matched buttons before the page counts as left (a passing tooltip is one frame).</summary>
    public const int LeaveFrames = 2;

    /// <summary>Frames of <see cref="TextVerdict.Unsure"/> text, with no frame reading the same since, before the markers stop for the room.</summary>
    public const int UnsureFrames = 3;

    private string? _eventId;
    private string? _page;                 // null: unknown, no markers until the room ends
    private EventScreen? _matchedScreen;
    private TextInk?[]? _refInks;          // per button: the fullest ink seen of the matched text
    private EventButtonMatch? _match;      // what Current shows while the text reads the same
    private IReadOnlyList<string>? _matchedKeys;
    private int _lastHover = -1;
    private int _away;
    private int _unsure;
    private int _logChosen;

    public EventButtonMatch? Current { get; private set; }

    /// <summary>The event the tracker follows; a new id starts over at its first page.</summary>
    public void SetEvent(string? eventId)
    {
        if (eventId == _eventId) return;
        _eventId = eventId;
        _page = eventId is null ? null : catalog.Find(eventId)?.InitialPage?.Page;
        _offered = null; // the last room's offered keys (log or seed replay) must not line up this room's buttons
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
            TextVerdict v = screen is null ? TextVerdict.Changed : Compare(screen);
            if (v == TextVerdict.Same)
            {
                _away = 0;
                _unsure = 0;
                if (screen!.HoveredIndex >= 0) _lastHover = screen.HoveredIndex;
                _match = _match! with { Buttons = screen.Buttons.Select(b => b.Bounds).ToArray() };
                Current = _match;
                _matchedScreen = screen;
                return Moved(before, Current);
            }
            if (v == TextVerdict.Unsure)
            {
                // Neither this page's text nor clearly another's: show nothing, and don't trust its hover.
                Current = null;
                if (++_unsure >= UnsureFrames)
                {
                    _page = null;
                    Unmatch();
                }
                return Moved(before, Current);
            }
            // Gone, or other text. A frame without buttons keeps the markers (a passing tooltip); other text hides
            // them at once, as they would point at another page's options.
            if (screen is not null) Current = null;
            if (++_away < LeaveFrames) return Moved(before, Current);
            LeavePage();
        }
        if (screen is not null && _page is not null) TryMatch(screen);
        return Moved(before, Current);
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
        _refInks = screen.Buttons.Select(b => b.Ink).ToArray();
        _matchedKeys = keys;
        _lastHover = screen.HoveredIndex;
        _away = 0;
        _unsure = 0;
        _match = new EventButtonMatch(page!.Page, keys, screen.Buttons.Select(b => b.Bounds).ToArray(), exact);
        Current = _match;
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
        _refInks = null;
        _match = null;
        _matchedKeys = null;
        _lastHover = -1;
        _away = 0;
        _unsure = 0;
        Current = null;
    }

    /// <summary>
    /// The matched page's buttons against a frame's: <see cref="TextVerdict.Changed"/> when the count or a locked face
    /// differs or any button's text does, else <see cref="TextVerdict.Unsure"/> when any button's is too close to
    /// call, else the same. A button reading the same with at least as much ink becomes the new reference (a page
    /// fading in), so a fading-out frame is always judged against the full text.
    /// </summary>
    private TextVerdict Compare(EventScreen screen)
    {
        EventScreen a = _matchedScreen!;
        if (a.Buttons.Count != screen.Buttons.Count) return TextVerdict.Changed;
        var worst = TextVerdict.Same;
        for (int i = 0; i < a.Buttons.Count; i++)
        {
            EventButton x = a.Buttons[i], y = screen.Buttons[i];
            if ((x.State == EventButtonState.Locked) != (y.State == EventButtonState.Locked)) return TextVerdict.Changed;
            TextVerdict v;
            if (_refInks![i] is { } refInk && y.Ink is { } ink)
            {
                v = TextInk.Judge(refInk, ink);
                if (v == TextVerdict.Same && ink.Count >= refInk.Count) _refInks[i] = ink;
            }
            else
            {
                // No ink (older callers): a hovered button is drawn a little larger, which moves its text in the
                // coarse signature, so only buttons idle in both frames are compared.
                v = x.State == EventButtonState.Idle && y.State == EventButtonState.Idle
                    && EventButtonDetector.SignatureDistance(x.TextSignature, y.TextSignature) > TextChanged
                    ? TextVerdict.Changed : TextVerdict.Same;
            }
            if (v == TextVerdict.Changed) return v; // no need to read the rest (a mismatch is the slow case)
            if (v > worst) worst = v;
        }
        return worst;
    }

    private static bool Moved(EventButtonMatch? before, EventButtonMatch? now)
        => !(before is null && now is null) && !SameBoxes(before, now);

    private static bool SameBoxes(EventButtonMatch? a, EventButtonMatch? b)
        => a is not null && b is not null && a.Buttons.SequenceEqual(b.Buttons) && a.Keys.SequenceEqual(b.Keys) && a.Page == b.Page;
}
