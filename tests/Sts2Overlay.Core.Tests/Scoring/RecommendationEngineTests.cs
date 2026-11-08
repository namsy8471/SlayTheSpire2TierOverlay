using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

public class RecommendationEngineTests
{
    private static readonly DataPack Pack = DefectPack();

    private static OfferEvaluation Evaluate(int act, string[] cards, DeckProfile? profile = null, DataPack? pack = null, RunState? state = null)
    {
        var engine = new RecommendationEngine(pack ?? Pack);
        var result = engine.EvaluateOffer(state ?? State(act: act), profile ?? new DeckProfile(), cards.Select(Parse).ToArray());
        AssertConsistent(result);
        return result;
    }

    private static void AssertConsistent(OfferEvaluation result)
    {
        ScoreAssert.Consistent(result.Skip);
        foreach (var c in result.Cards) ScoreAssert.ConsistentCard(c);
        Assert.Equal(result.Cards.Count, result.Cards.Select(c => c.Rank).Distinct().Count());
        Assert.Equal(result.SkipRecommended ? 0 : 1, result.Cards.Count(c => c.IsBest));
        Assert.Equal(result.SkipRecommended, result.Best is null);
        if (result.Best is { } best) Assert.Equal(best, result.Cards.Single(c => c.IsBest).Card);
    }

    private static Weights WithT0(double t0Act1)
    {
        var w = new Weights();
        w.Skip.T0[1] = t0Act1;
        return w;
    }

    [Fact]
    public void SkipRecommended_WhenAllCardsAreWeak()
    {
        var r = Evaluate(3, [Strike, Defend, Filler]);
        Assert.True(r.SkipRecommended);
        Assert.Null(r.Best);
        Assert.Equal(58, r.Skip.Total);
        Assert.Equal(RecommendationConfidence.Sure, r.Confidence); // skip 58 vs best card 35
        Assert.Null(r.FlipHint);
    }

    [Fact]
    public void BestCard_RankedByTotal()
    {
        var r = Evaluate(1, [Blocker, Star, Filler]);
        Assert.False(r.SkipRecommended);
        Assert.Equal(new CardRef(Star), r.Best);
        Assert.Equal(new[] { 2, 1, 3 }, r.Cards.Select(c => c.Rank));
        Assert.Equal(new[] { Blocker, Star, Filler }, r.Cards.Select(c => c.Card.Id)); // offer order kept
        Assert.Equal(RecommendationConfidence.Sure, r.Confidence);
        Assert.NotNull(r.Profile);
    }

    [Theory]
    [InlineData(49.6, true)]  // 50 < 49.6 + 0.5
    [InlineData(49.5, false)] // 50 ≥ 49.5 + 0.5
    public void CardMustBeatSkipByMargin(double t0, bool skip)
    {
        var r = Evaluate(1, [Blocker], pack: DefectPack(WithT0(t0)));
        Assert.Equal(skip, r.SkipRecommended);
        Assert.Equal(RecommendationConfidence.Close, r.Confidence);
        Assert.Contains("스킵", r.FlipHint);
    }

    [Fact]
    public void LowConfidenceCard_CannotBeBest()
    {
        var r = Evaluate(1, [LowConf, Blocker]);
        Assert.Equal(1, r.Cards[0].Rank);
        Assert.False(r.Cards[0].IsBest);
        Assert.Equal(new CardRef(Blocker), r.Best);
        Assert.Equal(RecommendationConfidence.Normal, r.Confidence); // 50 vs skip 45: gap 5
    }

    [Fact]
    public void OnlyLowConfidenceCardsBeatSkip_RecommendsSkipAsEstimate()
    {
        var r = Evaluate(1, [LowConf, Strike]);
        Assert.True(r.SkipRecommended);
        Assert.Equal(RecommendationConfidence.Estimate, r.Confidence);
    }

    [Fact]
    public void UnratedCard_CannotBeBest()
    {
        var r = Evaluate(1, [Unknown], pack: DefectPack(WithT0(20)));
        Assert.True(r.SkipRecommended);
        Assert.False(r.Cards[0].IsRated);
        Assert.Equal(RecommendationConfidence.Estimate, r.Confidence);
    }

    [Fact]
    public void EstimateCardAsBest_GivesEstimateConfidence()
    {
        var r = Evaluate(1, [Patched, Filler]);
        Assert.Equal(new CardRef(Patched), r.Best);
        Assert.Equal(RecommendationConfidence.Estimate, r.Confidence);
    }

    [Fact]
    public void NormalConfidence_BetweenGaps()
    {
        var r = Evaluate(3, [OrbCore]); // 65 vs skip 58
        Assert.Equal(RecommendationConfidence.Normal, r.Confidence);
        Assert.Null(r.FlipHint);
    }

    [Fact]
    public void Ties_BrokenByOfferOrder()
    {
        var r = Evaluate(1, [Blocker, DupeOk]);
        Assert.Equal(new CardRef(Blocker), r.Best);
        Assert.Equal(new[] { 1, 2 }, r.Cards.Select(c => c.Rank));
        Assert.Equal(RecommendationConfidence.Close, r.Confidence);
        Assert.NotNull(r.FlipHint);

        var swapped = Evaluate(1, [DupeOk, Blocker]);
        Assert.Equal(new CardRef(DupeOk), swapped.Best);
        Assert.Equal(new[] { 1, 2 }, swapped.Cards.Select(c => c.Rank));
    }

