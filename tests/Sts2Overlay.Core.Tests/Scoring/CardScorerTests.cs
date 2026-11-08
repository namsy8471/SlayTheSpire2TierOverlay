using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

public class CardScorerTests
{
    private static readonly DataPack Pack = DefectPack();
    private static readonly Weights W = Pack.Weights;

    private static CardEvaluation Score(string card, DeckProfile? profile = null, RunState? state = null, OfferContext context = OfferContext.CardReward, DataPack? pack = null)
    {
        var eval = new CardScorer(pack ?? Pack).Score(state ?? State(), profile ?? new DeckProfile(), Parse(card), context);
        ScoreAssert.ConsistentCard(eval);
        return eval;
    }

    // ---------------- base ----------------

    [Fact]
    public void Base_IsTierValue_AndNothingElseForEmptyDeck()
    {
        var e = Score(Blocker, state: State(act: 2));
        Assert.Equal(W.TierValue[Tier.B], e.Total);
        Assert.Single(e.Score.Components);
        Assert.Equal(Tier.B, e.DisplayTier);
        Assert.True(e.IsRated);
        Assert.False(e.IsEstimate);
        Assert.Equal(1.0, e.Confidence);
        Assert.Empty(e.Chips);
    }

    [Fact]
    public void Base_UsesActTierOverride()
    {
        var act1 = Score(AoeHit, state: State(act: 1));
        var act2 = Score(AoeHit, state: State(act: 2));
        Assert.Equal(W.TierValue[Tier.A], act1.Score.ValueOf(ComponentKeys.Base));
        Assert.Equal(Tier.A, act1.DisplayTier);
        Assert.Equal(W.TierValue[Tier.B], act2.Score.ValueOf(ComponentKeys.Base));
        Assert.Equal(Tier.B, act2.DisplayTier);
    }

    [Fact]
    public void Base_UpgradeAddsUpgradedDelta()
    {
        var e = Score(AoeHit + "+1", state: State(act: 2));
        Assert.Equal(W.TierValue[Tier.B] + 5, e.Score.ValueOf(ComponentKeys.Base));
        Assert.Equal(e.Total, e.Score.ValueOf(ComponentKeys.Base));
    }

    [Fact]
    public void Base_AliasResolvesToCurrentId()
    {
        var e = Score(OldId, state: State(act: 2));
        Assert.Equal(W.TierValue[Tier.B], e.Total);
        Assert.True(e.IsRated);
        Assert.Equal(OldId, e.Card.Id);
    }

    [Fact]
    public void UnratedCard_UsesUnknownTierAndConfidence()
    {
        var e = Score(Unknown);
        Assert.False(e.IsRated);
        Assert.True(e.IsEstimate);
        Assert.Equal(W.UnknownConfidence, e.Confidence);
        Assert.Equal(W.UnknownTier, e.DisplayTier);
        Assert.Equal(W.TierValue[W.UnknownTier], e.Total);
    }

    [Fact]
    public void PatchedAndLowConfidenceCards_AreEstimates()
    {
        Assert.True(Score(Patched).IsEstimate);
        Assert.True(Score(LowConf).IsEstimate);
        Assert.Equal(0.3, Score(LowConf).Confidence);
    }

    // ---------------- build ----------------

    [Theory]
    [InlineData(OrbCore, 1.0)]
    [InlineData(OrbEnabler, 0.7)]
    [InlineData(OrbPayoff, 0.9)]
    [InlineData(OrbSupport, 0.5)]
    public void Build_IsWArchTimesCommitTimesRoleWeight(string card, double roleWeight)
    {
        var profile = new DeckProfile { Provides = new Dictionary<string, int> { ["ORB"] = 1 } }.WithArchetypes(Pack, (OrbArchetype, 0.5));
        var e = Score(card, profile);
        var expected = W.WArch * 0.5 * roleWeight;
        Assert.Equal(expected, e.Score.ValueOf(ComponentKeys.Build), 9);
        var build = e.Score.Components.Single(c => c.Key == ComponentKeys.Build);
        Assert.Contains("구체 덱", build.Label);
        Assert.Contains("50%", build.Detail);
    }

