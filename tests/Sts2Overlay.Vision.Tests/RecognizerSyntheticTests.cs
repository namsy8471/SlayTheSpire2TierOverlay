namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// End-to-end runs of <see cref="CardRecognizer"/> on painted screens: synthetic "artworks" are drawn where the
/// game would draw the card portraits, and the database is built from the same textures.
/// </summary>
public class RecognizerSyntheticTests
{
    private static void PaintCard(Canvas c, float cx, float cy, float scale, int seed, bool upgraded = false)
    {
        c.FillRef(CardGeometry.Place(CardGeometry.Card, cx, cy, scale), 55, 45, 35);
        c.DrawImageRef(CardGeometry.Place(CardGeometry.Art, cx, cy, scale), Canvas.Texture(seed));
        // Title banner drawn over the top of the portrait, like the game; text as horizontal strokes.
        RectF title = CardGeometry.Place(CardGeometry.Title, cx, cy, scale);
        c.FillRef(title, 90, 70, 50);
        for (int k = 0; k < 4; k++)
        {
            var stroke = new RectF(title.X + title.Width * 0.15f, title.Y + title.Height * (0.3f + 0.12f * k), title.Width * 0.7f, title.Height * 0.06f);
            if (upgraded) c.FillRef(stroke, 127, 255, 0);
            else c.FillRef(stroke, 255, 246, 226);
        }
    }

