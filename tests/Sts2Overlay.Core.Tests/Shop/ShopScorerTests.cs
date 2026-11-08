using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;
using Sts2Overlay.Core.Tests.Scoring;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Shop;

public class ShopScorerTests
{
    private static readonly DataPack Pack = DefectPack();
    private static readonly Weights W = Pack.Weights;

    private static ShopEvaluation Evaluate(RunState state, ShopOffer offer, DeckProfile? profile = null)
    {
        var result = new ShopScorer(Pack).Evaluate(state, profile ?? new DeckProfile(), offer);
        ScoreAssert.Consistent(result.Skip);
        foreach (var item in result.Items) ScoreAssert.Consistent(item.Value);
        Assert.True(result.GoldLeft >= 0);
        Assert.True(result.GoldCorrectedByScreen || result.Gold == Math.Max(0, state.Gold));
        Assert.Equal(result.Gold - result.RecommendedPurchase.Sum(i => i.Price), result.GoldLeft);
        Assert.All(result.RecommendedPurchase, i => Assert.True(i.Value.Total > 0 && i.Affordable));
        return result;
    }

    private static ShopOffer Offer(int? removalPrice, params (string Id, int Price)[] cards)
        => new(cards.Select(c => new ShopCardOffer(Parse(c.Id), c.Price)).ToArray(), removalPrice);

    [Theory]
    [InlineData(0, 0, 75)]
    [InlineData(5, 2, 125)]
    [InlineData(6, 0, 100)]
    [InlineData(10, 1, 150)]
    public void RemovalPrice_NormalAndHighAscension(int ascension, int removals, int expected)
    {
        Assert.Equal(expected, ShopScorer.RemovalPrice(State(ascension: ascension, removals: removals), W));
    }

    [Fact]
    public void CardValue_IsCardScoreMinusSkip()
    {
        var r = Evaluate(State(act: 1, gold: 500), Offer(null, (Star, 150)));
        var item = Assert.Single(r.Items);
        Assert.Equal(ShopItemKind.Card, item.Kind);
        Assert.NotNull(item.CardEvaluation);
        Assert.Equal(item.CardEvaluation!.Total - r.Skip.Total, item.Value.Total, 9);
        Assert.Equal(80 - 45, item.Value.Total, 9);
        Assert.Equal(-r.Skip.Total, item.Value.ValueOf(ComponentKeys.SkipRelative));
        Assert.Equal(35.0 / 150, item.ValuePerGold, 9);
    }

    [Fact]
    public void Removal_TargetsCurseFirst()
    {
        var r = Evaluate(State(deck: [Strike, Curse, Defend], gold: 100), Offer(null));
        var removal = Assert.Single(r.Items);
        Assert.Equal(ShopItemKind.CardRemoval, removal.Kind);
        Assert.Equal(Curse, removal.RemovalTarget);
        Assert.Equal(W.Shop.CurseRemovalValue, removal.Value.Total);
        Assert.Equal(W.Shop.RemovalBase, removal.Price);
        Assert.True(removal.Affordable);
    }

    [Fact]
    public void Removal_CatalogCurseCounts()
    {
        var r = Evaluate(State(deck: [Strike, CatalogCurse]), Offer(null));
        Assert.Equal(CatalogCurse, r.Items.Single().RemovalTarget);
    }

    [Fact]
    public void Removal_PrefersStrikeOverDefend()
    {
        var r = Evaluate(State(deck: [Defend, Defend, Strike, Blocker]), Offer(null));
        var removal = r.Items.Single();
        Assert.Equal(Strike, removal.RemovalTarget);
        Assert.Equal(W.Shop.BasicRemovalValue, removal.Value.Total);
        Assert.Equal(ComponentKeys.RemovalValue, removal.Value.Components.Single().Key);
    }

    [Fact]
    public void Removal_DefendWhenNoStrike_NoneWhenNoBasics()
    {
        Assert.Equal(Defend, Evaluate(State(deck: [Blocker, Defend]), Offer(null)).Items.Single().RemovalTarget);
        Assert.Empty(Evaluate(State(deck: [Blocker, OrbCore]), Offer(null)).Items);
    }

    [Fact]
    public void Removal_UsesOfferPriceWhenGiven_ElseFormula()
    {
        Assert.Equal(60, Evaluate(State(deck: [Strike]), Offer(60)).Items.Single().Price);
        Assert.Equal(150, Evaluate(State(deck: [Strike], ascension: 7, removals: 1), Offer(null)).Items.Single().Price);
    }

    [Fact]
    public void Items_SortedByValuePerGold()
    {
        var r = Evaluate(State(act: 1, deck: [Strike], gold: 0), Offer(null, (Star, 150), (Blocker, 75), (AoeHit, 75)));
        // AoeHit 20/75, Star 35/150, removal 14/75, Blocker 5/75
        Assert.Equal(new[] { AoeHit, Star, "removal", Blocker }, r.Items.Select(i => i.Card?.Id ?? "removal"));
        Assert.All(r.Items, i => Assert.False(i.Affordable));
        Assert.Empty(r.RecommendedPurchase);
        Assert.Equal(0, r.GoldLeft);
    }

