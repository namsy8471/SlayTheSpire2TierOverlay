namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// A bought shop card leaves bare rug and no price label (RecognizedCard.SlotEmpty), while a card covered by a tooltip
/// or the merchant's hand is unread but not empty.
///
/// Calibration (all 98 real frames in data/local/test-captures recognised as a shop, 686 card slots): 636 slots
/// identified, 50 not. 11 of those have a resting art box at least 96 % rug and no label: every one is a bought slot
/// (checked by eye on crops). The covered cards score at most 5 % rug. Seven more bought slots sit at 42-89 % rug
/// because the hand, a speech bubble or a tooltip lies over them: not flagged, which is the safe side (the card just
/// stays listed until the slot is seen bare).
/// </summary>
public class ShopEmptySlotTests
{
    private static readonly Lazy<CardRecognizer> Rec = new(() => new CardRecognizer(
        CardArtDatabase.LoadOrBuild(TestPaths.ArtDir, TestPaths.Catalog, TestPaths.DbCache),
        new RecognizerOptions { Catalog = VisionCatalog.Load(TestPaths.Catalog) }));

    private static ScreenRecognition Read(string file)
    {
        ScreenRecognition r = Rec.Value.Recognize(ImageLoader.Load(Path.Combine(TestPaths.CapturesDir, file)));
        Assert.Equal(ScreenKind.Shop, r.Kind);
        Assert.DoesNotContain(r.Slots, s => s.Accepted && s.SlotEmpty);
        return r;
    }

    [LocalDataFact("shop_20261108_232345_842.png", "shop_20261109_003318_642.png")]
    public void A_bought_slot_is_empty()
    {
        // Pillage bought (slot 1); Inferno bought (slot 4) with the merchant's hand resting next to the bare slot.
        foreach ((string file, int slot) in new[] { ("shop_20261108_232345_842.png", 1), ("shop_20261109_003318_642.png", 4) })
        {
            ScreenRecognition r = Read(file);
            Assert.Equal([slot], r.Slots.Where(s => s.SlotEmpty).Select(s => s.Slot));
            Assert.True(r.Slots[slot].RugShare >= 0.95f, $"{file}: rug {r.Slots[slot].RugShare:F2}");
        }
    }

    [LocalDataFact("shop_20261108_212034_423.png", "shop_20261108_202430_409.png")]
    public void A_covered_card_is_not_empty()
    {
        // Slot 1 is under a hover tooltip (no price label read either); slot 3 under the merchant's hand.
        foreach ((string file, int slot) in new[] { ("shop_20261108_212034_423.png", 1), ("shop_20261108_202430_409.png", 3) })
        {
            ScreenRecognition r = Read(file);
            Assert.False(r.Slots[slot].Accepted);
            Assert.DoesNotContain(r.Slots, s => s.SlotEmpty);
            Assert.True(r.Slots[slot].RugShare < 0.1f, $"{file}: rug {r.Slots[slot].RugShare:F2}");
        }
    }
}
