using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;

namespace Sts2Overlay.App.Integration;

/// <summary>
/// Decides which evaluation of a card screen becomes its play-time <see cref="DecisionRecord"/>: one record per screen,
/// the last complete evaluation before the screen closed. Pure (no clock, no files); the controller feeds it every
/// evaluation it shows and every snapshot, and writes what it returns.
///
/// <code>
///  evaluation ─▶ Evaluated: same screen?  yes ─▶ keep it as the screen's record if it is a verdict (not Hold),
///                                                 not computed after the pick, and (shop) not after a purchase
///                                          no  ─▶ the open screen is finished: its record is returned; this one opens
///  snapshot   ─▶ Tick: floor / run changed, or the log says the screen is gone ─▶ the open screen is finished
/// </code>
/// A finished screen is remembered: seeing it again (closing animation, a late re-read, a stale result after the log
/// cleared it) never writes a second record.
/// </summary>
public sealed class DecisionRecorder
{
    private sealed class Open(ScreenKind kind, string run, int floor, string[] ids, int baseline, int items)
    {
        public ScreenKind Kind { get; } = kind;
        public string Run { get; } = run;
        public int Floor { get; } = floor;
        public string[] Ids { get; set; } = ids;
        public int Baseline { get; } = baseline;
        public int MaxItems { get; set; } = items;
        public bool Bought { get; set; }
        public DecisionRecord? Record { get; set; }
        public bool Complete { get; set; }
    }

    private Open? _open;
    private (ScreenKind Kind, string Run, int Floor, string[] Ids)? _finished;

    /// <summary>
    /// A card or shop screen was evaluated and shown.
    /// </summary>
    /// <param name="kind">Screen kind (Reward / Choice / Grid / Shop).</param>
    /// <param name="cards">The cards read, in slot order.</param>
    /// <param name="items">Recognized cards, relics and potions on a shop screen: a drop means something was bought. Rows: the card count.</param>
    /// <param name="verdict">The screen's <see cref="OfferLifecycle.Verdict"/>: a <see cref="PartialVerdict.Hold"/> evaluation showed no recommendation.</param>
    /// <param name="offeredInDeck">Deck cards that share an id with an offered card. It grows when the player takes one, so an
    /// evaluation made after the pick (the deck already has the card) is not the advice that was shown before it.</param>
    /// <param name="record">The record this evaluation would write.</param>
    /// <returns>The finished record of the previous screen when this evaluation belongs to another one, else null.</returns>
    public DecisionRecord? Evaluated(ScreenKind kind, IReadOnlyList<CardRef> cards, int items, PartialVerdict verdict, int offeredInDeck, DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(record);
        var ids = cards.Select(c => c.Id).Order(StringComparer.Ordinal).ToArray();
        DecisionRecord? finished = null;
        if (_open is { } o && !Same(o, kind, record.Run, record.Floor, ids)) finished = Finish();

        if (_open is null)
        {
            // The screen just finished, seen again: nothing new to say about it. Rows are told apart by their cards on any
            // floor (a stale reward result re-evaluated after the floor moved on is still the old screen), shops by floor.
            if (_finished is { } f && f.Kind == kind && f.Run == record.Run && (kind == ScreenKind.Shop ? f.Floor == record.Floor : Related(ids, f.Ids))) return finished;
            _open = new Open(kind, record.Run, record.Floor, ids, offeredInDeck, items);
        }

        var open = _open;
        if (kind == ScreenKind.Shop)
        {
            if (items < open.MaxItems) open.Bought = true; // the bundle after a purchase is advice for what is left
            open.MaxItems = Math.Max(open.MaxItems, items);
            if (open.Bought) return finished;
        }
        else if (ids.Length > open.Ids.Length)
        {
            open.Ids = ids; // a card read late
        }

        if (verdict == PartialVerdict.Hold || offeredInDeck > open.Baseline) return finished;
        if (verdict == PartialVerdict.Complete)
        {
            open.Record = record;
            open.Complete = true;
        }
        else if (!open.Complete)
        {
            open.Record = record; // shown with a card unread: kept only if no complete read follows
        }
        return finished;
    }

    /// <summary>
    /// Every snapshot. Finishes the open screen when the run or the floor changed, or when the log says the card/shop
    /// screen is gone (the tracker's hint left the reward/shop state).
    /// </summary>
    public DecisionRecord? Tick(string? runKey, int floor, bool screenGone)
        => _open is { } o && (screenGone || o.Run != runKey || o.Floor != floor) ? Finish() : null;

    /// <summary>Finishes the open screen (the app is closing).</summary>
    public DecisionRecord? Flush() => _open is null ? null : Finish();

    private DecisionRecord? Finish()
    {
        var o = _open!;
        _open = null;
        _finished = (o.Kind, o.Run, o.Floor, o.Ids);
        return o.Record;
    }

    private static bool Same(Open o, ScreenKind kind, string run, int floor, string[] ids)
        => o.Kind == kind && o.Run == run && o.Floor == floor && (kind == ScreenKind.Shop || Related(ids, o.Ids));

    /// <summary>One sorted id multiset contains the other (a card read late, or one not read yet).</summary>
    private static bool Related(string[] a, string[] b) => a.Length <= b.Length ? IsSubset(a, b) : IsSubset(b, a);

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
