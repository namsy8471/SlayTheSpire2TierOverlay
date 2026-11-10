namespace Sts2Overlay.Vision.Tests;

public class TrackerAndDatabaseTests
{
    private static readonly DateTime T0 = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

    private static RecognizedCard Card(int slot, string id, bool upgraded = false, bool upgradeKnown = true, float score = 0.95f) =>
        new(slot, CardSlotKind.Reward, id, upgraded, score, 0.4f, true, null, upgraded ? 0.08f : 0f, upgradeKnown, 0.8f,
            default, default, null, null, []);

    private static ScreenRecognition Frame(ScreenKind kind, int slotCount, params RecognizedCard[] accepted)
    {
        var slots = Enumerable.Range(0, slotCount)
            .Select(i => accepted.FirstOrDefault(c => c.Slot == i) ?? Card(i, "?", score: 0) with { CardId = null, Accepted = false })
            .ToList();
        return new ScreenRecognition(kind, accepted, slots, new ScreenGate(kind == ScreenKind.RewardCardSelect, kind == ScreenKind.Shop, ""), 1, "");
    }

    [Fact]
    public void The_inspect_view_drops_the_marks_at_once_instead_of_holding_them()
    {
        var tr = new RecognitionTracker();
        tr.Update(Frame(ScreenKind.CardGrid, 2, Card(0, "A"), Card(1, "B")), T0);
        // A plain "Other" frame (a big tooltip) is held for a moment ...
        ScreenRecognition other = Frame(ScreenKind.Other, 0);
        Assert.Equal(ScreenKind.CardGrid, tr.Update(other, T0.AddSeconds(0.3)).Kind);
        // ... the right-click inspect view is not: its dimmed cards still match, but it is another screen.
        ScreenRecognition inspect = other with { Gate = new ScreenGate(false, false, "") { InspectLikely = true } };
        Assert.Equal(ScreenKind.Other, tr.Update(inspect, T0.AddSeconds(0.6)).Kind);
        Assert.Empty(tr.Current.Cards);
    }

