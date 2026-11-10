using Sts2Overlay.Vision.Relics;

namespace Sts2Overlay.Vision;

/// <summary>What the overlay should currently show, merged over recent frames.</summary>
public sealed record TrackedScreen(ScreenKind Kind, IReadOnlyList<RecognizedCard> Cards)
{
    public static readonly TrackedScreen None = new(ScreenKind.Other, []);

    /// <summary>Shop: the merchant relics known to be for sale (identified, by slot).</summary>
    public IReadOnlyList<RecognizedRelic> Relics { get; init; } = [];

    /// <summary>
    /// Slots of the screen's layout, identified or not (a 3-card reward row has 3 even while only 2 cards are read;
    /// the shop has 7, a card grid 5 or 10). 0 for <see cref="None"/>.
    /// </summary>
    public int SlotCount { get; init; }

    /// <summary>Which screen this is: <see cref="RecognitionTracker.Generation"/> when it started.</summary>
    public int Generation { get; init; }

    /// <summary>The latest frame did not show this screen: it is kept for a moment (<see cref="RecognitionTracker.HoldFrames"/>).</summary>
    public bool Held { get; init; }

    /// <summary>
    /// The tracker waits for the next frame to decide something (a held screen, a shop slot that looked empty once):
    /// the capture loop must look at the next frame even when the picture did not change.
    /// </summary>
    public bool Unsettled { get; init; }
}

/// <summary>
/// Merges per-frame results while the same screen stays open.
///
/// A single frame often cannot see every card: the hover tooltip of one card covers its neighbour, the
/// merchant's hand points at a card, cards are still fading in. These are all transient, and the app polls
/// 2-4 times per second, so remembering the best accepted result per slot fills the gaps within a second.
/// A different card accepted in an already known slot means a new screen (next reward, rerolled reward):
/// the memory is reset. Short "Other" blips (a frame with a big tooltip) keep the last state for up to
/// <see cref="HoldFrames"/> frames in a row (and at most <see cref="HoldTime"/> after the first of them); the
/// inspect view (right-click on a card) does not: it is a different screen.
///
/// The hold counts FRAMES, not time since the screen was last seen: the capture loop skips frames whose picture did
/// not change, so the last sighting can be long ago while the screen was open the whole time. Counting misses from
/// the first one makes a real close clear in HoldFrames + 1 polls (about 0.7 s at 3 Hz), and one or two odd frames in
/// the middle of an open screen never do. The caller must keep evaluating frames while <see cref="TrackedScreen.Unsettled"/>.
/// Not thread-safe: call from the polling loop only.
/// </summary>
public sealed class RecognitionTracker
{
    private ScreenKind _kind = ScreenKind.Other;
    private int _slotCount;
    private readonly SortedDictionary<int, RecognizedCard> _best = [];
    private readonly SortedDictionary<int, RecognizedRelic> _relics = [];
    private readonly Dictionary<int, int> _emptySeen = [];
    private int _misses;
    private DateTime _firstMiss;

    /// <summary>How many frames in a row may miss the screen before it is dropped (the next miss drops it).</summary>
    public int HoldFrames { get; init; } = 2;

