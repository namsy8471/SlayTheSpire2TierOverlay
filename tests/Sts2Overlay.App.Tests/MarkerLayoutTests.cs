using Sts2Overlay.App.Views;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The on-game recommendation marks laid out by <see cref="BadgeLayout"/>: reason chips under the cards, the
/// "추천!"/"구매!" ribbon, the skip pill and the card-removal marker. Geometry comes from the game's own layout (reward
/// row and merchant slots in 1920x1080 reference units, x2 for 4K), so the checks match what the player sees.
/// </summary>
public class MarkerLayoutTests
{
    private static readonly PxBox Layer4K = new(0, 0, 3840, 2160);
    private static readonly PxBox LayerFhd = new(0, 0, 1920, 1080);

    /// <summary>A card of the given node scale centred at a reference point, in pixels of a layer with UI scale <paramref name="ui"/>.</summary>
    private static PxBox Card(double cx, double cy, double nodeScale, double ui)
        => new((int)((cx - 150 * nodeScale) * ui), (int)((cy - 211 * nodeScale) * ui), (int)(300 * nodeScale * ui), (int)(422 * nodeScale * ui));

    /// <summary>The 3-card reward row (x 610/960/1310, y 616, scale 0.8); <paramref name="hovered"/> is at 1.0.</summary>
    private static PxBox[] RewardRow(double ui, int hovered = -1)
        => [.. new[] { 610.0, 960, 1310 }.Select((x, i) => Card(x, 616, i == hovered ? 1.0 : 0.8, ui))];

    /// <summary>The merchant's 5 + 2 cards (scale 0.65) and their price labels.</summary>
    private static (PxBox[] Cards, PxBox?[] Prices) Shop(double ui)
    {
        (double X, double Y)[] centres = [(437, 383), (700, 383), (961, 383), (1220, 383), (1486, 380), (502, 756), (776, 756)];
        var cards = centres.Select(c => Card(c.X, c.Y, 0.65, ui)).ToArray();
        var prices = centres.Select(c => (PxBox?)new PxBox((int)((c.X - 148 * 0.65) * ui), (int)((c.Y + 220 * 0.65) * ui),
            (int)(297 * 0.65 * ui), (int)(58 * 0.65 * ui))).ToArray();
        return (cards, prices);
    }

    /// <summary>The card-removal coin (reference centre 1488, 767, 170 wide).</summary>
    private static PxBox Coin(double ui) => new((int)(1403 * ui), (int)(682 * ui), (int)(170 * ui), (int)(170 * ui));

    private static CardMarks[] Marks(int n, int chips, int ribbon = -1)
        => [.. Enumerable.Range(0, n).Select(i => new CardMarks(chips, i == ribbon))];

    /// <summary>Everything drawn for a frame, with a name for assertion messages.</summary>
    private static List<(string Name, DipRect Box)> Drawn(BadgeFrame f)
    {
        var all = new List<(string, DipRect)>();
        for (var i = 0; i < f.Badges.Count; i++) all.Add(($"badge {i}", f.Badges[i].Box));
        for (var i = 0; i < f.Chips.Count; i++) if (f.Chips[i] is { } c) all.Add(($"chips {i}", c.Box));
        for (var i = 0; i < f.Ribbons.Count; i++) if (f.Ribbons[i] is { } r) all.Add(($"ribbon {i}", r.Box));
        if (f.Skip is { } s) all.Add(("skip", s.Box));
        if (f.Removal is { } m) all.Add(("removal", m.Box));
        return all;
    }

