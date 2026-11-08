using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

/// <summary>
/// "초반엔 어떤 카드든 타격·수비보다 낫다": skip drops by <c>skip.basicPickBias[act] × min(1, basicShare / basicShareFull)</c>
/// (<see cref="ComponentKeys.SkipBasics"/>), so a fresh starting deck in act 1 picks any card without a real penalty.
/// </summary>
public class EarlyPickTests
{
    private const string DCard = "CARD.T_D_CARD";
    private const string GatedJunk = "CARD.T_GATED_JUNK";

    private static readonly string[] StarterDeck = [.. Enumerable.Repeat(Strike, 5), .. Enumerable.Repeat(Defend, 5)];

    /// <summary>The Defect test pack plus a plain D-tier card and a D-tier card that requires ORB.</summary>
    private static DataPack Pack(Weights? weights = null)
    {
        var pack = DefectPack(weights);
        var cards = pack.Tiers[Defect].Cards;
        cards[DCard] = new CardTierEntry { Tier = Tier.D, Type = "skill" };
        cards[GatedJunk] = new CardTierEntry { Tier = Tier.D, Type = "attack", Requires = ["ORB"] };
        return pack;
    }

    private static OfferEvaluation Offer(DataPack pack, int act, IEnumerable<string> deck, params string[] offered)
    {
        var state = State(act: act, deck: deck);
        var result = new RecommendationEngine(pack).EvaluateOffer(state, ProfileFor(state, pack), offered.Select(Parse).ToArray());
        ScoreAssert.Consistent(result.Skip);
        foreach (var c in result.Cards) ScoreAssert.ConsistentCard(c);
        return result;
    }

    [Fact]
    public void StarterDeck_CountsBasicCards()
    {
        var pack = Pack();
        var profile = DeckAnalyzer.AnalyzeDeck(State(deck: [.. StarterDeck, Filler, Blocker]), pack);
        Assert.Equal(10, profile.BasicCards);
        Assert.Equal(10.0 / 12, profile.BasicShare, 9);
        Assert.Equal(0, new DeckProfile().BasicShare);
    }

    [Fact]
    public void CatalogRarity_DecidesWhatIsBasic()
    {
        // Rarity "basic" from the catalog counts even without a Strike/Defend id; a catalog rarity overrides the id heuristic.
        var pack = new DataPack
        {
            Tiers = Pack().Tiers,
            Catalog = new CardCatalog
            {
                Cards = [new CatalogCard { Id = Filler, Rarity = "Basic" }, new CatalogCard { Id = Strike, Rarity = "Common" }],
            },
        };
        Assert.True(DeckAnalyzer.IsBasic(pack, Defect, Filler));
        Assert.False(DeckAnalyzer.IsBasic(pack, Defect, Strike));
        Assert.True(DeckAnalyzer.IsBasic(pack, Defect, Defend)); // no rarity known → Strike/Defend heuristic
        Assert.False(DeckAnalyzer.IsBasic(pack, Defect, Blocker));
    }

    [Fact]
    public void FreshStarterDeck_Act1_SkipSitsBelowDTier()
    {
        var pack = Pack();
        var w = pack.Weights;
        var skip = Offer(pack, 1, StarterDeck, DCard).Skip;
        Assert.Equal(-w.Skip.BasicPickBias[1], skip.ValueOf(ComponentKeys.SkipBasics));
        Assert.Equal(w.Skip.T0[1] - w.Skip.BasicPickBias[1], skip.Total, 9);
        Assert.True(skip.Total + w.Skip.Margin <= w.TierValue[Tier.D], $"skip {skip.Total} not below D {w.TierValue[Tier.D]}");
        Assert.Contains("10/10", skip.Components.Single(c => c.Key == ComponentKeys.SkipBasics).Detail);
    }

    [Theory]
    [InlineData(DCard)]  // D tier, nothing else
    [InlineData(Filler)] // C tier, no roles
    public void FreshStarterDeck_Act1_PicksAPlainCard(string card)
    {
        var r = Offer(Pack(), 1, StarterDeck, card);
        Assert.False(r.SkipRecommended);
        Assert.Equal(new CardRef(card), r.Best);
    }

    [Fact]
    public void FreshStarterDeck_Act1_RequiresGatedJunkCanStillBeSkipped()
    {
        // D (20) − "엔진 없음" (15) = 5 < skip 15: a real penalty still loses to skip.
        var r = Offer(Pack(), 1, StarterDeck, GatedJunk);
        Assert.True(r.Cards[0].Score.ValueOf(ComponentKeys.EngineMissing) < 0);
        Assert.True(r.SkipRecommended);
    }

