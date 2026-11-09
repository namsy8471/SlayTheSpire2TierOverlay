using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Tests.Map;
using Sts2Overlay.Core.Tests.Scoring;

namespace Sts2Overlay.Core.Tests.History;

public class PathStatsTests
{
    private static readonly Lazy<RunHistory[]> Runs = new(() => TestPaths.AllRuns.Select(RunHistoryReader.ReadFile).ToArray());

    private static PathFloor[] Floors() => Runs.Value.SelectMany(r => PathFloorExtractor.Extract(r)).ToArray();

    [Fact]
    public void Shrink_MovesFromPriorToMeasurementAsNGrows()
    {
        Assert.Equal(0.2, PathStats.Shrink(0.2, 0.3, n: 0, k: 8));
        Assert.Equal(0.3, PathStats.Shrink(0.2, 0.3, n: 5, k: 0));
        Assert.Equal(0.25, PathStats.Shrink(0.2, 0.3, n: 8, k: 8), 12);       // equal weight
        Assert.Equal(0.29, PathStats.Shrink(0.2, 0.3, n: 72, k: 8), 12);      // 72 real vs 8 pseudo
        Assert.Equal(0.2, PathStats.Shrink(0.2, double.NaN, n: 10, k: 8));    // nothing measured
    }

    [Fact]
    public void CombineSlope_WeightsByPrecision()
    {
        // Same uncertainty on both sides → halfway.
        var fit = new StrengthFit(100, 0.5, 0.2, 1, Slope: 0, SlopeSe: PathStats.SlopePriorSd);
        Assert.Equal(0.5, PathStats.CombineSlope(1.0, fit), 9);
        Assert.Equal(1.0, PathStats.CombineSlope(1.0, null));
        Assert.Equal(0.0, PathStats.CombineSlope(1.0, fit with { Slope = -3, SlopeSe = 0.01 })); // clamped at 0
    }

    [Fact]
    public void Percentile_Interpolates()
    {
        double[] xs = [0, 10, 20, 30, 40];
        Assert.Equal(20, PathStats.Percentile(xs, 0.5));
        Assert.Equal(36, PathStats.Percentile(xs, 0.9), 9);
        Assert.Equal(40, PathStats.Percentile(xs, 1));
        Assert.True(double.IsNaN(PathStats.Percentile([], 0.5)));
    }

    [Fact]
    public void Distinct_DropsCoopCopiesOfTheSameRun()
    {
        var runs = Runs.Value;
        var doubled = runs.Concat(runs.Select(r => r with { StartTime = r.StartTime + 3 })).ToArray();
        Assert.Equal(PathFloorExtractor.Distinct(runs).Count, PathFloorExtractor.Distinct(doubled).Count);
    }

    [Fact]
    public void Extract_ChainsHpAndMarksDeathsOnlyAtTheEndOfLostRuns()
    {
        foreach (var run in Runs.Value)
        {
            var rows = PathFloorExtractor.Extract(run);
            Assert.Equal(run.Nodes.Sum(n => n.Stats.Count(s => run.FindPlayer(s.PlayerId) is not null)), rows.Count);
            foreach (var player in rows.GroupBy(r => r.PlayerId))
            {
                var list = player.OrderBy(r => r.NodeIndex).ToArray();
                for (var i = 1; i < list.Length; i++)
                {
                    Assert.Equal(list[i - 1].HpAfter, list[i].HpBefore);
                    Assert.Equal(list[i - 1].GoldAfter, list[i].GoldBefore);
                }
            }
            foreach (var died in rows.Where(r => r.Died))
            {
                Assert.False(run.Win);
                Assert.Equal(run.Nodes.Count - 1, died.NodeIndex);
                Assert.Equal(0, died.HpAfter);
            }
            Assert.All(rows, r => Assert.True(double.IsNaN(r.DeckStrength))); // no pack given
        }
    }

    [Fact]
    public void Compute_ShrinksEveryHpValueBetweenPriorAndMeasurement()
    {
        var floors = Floors();
        var report = PathStats.Compute(floors, new PathWeights(), PathHpLoss.Legacy(), k: 8);

        Assert.Equal(PathFloorExtractor.Distinct(Runs.Value).Count, report.Runs);
        Assert.Equal(report.Runs, report.SoloRuns + report.CoopRuns);
        Assert.Equal(floors.Count(f => f.Type == MapNodeType.Monster && f.Act == 1), report.Find(PathStats.All, 1, "monster")!.N);
        Assert.NotNull(report.Fitted);

        var hp = report.Fits.Where(f => f.Name.Contains("HpLoss", StringComparison.Ordinal) && f.N > 0).ToArray();
        Assert.NotEmpty(hp);
        foreach (var f in hp)
        {
            var lo = Math.Min(f.Prior, f.Measured) - 0.0006; // values are rounded to 3 decimals
            var hi = Math.Max(f.Prior, f.Measured) + 0.0006;
            Assert.InRange(f.Fitted, lo, hi);
        }
        Assert.Equal(report.CoopSeparate, report.Fitted!.CoopHpLoss is not null);
        Assert.Equal(4, report.Coop.Count);
    }

