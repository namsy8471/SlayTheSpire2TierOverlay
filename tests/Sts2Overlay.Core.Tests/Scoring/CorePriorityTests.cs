using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using static Sts2Overlay.Core.Tests.Scoring.TestPacks;

namespace Sts2Overlay.Core.Tests.Scoring;

/// <summary>"코어 먼저": <c>core_priority</c> / <c>core_missing</c> components (<see cref="CorePriorityWeights"/>).</summary>
public class CorePriorityTests
{
    private static readonly Weights W = new();

    /// <summary>
    /// Test pack where the orb core is one tier BELOW its enabler and support (C 35 vs B 50), so without the rule the
    /// non-core cards win on tier alone.
    /// </summary>
    private static DataPack LowCorePack(Weights? weights = null)
    {
        var pack = DefectPack(weights);
        var cards = pack.Tiers[Defect].Cards;
        cards[OrbCore].Tier = Tier.C;
        cards[OrbSupport].Tier = Tier.B;
        return pack;
    }

    private static CardEvaluation Score(DataPack pack, string card, DeckProfile profile, int act = 2, OfferContext context = OfferContext.CardReward)
    {
        var eval = new CardScorer(pack).Score(State(act: act), profile, Parse(card), context);
        ScoreAssert.ConsistentCard(eval);
        return eval;
    }

    private static DeckProfile Leaning(DataPack pack, double orbCommit, params string[] owned)
        => new DeckProfile { Copies = owned.ToDictionary(id => id, _ => 1, StringComparer.Ordinal) }.WithArchetypes(pack, (OrbArchetype, orbCommit));

