using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Shop;

/// <summary>Recognised prices in the shop: estimates for unread labels, and gold checked against the label colours.</summary>
public class ShopPricingTests
{
    private const string ColorlessRare = "CARD.CL_RARE";

    /// <summary>The Defect test pack plus a tiny catalog that gives two cards a rarity and pool.</summary>
    private static readonly DataPack Pack = WithCatalog(DefectPack());

    private static DataPack WithCatalog(DataPack p) => new()
    {
        Weights = p.Weights,
        Tiers = p.Tiers,
        Archetypes = p.Archetypes,
        Combos = p.Combos,
        IdAliases = p.IdAliases,
        Catalog = new CardCatalog
        {
            Cards =
            [
                new CatalogCard { Id = Star, Rarity = "rare", Pool = Defect },
                new CatalogCard { Id = Blocker, Rarity = "common", Pool = Defect },
                new CatalogCard { Id = ColorlessRare, Rarity = "rare", Pool = DataPack.Colorless },
            ],
        },
    };

    private static ShopEvaluation Evaluate(int gold, params ShopCardOffer[] cards)
        => new ShopScorer(Pack).Evaluate(State(act: 1, gold: gold), new DeckProfile(), new ShopOffer(cards));

    [Theory]
    [InlineData("common", false, 50)]
    [InlineData("uncommon", false, 75)]
    [InlineData("rare", false, 150)]
    [InlineData(null, false, 75)]
    [InlineData("uncommon", true, 86)]
    [InlineData("rare", true, 172)]
    public void BasePrice_FollowsRarityAndColorless(string? rarity, bool colorless, int expected)
        => Assert.Equal(expected, ShopPricing.BasePrice(rarity, colorless));

    [Fact]
    public void Estimate_UsesCatalogRarityAndPool()
    {
        Assert.Equal(150, ShopPricing.Estimate(Pack, Defect, new CardRef(Star)));
        Assert.Equal(50, ShopPricing.Estimate(Pack, Defect, new CardRef(Blocker)));
        Assert.Equal(172, ShopPricing.Estimate(Pack, Defect, new CardRef(ColorlessRare)));
        Assert.Equal(75, ShopPricing.Estimate(Pack, Defect, new CardRef(AoeHit))); // no rarity anywhere
    }

    [Fact]
    public void UnreadPrice_IsEstimatedAndFlagged()
    {
        var r = Evaluate(500, new ShopCardOffer(new CardRef(Star), 0, PriceKnown: false), new ShopCardOffer(new CardRef(AoeHit), 73));
        var star = r.Items.Single(i => i.Card!.Value.Id == Star);
        Assert.True(star.PriceEstimated);
        Assert.Equal(150, star.Price);
        Assert.Equal(star.Value.Total / 150, star.ValuePerGold, 9);
        Assert.False(r.Items.Single(i => i.Card!.Value.Id == AoeHit).PriceEstimated);
    }

    [Fact]
    public void RedLabel_MarksUnaffordable_AndCorrectsStaleGold()
    {
        // The save still says 300 gold, but a 149 label is red: the player has spent gold since.
        var r = Evaluate(300,
            new ShopCardOffer(new CardRef(Star), 149, AffordableOnScreen: false),
            new ShopCardOffer(new CardRef(AoeHit), 75, AffordableOnScreen: true));
        Assert.True(r.GoldCorrectedByScreen);
        Assert.Equal(148, r.Gold);
        Assert.False(r.Items.Single(i => i.Card!.Value.Id == Star).Affordable);
        Assert.DoesNotContain(r.RecommendedPurchase, i => i.Card?.Id == Star);
        Assert.Equal(r.Gold - r.RecommendedPurchase.Sum(i => i.Price), r.GoldLeft);
    }

    [Fact]
    public void CreamLabel_RaisesGoldThatIsTooLow()
    {
        // Tracked 40 gold, but a 75 label is cream: the player picked up gold the tracker has not seen yet.
        var r = Evaluate(40, new ShopCardOffer(new CardRef(AoeHit), 75, AffordableOnScreen: true));
        Assert.True(r.GoldCorrectedByScreen);
        Assert.Equal(75, r.Gold);
        Assert.True(r.Items.Single().Affordable);
        Assert.Single(r.RecommendedPurchase);
    }

    [Fact]
    public void ContradictoryLabels_KeepTrackedGold()
    {
        // 75 red but 150 cream cannot both be true (a misread); ignore the colours.
        var r = Evaluate(100,
            new ShopCardOffer(new CardRef(AoeHit), 75, AffordableOnScreen: false),
            new ShopCardOffer(new CardRef(Star), 150, AffordableOnScreen: true));
        Assert.False(r.GoldCorrectedByScreen);
        Assert.Equal(100, r.Gold);
        Assert.All(r.RecommendedPurchase, i => Assert.True(i.Price <= 100));
    }

    [Fact]
    public void ConsistentLabels_LeaveGoldAlone_AndSaleIsReported()
    {
        var r = Evaluate(150,
            new ShopCardOffer(new CardRef(AoeHit), 37, AffordableOnScreen: true, OnSale: true),
            new ShopCardOffer(new CardRef(Star), 172, AffordableOnScreen: false));
        Assert.False(r.GoldCorrectedByScreen);
        Assert.Equal(150, r.Gold);
        Assert.True(r.Items.Single(i => i.Card!.Value.Id == AoeHit).OnSale);
    }

    [Fact]
    public void UnknownColour_FallsBackToGoldArithmetic()
    {
        var r = Evaluate(100, new ShopCardOffer(new CardRef(Star), 150), new ShopCardOffer(new CardRef(AoeHit), 0, PriceKnown: false));
        Assert.False(r.Items.Single(i => i.Card!.Value.Id == Star).Affordable);
        Assert.True(r.Items.Single(i => i.Card!.Value.Id == AoeHit).Affordable); // estimated 75 ≤ 100
    }
}
