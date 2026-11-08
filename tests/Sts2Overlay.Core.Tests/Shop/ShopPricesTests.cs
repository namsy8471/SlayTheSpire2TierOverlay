using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Shop;
using Sts2Overlay.Core.Tests.Data;

namespace Sts2Overlay.Core.Tests.Shop;

/// <summary>Merchant numbers from SPIKE §5 (code-confirmed).</summary>
public class ShopPricesTests
{
    private const string Defect = "CHARACTER.DEFECT";
    private static readonly Weights W = new();

    /// <summary>SPIKE §5: card removal costs <c>75 + 25 × removals</c>, ascension ≥ 6 <c>100 + 50 × removals</c>.</summary>
    [Theory]
    [InlineData(0, 0, 75)]
    [InlineData(0, 1, 100)]
    [InlineData(5, 3, 150)]
    [InlineData(6, 0, 100)]
    [InlineData(6, 1, 150)]
    [InlineData(20, 3, 250)]
    [InlineData(0, -1, 75)]
    public void RemovalPrice_MatchesSpikeSection5(int ascension, int removals, int expected)
    {
        var state = new RunState { Character = Defect, Ascension = ascension, CardRemovalsBought = removals };
        Assert.Equal(expected, ShopScorer.RemovalPrice(state, W));
    }

    [Theory]
    [InlineData("common", false, false, 50)]
    [InlineData("Uncommon", false, false, 75)]
    [InlineData("rare", false, false, 150)]
    [InlineData("uncommon", true, false, 86)] // 75 × 1.15 = 86.25
    [InlineData("rare", true, false, 173)] // 150 × 1.15 = 172.5
    [InlineData("common", false, true, 25)]
    [InlineData("rare", true, true, 86)] // 86.25
    public void Estimated(string rarity, bool colorless, bool onSale, int expected)
        => Assert.Equal(expected, ShopPrices.Estimated(W.Shop, rarity, colorless, onSale));

    [Theory]
    [InlineData("basic")]
    [InlineData("ancient")]
    [InlineData(null)]
    public void Estimated_UnsoldRarityIsNull(string? rarity)
    {
        Assert.Null(ShopPrices.Estimated(W.Shop, rarity, false));
        Assert.Null(ShopPrices.Range(W.Shop, rarity, false));
    }

    [Fact]
    public void Range_CoversRandomFactor()
    {
        Assert.Equal((47, 53), ShopPrices.Range(W.Shop, "common", false)); // 47.5 .. 52.5
        Assert.Equal((142, 158), ShopPrices.Range(W.Shop, "rare", false)); // 142.5 .. 157.5
        Assert.Equal((163, 182), ShopPrices.Range(W.Shop, "rare", true)); // 163.875 .. 181.125
        Assert.Equal((23, 27), ShopPrices.Range(W.Shop, "common", false, onSale: true)); // 23.75 .. 26.25
    }

    [Fact]
    public void ShopLayout_Defaults()
    {
        Assert.Equal(5, W.Shop.CharacterCardSlots.Values.Sum());
        Assert.Equal(2, W.Shop.CharacterCardSlots[CardTypes.Attack]);
        Assert.Equal(2, W.Shop.CharacterCardSlots[CardTypes.Skill]);
        Assert.Equal(1, W.Shop.CharacterCardSlots[CardTypes.Power]);
        Assert.Equal(1, W.Shop.ColorlessCardSlots["uncommon"]);
        Assert.Equal(1, W.Shop.ColorlessCardSlots["rare"]);
    }

    [Fact]
    public void ShopScorer_EstimatedPrice_UsesCatalog()
    {
        var scorer = new ShopScorer(new DataPack { Catalog = RealCatalog.Instance });
        Assert.Equal(150, scorer.EstimatedPrice(Defect, "CARD.ECHO_FORM"));
        Assert.Equal(75, scorer.EstimatedPrice(Defect, "CARD.CAPACITOR"));
        Assert.Equal(86, scorer.EstimatedPrice(Defect, "CARD.AUTOMATION")); // colorless uncommon
        Assert.Equal(173, scorer.EstimatedPrice(Defect, "CARD.ALCHEMIZE")); // colorless rare
        Assert.Equal(38, scorer.EstimatedPrice(Defect, "CARD.CAPACITOR", onSale: true)); // 37.5
        Assert.Equal(50, scorer.EstimatedPrice(Defect, "CARD.UNKNOWN", rarity: "common", colorless: false));
        Assert.Null(scorer.EstimatedPrice(Defect, "CARD.UNKNOWN"));
        Assert.Null(scorer.EstimatedPrice(Defect, "CARD.STRIKE_DEFECT"));
    }
}