    [Theory]
    [InlineData(2.0, 1.5, -1)] // 4K at 150 %
    [InlineData(2.0, 1.5, 0)]  // 4K, left card hovered (bigger, lower)
    [InlineData(2.0, 1.5, 2)]
    [InlineData(1.0, 1.0, -1)] // FHD at 100 %
    [InlineData(1.0, 1.0, 1)]
    [InlineData(2.0, 2.0, 1)]  // 4K at 200 %
    public void Reward_chips_sit_under_every_card_without_touching_anything_else(double ui, double dpi, int hovered)
    {
        PxBox[] cards = RewardRow(ui, hovered);
        PxBox layer = ui == 2 ? Layer4K : LayerFhd;
        BadgeFrame f = BadgeLayout.Compute(cards, [], layer, dpi, 1, skip: true, outlines: false, Marks(3, chips: 2, ribbon: 1));

        Assert.Equal(3, f.Chips.Count);
        var drawn = Drawn(f);
        for (var i = 0; i < 3; i++)
        {
            DipRect card = BadgeLayout.ToLayerDips(cards[i], layer, dpi);
            PlacedChips chips = Assert.IsType<PlacedChips>(f.Chips[i]);
            Assert.Equal(2, chips.Count);
            // Below its own card, never wider than it, centred on it.
            Assert.True(chips.Box.Y >= card.Bottom, $"chips {i} are not below the card");
            Assert.True(chips.Box.Width <= card.Width);
            Assert.Equal(card.X + card.Width / 2, chips.Box.X + chips.Box.Width / 2, 6);
            // Above the game's "넘기기" button (reference y 884), so it stays clickable and readable.
            Assert.True(chips.Box.Bottom <= BadgeLayout.RefYToLayerDips(884, layer, dpi), $"chips {i} reach the skip button");
            for (var j = 0; j < 3; j++)
            {
                if (j != i) Assert.False(chips.Box.Intersects(BadgeLayout.ToLayerDips(cards[j], layer, dpi)), $"chips {i} cover card {j}");
            }
        }
        AssertNoOverlapAndOnScreen(drawn, layer.Width / dpi, layer.Height / dpi);
    }

    [Fact]
    public void Chips_grow_with_the_card_and_stack_one_or_two_rows()
    {
        BadgeFrame one = BadgeLayout.Compute(RewardRow(2), [], Layer4K, 1.5, 1, false, false, Marks(3, chips: 1));
        BadgeFrame two = BadgeLayout.Compute(RewardRow(2), [], Layer4K, 1.5, 1, false, false, Marks(3, chips: 2));
        PlacedChips a = one.Chips[0]!.Value, b = two.Chips[0]!.Value;
        Assert.Equal(BadgeLayout.ChipHeight * a.Scale, a.Box.Height, 6);
        Assert.Equal((2 * BadgeLayout.ChipHeight + BadgeLayout.ChipGap) * b.Scale, b.Box.Height, 6);
        Assert.Equal(one.Badges[0].Scale, a.Scale); // same scale as the card's badge
    }

    [Fact]
    public void Cards_without_marks_get_no_chips_and_no_ribbon()
    {
        BadgeFrame f = BadgeLayout.Compute(RewardRow(2), [], Layer4K, 1.5, 1, false, false);
        Assert.All(f.Chips, c => Assert.Null(c));
        Assert.All(f.Ribbons, r => Assert.Null(r));
        Assert.Null(f.Removal);
        // More than two chips asked for: still two rows at most.
        BadgeFrame many = BadgeLayout.Compute(RewardRow(2), [], Layer4K, 1.5, 1, false, false, Marks(3, chips: 5));
        Assert.Equal(2, many.Chips[0]!.Value.Count);
    }

    [Fact]
    public void Chips_move_inside_the_card_when_there_is_no_room_below()
    {
        // A card touching the bottom of the screen: below it is off screen.
        PxBox[] cards = [Card(960, 1080 - 211 * 0.8, 0.8, 1)];
        BadgeFrame f = BadgeLayout.Compute(cards, [], LayerFhd, 1.0, 1, false, false, Marks(1, chips: 2));
        DipRect card = BadgeLayout.ToLayerDips(cards[0], LayerFhd, 1.0);
        DipRect chips = f.Chips[0]!.Value.Box;
        Assert.True(chips.Y >= card.Y && chips.Bottom <= card.Bottom, "chips not inside the card");
        Assert.True(chips.InsideArea(1920, 1080));
    }