    [Fact]
    public void Build_SumsArchetypesAndIsCapped()
    {
        // OrbCore is core of the orb deck (1.0) and payoff of the power deck (0.9): 20×1 + 20×0.9 = 38 → cap 25.
        var profile = new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 1.0), (PowerArchetype, 1.0));
        var e = Score(OrbCore, profile);
        Assert.Equal(W.BuildCap, e.Score.ValueOf(ComponentKeys.Build));

        var small = Score(OrbCore, new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 0.3), (PowerArchetype, 0.2)));
        Assert.Equal(W.WArch * (0.3 * 1.0 + 0.2 * 0.9), small.Score.ValueOf(ComponentKeys.Build), 9);
    }

    [Fact]
    public void Build_WeakRoleLowersCapTo12()
    {
        var profile = new DeckProfile { WeakRoles = [Roles.Block] }.WithArchetypes(Pack, (OrbArchetype, 1.0), (PowerArchetype, 1.0));
        var e = Score(OrbCore, profile);
        Assert.Equal(12, W.BuildCapWeak);
        Assert.Equal(W.BuildCapWeak, e.Score.ValueOf(ComponentKeys.Build));
    }

    [Fact]
    public void Build_ZeroForCardsOutsideArchetypes()
    {
        var e = Score(Blocker, new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 0.5)));
        Assert.Equal(0, e.Score.ValueOf(ComponentKeys.Build));
        Assert.DoesNotContain(e.Score.Components, c => c.Key == ComponentKeys.Build);
    }

    [Fact]
    public void BossReward_AddsSituationBonusNotBuild()
    {
        var profile = new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 0.5));
        var normal = Score(OrbCore, profile);
        var boss = Score(OrbCore, profile, context: OfferContext.BossReward);
        Assert.Equal(normal.Score.ValueOf(ComponentKeys.Build), boss.Score.ValueOf(ComponentKeys.Build));
        Assert.Equal(W.Situation.BossRewardBuildBonus * 0.5, boss.Score.ValueOf(ComponentKeys.Situation), 9);
        Assert.Equal(0, Score(Blocker, profile, context: OfferContext.BossReward).Score.ValueOf(ComponentKeys.Situation));
    }

    // ---------------- off-build ----------------

    [Fact]
    public void OffBuild_PenalisesCardsOutsideCommittedArchetype()
    {
        var profile = new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 0.8));
        var e = Score(Filler, profile);
        Assert.Equal(-W.OffBuildPenalty, e.Score.ValueOf(ComponentKeys.OffBuild));
        Assert.Equal(W.TierValue[Tier.C] - W.OffBuildPenalty, e.Total);
        Assert.Contains(e.Chips, c => c.Key == ComponentKeys.OffBuild && c.Label == "오프빌드");
    }

    [Fact]
    public void OffBuild_NotAppliedBelowCommitThreshold_OrToArchetypeCards()
    {
        Assert.Equal(0, Score(Filler, new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 0.6))).Score.ValueOf(ComponentKeys.OffBuild));
        Assert.Equal(0, Score(OrbSupport, new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 0.8))).Score.ValueOf(ComponentKeys.OffBuild));
        // A card of a non-committed archetype is still off-build relative to the committed one.
        Assert.Equal(-W.OffBuildPenalty,
            Score(PowerCore, new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 0.8), (PowerArchetype, 0.2))).Score.ValueOf(ComponentKeys.OffBuild));
    }

    [Fact]
    public void OffBuild_ExemptsCardsThatFixAWeakness()
    {
        var profile = new DeckProfile { RoleGap = new Dictionary<string, double> { [Roles.Block] = 0.5 } }
            .WithArchetypes(Pack, (OrbArchetype, 0.9));
        var e = Score(Blocker, profile);
        Assert.True(e.Score.ValueOf(ComponentKeys.Need) > 0);
        Assert.Equal(0, e.Score.ValueOf(ComponentKeys.OffBuild));
        // Same profile, card without block role: penalised.
        Assert.Equal(-W.OffBuildPenalty, Score(Filler, profile).Score.ValueOf(ComponentKeys.OffBuild));
    }

    // ---------------- combo ----------------

    [Fact]
    public void Combo_FullPairAddsW()
    {
        var e = Score(ComboA, new DeckProfile { Copies = new Dictionary<string, int> { [ComboB] = 1 } });
        Assert.Equal(6, e.Score.ValueOf(ComponentKeys.Combo));
        Assert.Contains("pair-ab", e.Score.Components.Single(c => c.Key == ComponentKeys.Combo).Detail);
    }

    [Fact]
    public void Combo_PairWithMissingPartnerAddsNothing()
    {
        Assert.Equal(0, Score(ComboB).Score.ValueOf(ComponentKeys.Combo));
    }

    [Fact]
    public void Combo_TrioPartialAddsHalf_FullAddsW()
    {
        var partial = Score(TrioA, new DeckProfile { Copies = new Dictionary<string, int> { [TrioB] = 1 } });
        Assert.Equal(3, partial.Score.ValueOf(ComponentKeys.Combo));
        var full = Score(TrioA, new DeckProfile { Copies = new Dictionary<string, int> { [TrioB] = 1, [TrioC] = 2 } });
        Assert.Equal(6, full.Score.ValueOf(ComponentKeys.Combo));
    }

    [Fact]
    public void Combo_IsCapped()
    {
        // pair-ab (6) + pair-ac (12) = 18 → cap 15.
        var e = Score(ComboA, new DeckProfile { Copies = new Dictionary<string, int> { [ComboB] = 1, [ComboC] = 1 } });
        Assert.Equal(W.ComboCap, e.Score.ValueOf(ComponentKeys.Combo));
    }

    // ---------------- requires gate ----------------

    [Fact]
    public void RequiresGate_ZeroesBuildAndCombo_AndAddsPenalty()
    {
        var withoutEngine = new DeckProfile { Copies = new Dictionary<string, int> { [OrbEnabler] = 1 } }
            .WithArchetypes(Pack, (OrbArchetype, 0.5));
        var e = Score(OrbPayoff, withoutEngine);
        Assert.Equal(0, e.Score.ValueOf(ComponentKeys.Build));
        Assert.Equal(0, e.Score.ValueOf(ComponentKeys.Combo));
        Assert.Equal(-W.RequiresPenalty, e.Score.ValueOf(ComponentKeys.EngineMissing));
        Assert.Contains(e.Chips, c => c.Label == "엔진 없음");

        var withEngine = withoutEngine with { Provides = new Dictionary<string, int> { ["ORB"] = 1 } };
        var ok = Score(OrbPayoff, withEngine);
        Assert.Equal(W.WArch * 0.5 * 0.9, ok.Score.ValueOf(ComponentKeys.Build), 9);
        Assert.Equal(6, ok.Score.ValueOf(ComponentKeys.Combo));
        Assert.Equal(0, ok.Score.ValueOf(ComponentKeys.EngineMissing));
    }

    [Fact]
    public void RequiresGate_AlsoDropsBossBonus()
    {
        var profile = new DeckProfile().WithArchetypes(Pack, (OrbArchetype, 0.5));
        var e = Score(OrbPayoff, profile, context: OfferContext.BossReward);
        Assert.Equal(0, e.Score.ValueOf(ComponentKeys.Situation));
    }

    // ---------------- need ----------------

    [Fact]
    public void Need_IsWNeedTimesGapUrgencyStrength()
    {
        var profile = new DeckProfile { RoleGap = new Dictionary<string, double> { [Roles.Aoe] = 0.8, [Roles.Block] = 0.4 } };
        var e = Score(AoeHit, profile, State(act: 2));
        Assert.Equal(W.WNeed * 0.8 * W.Urgency[2][Roles.Aoe] * 1.0, e.Score.ValueOf(ComponentKeys.Need), 9);
        Assert.Equal("광역 부족", e.Score.Components.Single(c => c.Key == ComponentKeys.Need).Label);
    }

    [Fact]
    public void LowHp_MovesExtraBlockNeedIntoSituation()
    {
        var profile = new DeckProfile { RoleGap = new Dictionary<string, double> { [Roles.Block] = 0.6 } };
        var healthy = Score(Blocker, profile, State(act: 2, hp: 50, maxHp: 50));
        var low = Score(Blocker, profile, State(act: 2, hp: 10, maxHp: 50));
        var need = W.WNeed * 0.6 * W.Urgency[2][Roles.Block] * 1.0;
        Assert.Equal(need, healthy.Score.ValueOf(ComponentKeys.Need), 9);
        Assert.Equal(0, healthy.Score.ValueOf(ComponentKeys.Situation));
        Assert.Equal(need, low.Score.ValueOf(ComponentKeys.Need), 9);
        Assert.Equal(need * W.Situation.LowHpBlockBoost, low.Score.ValueOf(ComponentKeys.Situation), 9);
        // Non-block cards get nothing from low HP.
        Assert.Equal(0, Score(AoeHit, profile, State(act: 2, hp: 10, maxHp: 50)).Score.ValueOf(ComponentKeys.Situation));
    }

    // ---------------- saturation ----------------

    [Fact]
    public void Saturation_ReducesOnlyPositiveSynergy()
    {
        // act 1, deck 20 → dilution present; SHIV: 4 producers vs 1 consumer → excess 4 − 2 = 2 → factor 0.7.
        var baseProfile = new DeckProfile
        {
            DeckSize = 20,
            RoleGap = new Dictionary<string, double> { [Roles.Frontload] = 1.0 },
            Consumes = new Dictionary<string, int> { ["SHIV"] = 1 },
        };
        var unsaturated = Score(ShivGen, baseProfile with { Provides = new Dictionary<string, int> { ["SHIV"] = 2 } });
        var saturated = Score(ShivGen, baseProfile with { Provides = new Dictionary<string, int> { ["SHIV"] = 4 } });

        Assert.Equal(0, unsaturated.Score.ValueOf(ComponentKeys.Saturation));
        var need = saturated.Score.ValueOf(ComponentKeys.Need);
        Assert.Equal(W.WNeed * 1.0 * W.Urgency[1][Roles.Frontload] * 0.5, need, 9);
        var factor = 1 - W.Saturation.PerExcess * 2;
        Assert.Equal(-need * (1 - factor), saturated.Score.ValueOf(ComponentKeys.Saturation), 9);
        Assert.Equal(unsaturated.Score.ValueOf(ComponentKeys.Dilution), saturated.Score.ValueOf(ComponentKeys.Dilution));
        Assert.True(saturated.Score.ValueOf(ComponentKeys.Dilution) < 0);
        Assert.Equal("자원 과잉", saturated.Score.Components.Single(c => c.Key == ComponentKeys.Saturation).Label);
    }

    [Fact]
    public void Saturation_NeverBelowMinFactor()
    {
        var profile = new DeckProfile
        {
            RoleGap = new Dictionary<string, double> { [Roles.Frontload] = 1.0 },
            Provides = new Dictionary<string, int> { ["SHIV"] = 30 },
        };
        var e = Score(ShivGen, profile);
        var need = e.Score.ValueOf(ComponentKeys.Need);
        Assert.Equal(-need * (1 - W.Saturation.MinFactor), e.Score.ValueOf(ComponentKeys.Saturation), 9);
    }

    [Fact]
    public void Saturation_WithoutPositiveSynergyIsZero()
    {
        var e = Score(ShivGen, new DeckProfile { Provides = new Dictionary<string, int> { ["SHIV"] = 10 } });
        Assert.Equal(0, e.Score.ValueOf(ComponentKeys.Saturation));
    }

    // ---------------- power saturation ----------------

    [Theory]
    [InlineData(5, 0)]
    [InlineData(6, -4)]
    [InlineData(8, -12)]
    public void PowerSaturation_PenalisesPowersAtOrAboveThreshold(int powers, double expected)
    {
        var e = Score(PowerCore, new DeckProfile { PowerCount = powers });
        Assert.Equal(expected, e.Score.ValueOf(ComponentKeys.PowerSaturation));
        Assert.Equal(0, Score(Blocker, new DeckProfile { PowerCount = powers }).Score.ValueOf(ComponentKeys.PowerSaturation));
    }

    // ---------------- dilution / duplicate ----------------

    [Theory]
    [InlineData(Blocker, 1.0)]
    [InlineData(OrbSupport, 0.5)]
    [InlineData(Drawer, 0.0)]
    public void Dilution_ScalesWithOversizeAndCycle(string card, double cycleFactor)
    {
        var e = Score(card, new DeckProfile { DeckSize = 20 }, State(act: 1));
        var expected = -W.LambdaDilution * (20 - W.IdealDeckSize[1]) * cycleFactor;
        Assert.Equal(expected, e.Score.ValueOf(ComponentKeys.Dilution), 9);
        if (expected != 0) Assert.Equal("덱 비대", e.Score.Components.Single(c => c.Key == ComponentKeys.Dilution).Label);
    }

    [Fact]
    public void Dilution_UsesActIdealSize()
    {
        Assert.Equal(0, Score(Blocker, new DeckProfile { DeckSize = 20 }, State(act: 2)).Score.ValueOf(ComponentKeys.Dilution));
        // Act 4 falls back to act 3 (ideal 25).
        Assert.Equal(-W.LambdaDilution * 5, Score(Blocker, new DeckProfile { DeckSize = 30 }, State(act: 4)).Score.ValueOf(ComponentKeys.Dilution), 9);
    }

    [Fact]
    public void Duplicate_IsRhoTimesCopiesTimesDupFactor()
    {
        var e = Score(Blocker, new DeckProfile { Copies = new Dictionary<string, int> { [Blocker] = 2 } });
        Assert.Equal(-W.RhoDuplicate * 2, e.Score.ValueOf(ComponentKeys.Duplicate));
        Assert.Equal("중복", e.Chips[0].Label);

        var ok = Score(DupeOk, new DeckProfile { Copies = new Dictionary<string, int> { [DupeOk] = 3 } });
        Assert.Equal(0, ok.Score.ValueOf(ComponentKeys.Duplicate));
        Assert.Single(ok.Score.Components);
    }

    // ---------------- chips / weights ----------------

    [Fact]
    public void Chips_AreTopThreeSignedReasons()
    {
        var profile = new DeckProfile
        {
            DeckSize = 18,
            RoleGap = new Dictionary<string, double> { [Roles.Block] = 0.5 },
            Copies = new Dictionary<string, int> { [Blocker] = 1 },
            PowerCount = 0,
        }.WithArchetypes(Pack, (OrbArchetype, 0.9));
        var e = Score(Blocker, profile, State(act: 1, hp: 5, maxHp: 50));
        Assert.Equal(3, e.Chips.Count);
        var expected = e.Score.TopReasons(3);
        Assert.Equal(expected.Select(c => (c.Key, c.Label, c.Value)), e.Chips.Select(c => (c.Key, c.Label, c.Value)));
        Assert.True(Math.Abs(e.Chips[0].Value) >= Math.Abs(e.Chips[1].Value));
    }

    [Fact]
    public void Weights_AreReadFromThePack()
    {
        var weights = new Weights { RhoDuplicate = 7, OffBuildPenalty = 3 };
        weights.TierValue[Tier.B] = 51;
        var pack = DefectPack(weights);
        var e = Score(Blocker, new DeckProfile { Copies = new Dictionary<string, int> { [Blocker] = 1 } }.WithArchetypes(pack, (OrbArchetype, 1.0)), pack: pack);
        Assert.Equal(51, e.Score.ValueOf(ComponentKeys.Base));
        Assert.Equal(-7, e.Score.ValueOf(ComponentKeys.Duplicate));
        Assert.Equal(-3, e.Score.ValueOf(ComponentKeys.OffBuild));
    }
}
