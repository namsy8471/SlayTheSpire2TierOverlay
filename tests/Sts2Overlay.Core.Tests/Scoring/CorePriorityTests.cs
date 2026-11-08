using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

/// <summary>
/// "코어 먼저, 그다음 필수 시너지" (<see cref="CorePriorityWeights"/>) on the small synthetic Defect pack:
/// synergy locked until a core (<c>core_missing</c>), gradual unlock when the core gets unlikely, the core bonus
/// (<c>core_priority</c>), staged essential → support synergy, and the build-role label.
/// </summary>
public class CorePriorityTests
{
    private static readonly Weights W = new();

    /// <summary>
    /// Test pack where the orb core is one tier BELOW its enabler and support (C 35 vs B 50), so without the rule the
    /// non-core cards win on tier alone. Orb build: core OrbCore, essential OrbEnabler + OrbPayoff, sub OrbSupport.
    /// </summary>
    private static DataPack LowCorePack(Weights? weights = null)
    {
        var pack = DefectPack(weights);
        var cards = pack.Tiers[Defect].Cards;
        cards[OrbCore].Tier = Tier.C;
        cards[OrbSupport].Tier = Tier.B;
        return pack;
    }

    private static CardEvaluation Score(DataPack pack, string card, DeckProfile profile, int act = 1, OfferContext context = OfferContext.CardReward)
    {
        var eval = new CardScorer(pack).Score(State(act: act), profile, Parse(card), context);
        ScoreAssert.ConsistentCard(eval);
        return eval;
    }

    private static DeckProfile Leaning(DataPack pack, double orbCommit, params string[] owned)
        => new DeckProfile { Copies = owned.ToDictionary(id => id, _ => 1, StringComparer.Ordinal) }.WithArchetypes(pack, (OrbArchetype, orbCommit));

    /// <summary>Same profile with every status' <see cref="ArchetypeStatus.CoreChance"/> set (as the detector would).</summary>
    private static DeckProfile WithCoreChance(DeckProfile p, double chance)
        => p with { Archetypes = p.Archetypes.Select(s => s with { CoreChance = chance }).ToArray() };

    private static ArchetypeSynergy SynergyIn(CardEvaluation e, string archetype) => e.Synergy.Single(s => s.ArchetypeId == archetype);

    // ---------------- 1. synergy locked until core ----------------

