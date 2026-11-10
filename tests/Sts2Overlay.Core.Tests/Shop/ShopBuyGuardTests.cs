using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Shop;

/// <summary>
/// "구매!" follows the reward screen's bar (same <see cref="SkipWeights"/>): a minimum margin over skipping, a full tier step
/// more for a low-confidence card, never a card without rating data; and a used card-removal service is not planned for.
/// </summary>
public class ShopBuyGuardTests
{
    private static ShopEvaluation Evaluate(double t0Act1, RunState state, ShopOffer offer)
    {
        var w = new Weights();
        w.Skip.T0[1] = t0Act1;
        return new ShopScorer(DefectPack(w)).Evaluate(state, new DeckProfile(), offer);
    }

    private static ShopOffer Offer(params string[] cards) => new(cards.Select(c => new ShopCardOffer(Parse(c), 50)).ToArray());

    private static string[] Bought(ShopEvaluation r) => r.RecommendedPurchase.Select(i => i.Card?.Id ?? "removal").ToArray();

    [Fact]
    public void Card_below_the_margin_is_listed_but_not_bought()
    {
        // Blocker 50 vs skip 49.6: worth +0.4, under skip.margin (0.5).
        var r = Evaluate(49.6, State(gold: 500), Offer(Blocker));
        var item = Assert.Single(r.Items);
        Assert.Equal(0.4, item.Value.Total, 9);
        Assert.Empty(r.RecommendedPurchase);
        Assert.Equal(500, r.GoldLeft);

        // At the margin it is bought, as on the reward screen.
        Assert.Equal(new[] { Blocker }, Bought(Evaluate(49.5, State(gold: 500), Offer(Blocker))));
    }

    [Theory]
    [InlineData(70.0, false)] // LowConf 80 − 70 = 10 < 0.5 + 15
    [InlineData(60.0, true)]  // 20 ≥ 15.5
    public void Low_confidence_card_needs_a_tier_step_more(double t0, bool bought)
    {
        var r = Evaluate(t0, State(gold: 500), Offer(LowConf, Star));
        Assert.Contains(Star, Bought(r)); // the same score with full confidence is bought either way
        Assert.Equal(bought, Bought(r).Contains(LowConf));
        Assert.Equal(2, r.Items.Count);
    }

    [Fact]
    public void Card_without_rating_data_is_never_bought()
    {
        var r = Evaluate(-100, State(gold: 500), Offer(Unknown));
        var item = Assert.Single(r.Items);
        Assert.True(item.Value.Total > 100);
        Assert.Equal("?", item.CardEvaluation!.TierText);
        Assert.Empty(r.RecommendedPurchase);
    }

    [Fact]
    public void Used_removal_service_is_left_out_of_rows_bundle_and_gold()
    {
        var state = State(deck: [Strike, Defend], gold: 300);
        var open = Evaluate(45, state, Offer());
        Assert.Equal(new[] { "removal" }, Bought(open));
        Assert.Equal(300 - 75, open.GoldLeft);

        var used = Evaluate(45, state, Offer() with { RemovalAvailable = false });
        Assert.DoesNotContain(used.Items, i => i.Kind == ShopItemKind.CardRemoval);
        Assert.Empty(used.RecommendedPurchase);
        Assert.Equal(300, used.GoldLeft);
    }
}
