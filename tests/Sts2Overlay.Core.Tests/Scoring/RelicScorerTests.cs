using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Scoring;

public class RelicScorerTests
{
    private const string Orb = "t.orb", Power = "t.power";
    private const string OrbRelic = "RELIC.T_ORB_RELIC", PowerRelic = "RELIC.T_POWER_RELIC", Plain = "RELIC.T_PLAIN";

    private static Archetype Build(string id, params KeyRelic[] relics)
        => new() { Id = id, Character = "CHARACTER.T", KeyRelics = [.. relics], Cards = new() { Core = ["CARD.T_" + id] } };

    private static DataPack Pack(RelicFit? fit = null, RelicStats? stats = null) => new()
    {
        Archetypes = [Build(Orb, new KeyRelic { Id = OrbRelic, W = 1.0 }), Build(Power, new KeyRelic { Id = PowerRelic, W = 0.4 })],
        RelicFit = fit,
        RelicStats = stats,
    };

    private static DeckProfile Profile(DataPack pack, params (string Id, double Commit, int Core)[] builds) => new()
    {
        Archetypes = builds.OrderByDescending(b => b.Commit)
            .Select(b => new ArchetypeStatus { Archetype = pack.Archetypes.Single(a => a.Id == b.Id), Commit = b.Commit, OwnedCore = b.Core, CoreTarget = 1 })
            .ToArray(),
    };

    private static double Rare => new Weights().Events.RelicTier["rare"];

    [Fact]
    public void NoLeaningBuild_IsExactlyTheRarityValue()
    {
        var pack = Pack();
        var e = new RelicScorer(pack).Score(new DeckProfile(), OrbRelic, "rare", null);
        Assert.Equal(Rare, e.Total);
        Assert.Single(e.Value.Components);
        Assert.Equal(1.0, e.FitMultiplier);

        // A build below the lean threshold (0.15) is ignored too.
        var weak = new RelicScorer(pack).Score(Profile(pack, (Orb, 0.1, 0)), OrbRelic, "rare", null);
        Assert.Equal(Rare, weak.Total);
    }

    [Fact]
    public void KeyRelicOfTheLeaningBuild_GetsTheKeyRelicBonusScaledByCommit()
    {
        var pack = Pack();
        var full = new RelicScorer(pack).Score(Profile(pack, (Orb, 1.0, 1)), OrbRelic, "rare", null);
        Assert.Equal(Rare * 1.5, full.Total, 9);                  // 1 + 0.5 × w(1.0)
        Assert.Equal(Orb, full.FitArchetype?.Id);
        Assert.Equal(Rare * 0.5, full.Value.Components.Single(c => c.Key == ComponentKeys.RelicFit).Value, 9);

        var half = new RelicScorer(pack).Score(Profile(pack, (Orb, 0.5, 0)), OrbRelic, "rare", null);
        Assert.Equal(Rare * (1 + 0.5 * 0.5), half.Total, 9);       // commit 0.5 → half the pull

        // Another build's key relic: no fit for this deck.
        var other = new RelicScorer(pack).Score(Profile(pack, (Orb, 1.0, 1)), PowerRelic, "rare", null);
        Assert.Equal(Rare, other.Total);
    }

    [Fact]
    public void TwoLeaningBuilds_ShareThePullByCommit()
    {
        var pack = Pack();
        // Σc = 1.6 > 1: pull = 0.8 × 0.5 / 1.6 for the orb relic (power build has no evidence on it → r = 1).
        var e = new RelicScorer(pack).Score(Profile(pack, (Orb, 0.8, 1), (Power, 0.8, 1)), OrbRelic, "rare", null);
        Assert.Equal(Rare * (1 + 0.8 * 0.5 / 1.6), e.Total, 9);
    }

    [Fact]
    public void MeasuredFit_UsesTheSquareRootOfTheFitRatio_AndOverridesTheKeyRelic()
    {
        var fit = DataPackLoader.LoadRelicFit("""
        { "archetypes": { "t.orb": {
            "RELIC.T_ORB_RELIC": { "use": true, "fitRatio": 1.96, "pickRateIn": 0.50, "pickRateOut": 0.40 },
            "RELIC.T_PLAIN":     { "use": true, "fitRatio": 0.49, "pickRateIn": 0.10, "pickRateOut": 0.50 } } } }
        """);
        var pack = Pack(fit);
        var scorer = new RelicScorer(pack);

        Assert.Equal(Rare * 1.4, scorer.Score(Profile(pack, (Orb, 1.0, 1)), OrbRelic, "rare", null).Total, 9);   // √1.96 (not the key relic's 1.5)
        var misfit = scorer.Score(Profile(pack, (Orb, 1.0, 1)), Plain, "rare", null);
        Assert.Equal(Rare * 0.7, misfit.Total, 9);                                                              // √0.49
        Assert.Equal(ScoringLabels.RelicMisfit, misfit.Value.Components.Single(c => c.Key == ComponentKeys.RelicFit).Label);
    }