    [Theory]
    [InlineData(1, 10, 10, 1.0)] // all basic: full bias
    [InlineData(1, 5, 10, 1.0)]  // half basic = basicShareFull: still full
    [InlineData(1, 2, 8, 0.5)]   // 20% basic: 0.2 / 0.5 of the bias
    [InlineData(2, 10, 10, 1.0)] // act 2: its own (smaller) bias
    [InlineData(3, 10, 10, 1.0)] // act 3: bias 0
    public void Bias_ScalesWithBasicShare(int act, int basics, int deckSize, double factor)
    {
        var pack = Pack();
        var w = pack.Weights;
        var deck = Enumerable.Repeat(Strike, basics).Concat(Enumerable.Repeat(Filler, deckSize - basics));
        var expected = -Weights.ForAct(w.Skip.BasicPickBias, act, 0.0) * Math.Min(factor, (double)basics / deckSize / w.Skip.BasicShareFull);
        var skip = Offer(pack, act, deck, Filler).Skip;
        Assert.Equal(expected, skip.ValueOf(ComponentKeys.SkipBasics), 9);
        Assert.Equal(act == 3 ? 0 : 1, skip.Components.Count(c => c.Key == ComponentKeys.SkipBasics));
    }

    [Fact]
    public void NoBasics_SkipUnchanged()
    {
        var pack = Pack();
        var skip = Offer(pack, 1, Enumerable.Repeat(Filler, 10), DCard).Skip;
        Assert.Equal(0, skip.ValueOf(ComponentKeys.SkipBasics));
        Assert.Equal(pack.Weights.Skip.T0[1], skip.Total);
    }

    [Fact]
    public void Act3_BloatedDeck_Unchanged()
    {
        // Act 3, 30 cards (still a third starter cards): skip = T0 + size, identical with the basic bias switched off entirely.
        var on = Pack();
        var offWeights = new Weights();
        offWeights.Skip.BasicPickBias.Clear();
        var off = Pack(offWeights);
        string[] deck = [.. StarterDeck, .. Enumerable.Repeat(Filler, 20)];
        var a = Offer(on, 3, deck, Filler, DCard, Blocker);
        var b = Offer(off, 3, deck, Filler, DCard, Blocker);
        Assert.Equal(b.Skip.Total, a.Skip.Total, 9);
        Assert.Equal(on.Weights.Skip.T0[3] + on.Weights.Skip.LambdaSize * (30 - on.Weights.IdealDeckSize[3]), a.Skip.Total, 9);
        Assert.Equal(b.SkipRecommended, a.SkipRecommended);
        Assert.True(a.SkipRecommended);
    }

    [Fact]
    public void DeckBelowIdealSize_SkipHasNoSizeTerm()
    {
        var skip = Offer(Pack(), 1, StarterDeck, Filler).Skip;
        Assert.Equal(0, skip.ValueOf(ComponentKeys.SkipDeckSize));
    }

    [Fact]
    public void LowConfidenceCard_BeatsSkipByATierStep_IsPickedAsEstimate()
    {
        // No confident card beats skip; LowConf (80) beats skip 45 by more than margin + lowConfidenceStep (15.5).
        var r = Offer(Pack(), 1, [Filler], LowConf, Strike);
        Assert.False(r.SkipRecommended);
        Assert.Equal(new CardRef(LowConf), r.Best);
        Assert.Equal(RecommendationConfidence.Estimate, r.Confidence);
    }

    [Fact]
    public void LowConfidenceCard_NeverReplacesAConfidentBest()
    {
        var r = Offer(Pack(), 1, [Filler], LowConf, Blocker);
        Assert.Equal(new CardRef(Blocker), r.Best);
    }

    [Fact]
    public void Validator_RejectsBadBasicWeights()
    {
        var w = new Weights();
        w.Skip.BasicPickBias[1] = -1;
        w.Skip.BasicShareFull = 0;
        w.Skip.LowConfidenceStep = -1;
        var errors = DataPackValidator.Validate(Pack(w)).Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.Where).ToList();
        Assert.Contains("weights.skip.basicPickBias", errors);
        Assert.Contains("weights.skip.basicShareFull", errors);
        Assert.Contains("weights.skip.lowConfidenceStep", errors);
    }
}
