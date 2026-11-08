namespace Sts2Overlay.Vision.Tests;

public class GeometryAndLayoutTests
{
    [Theory]
    // 4K panel that is 2 px wider than 16:9: scale 2, 1 px pillar-box on each side.
    [InlineData(3842, 2160, 2.0, 1.0, 0.0)]
    [InlineData(3840, 2160, 2.0, 0.0, 0.0)]
    [InlineData(1920, 1080, 1.0, 0.0, 0.0)]
    // Windowed capture with a 1 px shorter client area: height limits the scale.
    [InlineData(1920, 1079, 1079.0 / 1080, 0.8889, 0.0)]
    // 16:10: width limits, black bars top and bottom (letterbox).
    [InlineData(1920, 1200, 1.0, 0.0, 60.0)]
    // 21:9 ultrawide: height limits, bars left and right.
    [InlineData(3440, 1440, 4.0 / 3, 440.0, 0.0)]
    public void RefTransform_fits_16x9_and_centres(int w, int h, double scale, double ox, double oy)
    {
        RefTransform t = RefTransform.For(w, h);
        Assert.Equal(scale, t.Scale, 4);
        Assert.Equal(ox, t.OffsetX, 2);
        Assert.Equal(oy, t.OffsetY, 2);
        // The reference canvas centre always lands on the frame centre.
        Assert.Equal(w / 2.0, t.ToPixelX(960), 2);
        Assert.Equal(h / 2.0, t.ToPixelY(540), 2);
    }

    [Fact]
    public void ToRef_inverts_ToPixels()
    {
        RefTransform t = RefTransform.For(3440, 1440);
        var r = new RectF(610, 481.6f, 200, 152);
        RectF back = t.ToRef(t.ToPixels(r));
        Assert.Equal(r.X, back.X, 3);
        Assert.Equal(r.Y, back.Y, 3);
        Assert.Equal(r.Width, back.Width, 3);
        Assert.Equal(r.Height, back.Height, 3);
    }

    [Fact]
    public void ToPixelRect_covers_fractional_box()
    {
        var t = new RefTransform(1, 0, 0);
        Assert.Equal(new PixelRect(10, 20, 6, 3), t.ToPixelRect(new RectF(10.5f, 20.2f, 5.0f, 2.5f)));
    }

    [Theory]
    [InlineData(3, new float[] { 610, 960, 1310 })]
    [InlineData(2, new float[] { 785, 1135 })]
    [InlineData(4, new float[] { 435, 785, 1135, 1485 })]
    public void Reward_layout_matches_game_formula(int n, float[] xs)
    {
        SlotLayout[] slots = ScreenLayouts.Reward(n);
        Assert.Equal(xs, slots.Select(s => s.CenterX));
        Assert.All(slots, s => Assert.Equal(616f, s.CenterY));
        Assert.All(slots, s => Assert.Equal((0.8f, 1.0f), (s.SmallScale, s.HoverScale)));
    }

    [Theory]
    [InlineData(3, new float[] { 620, 960, 1300 })]
    [InlineData(5, new float[] { 280, 620, 960, 1300, 1640 })]
    public void Choose_a_card_layout_matches_game_formula(int n, float[] xs)
    {
        // NChooseACardSelectionScreen: _cardXSpacing 340, CardRow offset_top 60.
        SlotLayout[] slots = ScreenLayouts.Choice(n);
        Assert.Equal(xs, slots.Select(s => s.CenterX));
        Assert.All(slots, s => Assert.Equal((600f, CardSlotKind.Choice), (s.CenterY, s.Kind)));
    }

    [Fact]
    public void Grid_layout_matches_card_grid_scene()
    {
        // card_grid.tscn + NCardGrid: 5 columns from x 375, step 280; one row centred at y 460, else rows at 328.8, 706.4.
        SlotLayout[] one = ScreenLayouts.Grid(twoRows: false);
        Assert.Equal(new float[] { 375, 655, 935, 1215, 1495 }, one.Select(s => s.CenterX));
        Assert.All(one, s => Assert.Equal(460f, s.CenterY));
        SlotLayout[] two = ScreenLayouts.Grid(twoRows: true);
        Assert.Equal(10, two.Length);
        Assert.Equal(328.8f, two[0].CenterY, 3);
        Assert.Equal(706.4f, two[5].CenterY, 3);
        Assert.All(two, s => Assert.Equal(CardSlotKind.Grid, s.Kind));
    }

    [Fact]
    public void Reward_layout_spread_zero_stacks_cards_in_the_middle()
    {
        Assert.All(ScreenLayouts.Reward(3, 0f), s => Assert.Equal(960f, s.CenterX));
        Assert.Equal(785f, ScreenLayouts.Reward(3, 0.5f)[0].CenterX);
    }

    [Fact]
    public void Shop_layout_has_five_character_and_two_colourless_slots()
    {
        SlotLayout[] slots = ScreenLayouts.Shop();
        (float, float)[] expected = [(437, 383), (700, 383), (961, 383), (1220, 383), (1486, 380), (502, 756), (776, 756)];
        Assert.Equal(expected, slots.Select(s => (s.CenterX, s.CenterY)));
        Assert.Equal(5, slots.Count(s => s.Kind == CardSlotKind.ShopCharacter));
        Assert.Equal([5, 6], slots.Where(s => s.Kind == CardSlotKind.ShopColorless).Select(s => s.Index));
        Assert.All(slots, s => Assert.Equal((0.65f, 0.8f), (s.SmallScale, s.HoverScale)));
    }

    [Fact]
    public void Card_art_box_on_a_4K_reward_screen()
    {
        // Left reward card, unhovered: portrait (-125,-168,250,190) * 0.8 around (610,616), then x2 + 1 px pillar-box.
        RefTransform t = RefTransform.For(3842, 2160);
        RectF art = t.ToPixels(CardGeometry.Place(CardGeometry.Art, 610, 616, 0.8f));
        Assert.Equal(1021f, art.X, 2);
        Assert.Equal(963.2f, art.Y, 2);
        Assert.Equal(400f, art.Width, 2);
        Assert.Equal(304f, art.Height, 2);
    }

    [Fact]
    public void Shop_price_sits_below_the_card()
    {
        RectF card = CardGeometry.Place(CardGeometry.Card, 437, 383, 0.65f);
        RectF price = ScreenLayouts.ShopPrice(437, 383, 0.65f);
        Assert.True(price.Y > card.Y + card.Height * 0.9f);
        Assert.Equal(card.CenterX, price.CenterX, 0);
    }
}