    [Fact]
    public void CloseCall_FlipHintNamesTheDecidingRole()
    {
        // Blocker 50 + need 25×0.1×0.8 = 52 vs DupeOk 50 → Close, decided by block need.
        var profile = new DeckProfile { RoleGap = new Dictionary<string, double> { [Roles.Block] = 0.1 } };
        var r = Evaluate(1, [DupeOk, Blocker], profile);
        Assert.Equal(new CardRef(Blocker), r.Best);
        Assert.Equal(RecommendationConfidence.Close, r.Confidence);
        Assert.Equal("덱에 방어 카드가 더 있으면 순위가 바뀝니다.", r.FlipHint);
    }

    [Fact]
    public void CloseCall_DecidedByBaseTier()
    {
        // Star 80 vs OrbCore 65 + build 20×0.65 = 78 → Star wins by 2; the base difference (15) is the largest.
        // Core priority is switched off: with the default weights the orb build's missing core gets "코어 확보" +15 and
        // wins 93 vs 80 instead (see CorePriorityTests); this test is about the base-tier flip hint.
        var w = new Weights();
        w.CorePriority.Bonus = 0;
        var profile = new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 0.65));
        var r = Evaluate(1, [OrbCore, Star], profile, DefectPack(w));
        Assert.Equal(new CardRef(Star), r.Best);
        Assert.Equal(RecommendationConfidence.Close, r.Confidence);
        Assert.Contains("기본 티어", r.FlipHint);
    }

    [Theory]
    [InlineData(10.0, 0.0, "낮았다면")]
    [InlineData(0.0, 10.0, "높아지면")]
    public void FlipHint_BuildTemplates(double firstBuild, double secondBuild, string expected)
    {
        var a = new ScoreBreakdown([new ScoreComponent(ComponentKeys.Base, "기본 B", 52 - firstBuild / 2), new ScoreComponent(ComponentKeys.Build, "구체 덱 시너지", firstBuild)]);
        var b = new ScoreBreakdown([new ScoreComponent(ComponentKeys.Base, "기본 B", 50 - secondBuild / 2), new ScoreComponent(ComponentKeys.Build, "구체 덱 시너지", secondBuild)]);
        var hint = RecommendationEngine.FlipHint(a, false, b, false);
        Assert.Contains("빌드 몰입도", hint);
        Assert.Contains(expected, hint);
    }

    [Fact]
    public void EmptyOffer_RecommendsSkip()
    {
        var r = Evaluate(1, []);
        Assert.True(r.SkipRecommended);
        Assert.Empty(r.Cards);
        Assert.Equal(RecommendationConfidence.Sure, r.Confidence);
    }

    [Fact]
    public void FlipHint_DefaultTemplateUsesComponentLabel()
    {
        var a = new ScoreBreakdown([new ScoreComponent(ComponentKeys.Base, "기본 B", 50), new ScoreComponent(ComponentKeys.Dilution, "덱 비대", -1)]);
        var b = new ScoreBreakdown([new ScoreComponent(ComponentKeys.Base, "기본 B", 50), new ScoreComponent(ComponentKeys.Dilution, "덱 비대", -4)]);
        var hint = RecommendationEngine.FlipHint(a, false, b, false);
        Assert.Contains("덱 비대", hint);
        Assert.Contains("3", hint);
    }

    [Fact]
    [Trait("Category", "NeedsArchetypes")]
    public void EvaluateOffer_FullPath_UsesDetectedArchetypes()
    {
        var state = State(act: 2, deck: [OrbCore, OrbEnabler, OrbSupport, Strike, Strike, Strike, Defend, Defend, Defend]);
        var engine = new RecommendationEngine(Pack);
        var r = engine.EvaluateOffer(state, [new CardRef(OrbPayoff), new CardRef(Filler), new CardRef(Blocker)]);
        AssertConsistent(r);
        Assert.NotNull(r.Profile);
        Assert.Equal(OrbArchetype, r.Profile!.Primary!.Archetype.Id);
        Assert.Equal((1.0 + 0.7 + 0.5) / 4.0, r.Profile.MaxCommit, 9);
        Assert.True(r.Cards[0].Score.ValueOf(ComponentKeys.Build) > 0);

        var manual = engine.EvaluateOffer(state, ProfileFor(state, Pack), [new CardRef(OrbPayoff), new CardRef(Filler), new CardRef(Blocker)]);
        Assert.Equal(manual.Cards.Select(c => c.Total), r.Cards.Select(c => c.Total));
    }

    [Fact]
    [Trait("Category", "NeedsArchetypes")]
    public void Archetypes_ReportsEveryArchetypeOfTheCharacter()
    {
        var state = State(deck: [OrbCore, OrbEnabler]);
        var report = new RecommendationEngine(Pack).Archetypes(state, 5);
        Assert.Equal(2, report.Statuses.Count);
        Assert.Equal(OrbArchetype, report.Statuses[0].Archetype.Id);
    }
}