    [Fact]
    public void NoCore_SynergyLocked_CoreRanksAboveEnablerAndSupport()
    {
        var pack = LowCorePack();
        var profile = Leaning(pack, 0.5);

        var core = Score(pack, OrbCore, profile);
        var enabler = Score(pack, OrbEnabler, profile);
        var support = Score(pack, OrbSupport, profile);

        // core: 35 + build 20×0.5×1.0 + core_priority (starter 6 + lean 9 × ramp 1) = 60
        Assert.Equal(35 + 10 + 15, core.Total, 9);
        Assert.Equal(15, core.Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Equal(0, core.Score.ValueOf(ComponentKeys.CoreMissing), 9);
        // enabler: build 20×0.5×0.7 = 7 shown, all of it taken back by "코어 없음" → judged on its own (50)
        Assert.Equal(7, enabler.Score.ValueOf(ComponentKeys.Build), 9);
        Assert.Equal(-7, enabler.Score.ValueOf(ComponentKeys.CoreMissing), 9);
        Assert.Equal(50, enabler.Total, 9);
        Assert.Equal(0, SynergyIn(enabler, OrbArchetype).Value);
        Assert.Equal(7, SynergyIn(enabler, OrbArchetype).Ungated, 9);
        Assert.Equal(-5, support.Score.ValueOf(ComponentKeys.CoreMissing), 9);
        Assert.Equal(50, support.Total, 9);

        Assert.True(core.Total > enabler.Total);
        Assert.True(core.Total > support.Total);
        Assert.Contains(core.Chips, c => c.Key == ComponentKeys.CorePriority && c.Label == ScoringLabels.CorePriority);
        Assert.Contains("구체 덱", core.Score.Components.Single(c => c.Key == ComponentKeys.CorePriority).Detail);
        var missing = support.Score.Components.Single(c => c.Key == ComponentKeys.CoreMissing);
        Assert.Equal(ScoringLabels.CoreMissing, missing.Label);
        Assert.Equal("구체 덱 코어 전: 개별 성능으로 평가 · 먼저 노릴 코어: T_ORB_CORE", missing.Detail);
    }

    [Fact]
    public void WithoutTheRule_TheEnablerWouldWin()
    {
        var off = new Weights();
        off.CorePriority.Bonus = 0;
        off.CorePriority.StarterBonus = 0;
        off.CorePriority.NoCoreFactor.Clear();
        var pack = LowCorePack(off);
        var profile = Leaning(pack, 0.5);
        Assert.True(Score(pack, OrbEnabler, profile).Total > Score(pack, OrbCore, profile).Total);
        Assert.DoesNotContain(Score(pack, OrbEnabler, profile).Score.Components, c => c.Key == ComponentKeys.CoreMissing);
    }

    [Fact]
    public void NoCoreFactor_StaysConfigurable()
    {
        var w = new Weights();
        w.CorePriority.NoCoreFactor[ArchetypeRoles.Enabler] = 0.85;
        var pack = LowCorePack(w);
        var e = Score(pack, OrbEnabler, Leaning(pack, 0.5));
        Assert.Equal(-7 * 0.15, e.Score.ValueOf(ComponentKeys.CoreMissing), 9);
    }

    // ---------------- gradual unlock ----------------

    [Theory]
    [InlineData(0.9, 0.0)]  // core still likely: locked
    [InlineData(0.5, 0.0)]  // P = cap: exactly the lock threshold
    [InlineData(0.3, 0.2)]
    [InlineData(0.1, 0.4)]
    [InlineData(0.0, 0.5)]  // core will not come: half the synergy back, never more
    public void Unlock_IsCapMinusCoreChance(double coreChance, double unlock)
    {
        var pack = LowCorePack();
        var s = Status(pack, OrbArchetype, 0.5) with { CoreChance = coreChance };
        Assert.Equal(unlock, CardScorer.SynergyUnlock(s, act: 2, pack.Weights), 9);

        var e = Score(pack, OrbEnabler, WithCoreChance(Leaning(pack, 0.5), coreChance), act: 2);
        var syn = SynergyIn(e, OrbArchetype);
        Assert.Equal(syn.Ungated * unlock, syn.Value, 9);
        Assert.Equal(7 * unlock - 7, e.Score.ValueOf(ComponentKeys.CoreMissing), 9);
        if (unlock > 0)
        {
            Assert.Contains($"코어 전: 시너지 {unlock * 100:0}% 반영", e.Score.Components.Single(c => c.Key == ComponentKeys.CoreMissing).Detail);
        }
    }

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(2, 0.2)]
    [InlineData(3, 0.4)]
    public void Unlock_FallsBackToTheActRamp_WhenCoreChanceIsUnknown(int act, double unlock)
    {
        var pack = LowCorePack();
        Assert.Equal(unlock, CardScorer.SynergyUnlock(Status(pack, OrbArchetype, 0.5), act, pack.Weights), 9);
        var e = Score(pack, OrbEnabler, Leaning(pack, 0.5), act: act);
        Assert.Equal(7 * unlock, SynergyIn(e, OrbArchetype).Value, 9);
    }

    [Fact]
    public void Unlock_DoesNotGrowWithTheDecksOwnSynergyPile()
    {
        var pack = LowCorePack();
        double Ratio(double commit, params string[] owned)
        {
            var syn = SynergyIn(Score(pack, OrbSupport, WithCoreChance(Leaning(pack, commit, owned), 0.2), act: 3), OrbArchetype);
            return syn.Value / syn.Ungated;
        }
        var few = Ratio(0.2);
        Assert.Equal(few, Ratio(0.9, OrbEnabler, OrbPayoff), 9);
        Assert.Equal(0.3, few, 9);
    }

    [Fact]
    public void AtFullUnlock_TheCoreStillOutranksAOneTierBetterSameBuildCard()
    {
        var pack = LowCorePack();
        var profile = WithCoreChance(Leaning(pack, 0.5), 0.0); // unlock = cap 0.5
        var core = Score(pack, OrbCore, profile, act: 3);
        var enabler = Score(pack, OrbEnabler, profile, act: 3);
        // act 3: no starter, lean part = full bonus 15
        Assert.Equal(35 + 10 + 15, core.Total, 9);
        Assert.Equal(50 + 7 * 0.5, enabler.Total, 9);
        Assert.True(core.Total > enabler.Total);
    }

