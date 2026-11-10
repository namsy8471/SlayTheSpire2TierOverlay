namespace Sts2Overlay.App.Integration;

/// <summary>
/// The "hunt" for a reward / shop screen the log says is coming: from the moment the hint changes to
/// <see cref="ScreenHint.RewardLikely"/> or <see cref="ScreenHint.ShopLikely"/> until badges show, the hint leaves the
/// offer hints, or <see cref="Budget"/> passed, the capture runs at the dense hunt rate instead of the normal one, so
/// the frame that first shows the screen is taken a few tens of ms after it appears instead of up to one 3 Hz interval.
/// Pure (time is passed in) so it is unit-tested; <see cref="Controllers.AppController"/> owns one and asks it from
/// <c>UpdateCadence</c> on the UI thread.
///
/// <code>
///  hint → RewardLikely / ShopLikely ──▶ hunting ──┬─ badges shown        ─▶ Found    (no new hunt for this hint change)
///  (a new hint change, no badges up)              ├─ hint left the offers ─▶ HintLeft
///                                                 └─ Budget passed       ─▶ RanOut   (normal rate until the next change)
/// </code>
/// A hunt belongs to one hint change (its time stamp): whatever happens afterwards (the overlay regains the game,
/// badges come and go, snapshots repeat the same hint), it never starts again until the hint changes again. So a hint
/// that is already an offer hint at start-up hunts once, for at most the budget, and a stuck hint costs nothing more.
/// </summary>
public sealed class OfferHunt
{
    /// <summary>
    /// Longest hunt, counted from the hint change. 8 s: in 35 minutes of real play (39 offers with a hint) the hint came
    /// 1.06 to 6.95 s before the frame that first showed the screen for 27 of them (69 %: 21 of 27 rewards, 6 of 12
    /// shops), and 9.6 to 29.7 s for the other 12, spread evenly over that range (most likely screens opened by a later
    /// click: the merchant, a reward taken last). 7 to 9.5 s cover the same 27; 8 keeps a margin above the slowest of
    /// them. Covering the tail would buy about one more offer per 2.5 s of budget, paid by every hunt that runs out.
    /// </summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(8);

    private DateTime _change; // the hint change the current (or last) hunt belongs to; default = none seen yet
    private bool _found;

    /// <summary>Whether the last <see cref="Update"/> said to hunt.</summary>
    public bool Hunting { get; private set; }

    /// <summary>Why the last hunt ended; <see cref="HuntEnd.None"/> until one did.</summary>
    public HuntEnd LastEnd { get; private set; }

    /// <summary>
    /// Whether to poll at the hunt rate now.
    /// </summary>
    /// <param name="enabled">The hunt rate setting is above 0.</param>
    /// <param name="hint">The log's current screen hint.</param>
    /// <param name="hintChangedAt">When <paramref name="hint"/> last changed (UTC); default = it never did.</param>
    /// <param name="offerShown">Badges for a reward / shop screen are up.</param>
    /// <param name="now">UTC now.</param>
    /// <param name="recheckIn">While hunting: when the budget runs out, so the caller drops the rate even if no
    /// recognition result arrives to ask again. Otherwise null.</param>
    public bool Update(bool enabled, ScreenHint hint, DateTime hintChangedAt, bool offerShown, DateTime now, out TimeSpan? recheckIn)
    {
        recheckIn = null;
        if (hintChangedAt != _change)
        {
            _change = hintChangedAt; // a new hint change: a new chance to hunt
            _found = false;
        }
        // Badges up (this screen, or one already shown when the hint came): nothing left to look for until the hint
        // changes again, even if they go away (picked card, a held frame) while the hint stays.
        if (offerShown) _found = true;

        var age = now - _change;
        var hunting = enabled && IsOfferHint(hint) && _change != default && !_found
                      && age >= TimeSpan.Zero && age < Budget; // a clock set back never makes a hunt longer
        if (hunting) recheckIn = Budget - age;
        if (Hunting && !hunting)
            LastEnd = !enabled ? HuntEnd.Off : _found ? HuntEnd.Found : !IsOfferHint(hint) ? HuntEnd.HintLeft : HuntEnd.RanOut;
        Hunting = hunting;
        return hunting;
    }

    private static bool IsOfferHint(ScreenHint h) => h is ScreenHint.RewardLikely or ScreenHint.ShopLikely;
}

/// <summary>Why a hunt ended (<see cref="OfferHunt.LastEnd"/>).</summary>
public enum HuntEnd
{
    None,
    /// <summary>Badges for the screen are up.</summary>
    Found,
    /// <summary>The hint changed to something else (combat, map, rest...) before a screen was found.</summary>
    HintLeft,
    /// <summary>The budget passed without a screen.</summary>
    RanOut,
    /// <summary>The hunt rate was set to 0 meanwhile.</summary>
    Off,
}