    [Fact]
    public void Two_row_card_grid_never_overlaps_chips_badges_or_cards()
    {
        // An event's "pick 1 of 5": 3 cards on top, 2 below, rows close together. The top row's chips would run into
        // the bottom row's badges, so they go inside their own cards.
        PxBox[] cards = [Card(610, 330, 0.8, 2), Card(960, 330, 0.8, 2), Card(1310, 330, 0.8, 2), Card(785, 760, 0.8, 2), Card(1135, 760, 0.8, 2)];
        BadgeFrame f = BadgeLayout.Compute(cards, [], Layer4K, 1.5, 1, false, false, Marks(5, chips: 2, ribbon: 3));
        var drawn = Drawn(f);
        AssertNoOverlapAndOnScreen(drawn, 3840 / 1.5, 2160 / 1.5);
        for (var i = 0; i < 5; i++)
            for (var j = 0; j < 5; j++)
                if (i != j) Assert.False(f.Chips[i]!.Value.Box.Intersects(BadgeLayout.ToLayerDips(cards[j], Layer4K, 1.5)), $"chips {i} cover card {j}");
    }

    [Theory]
    [InlineData(2.0, 1.5)]
    [InlineData(1.0, 1.0)]
    public void Recommend_ribbon_pins_to_the_right_end_of_the_badge_and_sticks_out_above(double ui, double dpi)
    {
        PxBox layer = ui == 2 ? Layer4K : LayerFhd;
        BadgeFrame f = BadgeLayout.Compute(RewardRow(ui), [], layer, dpi, 1, false, false, Marks(3, chips: 0, ribbon: 1));
        Assert.Null(f.Ribbons[0]);
        Assert.Null(f.Ribbons[2]);
        PlacedBadge ribbon = Assert.IsType<PlacedBadge>(f.Ribbons[1]);
        DipRect badge = f.Badges[1].Box;
        Assert.Equal(f.Badges[1].Scale, ribbon.Scale);
        Assert.True(ribbon.Box.Y < badge.Y && ribbon.Box.Bottom > badge.Y, "ribbon does not straddle the badge's top edge");
        Assert.True(ribbon.Box.X > badge.X + badge.Width / 2, "ribbon is not on the right end");
        // The card itself (name banner, art) is not covered: the ribbon stays above the card's top.
        Assert.True(ribbon.Box.Bottom <= BadgeLayout.ToLayerDips(RewardRow(ui)[1], layer, dpi).Y);
    }

    [Fact]
    public void Ribbon_falls_back_inside_the_badge_at_the_top_edge()
    {
        // The card is so high its badge sits at the very top: a tag sticking out above would leave the screen.
        var scale = BadgeLayout.ScaleFor(240);
        PxBox[] cards = [new(840, (int)(4 + (40 + 6) * scale), 240, 338)];
        BadgeFrame f = BadgeLayout.Compute(cards, [], LayerFhd, 1.0, 1, false, false, Marks(1, 0, ribbon: 0));
        DipRect badge = f.Badges[0].Box, ribbon = f.Ribbons[0]!.Value.Box;
        Assert.True(ribbon.Y >= badge.Y && ribbon.Bottom <= badge.Bottom, "ribbon not inside the badge");
        Assert.True(ribbon.InsideArea(1920, 1080));
    }

    [Theory]
    [InlineData(2.0, 1.5)]
    [InlineData(1.0, 1.0)]
    public void Shop_buy_ribbons_never_cover_a_price_label_or_another_card(double ui, double dpi)
    {
        (PxBox[] cards, PxBox?[] prices) = Shop(ui);
        PxBox layer = ui == 2 ? Layer4K : LayerFhd;
        // Every card recommended: the worst case for the bottom row's ribbons (under the top row's prices).
        CardMarks[] marks = [.. cards.Select(_ => new CardMarks(0, true))];
        BadgeFrame f = BadgeLayout.Compute(cards, prices, layer, dpi, 1, false, outlines: true, marks, Coin(ui));

        DipRect[] priceBoxes = [.. prices.Select(p => BadgeLayout.ToLayerDips(p!.Value, layer, dpi))];
        for (var i = 0; i < cards.Length; i++)
        {
            DipRect ribbon = f.Ribbons[i]!.Value.Box;
            Assert.DoesNotContain(priceBoxes, p => p.Intersects(ribbon));
            for (var j = 0; j < cards.Length; j++)
                if (j != i) Assert.False(ribbon.Intersects(BadgeLayout.ToLayerDips(cards[j], layer, dpi)), $"ribbon {i} covers card {j}");
        }
        AssertNoOverlapAndOnScreen(Drawn(f), layer.Width / dpi, layer.Height / dpi);
    }