    // ---------------- 2. no core → no build commitment ----------------

    [Fact]
    public void OffBuild_OnlyAfterTheCommittedBuildSecuresItsCore()
    {
        var pack = LowCorePack();
        Assert.Equal(0, Score(pack, Filler, Leaning(pack, 0.9)).Score.ValueOf(ComponentKeys.OffBuild));
        // With the core the build is committed: off-build applies, capped so that with the secured-essential floor it stays below two tiers.
        var step = W.TierValue[Tier.A] - W.TierValue[Tier.B];
        Assert.Equal(-Math.Min(W.OffBuildPenalty, 2 * step - W.CorePriority.SecuredEssentialFloor - CardScorer.TwoTierMargin),
            Score(pack, Filler, Leaning(pack, 0.9, OrbCore)).Score.ValueOf(ComponentKeys.OffBuild), 9);
        // the detector's own commit is unchanged by the rule (forecast depends on it)
        var state = State(act: 2, deck: [OrbEnabler, OrbPayoff, OrbSupport]);
        Assert.True(ArchetypeDetector.Detect(state, pack).Single(s => s.Archetype.Id == OrbArchetype).Commit > 0.5);
    }

    [Fact]
    public void BossReward_BuildBonus_UsesTheCoreGatedCommit()
    {
        var pack = LowCorePack();
        Assert.Equal(0, Score(pack, OrbEnabler, Leaning(pack, 0.5), context: OfferContext.BossReward).Score.ValueOf(ComponentKeys.Situation), 9);
        Assert.Equal(W.Situation.BossRewardBuildBonus * 0.5,
            Score(pack, OrbCore, Leaning(pack, 0.5), context: OfferContext.BossReward).Score.ValueOf(ComponentKeys.Situation), 9);
        Assert.Equal(W.Situation.BossRewardBuildBonus * 0.5,
            Score(pack, OrbEnabler, Leaning(pack, 0.5, OrbCore), context: OfferContext.BossReward).Score.ValueOf(ComponentKeys.Situation), 9);
    }

    // ---------------- 3. core bonus ----------------

    [Theory]
    [InlineData(1, 0.10, 6.0)]                 // below leanMin: starter only
    [InlineData(2, 0.20, 6 + 9 * 0.2 / 0.4)]    // starter + half the lean part
    [InlineData(2, 0.90, 15.0)]                // capped at bonus
    [InlineData(3, 0.10, 0.0)]                 // no starter after act 2, not leaning
    [InlineData(3, 0.20, 15 * 0.2 / 0.4)]      // act 3: the lean part alone reaches the full bonus
    public void Bonus_IsStarterPlusLeanRamp(int act, double commit, double expected)
    {
        var pack = LowCorePack();
        Assert.Equal(expected, Score(pack, OrbCore, Leaning(pack, commit), act: act).Score.ValueOf(ComponentKeys.CorePriority), 9);
    }

