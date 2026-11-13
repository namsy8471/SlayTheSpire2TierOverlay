using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Learning;
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

    // ---- Relics on the merchant's rug as shop items ----

    private static ShopOffer RelicOffer(params ShopRelicOffer[] relics) => new([]) { Relics = relics };

    [Fact]
    public void RelicItem_ValueIsScoreRelic_WithoutSkipBaseline()
    {
        var state = State(deck: [Blocker, OrbCore], gold: 500);
        var profile = new DeckProfile();
        var r = Evaluate(state, RelicOffer(new ShopRelicOffer("RELIC.TEST_TENT", "Uncommon", 230)), profile);
        var item = Assert.Single(r.Items);
        Assert.Equal(ShopItemKind.Relic, item.Kind);
        Assert.Equal("RELIC.TEST_TENT", item.RelicId);
        Assert.Null(item.Card);
        var expected = new ShopScorer(Pack).ScoreRelic(profile, "RELIC.TEST_TENT", "uncommon");
        Assert.NotNull(item.RelicEvaluation);
        Assert.Equal(expected.Total, item.Value.Total, 9);
        Assert.Equal(expected.Value.Components.Select(c => c.Key), item.Value.Components.Select(c => c.Key));
        Assert.Equal(0, item.Value.ValueOf(ComponentKeys.SkipRelative));
        Assert.Equal(230, item.Price);
        Assert.False(item.PriceEstimated);
        Assert.Equal(item.Value.Total / 230, item.ValuePerGold, 9);
    }

    [Theory]
    [InlineData("Common", 175)]
    [InlineData("Uncommon", 225)]
    [InlineData("Rare", 275)]
    [InlineData("Shop", 200)]
    [InlineData(null, 225)]
    public void RelicItem_UnreadPrice_IsEstimatedFromRarity(string? rarity, int expected)
    {
        var r = Evaluate(State(deck: [Blocker], gold: 1000), RelicOffer(new ShopRelicOffer("RELIC.TEST_RUG", rarity, 0, PriceKnown: false)));
        var item = Assert.Single(r.Items);
        Assert.Equal(expected, item.Price);
        Assert.True(item.PriceEstimated);
        Assert.True(item.Affordable);
    }

    [Fact]
    public void RelicItem_RedLabel_IsUnaffordable_AndBoundsTheGold()
    {
        // The save still says 500 gold, but the game drew the relic's 210 in red: the player has at most 209.
        var r = Evaluate(State(deck: [Blocker], gold: 500), RelicOffer(new ShopRelicOffer("RELIC.TEST_RUG", "Shop", 210, AffordableOnScreen: false)));
        Assert.True(r.GoldCorrectedByScreen);
        Assert.Equal(209, r.Gold);
        var item = Assert.Single(r.Items);
        Assert.False(item.Affordable);
        Assert.Empty(r.RecommendedPurchase);
    }

    [Fact]
    public void Bundle_IncludesRelicsAndCards()
    {
        // Star is worth 35 for 150 gold; a rare relic is worth the rare tier value (20 by default) for 275.
        var offer = new ShopOffer([new ShopCardOffer(Parse(Star), 150)]) { Relics = [new ShopRelicOffer("RELIC.NOT_LISTED", "Rare", 275)] };
        var both = Evaluate(State(act: 1, deck: [Blocker], gold: 425), offer);
        Assert.Equal(2, both.RecommendedPurchase.Count);
        Assert.Contains(both.RecommendedPurchase, i => i.Kind == ShopItemKind.Relic && i.RelicId == "RELIC.NOT_LISTED");
        Assert.Equal(0, both.GoldLeft);

        var one = Evaluate(State(act: 1, deck: [Blocker], gold: 300), offer);
        Assert.Equal(new[] { Star }, one.RecommendedPurchase.Select(i => i.Card!.Value.Id));
        Assert.Equal(new[] { ShopItemKind.Card, ShopItemKind.Relic }, one.Items.Select(i => i.Kind)); // 35/150 > 20/275
    }

    private static ShopEvaluation BaselineShop(Dictionary<string, double> weights, RunState state, ShopOffer offer, DataPack? legalityPack = null)
    {
        var pack = new DataPack { Tiers = Pack.Tiers, Weights = Pack.Weights, Catalog = Pack.Catalog, RunBaseline = new RunBaselinePolicy(weights) };
        state = state with { Character = "CHARACTER.IRONCLAD" };
        return new ShopScorer(pack, useLearnedModel: true, legalityPack).Evaluate(state, DeckAnalyzer.AnalyzeDeck(state, pack), offer);
    }

    [Fact]
    public void RunPolicy_OnlyTheCurrentShelfActionIsRecommended()
    {
        var offer = new ShopOffer([]) { RemovalAvailable = false, Relics = [
            new("RELIC.FIRST", "common", 50), new("RELIC.SECOND", "rare", 50)] };
        var state = State(gold: 500);
        var result = BaselineShop(new() { ["shop:relic|FIRST"] = 100 }, state, offer);
        Assert.True(result.SingleStep);
        Assert.Equal("RELIC.FIRST", Assert.Single(result.RecommendedPurchase).RelicId);
        Assert.Equal(450, result.GoldLeft);
        Assert.Equal(2, result.Items.Count);
        Assert.Empty(BaselineShop(new() { ["shop:leave"] = 100 }, state, offer).RecommendedPurchase);
    }

    [Fact]
    public void RunPolicy_RankTwoUsesRawValueOfAffordableAlternatives()
    {
        var offer = new ShopOffer([]) { RemovalAvailable = false, Relics = [
            new("RELIC.RULE", "rare", 1), new("RELIC.CHEAP", "common", 2),
            new("RELIC.RANK_TWO", "rare", 100), new("RELIC.TOO_EXPENSIVE", "rare", 1000)] };
        var result = BaselineShop(new() { ["shop:rank2"] = 100, ["shop:relic|TOO_EXPENSIVE"] = 200 }, State(gold: 150), offer);
        Assert.Equal("RELIC.RANK_TWO", Assert.Single(result.RecommendedPurchase).RelicId);
    }

    [Fact]
    public void RunPolicy_CanChooseARecognizedRemovalEvenWhenTheRuleHasNoTarget()
    {
        var state = State(deck: [Star + "+1", Blocker], gold: 150);
        var weights = new Dictionary<string, double> { ["shop:remove|" + Star["CARD.".Length..]] = 100 };
        var result = BaselineShop(weights, state, Offer(75));
        var pick = Assert.Single(result.RecommendedPurchase);
        Assert.Same(pick, Assert.Single(result.Items));
        Assert.Equal(new CardRef(Star, 1), pick.RemovalCopy!.Card);
        Assert.Empty(BaselineShop(weights, state, new ShopOffer([]) { RemovalAvailable = false }).RecommendedPurchase);
    }

    [Fact]
    public void RunPolicy_ExcludesEternalCardsAndCopiesMadeEternalByEnchantment()
    {
        var state = State(gold: 150) with { Deck = [new(Parse(Strike), EnchantmentId: "ENCHANTMENT.TEZCATARAS_EMBER"), new(Parse(Curse))] };
        var legal = new DataPack { Catalog = new CardCatalog { Cards = [new CatalogCard { Id = Curse, Keywords = ["Eternal"] }] } };
        var result = BaselineShop(new() { ["shop:remove"] = 100 }, state, Offer(75), legal);
        Assert.Empty(result.RecommendedPurchase);
        Assert.DoesNotContain(result.Items, i => i.Kind == ShopItemKind.CardRemoval);
    }

    [Fact]
    public void RunPolicy_PotionsNeedAnEmptyBeltSlotAKnownAffordablePriceAndPermissionToProcure()
    {
        var offer = new ShopOffer([]) { RemovalAvailable = false, Potions = [new("POTION.TEST", 30)] };
        var weights = new Dictionary<string, double> { ["shop:potion|TEST"] = 100 };
        var state = State(gold: 70) with { EmptyPotionSlots = 1 };
        var chosen = Assert.Single(BaselineShop(weights, state, offer).RecommendedPurchase);
        Assert.Equal("POTION.TEST", chosen.PotionId);
        Assert.Equal(30, chosen.Price);
        Assert.Empty(BaselineShop(weights, state with { EmptyPotionSlots = 0 }, offer).RecommendedPurchase);
        Assert.Empty(BaselineShop(weights, state with { EmptyPotionSlots = null }, offer).RecommendedPurchase);
        Assert.Empty(BaselineShop(weights, state with { Relics = ["RELIC.SOZU"] }, offer).RecommendedPurchase);
        Assert.Empty(BaselineShop(weights, state, offer with { Potions = [new("POTION.TEST", null)] }).RecommendedPurchase);
        Assert.Empty(BaselineShop(weights, state, offer with { Potions = [new("POTION.TEST", 30, false)] }).RecommendedPurchase);
        Assert.Empty(BaselineShop(weights, state with { Gold = 10 }, offer).RecommendedPurchase);
        Assert.Empty(BaselineShop(new() { ["shop:rank2"] = 100 }, state, offer).RecommendedPurchase);
    }
}
