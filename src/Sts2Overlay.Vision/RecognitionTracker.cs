namespace Sts2Overlay.Vision;

/// <summary>What the overlay should currently show, merged over recent frames.</summary>
public sealed record TrackedScreen(ScreenKind Kind, IReadOnlyList<RecognizedCard> Cards)
{
    public static readonly TrackedScreen None = new(ScreenKind.Other, []);
}

/// <summary>
/// Merges per-frame results while the same screen stays open.
///
/// A single frame often cannot see every card: the hover tooltip of one card covers its neighbour, the
/// merchant's hand points at a card, cards are still fading in. These are all transient, and the app polls
/// 2-4 times per second, so remembering the best accepted result per slot fills the gaps within a second.
/// A different card accepted in an already known slot means a new screen (next reward, rerolled reward):
/// the memory is reset. Short "Other" blips (a frame with a big tooltip) keep the last state for
/// <see cref="HoldTime"/>.
/// Not thread-safe: call from the polling loop only.
/// </summary>
public sealed class RecognitionTracker
{
    private ScreenKind _kind = ScreenKind.Other;
    private int _slotCount;
    private readonly SortedDictionary<int, RecognizedCard> _best = [];
    private DateTime _lastSeen;

    /// <summary>How long to keep showing the last screen when frames stop recognising it.</summary>
    public TimeSpan HoldTime { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Incremented whenever a new screen starts (or the memory is cleared).</summary>
    public int Generation { get; private set; }

    public TrackedScreen Current => _kind == ScreenKind.Other ? TrackedScreen.None : new TrackedScreen(_kind, _best.Values.ToList());

    public TrackedScreen Update(ScreenRecognition frame, DateTime now)
    {
        if (frame.Kind == ScreenKind.Other)
        {
            if (_kind != ScreenKind.Other && now - _lastSeen > HoldTime) Reset(ScreenKind.Other, 0);
            return Current;
        }

        bool contradicts = frame.Cards.Any(c => _best.TryGetValue(c.Slot, out RecognizedCard? old) && old.CardId != c.CardId);
        if (frame.Kind != _kind || frame.Slots.Count != _slotCount || contradicts) Reset(frame.Kind, frame.Slots.Count);

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
        _lastSeen = now;
        return Current;
    }

    public void Reset() => Reset(ScreenKind.Other, 0);

    private void Reset(ScreenKind kind, int slots)
    {
        Generation++;
        _kind = kind;
        _slotCount = slots;
        _best.Clear();
    }
}
