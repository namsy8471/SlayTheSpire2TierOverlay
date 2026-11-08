using Sts2Overlay.App.Views;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// Badge placement math (BadgeLayout): physical px → layer DIPs, badge scale, above/below/inside placement, the skip
/// pill and the debug outlines. The same code draws VisionBench's offline previews.
/// </summary>
public class BadgeLayoutTests
{
    [Fact]
    public void Screen_pixels_become_layer_dips()
    {
        // Layer (the game's client area) at (100, 50) on a 150 % monitor.
        var r = BadgeLayout.ToLayerDips(new PxBox(400, 350, 300, 450), new PxBox(100, 50, 3840, 2160), 1.5);
        Assert.Equal(new DipRect(200, 200, 200, 300), r);
    }

    [Theory]
    [InlineData(170 / 0.72, 1.0)]
    [InlineData(10, 0.5)]
    [InlineData(5000, 3.0)]
    public void Badge_scale_follows_the_card_width_within_limits(double cardWidth, double expected)
        => Assert.Equal(expected, BadgeLayout.ScaleFor(cardWidth), 6);

    [Fact]
    public void Badge_goes_centred_above_the_card()
    {
        var (left, top) = BadgeLayout.Place(new DipRect(500, 300, 200, 300), 170, 40, 1920, 1080, gap: 6);
        Assert.Equal(515, left);
        Assert.Equal(300 - 40 - 6, top);
    }

    [Fact]
    public void Badge_goes_below_a_card_touching_the_top_edge()
    {
        var (_, top) = BadgeLayout.Place(new DipRect(500, 10, 200, 300), 170, 40, 1920, 1080, gap: 6);
        Assert.Equal(316, top);
    }

    [Fact]
    public void Badge_goes_inside_a_card_that_fills_the_height()
    {
        var (_, top) = BadgeLayout.Place(new DipRect(500, 10, 200, 1060), 170, 40, 1920, 1080, gap: 6);
        Assert.Equal(12, top);
    }

    [Fact]
    public void Badge_stays_on_screen_horizontally()
    {
        var (right, _) = BadgeLayout.Place(new DipRect(1850, 300, 200, 300), 170, 40, 1920, 1080, gap: 6);
        Assert.Equal(1920 - 170 - 2, right);
        var (left, _) = BadgeLayout.Place(new DipRect(-150, 300, 200, 300), 170, 40, 1920, 1080, gap: 6);
        Assert.Equal(2, left);
    }

    /// <summary>Three reward cards at 4K, as recognised in a real capture (physical px).</summary>
    private static readonly PxBox[] Reward4K = [new(985, 893, 474, 670), new(1683, 893, 474, 670), new(2381, 893, 474, 670)];

    private static readonly PxBox Layer4K = new(0, 0, 3840, 2160);

    [Theory]
    [InlineData(1.5)] // 4K at 150 %
    [InlineData(1.0)] // 4K at 100 %
    [InlineData(2.0)] // 4K at 200 %
    public void Reward_badges_sit_above_their_cards_without_overlapping(double dpi)
    {
        BadgeFrame f = BadgeLayout.Compute(Reward4K, [], Layer4K, dpi, 1, skip: false, outlines: false);

        Assert.Equal(3, f.Badges.Count);
        for (var i = 0; i < 3; i++)
        {
            DipRect b = f.Badges[i].Box, card = BadgeLayout.ToLayerDips(Reward4K[i], Layer4K, dpi);
            Assert.True(b.Bottom <= card.Y, $"badge {i} is not above its card");
            Assert.Equal(card.X + card.Width / 2, b.X + b.Width / 2, 6);
            for (var j = 0; j < 3; j++)
            {
                if (j != i) Assert.False(b.Intersects(f.Badges[j].Box), $"badge {i} overlaps badge {j}");
            }
            // Same physical size whatever the DPI: the badge follows the card, not the Windows scale.
            Assert.Equal(0.72 * Reward4K[i].Width, b.Width * dpi, 6);
        }
        Assert.Empty(f.CardOutlines);
        Assert.Null(f.Skip);
    }

    [Fact]
    public void Ui_scale_multiplier_grows_the_badge()
    {
        BadgeFrame auto = BadgeLayout.Compute(Reward4K, [], Layer4K, 1.5, 1, false, false);
        BadgeFrame big = BadgeLayout.Compute(Reward4K, [], Layer4K, 1.5, 1.25, false, false);
        Assert.Equal(auto.Badges[0].Scale * 1.25, big.Badges[0].Scale, 6);
        Assert.Equal(auto.Badges[0].Box.Width * 1.25, big.Badges[0].Box.Width, 6);
    }

    [Fact]
    public void Skip_pill_sits_below_the_games_skip_button_at_the_smallest_badge_scale()
    {
        BadgeFrame f = BadgeLayout.Compute(Reward4K, [], Layer4K, 1.5, 1, skip: true, outlines: false);

        PlacedBadge skip = Assert.IsType<PlacedBadge>(f.Skip);
        double rowCentre = (Reward4K[0].X + Reward4K[2].Right) / 2.0 / 1.5;
        Assert.Equal(f.Badges.Min(b => b.Scale), skip.Scale);
        // The "넘기기" button ends at reference y 957 = 1914 px at 4K (UI scale 2) = 1276 DIPs at 150 %.
        Assert.Equal(1914 / 1.5 + 8 * skip.Scale, skip.Box.Y, 6);
        Assert.Equal(rowCentre, skip.Box.X + skip.Box.Width / 2, 6);
        Assert.Equal(BadgeLayout.SkipWidth * skip.Scale, skip.Box.Width, 6);
        Assert.True(skip.Box.InsideArea(3840 / 1.5, 2160 / 1.5));
    }

    [Fact]
    public void Debug_outlines_cover_every_card_and_each_price_label_present()
    {
        PxBox?[] prices = [new PxBox(1000, 1580, 400, 60), null, new PxBox(2400, 1580, 400, 60)];
        BadgeFrame f = BadgeLayout.Compute(Reward4K, prices, Layer4K, 1.5, 1, skip: false, outlines: true);

        Assert.Equal(Reward4K.Select(c => BadgeLayout.ToLayerDips(c, Layer4K, 1.5)), f.CardOutlines);
        Assert.Equal([new DipRect(1000 / 1.5, 1580 / 1.5, 400 / 1.5, 40), new DipRect(1600, 1580 / 1.5, 400 / 1.5, 40)], f.PriceOutlines);
        // Outlines never move the badges.
        Assert.Equal(BadgeLayout.Compute(Reward4K, prices, Layer4K, 1.5, 1, false, false).Badges, f.Badges);
    }

    [Fact]
    public void No_cards_means_no_badges_and_no_skip_pill()
    {
        BadgeFrame f = BadgeLayout.Compute([], [], Layer4K, 1.5, 1, skip: true, outlines: true);
        Assert.Empty(f.Badges);
        Assert.Null(f.Skip);
        Assert.Empty(f.CardOutlines);
    }
}