    [Theory]
    [InlineData(2.0, 1.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 2.0)]
    public void Removal_marker_sits_above_the_coin_clear_of_the_cards(double ui, double dpi)
    {
        (PxBox[] cards, PxBox?[] prices) = Shop(ui);
        PxBox layer = ui == 2 ? Layer4K : LayerFhd;
        BadgeFrame f = BadgeLayout.Compute(cards, prices, layer, dpi, 1, false, false, Marks(7, 0, ribbon: 4), Coin(ui));

        PlacedBadge m = Assert.IsType<PlacedBadge>(f.Removal);
        DipRect coin = BadgeLayout.ToLayerDips(Coin(ui), layer, dpi);
        // Above the coin with room for the ~20 % hover growth (the coin grows about its centre).
        Assert.True(m.Box.Bottom <= coin.Y - 0.1 * coin.Height, "marker too close to the coin");
        Assert.Equal(coin.X + coin.Width / 2, m.Box.X + m.Box.Width / 2, 6);
        // Same size relative to the game at FHD and 4K: scale 1 for a 170-DIP coin.
        Assert.Equal(coin.Width / BadgeLayout.RemovalCoinDesignWidth, m.Scale, 6);
        foreach (PxBox c in cards) Assert.False(m.Box.Intersects(BadgeLayout.ToLayerDips(c, layer, dpi)));
        foreach (PxBox? p in prices) Assert.False(m.Box.Intersects(BadgeLayout.ToLayerDips(p!.Value, layer, dpi)));
        AssertNoOverlapAndOnScreen(Drawn(f), layer.Width / dpi, layer.Height / dpi);
    }

    [Fact]
    public void Removal_marker_goes_below_a_coin_at_the_top_edge()
    {
        var coin = new PxBox(800, 0, 170, 170);
        BadgeFrame f = BadgeLayout.Compute([], [], LayerFhd, 1.0, 1, false, false, removal: coin);
        DipRect m = f.Removal!.Value.Box;
        Assert.True(m.Y >= coin.Bottom, "marker not below the coin");
        Assert.True(m.InsideArea(1920, 1080));
    }

    [Fact]
    public void Ui_scale_multiplier_grows_the_marks_too()
    {
        BadgeFrame auto = BadgeLayout.Compute(RewardRow(2), [], Layer4K, 1.5, 1, true, false, Marks(3, 2, 1), Coin(2));
        BadgeFrame big = BadgeLayout.Compute(RewardRow(2), [], Layer4K, 1.5, 1.25, true, false, Marks(3, 2, 1), Coin(2));
        Assert.Equal(auto.Ribbons[1]!.Value.Box.Width * 1.25, big.Ribbons[1]!.Value.Box.Width, 6);
        Assert.Equal(auto.Chips[0]!.Value.Box.Height * 1.25, big.Chips[0]!.Value.Box.Height, 6);
        Assert.Equal(auto.Removal!.Value.Box.Width * 1.25, big.Removal!.Value.Box.Width, 6);
        Assert.Equal(auto.Skip!.Value.Box.Width * 1.25, big.Skip!.Value.Box.Width, 6);
    }

    private static void AssertNoOverlapAndOnScreen(List<(string Name, DipRect Box)> drawn, double width, double height)
    {
        foreach (var (name, box) in drawn) Assert.True(box.InsideArea(width, height), $"{name} leaves the screen: {box}");
        for (var i = 0; i < drawn.Count; i++)
            for (var j = i + 1; j < drawn.Count; j++)
            {
                var (a, b) = (drawn[i], drawn[j]);
                // A ribbon is pinned onto its own badge on purpose.
                if (a.Name.StartsWith("badge", StringComparison.Ordinal) && b.Name == "ribbon" + a.Name[5..]) continue;
                Assert.False(a.Box.Intersects(b.Box), $"{a.Name} overlaps {b.Name}");
            }
    }
}
