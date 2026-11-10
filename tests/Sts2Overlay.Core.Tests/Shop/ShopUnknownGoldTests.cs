using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Shop;

/// <summary>
/// A log-only run's gold is a placeholder (<see cref="RunState.VitalsKnown"/> false): affordability comes from the price
/// colours on screen alone, and without a red label everything counts as affordable.
/// </summary>
public class ShopUnknownGoldTests
{
    private static readonly DataPack Pack = DefectPack();

    private static ShopEvaluation Evaluate(RunState state, ShopOffer offer) => new ShopScorer(Pack).Evaluate(state, new DeckProfile(), offer);

    private static ShopOffer Offer(params ShopCardOffer[] cards) => new(cards);

    [Fact]
    public void Without_price_colours_every_item_counts_as_affordable()
    {
        // Placeholder gold 0 would make nothing affordable; unknown gold must not.
        var state = State(act: 1, deck: [Strike], gold: 0) with { VitalsKnown = false };
        var r = Evaluate(state, Offer(new ShopCardOffer(Parse(Star), 150), new ShopCardOffer(Parse(AoeHit), 75)));

        Assert.False(r.GoldKnown);
        Assert.False(r.GoldCorrectedByScreen);
        Assert.All(r.Items, i => Assert.True(i.Affordable));
        Assert.Contains(r.RecommendedPurchase, i => i.Card?.Id == Star);
        Assert.Contains(r.RecommendedPurchase, i => i.Card?.Id == AoeHit);
    }

    [Fact]
    public void Price_colours_decide_affordability_and_cap_the_bundle()
    {
        // Placeholder gold 1000; the screen shows the 150 card in red (can't pay) and the 75 card in cream (can pay).
        var state = State(act: 1, deck: [Strike], gold: 1000) with { VitalsKnown = false };
        var r = Evaluate(state, Offer(
            new ShopCardOffer(Parse(Star), 150, AffordableOnScreen: false),
            new ShopCardOffer(Parse(AoeHit), 75, AffordableOnScreen: true)));

        Assert.False(r.Items.Single(i => i.Card?.Id == Star).Affordable);
        Assert.True(r.Items.Single(i => i.Card?.Id == AoeHit).Affordable);
        Assert.Equal(149, r.Gold); // the most the red label allows, not the placeholder
        Assert.DoesNotContain(r.RecommendedPurchase, i => i.Card?.Id == Star);
    }

    [Fact]
    public void Known_gold_is_unchanged()
    {
        var r = Evaluate(State(act: 1, deck: [Strike], gold: 0), Offer(new ShopCardOffer(Parse(Star), 150)));
        Assert.True(r.GoldKnown);
        Assert.Equal(0, r.Gold);
        Assert.All(r.Items, i => Assert.False(i.Affordable));
    }

    [Fact]
    public void Gold_ceiling_is_one_below_the_cheapest_red_price()
    {
        var offer = Offer(new ShopCardOffer(Parse(Star), 150, AffordableOnScreen: false), new ShopCardOffer(Parse(AoeHit), 90, AffordableOnScreen: false));
        Assert.Equal(89, ShopPricing.GoldCeiling(offer));
        Assert.Equal(int.MaxValue, ShopPricing.GoldCeiling(Offer(new ShopCardOffer(Parse(Star), 150, AffordableOnScreen: true))));
    }
}
