using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

/// <summary>Seeded random decks and offers: no NaN, components always sum to the total, invariants hold.</summary>
public class ScoringPropertyTests
{
    private static readonly DataPack Pack = DefectPack();

    private static readonly string[] Pool =
        Pack.Tiers[Defect].Cards.Keys.Concat([Unknown, OldId, "CARD.T_OTHER_UNKNOWN"]).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20261108)]
    public void RandomDecksAndOffers_AreConsistent(int seed)
    {
        var rng = new Random(seed);
        var engine = new RecommendationEngine(Pack);
        var shop = new ShopScorer(Pack);
        var contexts = Enum.GetValues<OfferContext>();

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var deck = Enumerable.Range(0, rng.Next(0, 45)).Select(_ => RandomCard(rng)).ToArray();
            var maxHp = rng.Next(0, 90);
            var state = State(act: rng.Next(1, 5), deck: deck, hp: rng.Next(0, Math.Max(1, maxHp + 1)), maxHp: maxHp,
                gold: rng.Next(-10, 600), ascension: rng.Next(0, 11), removals: rng.Next(0, 4));

            var profile = DeckAnalyzer.AnalyzeDeck(state, Pack) with { Archetypes = RandomStatuses(rng) };
            Assert.All(profile.RoleGap.Values, g => Assert.InRange(g, 0.0, 1.0));

            var offer = Enumerable.Range(0, rng.Next(0, 5)).Select(_ => Parse(RandomCard(rng))).ToArray();
            var result = engine.EvaluateOffer(state, profile, offer, contexts[rng.Next(contexts.Length)]);

            ScoreAssert.Consistent(result.Skip);
            foreach (var card in result.Cards)
            {
                ScoreAssert.ConsistentCard(card);
                var w = Pack.Weights;
                Assert.True(card.Score.ValueOf(ComponentKeys.Build) <= w.BuildCap + 1e-9);
                Assert.True(card.Score.ValueOf(ComponentKeys.Combo) <= w.ComboCap + 1e-9);
                Assert.True(card.Score.ValueOf(ComponentKeys.Saturation) <= 0);
                if (card.Score.ValueOf(ComponentKeys.EngineMissing) < 0)
                {
                    Assert.Equal(0, card.Score.ValueOf(ComponentKeys.Build));
                    Assert.Equal(0, card.Score.ValueOf(ComponentKeys.Combo));
                }
            }
            Assert.Equal(Enumerable.Range(1, offer.Length), result.Cards.Select(c => c.Rank).Order());
            if (result.Best is { } best)
            {
                var chosen = result.Cards.Single(c => c.IsBest);
                Assert.Equal(best, chosen.Card);
                Assert.True(chosen.Confidence >= Pack.Weights.Skip.LowConfidence);
                Assert.True(chosen.Total >= result.Skip.Total + Pack.Weights.Skip.Margin);
            }

            var shopOffer = new ShopOffer(offer.Select(c => new ShopCardOffer(c, rng.Next(0, 200))).ToArray(),
                rng.Next(3) == 0 ? rng.Next(0, 200) : null);
            var shopResult = shop.Evaluate(state, profile, shopOffer);
            ScoreAssert.Consistent(shopResult.Skip);
            foreach (var item in shopResult.Items)
            {
                ScoreAssert.Consistent(item.Value);
                Assert.True(double.IsFinite(item.ValuePerGold));
            }
            Assert.True(shopResult.GoldLeft >= 0);
            Assert.True(shopResult.RecommendedPurchase.Sum(i => i.Price) <= Math.Max(0, state.Gold));
            var perGold = shopResult.Items.Select(i => i.ValuePerGold).ToArray();
            Assert.Equal(perGold.OrderByDescending(v => v), perGold);
        }
    }

    [Fact]
    public void ContractProfiles_AreConsistent()
    {
        var rng = new Random(7);
        var engine = new RecommendationEngine(Pack);
        for (var i = 0; i < 100; i++)
        {
            var state = State(act: rng.Next(1, 4), deck: Enumerable.Range(0, rng.Next(5, 30)).Select(_ => RandomCard(rng)));
            var profile = ProfileFor(state, Pack);
            var result = engine.EvaluateOffer(state, profile, [Parse(RandomCard(rng)), Parse(RandomCard(rng)), Parse(RandomCard(rng))]);
            foreach (var card in result.Cards) ScoreAssert.ConsistentCard(card);
        }
    }

    private static string RandomCard(Random rng)
    {
        var id = Pool[rng.Next(Pool.Length)];
        return rng.Next(4) == 0 ? id + "+1" : id;
    }

    private static IReadOnlyList<ArchetypeStatus> RandomStatuses(Random rng)
    {
        double[] specials = [0, 1, 0.7, double.NaN, 1.5, -0.2];
        return Pack.Archetypes
            .Where(_ => rng.Next(3) > 0)
            .Select(a => new ArchetypeStatus
            {
                Archetype = a,
                Commit = rng.Next(5) == 0 ? specials[rng.Next(specials.Length)] : rng.NextDouble(),
            })
            .OrderByDescending(s => double.IsNaN(s.Commit) ? -1 : s.Commit)
            .ToArray();
    }
}