    [Fact]
    public void Compute_SplitsHallwayFightsByEncounterPool()
    {
        var floors = Floors();
        var monsters = floors.Where(f => f.Type == MapNodeType.Monster).ToArray();
        Assert.Contains(monsters, f => f.EasyPool == true);
        Assert.Contains(monsters, f => f.EasyPool == false);
        Assert.All(monsters.Where(f => f.EasyPool == true), f => Assert.EndsWith("_WEAK", f.MonsterEncounterId));

        var report = PathStats.Compute(floors, new PathWeights(), PathHpLoss.Legacy(), k: 8);
        var easy = report.Find(PathStats.All, 1, PathStats.MonsterEasy);
        var hard = report.Find(PathStats.All, 1, PathStats.MonsterHard);
        Assert.Equal(monsters.Count(f => f.Act == 1 && f.EasyPool == true), easy?.N ?? 0);
        Assert.Equal(monsters.Count(f => f.Act == 1 && f.EasyPool == false), hard?.N ?? 0);

        // The fitted table carries both pools; each lies between the prior and its own measurement.
        var w = report.Fitted!;
        var table = report.CoopSeparate ? null : w.HpLoss;
        if (table is not null && easy is not null && hard is not null)
        {
            var prior = PathHpLoss.Legacy().Monster[1];
            Assert.InRange(table.Monster[1], Math.Min(prior, easy.DamageNormalizedMean) - 0.0006, Math.Max(prior, easy.DamageNormalizedMean) + 0.0006);
            Assert.InRange(table.HardMonster[1], Math.Min(prior, hard.DamageNormalizedMean) - 0.0006, Math.Max(prior, hard.DamageNormalizedMean) + 0.0006);
        }
    }

    [Fact]
    public void Compute_WithoutStrengths_KeepsThePriorSlope_UnlessOverridden()
    {
        var floors = Floors();
        var prior = new PathWeights { StrengthDamageSlope = 0.7 };
        Assert.Null(PathStats.Compute(floors, prior).Strength); // no pack → no strengths → nothing to fit
        Assert.Equal(0.7, PathStats.Compute(floors, prior).Fitted!.StrengthDamageSlope);
        Assert.Equal(0.0, PathStats.Compute(floors, prior, strengthSlope: 0).Fitted!.StrengthDamageSlope);
    }

    [Fact]
    public void Backtest_FixtureRuns_GivesSaneNumbers()
    {
        var runs = PathFloorExtractor.Distinct(Runs.Value);
        var pack = TestPacks.DefectPack();
        var floors = runs.SelectMany(r => PathFloorExtractor.Extract(r, pack)).ToArray();
        Assert.All(floors, f => Assert.InRange(f.DeckStrength, 0, 1));

        var fights = PathBacktest.Fights(floors, pack.Weights.Path);
        var acts = PathBacktest.Acts(runs, pack);
        var sum = PathBacktest.Summarize("x", fights, acts);

        Assert.Equal(floors.Count(f => f.Type is MapNodeType.Monster or MapNodeType.Elite or MapNodeType.Boss or MapNodeType.Unknown), sum.Fights);
        Assert.Equal(floors.Count(f => f.Died), sum.Deaths);
        Assert.All(fights, f => Assert.InRange(f.DeathChance, 0, 1));
        Assert.NotEmpty(acts);
        foreach (var a in acts)
        {
            Assert.InRange(a.PredictedArrival, 0, 1);
            Assert.InRange(a.PredictedBossHp, 0, 1);
            Assert.Equal(a.ReachedBoss, a.ActualBossHp is not null);
            Assert.False(a.ReachedBoss && a.DiedBeforeBoss);
        }
        Assert.Equal(acts.Count(a => a.ReachedBoss), sum.ReachedBoss);
    }