    [Fact]
    public void Merges_slots_seen_in_different_frames()
    {
        var tr = new RecognitionTracker();
        tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(1, "B")), T0);
        // Next poll: a tooltip covers slot 1 but slot 2 is visible now.
        TrackedScreen s = tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(2, "C")), T0.AddSeconds(0.5));
        Assert.Equal(ScreenKind.RewardCardSelect, s.Kind);
        Assert.Equal(["A", "B", "C"], s.Cards.Select(c => c.CardId));
    }

    [Fact]
    public void A_different_card_in_a_known_slot_starts_a_new_screen()
    {
        var tr = new RecognitionTracker();
        tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(1, "B"), Card(2, "C")), T0);
        int gen = tr.Generation;
        TrackedScreen s = tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "X"), Card(1, "B")), T0.AddSeconds(1));
        Assert.Equal(gen + 1, tr.Generation);
        Assert.Equal(["X", "B"], s.Cards.Select(c => c.CardId));
    }

    [Fact]
    public void Short_other_blips_are_held_then_cleared()
    {
        // Slow polling: the hold also ends HoldTime after the FIRST missed frame.
        var tr = new RecognitionTracker { HoldTime = TimeSpan.FromSeconds(1.5) };
        tr.Update(Frame(ScreenKind.Shop, 7, Card(0, "A")), T0);
        Assert.Equal(ScreenKind.Shop, tr.Update(Frame(ScreenKind.Other, 0), T0.AddSeconds(1)).Kind);
        Assert.Same(TrackedScreen.None, tr.Update(Frame(ScreenKind.Other, 0), T0.AddSeconds(2.6)));
    }

    [Theory]
    [InlineData(3.0)] // reward hint: 3 Hz
    [InlineData(1.5)] // idle watch
    public void A_closed_screen_clears_on_the_third_missed_frame(double hz)
    {
        // The reward closes and the next screen is perfectly still: two held frames, the third clears
        // (0.67 s after the first miss at 3 Hz, 1.33 s at 1.5 Hz).
        var tr = new RecognitionTracker();
        TimeSpan dt = TimeSpan.FromSeconds(1 / hz);
        tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(1, "B"), Card(2, "C")), T0);
        TrackedScreen s1 = tr.Update(Frame(ScreenKind.Other, 0), T0 + dt);
        TrackedScreen s2 = tr.Update(Frame(ScreenKind.Other, 0), T0 + 2 * dt);
        Assert.Equal(ScreenKind.RewardCardSelect, s1.Kind);
        Assert.Equal(ScreenKind.RewardCardSelect, s2.Kind);
        Assert.True(s1.Held && s2.Held);
        Assert.Same(TrackedScreen.None, tr.Update(Frame(ScreenKind.Other, 0), T0 + 3 * dt));
    }

    [Fact]
    public void One_or_two_missed_frames_in_an_open_screen_do_not_blink()
    {
        var tr = new RecognitionTracker();
        tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(1, "B"), Card(2, "C")), T0);
        int gen = tr.Generation;
        for (int i = 1; i <= 2; i++)
            Assert.Equal(["A", "B", "C"], tr.Update(Frame(ScreenKind.Other, 0), T0.AddSeconds(i / 3.0)).Cards.Select(c => c.CardId));
        TrackedScreen back = tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(1, "B")), T0.AddSeconds(1));
        Assert.False(back.Held);
        Assert.Equal(gen, back.Generation); // the same screen, not a new one
        Assert.Equal(["A", "B", "C"], back.Cards.Select(c => c.CardId));
        // The miss count starts over: two more blips are held again.
        tr.Update(Frame(ScreenKind.Other, 0), T0.AddSeconds(1.33));
        Assert.Equal(ScreenKind.RewardCardSelect, tr.Update(Frame(ScreenKind.Other, 0), T0.AddSeconds(1.67)).Kind);
    }

    [Fact]
    public void The_hold_counts_from_the_first_missed_frame_not_from_the_last_sighting()
    {
        // The capture loop skips unchanged pictures, so a reward left still for 10 s was last recognised 10 s ago.
        // Hovering a card then gives one odd frame: it must be held, not taken for a closed screen.
        var tr = new RecognitionTracker();
        tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(1, "B"), Card(2, "C")), T0);
        Assert.Equal(ScreenKind.RewardCardSelect, tr.Update(Frame(ScreenKind.Other, 0), T0.AddSeconds(10)).Kind);
        Assert.Equal(ScreenKind.RewardCardSelect, tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A")), T0.AddSeconds(10.33)).Kind);
    }

    [Fact]
    public void Tracked_screen_reports_its_slots_and_generation()
    {
        var tr = new RecognitionTracker();
        TrackedScreen s = tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(2, "C")), T0);
        Assert.Equal(3, s.SlotCount);
        Assert.Equal(2, s.Cards.Count);
        Assert.Equal(tr.Generation, s.Generation);
        tr.Reset();
        Assert.Same(TrackedScreen.None, tr.Current);
        Assert.NotEqual(s.Generation, tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(2, "C")), T0.AddSeconds(1)).Generation);
    }

    private static RecognizedCard ShopCard(int slot, string id) => Card(slot, id) with { SlotKind = CardSlotKind.ShopCharacter };

    /// <summary>A shop frame: the given cards read, <paramref name="empty"/> slots bare rug, the rest unread (covered).</summary>
    private static ScreenRecognition ShopFrame(int[] empty, params RecognizedCard[] accepted)
    {
        ScreenRecognition f = Frame(ScreenKind.Shop, 7, accepted);
        return f with { Slots = f.Slots.Select(s => empty.Contains(s.Slot) ? s with { SlotEmpty = true } : s).ToList() };
    }

    [Fact]
    public void A_bought_shop_card_is_dropped_after_two_empty_frames()
    {
        var tr = new RecognitionTracker();
        tr.Update(ShopFrame([], ShopCard(0, "A"), ShopCard(1, "B"), ShopCard(2, "C")), T0);
        // Bought B: its slot shows bare rug. One frame is not enough ...
        Assert.Equal(["A", "B", "C"], tr.Update(ShopFrame([1], ShopCard(0, "A"), ShopCard(2, "C")), T0.AddSeconds(0.33)).Cards.Select(c => c.CardId));
        // ... the second in a row drops it; the rest of the shop stays the same screen.
        int gen = tr.Generation;
        TrackedScreen s = tr.Update(ShopFrame([1], ShopCard(0, "A"), ShopCard(2, "C")), T0.AddSeconds(0.67));
        Assert.Equal(["A", "C"], s.Cards.Select(c => c.CardId));
        Assert.Equal(gen, tr.Generation);
    }

    [Fact]
    public void A_covered_shop_card_is_kept_and_an_interrupted_empty_streak_starts_over()
    {
        var tr = new RecognitionTracker();
        tr.Update(ShopFrame([], ShopCard(0, "A"), ShopCard(1, "B")), T0);
        // The merchant's hand covers B for a long time: unread but not empty, so B stays.
        for (int i = 1; i <= 10; i++)
            Assert.Contains("B", tr.Update(ShopFrame([], ShopCard(0, "A")), T0.AddSeconds(i / 3.0)).Cards.Select(c => c.CardId));
        // Empty, covered, empty: never two in a row.
        tr.Update(ShopFrame([1], ShopCard(0, "A")), T0.AddSeconds(4));
        tr.Update(ShopFrame([], ShopCard(0, "A")), T0.AddSeconds(4.33));
        Assert.Contains("B", tr.Update(ShopFrame([1], ShopCard(0, "A")), T0.AddSeconds(4.67)).Cards.Select(c => c.CardId));
        // Seen again: it comes back as it was.
        Assert.Equal(["A", "B"], tr.Update(ShopFrame([], ShopCard(0, "A"), ShopCard(1, "B")), T0.AddSeconds(5)).Cards.Select(c => c.CardId));
    }

    [Fact]
    public void Screen_kind_change_resets()
    {
        var tr = new RecognitionTracker();
        tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A"), Card(1, "B")), T0);
        TrackedScreen s = tr.Update(Frame(ScreenKind.Shop, 7, Card(3, "S")), T0.AddSeconds(1));
        Assert.Equal(ScreenKind.Shop, s.Kind);
        Assert.Equal(["S"], s.Cards.Select(c => c.CardId));
    }

    [Theory]
    // (first frame upgraded/known, second frame upgraded/known) -> merged upgraded/known
    [InlineData(false, false, true, true, true, true)]   // title hidden first, then seen green
    [InlineData(false, true, false, false, false, true)] // seen cream first, then hidden: stays "not upgraded", known
    [InlineData(true, true, false, false, true, true)]   // seen green, then hidden: stays upgraded
    [InlineData(false, false, false, false, false, false)]
    public void Upgrade_evidence_prefers_frames_where_the_title_was_visible(bool u1, bool k1, bool u2, bool k2, bool upgraded, bool known)
    {
        var tr = new RecognitionTracker();
        tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A", u1, k1), Card(1, "B")), T0);
        RecognizedCard a = tr.Update(Frame(ScreenKind.RewardCardSelect, 3, Card(0, "A", u2, k2), Card(1, "B")), T0.AddSeconds(0.5)).Cards[0];
        Assert.Equal((upgraded, known), (a.Upgraded, a.UpgradeKnown));
    }

    [Fact]
    public void Shop_price_survives_a_frame_where_the_label_is_covered_and_newer_readings_win()
    {
        static RecognizedCard Priced(int? price, PriceTint tint) => Card(0, "A") with
        {
            SlotKind = CardSlotKind.ShopCharacter,
            Price = price,
            PriceReading = new PriceReading(price, price?.ToString() ?? "", tint, 0.9f, 0.3f, price != null, default),
        };
        var tr = new RecognitionTracker();
        tr.Update(Frame(ScreenKind.Shop, 7, Priced(75, PriceTint.Cream)), T0);
        // the merchant's hand covers the label: no price this frame
        RecognizedCard a = tr.Update(Frame(ScreenKind.Shop, 7, Priced(null, PriceTint.Unknown)), T0.AddSeconds(0.5)).Cards[0];
        Assert.Equal(75, a.Price);
        Assert.Equal(PriceTint.Cream, a.PriceReading?.Tint);
        // the player bought something else: the label turns red
        a = tr.Update(Frame(ScreenKind.Shop, 7, Priced(75, PriceTint.Red)), T0.AddSeconds(1)).Cards[0];
        Assert.True(a.Unaffordable);
    }

    [Fact]
    public void TopK_keeps_one_entry_per_card_and_respects_the_filter()
    {
        // C0 has two art variants (seed 100 and a near copy); C1 is different.
        CardArtDatabase db = SyntheticDb.Create(("CARD.C0", SyntheticDb.Ironclad, 100), ("CARD.C0", SyntheticDb.Ironclad, 100),
            ("CARD.C1", SyntheticDb.Silent, 101), ("CARD.C2", SyntheticDb.Ironclad, 102));
        float[] q = db.FullVector(0).ToArray();
        ArtMatch[] top = db.TopK(q, 3);
        Assert.Equal(3, top.Length);
        Assert.Equal("CARD.C0", top[0].CardId);
        Assert.Equal(1f, top[0].Score, 4);
        Assert.Equal(3, top.Select(m => m.CardId).Distinct().Count());
        Assert.DoesNotContain(db.TopK(q, 3, e => e.Pool == SyntheticDb.Silent), m => m.CardId != "CARD.C1");
    }

    [Fact]
    public void Cache_file_round_trips_and_rejects_garbage()
    {
        CardArtDatabase db = SyntheticDb.Create(("CARD.A", SyntheticDb.Ironclad, 1), ("CARD.B", SyntheticDb.Silent, 1), ("CARD.C", SyntheticDb.Ironclad, 2));
        string dir = Path.Combine(Path.GetTempPath(), "sts2-vision-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "art.bin");
            db.Save(path);
            CardArtDatabase? back = CardArtDatabase.TryLoad(path);
            Assert.NotNull(back);
            Assert.Equal(db.SourceFingerprint, back.SourceFingerprint);
            Assert.Equal(db.Entries, back.Entries);
            Assert.Equal(db.Twins, back.Twins);
            Assert.True(db.FullVector(2).SequenceEqual(back.FullVector(2)));
            Assert.True(db.CoarseVector(1).SequenceEqual(back.CoarseVector(1)));
            Assert.Equal(["CARD.B"], back.TwinsOf("CARD.A"));

            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
            Assert.Null(CardArtDatabase.TryLoad(path));
            Assert.Null(CardArtDatabase.TryLoad(Path.Combine(dir, "missing.bin")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