    [Fact]
    public void Bonus_AppliesToAnyBuildEarly_OnlyLeaningBuildsLater()
    {
        // Power 0.6, orb 0.3 → orb is more than hybridMargin (0.15) behind: not leaning, starter part only.
        var pack = LowCorePack();
        var profile = new DeckProfile().WithArchetypes(pack, (PowerArchetype, 0.6), (OrbArchetype, 0.3));
        Assert.Equal(15, Score(pack, PowerCore, profile, act: 2).Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Equal(6, Score(pack, OrbCore, profile, act: 2).Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Equal(0, Score(pack, OrbCore, profile, act: 3).Score.ValueOf(ComponentKeys.CorePriority), 9);
        // Within the hybrid margin both builds lean.
        var hybrid = new DeckProfile().WithArchetypes(pack, (PowerArchetype, 0.5), (OrbArchetype, 0.4));
        Assert.Equal(15, Score(pack, OrbCore, hybrid, act: 3).Score.ValueOf(ComponentKeys.CorePriority), 9);
    }

    [Fact]
    public void StarterPart_StopsOnceAnyBuildIsCoreSecured_LeanPartStays()
    {
        var pack = LowCorePack();
        var copies = new Dictionary<string, int>(StringComparer.Ordinal) { [PowerCore] = 1 };
        // power build secured (owns PowerCore); orb far behind → orb core gets nothing in act 1
        var behind = new DeckProfile { Copies = copies }.WithArchetypes(pack, (PowerArchetype, 0.6), (OrbArchetype, 0.1));
        Assert.Equal(0, Score(pack, OrbCore, behind).Score.ValueOf(ComponentKeys.CorePriority), 9);
        // near-hybrid second build: lean part only (15 × 0.5 / 0.4 → capped 15 at commit ≥ leanFull)
        var hybrid = new DeckProfile { Copies = copies }.WithArchetypes(pack, (PowerArchetype, 0.6), (OrbArchetype, 0.5));
        Assert.Equal(15, Score(pack, OrbCore, hybrid).Score.ValueOf(ComponentKeys.CorePriority), 9);
        var hybridLow = new DeckProfile { Copies = copies }.WithArchetypes(pack, (PowerArchetype, 0.3), (OrbArchetype, 0.2));
        Assert.Equal(15 * 0.2 / 0.4, Score(pack, OrbCore, hybridLow).Score.ValueOf(ComponentKeys.CorePriority), 9);
    }

    [Fact]
    public void WeakDeck_ScalesTheBonus()
    {
        var pack = LowCorePack();
        var weak = Leaning(pack, 0.5) with { WeakRoles = [Roles.Block] };
        var e = Score(pack, OrbCore, weak);
        Assert.Equal(15 * W.CorePriority.WeakFactor, e.Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Contains("약점 우선", e.Score.Components.Single(c => c.Key == ComponentKeys.CorePriority).Detail);
    }

    [Fact]
    public void RequiresGate_StillWins_NoBonusForAnEngineLessCore()
    {
        var pack = LowCorePack();
        pack.Tiers[Defect].Cards[OrbCore].Requires = ["FROST"];
        var e = Score(pack, OrbCore, Leaning(pack, 0.5));
        Assert.Equal(-W.RequiresPenalty, e.Score.ValueOf(ComponentKeys.EngineMissing), 9);
        Assert.Equal(0, e.Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Equal(0, e.Score.ValueOf(ComponentKeys.Build), 9);
        Assert.All(e.Synergy, s => Assert.Equal(0, s.Value));
    }

    [Fact]
    public void CoreOfTwoLeaningBuilds_TakesTheMax_NotTheSum()
    {
        var pack = LowCorePack();
        pack.Archetypes.Single(a => a.Id == PowerArchetype).Cards.Core.Add(OrbCore);
        pack.Archetypes.Single(a => a.Id == PowerArchetype).Cards.Payoff.Remove(OrbCore);
        var profile = new DeckProfile().WithArchetypes(pack, (PowerArchetype, 0.3), (OrbArchetype, 0.4));
        Assert.Equal(15, Score(pack, OrbCore, profile).Score.ValueOf(ComponentKeys.CorePriority), 9);
    }

    [Fact]
    public void CoreTarget2_SecondCoreKeepsASmallerBonus_SynergyHalfUnlocked()
    {
        var pack = LowCorePack();
        var orb = pack.Archetypes.Single(a => a.Id == OrbArchetype);
        orb.Cards.Core.Add(PowerCore);
        orb.CoreTarget = 2;

        Assert.Equal(15, Score(pack, PowerCore, Leaning(pack, 0.5)).Score.ValueOf(ComponentKeys.CorePriority), 9);
        var oneOwned = Leaning(pack, 0.5, OrbCore);
        Assert.Equal(15 * 0.5, Score(pack, PowerCore, oneOwned).Score.ValueOf(ComponentKeys.CorePriority), 9);
        var enabler = Score(pack, OrbEnabler, oneOwned);
        Assert.Equal(-7 * 0.5, enabler.Score.ValueOf(ComponentKeys.CoreMissing), 9);
        Assert.Contains("코어 1/2: 시너지 50% 반영", enabler.Score.Components.Single(c => c.Key == ComponentKeys.CoreMissing).Detail);

        var both = Leaning(pack, 0.5, OrbCore, PowerCore);
        Assert.Equal(0, Score(pack, OrbCore, both).Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Equal(0, Score(pack, OrbEnabler, both).Score.ValueOf(ComponentKeys.CoreMissing), 9);
    }

    // ---------------- 4. staged synergy after core ----------------

    [Fact]
    public void AfterCore_EssentialsCountFully_SupportWaitsForTheEssentialTarget()
    {
        var pack = LowCorePack();
        var coreOnly = Leaning(pack, 0.5, OrbCore);

        var enabler = Score(pack, OrbEnabler, coreOnly);
        Assert.Equal(7, enabler.Score.ValueOf(ComponentKeys.Build), 9);
        Assert.Equal(0, enabler.Score.ValueOf(ComponentKeys.CoreMissing), 9);
        Assert.Equal(0, Score(pack, OrbCore, coreOnly).Score.ValueOf(ComponentKeys.CorePriority), 9);

        var support = Score(pack, OrbSupport, coreOnly);
        Assert.Equal(5 * W.CorePriority.SubBeforeEssential, support.Score.ValueOf(ComponentKeys.Build), 9);
        Assert.Contains("보조 시너지 40% (필수 시너지 0/2 전)", support.Score.Components.Single(c => c.Key == ComponentKeys.Build).Detail);
        Assert.True(enabler.Total > support.Total); // equal tier (B): essential first

        Assert.Equal(5 * 0.4, Score(pack, OrbSupport, Leaning(pack, 0.5, OrbCore, OrbEnabler)).Score.ValueOf(ComponentKeys.Build), 9);
        Assert.Equal(5, Score(pack, OrbSupport, Leaning(pack, 0.5, OrbCore, OrbEnabler, OrbPayoff)).Score.ValueOf(ComponentKeys.Build), 9);
    }

    [Fact]
    public void EssentialTarget_PerArchetypeOverride()
    {
        var pack = LowCorePack();
        pack.Archetypes.Single(a => a.Id == OrbArchetype).EssentialTarget = 1;
        Assert.Equal(5, Score(pack, OrbSupport, Leaning(pack, 0.5, OrbCore, OrbEnabler)).Score.ValueOf(ComponentKeys.Build), 9);
        pack.Archetypes.Single(a => a.Id == OrbArchetype).EssentialTarget = 0;
        Assert.Equal(5, Score(pack, OrbSupport, Leaning(pack, 0.5, OrbCore)).Score.ValueOf(ComponentKeys.Build), 9);
    }

    // ---------------- engine, skip, detector, validator, json ----------------

    [Fact]
    public void SkipScore_IsUnchangedByTheRule()
    {
        var on = LowCorePack();
        var offWeights = new Weights();
        offWeights.CorePriority.Bonus = 0;
        offWeights.CorePriority.NoCoreFactor.Clear();
        var off = LowCorePack(offWeights);
        var state = State(act: 2, deck: [OrbEnabler, OrbSupport]);
        Assert.Equal(new SkipScorer(off).Score(state, Leaning(off, 0.5)).Total, new SkipScorer(on).Score(state, Leaning(on, 0.5)).Total, 9);
    }

    [Fact]
    public void OfferRanking_CoreFirst_AndFlipHintNamesTheCore()
    {
        var pack = LowCorePack();
        var engine = new RecommendationEngine(pack);
        var r = engine.EvaluateOffer(State(act: 2), Leaning(pack, 0.5), [Parse(OrbEnabler), Parse(OrbCore), Parse(OrbSupport)]);
        Assert.Equal(new CardRef(OrbCore), r.Best);
        foreach (var c in r.Cards) ScoreAssert.ConsistentCard(c);

        var a = new ScoreBreakdown([new ScoreComponent(ComponentKeys.Base, "기본 C", 35), new ScoreComponent(ComponentKeys.CorePriority, ScoringLabels.CorePriority, 15)]);
        var b = new ScoreBreakdown([new ScoreComponent(ComponentKeys.Base, "기본 B", 48)]);
        Assert.Contains("코어 카드", RecommendationEngine.FlipHint(a, false, b, false));
    }

    [Fact]
    public void Detector_ReportsCoreAndEssentialProgress()
    {
        var pack = LowCorePack();
        var orb = pack.Archetypes.Single(a => a.Id == OrbArchetype);
        orb.Cards.Core.Add(PowerCore);
        orb.CoreTarget = 2;
        var state = State(act: 2, deck: [OrbCore, OrbEnabler]);
        var status = ArchetypeDetector.Detect(state, pack).Single(s => s.Archetype.Id == OrbArchetype);
        Assert.Equal(1, status.OwnedCore);
        Assert.Equal(2, status.CoreTarget);
        Assert.False(status.CoreSecured);
        Assert.Equal([PowerCore], status.MissingCore.Select(m => m.CardId));
        Assert.Equal(1, status.OwnedEssential);
        Assert.Equal(2, status.EssentialTarget);
        Assert.NotNull(status.CoreChance);

        // coreTarget larger than the core list is clamped; no core cards at all → always secured, no core chance
        orb.CoreTarget = 9;
        Assert.Equal(2, ArchetypeDetector.Detect(state, pack).Single(s => s.Archetype.Id == OrbArchetype).CoreTarget);
        orb.Cards.Core.Clear();
        var none = ArchetypeDetector.Detect(state, pack).Single(s => s.Archetype.Id == OrbArchetype);
        Assert.Equal(0, none.CoreTarget);
        Assert.True(none.CoreSecured);
        Assert.Null(none.CoreChance);
        // essential target is clamped to the essential cards (orb: enabler + payoff = 2)
        orb.EssentialTarget = 5;
        Assert.Equal(2, ArchetypeDetector.Detect(state, pack).Single(s => s.Archetype.Id == OrbArchetype).EssentialTarget);
    }

    [Fact]
    public void CoreChance_FallsAsTheRunGoesOn()
    {
        var pack = LowCorePack();
        double Chance(int act, int floor)
        {
            var state = State(act: act) with { Floor = floor };
            return ArchetypeDetector.Detect(state, pack).Single(s => s.Archetype.Id == OrbArchetype).CoreChance!.Value;
        }
        var early = Chance(1, 2);
        var mid = Chance(2, 22);
        var late = Chance(3, 42);
        Assert.InRange(early, 0, 1);
        Assert.True(early > mid && mid > late, $"{early} {mid} {late}");

        Assert.Equal((22, 3), ArchetypeDetector.RemainingRunRewards(State(act: 1) with { Floor = 2 }, W));
        Assert.Equal((15, 2), ArchetypeDetector.RemainingRunRewards(State(act: 2) with { Floor = 5 }, W)); // floor below act start → act start (15)
        Assert.Equal((0, 1), ArchetypeDetector.RemainingRunRewards(State(act: 3) with { Floor = 50 }, W));
    }

    [Fact]
    public void Validator_ChecksCoreAndEssentialSettings()
    {
        var pack = LowCorePack();
        pack.Archetypes[0].CoreTarget = 0;
        pack.Archetypes[1].CoreTarget = 5;
        pack.Archetypes[0].EssentialTarget = 9;
        pack.Archetypes[1].EssentialTarget = -1;
        pack.Weights.CorePriority.LeanFull = 0;
        pack.Weights.CorePriority.WeakFactor = 2;
        pack.Weights.CorePriority.Bonus = -1;
        pack.Weights.CorePriority.NoCoreFactor["core"] = 0.5;
        pack.Weights.CorePriority.NoCoreFactor[ArchetypeRoles.Payoff] = 1.5;
        pack.Weights.CorePriority.SubBeforeEssential = 1.5;
        pack.Weights.CorePriority.EssentialTarget = -1;
        pack.Weights.CorePriority.NoCoreMaxUnlock = 2;
        pack.Weights.CorePriority.NoCoreUnlockByAct[2] = -0.1;
        var issues = DataPackValidator.Validate(pack);
        void Has(ValidationSeverity sev, string where) => Assert.Contains(issues, i => i.Severity == sev && i.Where == where);
        Has(ValidationSeverity.Error, $"archetypes[{OrbArchetype}].coreTarget");
        Has(ValidationSeverity.Warning, $"archetypes[{PowerArchetype}].coreTarget");
        Has(ValidationSeverity.Warning, $"archetypes[{OrbArchetype}].essentialTarget");
        Has(ValidationSeverity.Error, $"archetypes[{PowerArchetype}].essentialTarget");
        Has(ValidationSeverity.Error, "weights.corePriority.leanFull");
        Has(ValidationSeverity.Error, "weights.corePriority.weakFactor");
        Has(ValidationSeverity.Error, "weights.corePriority.bonus");
        Has(ValidationSeverity.Warning, "weights.corePriority.noCoreFactor.core");
        Has(ValidationSeverity.Error, "weights.corePriority.noCoreFactor.payoff");
        Has(ValidationSeverity.Error, "weights.corePriority.subBeforeEssential");
        Has(ValidationSeverity.Error, "weights.corePriority.essentialTarget");
        Has(ValidationSeverity.Error, "weights.corePriority.noCoreMaxUnlock");
        Has(ValidationSeverity.Error, "weights.corePriority.noCoreUnlockByAct.2");
    }

    [Fact]
    public void WeightsJson_OverridesCorePriority()
    {
        var w = System.Text.Json.JsonSerializer.Deserialize<Weights>(
            """{ "corePriority": { "bonus": 9, "noCoreFactor": { "payoff": 0.2 }, "noCoreUnlockByAct": { "3": 0.3 }, "essentialTarget": 3, "subBeforeEssential": 0.25, "noCoreMaxUnlock": 0.4 } }""",
            DataPackLoader.JsonOptions)!;
        Assert.Equal(9, w.CorePriority.Bonus);
        Assert.Equal(0.2, w.CorePriority.NoCoreFactor[ArchetypeRoles.Payoff]);
        Assert.Equal(0, w.CorePriority.NoCoreFactor[ArchetypeRoles.Enabler]); // populated, not replaced
        Assert.Equal(0.3, w.CorePriority.NoCoreUnlockByAct[3]);
        Assert.Equal(0.2, w.CorePriority.NoCoreUnlockByAct[2]);
        Assert.Equal(3, w.CorePriority.EssentialTarget);
        Assert.Equal(0.25, w.CorePriority.SubBeforeEssential);
        Assert.Equal(0.4, w.CorePriority.NoCoreMaxUnlock);
        Assert.Equal(0.4, w.CorePriority.LeanFull);
        var a = System.Text.Json.JsonSerializer.Deserialize<Archetype>("""{ "id": "x", "essentialTarget": 1 }""", DataPackLoader.JsonOptions)!;
        Assert.Equal(1, a.EssentialTarget);
        Assert.Null(new Archetype().EssentialTarget);
    }

    // ---------------- build-role label ----------------

    [Fact]
    public void BuildRoles_NameEachBuildAndRole_HighestCommitFirst_MaxTwo()
    {
        var pack = LowCorePack();
        // OrbCore: orb core, power payoff (essential).
        var orbFirst = new DeckProfile().WithArchetypes(pack, (OrbArchetype, 0.5), (PowerArchetype, 0.2));
        var e = Score(pack, OrbCore, orbFirst);
        Assert.Equal([new BuildRole(OrbArchetype, "구체 덱", BuildRoleKind.Core), new BuildRole(PowerArchetype, "파워 덱", BuildRoleKind.Essential)], e.BuildRoles);
        Assert.Equal("구체 덱 코어 · 파워 덱 필수 시너지", ScoringLabels.BuildRoleText(e.BuildRoles));

        var powerFirst = new DeckProfile().WithArchetypes(pack, (PowerArchetype, 0.5), (OrbArchetype, 0.2));
        Assert.Equal("파워 덱 필수 시너지 · 구체 덱 코어", ScoringLabels.BuildRoleText(Score(pack, OrbCore, powerFirst).BuildRoles));

        Assert.Equal("구체 덱 서브 시너지", ScoringLabels.BuildRoleText(Score(pack, OrbSupport, orbFirst).BuildRoles));
        Assert.Equal("구체 덱 필수 시너지", ScoringLabels.BuildRoleText(Score(pack, OrbEnabler, orbFirst).BuildRoles));
        Assert.Empty(Score(pack, Filler, orbFirst).BuildRoles);
        Assert.Null(ScoringLabels.BuildRoleText([]));

        // a third build listing the card, with the lowest commit, is left out
        var third = new ArchetypeStatus
        {
            Archetype = new Archetype
            {
                Id = "defect.third", Character = Defect, Names = new() { ["kor"] = "셋째" },
                Cards = new ArchetypeCards { Core = [PowerCore], Support = [OrbCore] },
            },
            Commit = 0.1,
        };
        var two = new DeckProfile().WithArchetypes(pack, (OrbArchetype, 0.5), (PowerArchetype, 0.3));
        var three = two with { Archetypes = [.. two.Archetypes, third] };
        Assert.Equal(CardScorer.MaxBuildRoles, Score(pack, OrbCore, three).BuildRoles.Count);
        Assert.DoesNotContain(Score(pack, OrbCore, three).BuildRoles, r => r.ArchetypeId == "defect.third");
    }
}