    [Fact]
    public void LeaningCorelessDeck_CoreOneTierLower_RanksAboveEnablerAndSupport()
    {
        var pack = LowCorePack();
        var profile = Leaning(pack, 0.5);

        var core = Score(pack, OrbCore, profile);
        var enabler = Score(pack, OrbEnabler, profile);
        var support = Score(pack, OrbSupport, profile);

        // core: 35 + build 20×0.5×1.0 + core_priority 15 = 60
        Assert.Equal(35 + 10 + 15, core.Total, 9);
        Assert.Equal(15, core.Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Equal(0, core.Score.ValueOf(ComponentKeys.CoreMissing), 9);
        // enabler: 50 + build 20×0.5×0.7 = 7, of which (1 − 0.85) is lost while no core is owned
        Assert.Equal(7, enabler.Score.ValueOf(ComponentKeys.Build), 9);
        Assert.Equal(-7 * 0.15, enabler.Score.ValueOf(ComponentKeys.CoreMissing), 9);
        // support: 50 + build 20×0.5×0.5 = 5, keeps 0.6 of it
        Assert.Equal(-5 * 0.4, support.Score.ValueOf(ComponentKeys.CoreMissing), 9);

        Assert.True(core.Total > enabler.Total);
        Assert.True(core.Total > support.Total);
        Assert.Contains(core.Chips, c => c.Key == ComponentKeys.CorePriority && c.Label == ScoringLabels.CorePriority);
        Assert.Contains("구체 덱", core.Score.Components.Single(c => c.Key == ComponentKeys.CorePriority).Detail);
        var missing = support.Score.Components.Single(c => c.Key == ComponentKeys.CoreMissing);
        Assert.Equal(ScoringLabels.CoreMissing, missing.Label);
        Assert.Contains("코어 먼저: T_ORB_CORE", missing.Detail);
    }

    [Fact]
    public void WithoutTheRule_TheEnablerWouldWin()
    {
        var off = new Weights();
        off.CorePriority.Bonus = 0;
        off.CorePriority.NoCoreFactor.Clear();
        var pack = LowCorePack(off);
        var profile = Leaning(pack, 0.5);
        Assert.True(Score(pack, OrbEnabler, profile).Total > Score(pack, OrbCore, profile).Total);
        Assert.DoesNotContain(Score(pack, OrbEnabler, profile).Score.Components, c => c.Key == ComponentKeys.CoreMissing);
    }

    [Fact]
    public void OnceACoreIsOwned_BonusAndDiscountDisappear()
    {
        var pack = LowCorePack();
        var profile = Leaning(pack, 0.5, OrbCore);

        var core = Score(pack, OrbCore, profile);
        Assert.Equal(0, core.Score.ValueOf(ComponentKeys.CorePriority), 9);
        var enabler = Score(pack, OrbEnabler, profile);
        Assert.Equal(0, enabler.Score.ValueOf(ComponentKeys.CoreMissing), 9);
        Assert.Equal(7, enabler.Score.ValueOf(ComponentKeys.Build), 9);
    }

    [Fact]
    public void Bonus_RampsWithCommit_AndNeedsLeanMin()
    {
        var pack = LowCorePack();
        Assert.Equal(15 * 0.2 / 0.4, Score(pack, OrbCore, Leaning(pack, 0.2)).Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Equal(15, Score(pack, OrbCore, Leaning(pack, 0.9)).Score.ValueOf(ComponentKeys.CorePriority), 9);
        // below leanMin (0.15) in act 2: no bonus at all
        Assert.Equal(0, Score(pack, OrbCore, Leaning(pack, 0.1)).Score.ValueOf(ComponentKeys.CorePriority), 9);
    }

    [Fact]
    public void WeakDeck_HalvesTheBonus()
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
    }

    [Fact]
    public void OnlyTheLeaningBuild_GetsTheBonus()
    {
        // Power build at 0.6, orb build at 0.3 → orb is more than hybridMargin (0.15) behind: not leaning.
        var pack = LowCorePack();
        var profile = new DeckProfile().WithArchetypes(pack, (PowerArchetype, 0.6), (OrbArchetype, 0.3));

        Assert.Equal(15, Score(pack, PowerCore, profile).Score.ValueOf(ComponentKeys.CorePriority), 9);
        var orbCore = Score(pack, OrbCore, profile);
        Assert.Equal(0, orbCore.Score.ValueOf(ComponentKeys.CorePriority), 9);
        // OrbCore is the power build's payoff and that build has no core yet → its power-build value is discounted.
        Assert.Equal(-20 * 0.6 * 0.9 * (1 - 0.4), orbCore.Score.ValueOf(ComponentKeys.CoreMissing), 9);

        // Within the hybrid margin both builds lean.
        var hybrid = new DeckProfile().WithArchetypes(pack, (PowerArchetype, 0.5), (OrbArchetype, 0.4));
        Assert.Equal(15, Score(pack, OrbCore, hybrid).Score.ValueOf(ComponentKeys.CorePriority), 9);
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
    public void StarterBonus_OnlyInAct1_WhenNothingLeans()
    {
        var pack = LowCorePack();
        var early = Leaning(pack, 0.1);
        Assert.Equal(W.CorePriority.StarterBonus, Score(pack, OrbCore, early, act: 1).Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Equal(0, Score(pack, OrbCore, early, act: 2).Score.ValueOf(ComponentKeys.CorePriority), 9);
        // a leaning build switches the starter bonus off for the other builds' cores
        var leaning = new DeckProfile().WithArchetypes(pack, (OrbArchetype, 0.5), (PowerArchetype, 0.1));
        Assert.Equal(0, Score(pack, PowerCore, leaning, act: 1).Score.ValueOf(ComponentKeys.CorePriority), 9);
    }

    [Fact]
    public void CoreTarget2_KeepsPushingUntilTwoCoresAreOwned_DiscountStopsAtTheFirst()
    {
        var pack = LowCorePack();
        var orb = pack.Archetypes.Single(a => a.Id == OrbArchetype);
        orb.Cards.Core.Add(PowerCore);
        orb.CoreTarget = 2;
        var oneOwned = Leaning(pack, 0.5, OrbCore);
        Assert.Equal(15, Score(pack, PowerCore, oneOwned).Score.ValueOf(ComponentKeys.CorePriority), 9);
        Assert.Equal(0, Score(pack, OrbEnabler, oneOwned).Score.ValueOf(ComponentKeys.CoreMissing), 9);
        var both = Leaning(pack, 0.5, OrbCore, PowerCore);
        Assert.Equal(0, Score(pack, OrbCore, both).Score.ValueOf(ComponentKeys.CorePriority), 9);
    }

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
    public void Detector_ReportsOwnedAndMissingCore()
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
        Assert.All(status.MissingCore, m => Assert.Equal(ArchetypeRoles.Core, m.Role));

        // coreTarget larger than the core list is clamped; no core cards at all → always secured
        orb.CoreTarget = 9;
        Assert.Equal(2, ArchetypeDetector.Detect(state, pack).Single(s => s.Archetype.Id == OrbArchetype).CoreTarget);
        orb.Cards.Core.Clear();
        var none = ArchetypeDetector.Detect(state, pack).Single(s => s.Archetype.Id == OrbArchetype);
        Assert.Equal(0, none.CoreTarget);
        Assert.True(none.CoreSecured);
    }

    [Fact]
    public void Validator_ChecksCoreTargetAndCorePriorityWeights()
    {
        var pack = LowCorePack();
        pack.Archetypes[0].CoreTarget = 0;
        pack.Archetypes[1].CoreTarget = 5;
        pack.Weights.CorePriority.LeanFull = 0;
        pack.Weights.CorePriority.WeakFactor = 2;
        pack.Weights.CorePriority.Bonus = -1;
        pack.Weights.CorePriority.NoCoreFactor["core"] = 0.5;
        pack.Weights.CorePriority.NoCoreFactor[ArchetypeRoles.Payoff] = 1.5;
        var issues = DataPackValidator.Validate(pack);
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where.EndsWith(".coreTarget", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning && i.Where == $"archetypes[{PowerArchetype}].coreTarget");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.corePriority.leanFull");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.corePriority.weakFactor");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.corePriority.bonus");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning && i.Where == "weights.corePriority.noCoreFactor.core");
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Where == "weights.corePriority.noCoreFactor.payoff");
    }

    [Fact]
    public void WeightsJson_OverridesCorePriority()
    {
        var w = System.Text.Json.JsonSerializer.Deserialize<Weights>(
            """{ "corePriority": { "bonus": 9, "noCoreFactor": { "payoff": 0.2 } } }""", DataPackLoader.JsonOptions)!;
        Assert.Equal(9, w.CorePriority.Bonus);
        Assert.Equal(0.2, w.CorePriority.NoCoreFactor[ArchetypeRoles.Payoff]);
        Assert.Equal(0.85, w.CorePriority.NoCoreFactor[ArchetypeRoles.Enabler]); // populated, not replaced
        Assert.Equal(0.4, w.CorePriority.LeanFull);
    }
}
