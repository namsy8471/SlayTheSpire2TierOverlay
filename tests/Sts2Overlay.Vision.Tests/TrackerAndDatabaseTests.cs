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
        var tr = new RecognitionTracker { HoldTime = TimeSpan.FromSeconds(1.5) };
        tr.Update(Frame(ScreenKind.Shop, 7, Card(0, "A")), T0);
        Assert.Equal(ScreenKind.Shop, tr.Update(Frame(ScreenKind.Other, 0), T0.AddSeconds(1)).Kind);
        Assert.Same(TrackedScreen.None, tr.Update(Frame(ScreenKind.Other, 0), T0.AddSeconds(2)));
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