    [Fact]
    public void MeasuredFit_WithoutFitRatio_UsesTheRateRatio_AndClamps()
    {
        var fit = DataPackLoader.LoadRelicFit("""
        { "archetypes": { "t.orb": { "RELIC.T_PLAIN": { "use": true, "pickRateIn": 0.9, "pickRateOut": 0.1 } } } }
        """);
        var pack = Pack(fit);
        Assert.Equal(Rare * 1.5, new RelicScorer(pack).Score(Profile(pack, (Orb, 1.0, 1)), Plain, "rare", null).Total, 9);  // √9 = 3 → 1.5
    }

    [Fact]
    public void CoreSplit_IsUsedOnlyWithEnoughDecisions()
    {
        var fit = DataPackLoader.LoadRelicFit("""
        { "archetypes": { "t.orb": {
            "RELIC.T_ORB_RELIC": { "use": true, "fitRatio": 1.0, "pickRateIn": 0.4,
                                   "pickRateCoreHeld": 0.64, "nCoreHeld": 30, "pickRateNoCore": 0.1, "nNoCore": 29 } } } }
        """);
        var pack = Pack(fit);
        var scorer = new RelicScorer(pack);
        var held = scorer.Score(Profile(pack, (Orb, 1.0, 1)), OrbRelic, "rare", null);
        Assert.Equal(Rare * Math.Sqrt(1.6), held.Total, 9);                                                     // 1.0 × 0.64/0.4
        Assert.Contains("코어 보유 시", held.Value.Components.Single(c => c.Key == ComponentKeys.RelicFit).Detail);
        // No core yet: the no-core rate rests on 29 decisions (< 30) → plain fitRatio 1.0, no fit component.
        var none = scorer.Score(Profile(pack, (Orb, 1.0, 0)), OrbRelic, "rare", null);
        Assert.Equal(Rare, none.Total, 9);
        Assert.Single(none.Value.Components);
    }

    [Fact]
    public void MeasuredFitUseFalse_FallsBackToTheKeyRelic()
    {
        var fit = DataPackLoader.LoadRelicFit("""
        { "archetypes": { "t.orb": { "RELIC.T_ORB_RELIC": { "use": false, "pickRateIn": 0.1, "pickRateOut": 0.9 } } } }
        """);
        var pack = Pack(fit);
        Assert.Equal(Rare * 1.5, new RelicScorer(pack).Score(Profile(pack, (Orb, 1.0, 1)), OrbRelic, "rare", null).Total, 9);
    }

    [Fact]
    public void Fit_MultipliesTheMeasuredBaseValue_AndComponentsSum()
    {
        var stats = DataPackLoader.LoadRelicStats("""
        { "shopMedianPickRate": 0.5, "ancientNeutralPickRate": 0.333, "relics": {
          "RELIC.T_ORB_RELIC": { "signal": "shop", "use": true, "pickRate": 0.6 } } }
        """);
        var pack = Pack(stats: stats);
        var e = new RelicScorer(pack).Score(Profile(pack, (Orb, 1.0, 1)), OrbRelic, "rare", stats);
        Assert.Equal(Rare * 1.2 * 1.5, e.Total, 9);
        ScoreAssert.Consistent(e.Value);
    }

    [Fact]
    public void UnknownRelic_IsTheDefaultValueWithoutFit()
    {
        var pack = Pack();
        var e = new RelicScorer(pack).Score(Profile(pack, (Orb, 1.0, 1)), null, null, null);
        Assert.Equal(new Weights().Events.RelicDefault, e.Total);
    }

    [Fact]
    public void Validator_RejectsUnknownBuildsAndBadRates()
    {
        var fit = DataPackLoader.LoadRelicFit("""
        { "archetypes": {
            "t.nope": { "RELIC.A": { "use": true, "pickRateIn": 0.5, "pickRateOut": 0.5 } },
            "t.orb":  { "BAD": { "pickRateIn": 1.5, "pickRateOut": 0.5, "pickRateCoreHeld": -1 } } } }
        """);
        var issues = DataPackValidator.Validate(Pack(fit)).Select(i => i.Where).ToList();
        Assert.Contains("relic-fit.archetypes[t.nope]", issues);
        Assert.Contains("relic-fit.archetypes[t.orb][BAD]", issues);
        Assert.Contains("relic-fit.archetypes[t.orb][BAD].use", issues);
        Assert.Contains("relic-fit.archetypes[t.orb][BAD].pickRateIn", issues);
        Assert.Contains("relic-fit.archetypes[t.orb][BAD].pickRateCoreHeld", issues);
    }
}