    [Fact]
    public void LeaveOneRunOut_NeverFitsOnTheHeldOutRun()
    {
        var runs = PathFloorExtractor.Distinct(Runs.Value).Take(6).ToArray();
        var pack = TestPacks.DefectPack();
        var floors = runs.SelectMany(r => PathFloorExtractor.Extract(r, pack)).ToArray();
        var allKeys = runs.Select(PathFloorExtractor.KeyOf).ToHashSet();
        var heldOut = new List<string>();

        var sum = PathBacktest.LeaveOneRunOut("loro", runs, floors, pack, train =>
        {
            var seen = train.Select(f => f.RunKey).ToHashSet();
            var missing = Assert.Single(allKeys.Except(seen)); // exactly one run is left out of every fit
            heldOut.Add(missing);
            return new PathWeights();
        });

        Assert.Equal(allKeys.OrderBy(k => k, StringComparer.Ordinal), heldOut.OrderBy(k => k, StringComparer.Ordinal)); // each run once
        Assert.Equal(floors.Count(f => f.Type is MapNodeType.Monster or MapNodeType.Elite or MapNodeType.Boss or MapNodeType.Unknown), sum.Fights);
        Assert.True(double.IsFinite(sum.BaselineBossHpMae) || sum.ReachedBoss == 0);
    }

    [Fact]
    public void FitLogistic_RecoversAKnownCenter()
    {
        // Deterministic sample: P(heal | hp) = 1 / (1 + exp((hp - 0.45) / 0.1)), 20 visits per HP step.
        var data = new List<(double, bool)>();
        for (var i = 0; i <= 100; i += 5)
        {
            var hp = i / 100.0;
            var p = 1 / (1 + Math.Exp((hp - 0.45) / 0.1));
            var heals = (int)Math.Round(20 * p);
            for (var j = 0; j < 20; j++) data.Add((hp, j < heals));
        }
        var (center, softness) = PathStats.FitLogistic(data);
        Assert.Equal(0.45, center, 2);
        Assert.Equal(0.1, softness, 2);
        Assert.True(double.IsNaN(PathStats.FitLogistic([(0.1, true), (0.9, true)]).Center)); // too few / no variation
    }

    [Fact]
    public void MatchPaths_FindsTheVisitedPrefixOnTheRealMap()
    {
        var map = MapFixture.Load("map_act2.json");
        var visited = map.Visited.Skip(1).ToArray(); // without the start
        var types = visited.Select(c => map.Nodes[c].Type).ToArray();

        var matches = PathBacktest.MatchPaths(map, types);

        Assert.Contains(matches, m => m.SequenceEqual(visited));
        Assert.All(matches, m => Assert.Equal(types, m.Select(c => map.Nodes[c].Type)));
    }

    [Fact]
    public void RealMap_ReplaysEveryStep_OnASmallMap()
    {
        // start → elite (0,1) or monster (1,1) → rest (0,2) → boss (0,3); the player took the monster.
        var map = new MapBuilder(3, 1, (0, 1), (1, 1))
            .Add(0, 1, MapNodeType.Elite, (0, 2))
            .Add(1, 1, MapNodeType.Monster, (0, 2))
            .Add(0, 2, MapNodeType.RestSite, (0, 3))
            .Build();
        var run = SyntheticRun(("ancient", 80), ("monster", 70), ("rest_site", 80), ("boss", 50));

        var r = PathBacktest.RealMap(run, 1, 0, map, TestMaps.Pack);

        Assert.NotNull(r);
        Assert.Equal([new MapCoord(1, 1), new MapCoord(0, 2), map.Boss], r!.Taken);
        Assert.Equal(3, r.Decisions.Count);
        Assert.Equal(1, r.RealChoices);
        Assert.Equal(2, r.TotalPaths);
        Assert.InRange(r.TakenRank, 1, 2);
        Assert.True(r.SurvivedAct);
        Assert.Equal(0.8, r.ActualBossHp!.Value, 9);
        Assert.Null(PathBacktest.RealMap(SyntheticRun(("ancient", 80), ("shop", 80)), 1, 0, map, TestMaps.Pack)); // does not fit
    }

    private static RunHistory SyntheticRun(params (string Type, int Hp)[] points) => new()
    {
        Seed = "TEST",
        Win = true,
        Players = [new RunPlayer { Id = 1, Character = TestPacks.Defect, Deck = [new DeckCard(new CardRef(TestPacks.Strike))] }],
        Nodes = points.Select((p, i) => new RunNode
        {
            ActIndex = 0,
            IndexInAct = i,
            Floor = i + 1,
            MapPointType = p.Type,
            Stats = [new PlayerNodeStats { PlayerId = 1, CurrentHp = p.Hp, MaxHp = 100, CurrentGold = 99 }],
        }).ToArray(),
    };
}
