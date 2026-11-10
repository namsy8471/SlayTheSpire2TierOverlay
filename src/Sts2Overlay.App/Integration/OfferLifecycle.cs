namespace Sts2Overlay.App.Integration;

/// <summary>
/// The App-side rules of a card screen's life: which recognition results may (re)draw badges, when the panel jumps to
/// the 추천 tab, and when a half-read reward row may show its verdict. Pure (time is passed in) so it is unit-tested;
/// <see cref="Controllers.AppController"/> owns one and calls it from the UI thread.
///
/// <code>
///  log says the screen is gone ─▶ Forget(shown) ─▶ results from before the reset are dropped (epoch), and for a
///                                                  moment a result showing only the forgotten cards is suppressed
///  vision result ─▶ Accept ─▶ SelectTab (only for a newly opened screen) ─▶ Verdict (hold a half-read row)
/// </code>
/// </summary>
public sealed class OfferLifecycle
{
    /// <summary>How long after <see cref="Forget"/> the forgotten cards stay suppressed unless the screen is seen closing.</summary>
    public TimeSpan SuppressFor { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a reward / choose-a-card row with unread slots waits for them before showing its verdict anyway.</summary>
    public TimeSpan PartialWait { get; init; } = TimeSpan.FromSeconds(2);

    private int _dropBefore;
    private (ScreenKind Kind, string[] Ids)? _forgotten;
    private DateTime _forgottenUntil;
    private (ScreenKind Kind, string[] Ids)? _tabScreen;
    private bool _tabScreenOpen;
    private int? _partialScreen;
    private DateTime _partialSince;

    /// <summary>
    /// The log says the card screen closed (card picked, room left): forget it for good. Results computed before the
    /// recogniser was reset (<paramref name="epoch"/>, from <c>CapturePipeline.ResetRecognition</c>) are dropped, and
    /// fresh results that show only the forgotten cards (the closing animation still shows them for a moment) are
    /// suppressed until the screen is seen closed or <see cref="SuppressFor"/> passed. A new screen (other cards) shows at once.
    /// </summary>
    public void Forget(RecognitionResult? shown, int epoch, DateTime now)
    {
        _dropBefore = Math.Max(_dropBefore, epoch);
        if (shown is { ScreenKind: not ScreenKind.Other } s && s.Cards.Count > 0)
        {
            _forgotten = (s.ScreenKind, Ids(s));
            _forgottenUntil = now + SuppressFor;
        }
    }

    /// <summary>
    /// Whether a recognition result may be applied at all. False for a result from before the last <see cref="Forget"/>
    /// and for a still-suppressed view of the forgotten screen. An "Other" result ends the suppression.
    /// </summary>
    public bool Accept(RecognitionResult r, DateTime now)
    {
        if (r.Epoch < _dropBefore) return false;
        if (_forgotten is not { } f) return true;
        if (r.ScreenKind == ScreenKind.Other || now >= _forgottenUntil || r.ScreenKind != f.Kind || !IsSubset(Ids(r), f.Ids))
        {
            _forgotten = null; // the forgotten screen closed, or this is another one
            return true;
        }
        return false;
    }

    /// <summary>
    /// A card / shop screen is shown: true when the panel should switch to the 추천 tab, i.e. the screen newly opened
    /// (nothing was open, or other cards than the open screen's). Prices, label colours, relic slots, a card read late
    /// or a bought card never count, and neither does the same open screen again after <see cref="Forget"/>.
    /// </summary>
    public bool SelectTab(RecognitionResult r)
    {
        var ids = Ids(r);
        var same = _tabScreenOpen && _tabScreen is { } s && s.Kind == r.ScreenKind
                   && (r.ScreenKind == ScreenKind.Shop || IsSubset(ids, s.Ids) || IsSubset(s.Ids, ids));
        _tabScreen = (r.ScreenKind, ids);
        _tabScreenOpen = true;
        return !same;
    }

    /// <summary>Vision says no card screen is open (it closed for real, past the tracker's hold).</summary>
    public void ScreenClosed()
    {
        _tabScreenOpen = false;
        _partialScreen = null;
    }

    /// <summary>
    /// Reward / choose-a-card row with fewer cards read than the layout has slots (one is enlarged under the mouse,
    /// or still fading in): <see cref="PartialVerdict.Hold"/> while the missing card may still be read (best pick and
    /// skip verdict wait), <see cref="PartialVerdict.ShowIncomplete"/> once <see cref="PartialWait"/> passed.
    /// </summary>
    public PartialVerdict Verdict(RecognitionResult r, DateTime now)
    {
        if (r.ScreenKind is not (ScreenKind.Reward or ScreenKind.Choice) || r.Slots <= r.Cards.Count)
            return PartialVerdict.Complete;
        if (_partialScreen != r.ScreenId)
        {
            _partialScreen = r.ScreenId;
            _partialSince = now;
        }
        return now - _partialSince < PartialWait ? PartialVerdict.Hold : PartialVerdict.ShowIncomplete;
    }

    /// <summary>When a held verdict is due (to re-render then even if no new frame arrives), or null.</summary>
    public DateTime? VerdictDue(RecognitionResult r)
        => _partialScreen == r.ScreenId && r.Slots > r.Cards.Count ? _partialSince + PartialWait : null;

    private static string[] Ids(RecognitionResult r) => r.Cards.Select(c => c.Card.Id).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Multiset inclusion of two sorted id lists.</summary>
    private static bool IsSubset(string[] small, string[] big)
    {
        int j = 0;
        foreach (var id in small)
        {
            while (j < big.Length && string.CompareOrdinal(big[j], id) < 0) j++;
            if (j == big.Length || big[j] != id) return false;
            j++;
        }
        return true;
    }
}

/// <summary>What a partly read reward row may show (<see cref="OfferLifecycle.Verdict"/>).</summary>
public enum PartialVerdict
{
    /// <summary>Every slot read (or not a row screen): show everything.</summary>
    Complete,
    /// <summary>Some slots unread, still waiting: tier badges only, no best-pick ribbon, no skip verdict.</summary>
    Hold,
    /// <summary>Waited long enough: show the verdict with a note that a card was not read.</summary>
    ShowIncomplete,
}

/// <summary>
/// Capture pause: nothing is drawn while the overlay is hidden by the user or the game is not in front (or minimised),
/// so after <see cref="After"/> of that the capture session is stopped. A short alt-tab keeps it (restarting a WGC
/// session costs a frame or two). Pure; time is passed in.
/// </summary>
public sealed class CapturePauseClock
{
    public TimeSpan After { get; init; } = TimeSpan.FromSeconds(3);

    private DateTime? _idleSince;

    /// <summary>Whether capture should be paused now; <paramref name="recheckIn"/> is when to ask again (grace period running), else null.</summary>
    public bool ShouldPause(bool overlayShown, DateTime now, out TimeSpan? recheckIn)
    {
        recheckIn = null;
        if (overlayShown)
        {
            _idleSince = null;
            return false;
        }
        _idleSince ??= now;
        var idle = now - _idleSince.Value;
        if (idle >= After) return true;
        recheckIn = After - idle;
        return false;
    }
}