    private static Canvas RewardScreen(int w, int h, (int Seed, float Scale, bool Upgraded)[] cards)
    {
        var c = new Canvas(w, h);
        ClassifierAndUpgradeTests.PaintRewardBanner(c);
        SlotLayout[] slots = ScreenLayouts.Reward(cards.Length);
        for (int i = 0; i < cards.Length; i++) PaintCard(c, slots[i].CenterX, slots[i].CenterY, cards[i].Scale, cards[i].Seed, cards[i].Upgraded);
        return c;
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3842, 2160)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1200)]
    public void Reward_screen_three_cards_with_hover_and_upgrade(int w, int h)
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        // Middle card hovered (scale 1.0), right card upgraded.
        Canvas c = RewardScreen(w, h, [(103, 0.8f, false), (107, 1.0f, false), (101, 0.8f, true)]);

        ScreenRecognition r = rec.Recognize(c.Frame);

        Assert.Equal(ScreenKind.RewardCardSelect, r.Kind);
        Assert.Equal(["CARD.C3", "CARD.C7", "CARD.C1"], r.Cards.Select(x => x.CardId));
        Assert.Equal([false, false, true], r.Cards.Select(x => x.Upgraded));
        Assert.All(r.Cards, x => Assert.True(x.Score > 0.95f, x.ToString()));
        Assert.Equal(1.0f, r.Cards[1].NodeScale, 1);

        // Bounds come back in frame pixels: the hovered card is 300x422 reference units at scale 1.
        RefTransform t = RefTransform.For(w, h);
        PixelRect b = r.Cards[1].CardBounds;
        Assert.InRange(b.Width, 300 * t.Scale - 3, 300 * t.Scale + 3);
        Assert.InRange(b.X + b.Width / 2f, t.ToPixelX(960) - 3, t.ToPixelX(960) + 3);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Reward_screen_with_other_card_counts(int n)
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        Canvas c = RewardScreen(1920, 1080, Enumerable.Range(0, n).Select(i => (100 + i, 0.8f, false)).ToArray());
        ScreenRecognition r = rec.Recognize(c.Frame);
        Assert.Equal(ScreenKind.RewardCardSelect, r.Kind);
        Assert.Equal(Enumerable.Range(0, n).Select(i => $"CARD.C{i}"), r.Cards.Select(x => x.CardId));
    }

    [Fact]
    public void Card_shifted_and_scaled_off_the_layout_is_still_found()
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        var c = new Canvas(1920, 1080);
        ClassifierAndUpgradeTests.PaintRewardBanner(c);
        PaintCard(c, 610 + 9, 616 - 7, 0.86f, 104);
        PaintCard(c, 960, 616, 0.8f, 105);
        PaintCard(c, 1310 - 5, 616 + 4, 0.78f, 106);
        ScreenRecognition r = rec.Recognize(c.Frame);
        Assert.Equal(["CARD.C4", "CARD.C5", "CARD.C6"], r.Cards.Select(x => x.CardId));
        Assert.Equal(0.86f, r.Cards[0].NodeScale, 1);
    }

    [Fact]
    public void Unknown_art_on_a_reward_banner_is_not_accepted()
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        Canvas c = RewardScreen(1920, 1080, [(900, 0.8f, false), (901, 0.8f, false), (902, 0.8f, false)]);
        ScreenRecognition r = rec.Recognize(c.Frame);
        Assert.Equal(ScreenKind.Other, r.Kind);
        Assert.Empty(r.Cards);
        Assert.All(r.Slots, s => Assert.False(s.Accepted));
    }

    [Fact]
    public void Empty_dark_slots_are_not_cards()
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        var c = new Canvas(1920, 1080);
        ClassifierAndUpgradeTests.PaintRewardBanner(c);
        ScreenRecognition r = rec.Recognize(c.Frame);
        Assert.Equal(ScreenKind.Other, r.Kind);
    }

    [Theory]
    [InlineData("CHARACTER.SILENT", "CARD.T_SILENT", null)]
    [InlineData("CHARACTER.IRONCLAD", "CARD.T_IRON", null)]
    [InlineData(null, "CARD.T_IRON", "CARD.T_SILENT")]
    public void Identical_art_is_resolved_by_the_character_hint(string? character, string expected, string? ambiguous)
    {
        CardArtDatabase db = SyntheticDb.Create(
            ("CARD.T_IRON", SyntheticDb.Ironclad, 500), ("CARD.T_SILENT", SyntheticDb.Silent, 500),
            ("CARD.C1", SyntheticDb.Ironclad, 101), ("CARD.C2", SyntheticDb.Silent, 102));
        Assert.Single(db.Twins);
        var rec = new CardRecognizer(db);
        Canvas c = RewardScreen(1920, 1080, [(500, 0.8f, false), (101, 0.8f, false), (102, 0.8f, false)]);

        ScreenRecognition r = rec.Recognize(c.Frame, new RecognitionHints(character));

        RecognizedCard twin = r.Cards[0];
        Assert.Equal(expected, twin.CardId);
        Assert.Equal(ambiguous, twin.AmbiguousWith);
        // The twin must not count as the runner-up, or the margin would be 0 and the card rejected.
        Assert.True(twin.Accepted);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3842, 2160)]
    public void Shop_with_seven_cards(int w, int h)
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        var c = new Canvas(w, h);
        ClassifierAndUpgradeTests.PaintRug(c);
        SlotLayout[] slots = ScreenLayouts.Shop();
        for (int i = 0; i < slots.Length; i++)
            PaintCard(c, slots[i].CenterX, slots[i].CenterY, i == 2 ? slots[i].HoverScale : slots[i].SmallScale, 100 + i, upgraded: i == 4);

        ScreenRecognition r = rec.Recognize(c.Frame);

        Assert.Equal(ScreenKind.Shop, r.Kind);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"CARD.C{i}"), r.Cards.Select(x => x.CardId));
        Assert.Equal([4], r.Cards.Where(x => x.Upgraded).Select(x => x.Slot));
        Assert.All(r.Cards, x => Assert.NotNull(x.PriceBounds));
        Assert.Equal([CardSlotKind.ShopColorless, CardSlotKind.ShopColorless], r.Cards.Skip(5).Select(x => x.SlotKind));
    }

    [Fact]
    public void Shop_with_sold_slots_reports_the_rest()
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        var c = new Canvas(1920, 1080);
        ClassifierAndUpgradeTests.PaintRug(c);
        SlotLayout[] slots = ScreenLayouts.Shop();
        foreach (int i in new[] { 0, 3, 6 }) PaintCard(c, slots[i].CenterX, slots[i].CenterY, slots[i].SmallScale, 100 + i);
        ScreenRecognition r = rec.Recognize(c.Frame);
        Assert.Equal(ScreenKind.Shop, r.Kind);
        Assert.Equal([0, 3, 6], r.Cards.Select(x => x.Slot));
        Assert.Equal(7, r.Slots.Count);
    }
}