    /// <summary>Longest hold, counted from the first missed frame (matters only at slow polling rates).</summary>
    public TimeSpan HoldTime { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Shop: frames in a row a card slot must look empty (<see cref="RecognizedCard.SlotEmpty"/>) before its card is
    /// dropped (bought). Two, so one odd frame cannot take a card away.
    /// </summary>
    public int EmptyFrames { get; init; } = 2;

    /// <summary>Incremented whenever a new screen starts (or the memory is cleared).</summary>
    public int Generation { get; private set; }

    public TrackedScreen Current => _kind == ScreenKind.Other ? TrackedScreen.None
        : new TrackedScreen(_kind, _best.Values.ToList())
        {
            Relics = _relics.Values.ToList(),
            SlotCount = _slotCount,
            Generation = Generation,
            Held = _misses > 0,
            Unsettled = _misses > 0 || _emptySeen.Values.Any(n => n < EmptyFrames),
        };

    public TrackedScreen Update(ScreenRecognition frame, DateTime now)
    {
        // The inspect view covers the screen at once: drop the marks now instead of holding them.
        if (frame.Gate.InspectLikely)
        {
            if (_kind != ScreenKind.Other) Reset(ScreenKind.Other, 0);
            return Current;
        }
        if (frame.Kind == ScreenKind.Other)
        {
            if (_kind == ScreenKind.Other) return Current;
            if (_misses++ == 0) _firstMiss = now;
            if (_misses > HoldFrames || now - _firstMiss > HoldTime) Reset(ScreenKind.Other, 0);
            return Current;
        }
        _misses = 0;

        bool contradicts = frame.Cards.Any(c => _best.TryGetValue(c.Slot, out RecognizedCard? old) && old.CardId != c.CardId);
        if (frame.Kind != _kind || frame.Slots.Count != _slotCount || contradicts) Reset(frame.Kind, frame.Slots.Count);

        DropEmptySlots(frame.Slots);
        foreach (RecognizedCard c in frame.Cards)
        {
            if (!_best.TryGetValue(c.Slot, out RecognizedCard? old))
            {
                _best[c.Slot] = c;
                continue;
            }
            // Same card id. Keep the latest position (the card may have moved: hover, layout tween) and the best
            // upgrade evidence: an observation where the title was visible beats one where it was covered.
            bool upgraded = old.UpgradeKnown && !c.UpgradeKnown ? old.Upgraded : c.UpgradeKnown && !old.UpgradeKnown ? c.Upgraded : old.Upgraded || c.Upgraded;
            _best[c.Slot] = c with
            {
                Upgraded = upgraded,
                UpgradeKnown = old.UpgradeKnown || c.UpgradeKnown,
                Score = Math.Max(old.Score, c.Score),
                // Shop price: the merchant's hand or a tooltip can hide the label for a frame, so keep the last
                // clear reading. A newer clear reading wins (the label turns red once the player has spent gold).
                Price = c.Price ?? old.Price,
                PriceReading = c.Price != null ? c.PriceReading : old.PriceReading ?? c.PriceReading,
            };
        }
        MergeRelics(frame.Relics);
        return Current;
    }

    /// <summary>
    /// Shop: a card slot that shows bare rug and no price label for <see cref="EmptyFrames"/> frames in a row lost its
    /// card (bought). A covered slot (tooltip, hand) is not "empty", so its card is kept and comes back as it was.
    /// </summary>
    private void DropEmptySlots(IReadOnlyList<RecognizedCard> slots)
    {
        foreach (RecognizedCard s in slots)
        {
            if (!s.SlotEmpty)
            {
                _emptySeen.Remove(s.Slot);
                continue;
            }
            int n = _emptySeen.GetValueOrDefault(s.Slot) + 1;
            _emptySeen[s.Slot] = n;
            if (n >= EmptyFrames) _best.Remove(s.Slot);
        }
    }

    /// <summary>
    /// Merchant relics per slot: an identified relic replaces whatever the slot held (The Courier restocks a bought
    /// slot with a new relic), an empty slot drops it (bought), an unknown one (tooltip, hand, hovered card) keeps it.
    /// </summary>
    private void MergeRelics(IReadOnlyList<RecognizedRelic> relics)
    {
        foreach (RecognizedRelic r in relics)
            switch (r.State)
            {
                case RelicSlotState.Identified:
                    _relics[r.Slot] = _relics.TryGetValue(r.Slot, out RecognizedRelic? old) && old.RelicId == r.RelicId && r.Price == null
                        ? r with { Price = old.Price, PriceReading = old.PriceReading ?? r.PriceReading } // keep the last clear price
                        : r;
                    break;
                case RelicSlotState.Empty:
                    _relics.Remove(r.Slot);
                    break;
            }
    }

    public void Reset() => Reset(ScreenKind.Other, 0);

    private void Reset(ScreenKind kind, int slots)
    {
        Generation++;
        _kind = kind;
        _slotCount = slots;
        _misses = 0;
        _best.Clear();
        _relics.Clear();
        _emptySeen.Clear();
    }
}