    [Theory]
    [InlineData(150, new[] { Star }, 0)]
    [InlineData(149, new[] { AoeHit }, 74)]
    [InlineData(300, new[] { AoeHit, Star, "removal" }, 0)]
    [InlineData(1000, new[] { AoeHit, Star, "removal", Blocker }, 625)]
    public void Bundle_MaximisesValueWithinGold(int gold, string[] expected, int goldLeft)
    {
        var r = Evaluate(State(act: 1, deck: [Strike], gold: gold), Offer(null, (Star, 150), (Blocker, 75), (AoeHit, 75)));
        Assert.Equal(expected, r.RecommendedPurchase.Select(i => i.Card?.Id ?? "removal"));
        Assert.Equal(goldLeft, r.GoldLeft);
    }

    [Fact]
    public void Bundle_SkipsNegativeValueItems()
    {
        var r = Evaluate(State(act: 1, gold: 500), Offer(null, (Strike, 10), (Star, 150)));
        Assert.Equal(new[] { Star }, r.RecommendedPurchase.Select(i => i.Card!.Value.Id));
        Assert.True(r.Items.Single(i => i.Card!.Value.Id == Strike).Value.Total < 0);
    }

    [Fact]
    public void Bundle_HandlesManyItems()
    {
        var cards = Enumerable.Range(0, 16).Select(i => (i % 2 == 0 ? Star : AoeHit, 40 + i)).ToArray();
        var r = Evaluate(State(act: 1, gold: 200), Offer(null, cards));
        Assert.True(r.RecommendedPurchase.Sum(i => i.Price) <= 200);
        Assert.NotEmpty(r.RecommendedPurchase);
    }

    [Fact]
    [Trait("Category", "NeedsArchetypes")]
    public void Engine_EvaluateShop_UsesOneProfile()
    {
        var state = State(act: 1, deck: [Strike, Strike, Defend, OrbCore], gold: 200);
        var offer = Offer(null, (Star, 150), (OrbEnabler, 50));
        var viaEngine = new RecommendationEngine(Pack).EvaluateShop(state, offer);
        var direct = new ShopScorer(Pack).Evaluate(state, ProfileFor(state, Pack), offer);
        Assert.Equal(direct.Items.Select(i => i.Value.Total), viaEngine.Items.Select(i => i.Value.Total));
        Assert.Equal(direct.GoldLeft, viaEngine.GoldLeft);
    }

    // ---- Relic value with measured buy rates (relics.json) ----

    private static readonly RelicStats ShopRelics = DataPackLoader.LoadRelicStats("""
    { "shopMedianPickRate": 0.327, "ancientNeutralPickRate": 0.333, "relics": {
      "RELIC.TEST_TENT":  { "signal": "shop", "use": true,  "pickRate": 0.65 },
      "RELIC.TEST_RUG":   { "signal": "shop", "use": true,  "pickRate": 0.12 },
      "RELIC.TEST_BREAD": { "signal": "shop", "use": true,  "pickRate": 0.24 },
      "RELIC.TEST_FAN":   { "signal": "shop", "use": false, "pickRate": 0.65 } } }
    """);

    private static ShopScorer RelicScorer(RelicStats? stats) => new(new DataPack { RelicStats = stats });

    [Fact]
    public void RelicValue_UseTrue_ScalesTheRarityValueByTheRateOverTheShopMedian()
    {
        var shop = new Weights().Events.RelicTier["shop"];
        var scorer = RelicScorer(ShopRelics);
        Assert.Equal(shop * 1.5, scorer.RelicValue("RELIC.TEST_TENT", "shop"), 9);              // 0.65 / 0.327 = 1.99 → clamped to 1.5
        Assert.Equal(shop * 0.5, scorer.RelicValue("RELIC.TEST_RUG", "shop"), 9);               // 0.12 / 0.327 = 0.37 → clamped to 0.5
        Assert.Equal(shop * (0.24 / 0.327), scorer.RelicValue("RELIC.TEST_BREAD", "shop"), 9);  // inside the clamp
    }

    [Fact]
    public void RelicValue_UseFalseOrNoEntryOrNoFile_IsTheRarityValue()
    {
        var w = new Weights().Events;
        Assert.Equal(w.RelicTier["shop"], RelicScorer(ShopRelics).RelicValue("RELIC.TEST_FAN", "shop"));
        Assert.Equal(w.RelicTier["rare"], RelicScorer(ShopRelics).RelicValue("RELIC.NOT_LISTED", "rare"));
        Assert.Equal(w.RelicTier["uncommon"], RelicScorer(null).RelicValue("RELIC.TEST_TENT", "uncommon"));
        Assert.Equal(w.RelicDefault, RelicScorer(null).RelicValue("RELIC.TEST_TENT", null));
    }
}
