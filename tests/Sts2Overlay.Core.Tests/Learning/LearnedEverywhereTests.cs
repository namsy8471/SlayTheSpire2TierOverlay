using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;
using Sts2Overlay.Core.Tests.Scoring;

namespace Sts2Overlay.Core.Tests.Learning;

/// <summary>
/// One card, one value on every screen: the reward screen, the shop and an event's "gain this card" all value a card through
/// <see cref="CardValuer"/>, with the learned model when it is on and the hand-written engine (exactly as before) when it is off.
/// </summary>
public class LearnedEverywhereTests
{
    private static string PackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1");

    private static readonly Lazy<DataPack> Pack = new(() =>
        DataPackLoader.LoadDirectory(PackDir, DataPackLoader.LoadCatalogFile(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json"))));

    private static RecommendationEngine Learned => new(Pack.Value);

    private static RecommendationEngine Engine => new(Pack.Value) { UseLearnedModel = false };

    private static IEnumerable<CardDecision> Decisions(string file, Func<CardDecision, bool> keep)
    {
        var run = RunHistoryReader.ReadFile(TestPaths.Run(file));
        return run.Players.SelectMany(p => DecisionExtractor.Extract(run, p.Id)).Where(keep);
    }

    private static ShopOffer AsShop(IEnumerable<CardRef> cards)
        => new(cards.Select(c => new ShopCardOffer(c, 0, PriceKnown: false)).ToArray(), RemovalPrice: 75);

    [Theory]
    [MemberData(nameof(PickExportTests.Runs), MemberType = typeof(PickExportTests))]
    public void A_cards_value_does_not_depend_on_the_other_offered_cards(string file)
    {
        var learned = Learned;
        foreach (var d in Decisions(file, d => d.Context != OfferContext.Shop && d.Offered.Count >= 2))
        {
            var profile = learned.Analyze(d.StateBefore);
            var together = learned.EvaluateOffer(d.StateBefore, profile, d.Offered, d.Context);
            for (var i = 0; i < d.Offered.Count; i++)
            {
                var alone = learned.EvaluateOffer(d.StateBefore, profile, [d.Offered[i]], d.Context);
                Assert.Equal(together.Cards[i].Total, alone.Cards[0].Total, 9);
                Assert.Equal(together.Skip.Total, alone.Skip.Total, 9);
            }
        }
    }

    /// <summary>Learned shop: card value = learned card total − learned skip total, and the breakdown still sums to it.</summary>
    [Theory]
    [MemberData(nameof(PickExportTests.Runs), MemberType = typeof(PickExportTests))]
    public void Learned_shop_card_value_is_the_reward_value_minus_skip_and_sums_to_its_total(string file)
    {
        var learned = Learned;
        var model = Pack.Value.CardPolicy!;
        foreach (var d in Decisions(file, d => d.Context == OfferContext.Shop && d.Offered.Count > 0))
        {
            var shop = learned.EvaluateShop(d.StateBefore, AsShop(d.Offered));
            var reward = learned.EvaluateOffer(d.StateBefore, d.Offered, OfferContext.CardReward);
            Assert.Equal(reward.Skip.Total, shop.Skip.Total, 9);
            ScoreAssert.Consistent(shop.Skip);
            foreach (var item in shop.Items)
            {
                ScoreAssert.Consistent(item.Value);
                Assert.Equal(item.Value.Components.Sum(c => c.Value), item.Value.Total, 9);
                if (item.Kind != ShopItemKind.Card) continue;

                var e = item.CardEvaluation!;
                var same = reward.Cards.First(c => c.Card == item.Card);
                Assert.Equal(same.Total - reward.Skip.Total, item.Value.Total, 6);
                Assert.Equal(same.Total, e.Total, 9);
                Assert.Equal(-reward.Skip.Total, item.Value.ValueOf(ComponentKeys.SkipRelative), 9);
                Assert.Equal(model.Knows(Pack.Value.Canonical(e.Card.Id)), e.KnownToModel);
                Assert.Equal(same.IsEstimate, e.IsEstimate);
                Assert.Equal(same.Confidence, e.Confidence);
            }
        }
    }

    /// <summary><c>cardModel: engine</c>: the shop is exactly the hand-written engine, as before the learned model reached it.</summary>
    [Theory]
    [MemberData(nameof(PickExportTests.Runs), MemberType = typeof(PickExportTests))]
    public void Engine_mode_shop_is_the_hand_written_engine(string file)
    {
        var pack = Pack.Value;
        var engine = Engine;
        foreach (var d in Decisions(file, d => d.Context == OfferContext.Shop && d.Offered.Count > 0))
        {
            var offer = AsShop(d.Offered);
            var viaEngine = engine.EvaluateShop(d.StateBefore, offer);
            var profile = engine.Analyze(d.StateBefore);
            var direct = new ShopScorer(pack).Evaluate(d.StateBefore, profile, offer);
            var skip = new SkipScorer(pack).Score(d.StateBefore, profile).Total;

            Assert.Equal(skip, viaEngine.Skip.Total, 9);
            Assert.Equal(direct.Items.Select(i => i.Value.Total), viaEngine.Items.Select(i => i.Value.Total));
            Assert.Equal(direct.RecommendedPurchase.Select(i => i.Card), viaEngine.RecommendedPurchase.Select(i => i.Card));
            foreach (var item in viaEngine.Items.Where(i => i.Kind == ShopItemKind.Card))
            {
                var plain = new CardScorer(pack).Score(d.StateBefore, profile, item.Card!.Value, OfferContext.Shop);
                Assert.Equal(plain.Total - skip, item.Value.Total, 9);
                Assert.Equal(plain.Score.Components.Count + 1, item.Value.Components.Count);
                Assert.False(item.CardEvaluation!.KnownToModel);
                Assert.False(item.CardEvaluation.CoopOnlyNoSoloData);
                Assert.Equal(!plain.IsRated, item.CardEvaluation.Unjudged);
            }
        }
    }

    [Fact]
    public void The_switch_changes_shop_values()
    {
        var differs = 0;
        foreach (var file in TestPaths.RunFileNames.Select(r => (string)r[0]))
        {
            foreach (var d in Decisions(file, d => d.Context == OfferContext.Shop && d.Offered.Count > 0))
            {
                var a = Learned.EvaluateShop(d.StateBefore, AsShop(d.Offered)).Items.Where(i => i.Kind == ShopItemKind.Card).Select(i => i.Value.Total).Order();
                var b = Engine.EvaluateShop(d.StateBefore, AsShop(d.Offered)).Items.Where(i => i.Kind == ShopItemKind.Card).Select(i => i.Value.Total).Order();
                if (!a.SequenceEqual(b)) differs++;
            }
        }

        Assert.True(differs > 0);
    }

    private static RunState Ironclad(bool multiplayer) => new()
    {
        Character = "CHARACTER.IRONCLAD",
        Act = 1,
        Floor = 5,
        Hp = 70,
        MaxHp = 80,
        Gold = 300,
        IsMultiplayer = multiplayer,
        Deck = Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_IRONCLAD")), 5)
            .Concat(Enumerable.Repeat(new DeckCard(new CardRef("CARD.DEFEND_IRONCLAD")), 4))
            .Append(new DeckCard(new CardRef("CARD.BASH")))
            .ToArray(),
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // a solo run should not offer it, but if it does the card says the same
    public void A_coop_only_card_says_the_model_has_no_solo_data(bool multiplayer)
    {
        var pack = Pack.Value;
        Assert.True(pack.FindCatalog("CARD.TANK")!.MultiplayerOnly);
        Assert.False(pack.CardPolicy!.Knows("CARD.TANK"));
        var state = Ironclad(multiplayer);
        CardRef[] offer = [new("CARD.TANK"), new("CARD.INFLAME")];

        var plain = Engine.EvaluateOffer(state, offer).Cards[0];
        var learned = Learned.EvaluateOffer(state, offer);
        var tank = learned.Cards[0];
        Assert.True(tank.CoopOnlyNoSoloData);
        Assert.True(tank.IsEstimate);
        Assert.False(tank.KnownToModel);
        Assert.Equal(plain.Confidence, tank.Confidence); // not raised
        Assert.False(learned.Cards[1].CoopOnlyNoSoloData); // a personal card is left alone
        Assert.False(plain.CoopOnlyNoSoloData); // engine mode: no model, nothing to say

        var shop = Learned.EvaluateShop(state, new ShopOffer([new ShopCardOffer(new CardRef("CARD.TANK"), 150)]));
        var item = shop.Items.Single(i => i.Kind == ShopItemKind.Card);
        Assert.True(item.CardEvaluation!.CoopOnlyNoSoloData);
        Assert.True(item.CardEvaluation.IsEstimate);
    }

    /// <summary>
    /// The shop's "never buy an unjudged card" rule follows the model when it is active: a card with no tier entry that the
    /// model knows is judged (it may be bought on its value); engine-only it stays unjudged and is never bought.
    /// </summary>
    [Fact]
    public void A_card_known_only_to_the_model_is_judged_in_a_learned_shop()
    {
        var pack = Pack.Value;
        const string id = "CARD.SIDESTEP"; // in the model's training data, not in the tier files
        Assert.Null(pack.FindTier("CHARACTER.SILENT", id));
        Assert.True(pack.CardPolicy!.Knows(id));
        var state = Ironclad(false) with { Character = "CHARACTER.SILENT", Deck = [] };
        var offer = new ShopOffer([new ShopCardOffer(new CardRef(id), 10)]);

        var engineItem = Engine.EvaluateShop(state, offer);
        Assert.True(engineItem.Items.Single(i => i.Kind == ShopItemKind.Card).CardEvaluation!.Unjudged);
        Assert.DoesNotContain(engineItem.RecommendedPurchase, i => i.Kind == ShopItemKind.Card);

        var learned = Learned.EvaluateShop(state, offer);
        var item = learned.Items.Single(i => i.Kind == ShopItemKind.Card);
        Assert.False(item.CardEvaluation!.Unjudged);
        var s = pack.Weights.Skip;
        var bar = item.CardEvaluation.Confidence >= s.LowConfidence ? s.Margin : s.Margin + Math.Max(0.0, s.LowConfidenceStep);
        Assert.Equal(item.Value.Total >= bar, learned.RecommendedPurchase.Contains(item));
    }

    private const string GainCardEvent = """
    {
      "events": [
        {
          "id": "EVENT.TEST_GAIN",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "TEST_GAIN.pages.INITIAL.options.TAKE", "effects": [ { "type": "cardAdd", "card": "CARD.INFLAME", "count": 1, "how": "fixed" } ] },
              { "key": "TEST_GAIN.pages.INITIAL.options.LEAVE", "effects": [], "leave": true }
            ]}
          ]
        }
      ]
    }
    """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_events_gain_card_effect_is_the_same_value_as_the_reward_screen(bool useLearned)
    {
        var pack = Pack.Value;
        var state = Ironclad(false);
        var scorer = new EventScorer(pack, EventCatalog.Parse(GainCardEvent)) { UseLearnedModel = useLearned };
        var eval = scorer.Evaluate(state, "EVENT.TEST_GAIN")!;
        var card = eval.Options.Single(o => o.Option.Key.EndsWith(".TAKE", StringComparison.Ordinal)).Score.ValueOf(EventComponentKeys.Card);

        var engine = new RecommendationEngine(pack) { UseLearnedModel = useLearned };
        var reward = engine.EvaluateOffer(state, [new CardRef("CARD.INFLAME")], OfferContext.Event);
        Assert.Equal(reward.Cards[0].Total - reward.Skip.Total, card, 9);
    }

    [Fact]
    public void Event_scorer_defaults_to_the_engine_as_before()
    {
        var pack = Pack.Value;
        var state = Ironclad(false);
        var plain = new EventScorer(pack, EventCatalog.Parse(GainCardEvent)).Evaluate(state, "EVENT.TEST_GAIN")!;
        var learned = new EventScorer(pack, EventCatalog.Parse(GainCardEvent)) { UseLearnedModel = true }.Evaluate(state, "EVENT.TEST_GAIN")!;
        var profile = DeckAnalyzer.Analyze(state, pack);
        var expected = new CardScorer(pack).Score(state, profile, new CardRef("CARD.INFLAME"), OfferContext.Event).Total
            - new SkipScorer(pack).Score(state, profile).Total;
        Assert.Equal(expected, plain.Options[0].Score.ValueOf(EventComponentKeys.Card), 9);
        Assert.NotEqual(plain.Options[0].Score.ValueOf(EventComponentKeys.Card), learned.Options[0].Score.ValueOf(EventComponentKeys.Card), 6);
    }

    /// <summary>The run review judges with the caller's <c>cardModel</c> setting, not always with the model.</summary>
    [Theory]
    [MemberData(nameof(PickExportTests.Runs), MemberType = typeof(PickExportTests))]
    public void Replay_uses_the_callers_model_setting(string file)
    {
        var pack = Pack.Value;
        var run = RunHistoryReader.ReadFile(TestPaths.Run(file));
        foreach (var useLearned in new[] { true, false })
        {
            var engine = new RecommendationEngine(pack) { UseLearnedModel = useLearned };
            var replay = new ReplayAnalyzer(pack, useLearned).Replay(run);
            foreach (var r in replay.Decisions.Where(r => r.Offer is not null))
            {
                var expected = engine.EvaluateOffer(r.Decision.StateBefore, r.Decision.Offered, r.Decision.Context);
                Assert.Equal(expected.Cards.Select(c => c.Total), r.Offer!.Cards.Select(c => c.Total));
                Assert.Equal(expected.Skip.Total, r.Offer.Skip.Total, 9);
            }
        }
    }
}
