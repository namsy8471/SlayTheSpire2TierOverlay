using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Tests.Scoring;
using Xunit.Abstractions;
using static Sts2Overlay.Core.Tests.Map.TestMaps;

namespace Sts2Overlay.Core.Tests.Map;

public class PathAdvisorTests
{
    private readonly ITestOutputHelper _out;

    public PathAdvisorTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static PathRecommendation Recommend(RunState state, MapGraph map, DataPack? pack = null)
    {
        var r = new PathAdvisor(pack ?? Pack).Recommend(state, map);
        PathAssert.WellFormed(map, r);
        return r;
    }

    /// <summary>start → elite (0,1) or monster (1,1) → rest (0,2) → boss (0,3).</summary>
    private static MapGraph EliteOrMonster(MapCoord? current = null) => new MapBuilder(3, 1, (0, 1), (1, 1))
        .Add(0, 1, MapNodeType.Elite, (0, 2))
        .Add(1, 1, MapNodeType.Monster, (0, 2))
        .Add(0, 2, MapNodeType.RestSite, (0, 3))
        .Build(current);

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Learned_next_step_also_changes_the_displayed_full_route(int ascension)
    {
        var pack = new DataPack { RunBaseline = new RunBaselinePolicy(new Dictionary<string, double> { ["path:E|a1|h0"] = 20 }) };
        var state = State(0.2) with { Character = "CHARACTER.IRONCLAD", Ascension = ascension };
        var advisor = new PathAdvisor(pack);
        var map = EliteOrMonster();
        var raw = advisor.RankRoutes(state, map, null);
        var adjusted = advisor.Recommend(state, map);

        Assert.Equal(MapNodeType.Monster, raw.NextSteps[0].Type);
        Assert.Equal(MapNodeType.Elite, adjusted.NextSteps[0].Type);
        Assert.Equal(adjusted.NextSteps[0].Coord, adjusted.Top[0].Coords[0]);
        Assert.Contains("학습", adjusted.Top[0].Chips[0]);
        Assert.Equal(raw.NextStep, advisor.Recommend(state with { Ascension = 9 }, map).NextStep);
    }

    [Fact]
    public void FullHp_TakesTheElite_LowHp_AvoidsIt()
    {
        var map = EliteOrMonster();

        var high = Recommend(State(1.0), map);
        Assert.Null(high.EliteCap);
        Assert.Equal(new MapCoord(0, 1), high.NextStep);
        Assert.Equal(1, high.Top[0].Elites);
        Assert.Contains("엘리트 1", high.Top[0].Chips);
        Assert.Contains("HP 충분: 엘리트로 유물", high.Top[0].Chips);

        var low = Recommend(State(0.2), map);
        Assert.Equal(new MapCoord(1, 1), low.NextStep);
        Assert.Equal(0, low.Top[0].Elites);
        Assert.Contains("엘리트 없음", low.Top[0].Chips);
        Assert.Contains("HP 낮음: 전투 적은 길", low.Top[0].Chips);
        // The elite path is still listed, ranked second, with its death risk visible in the breakdown.
        Assert.Equal(2, low.Top.Count);
        Assert.True(low.Top[1].Score.ValueOf(PathComponentKeys.DeathRisk) < -10);
    }

    [Fact]
    public void NextSteps_ScoreEveryChild_AndMarkTheBest()
    {
        var r = Recommend(State(1.0), EliteOrMonster());

        Assert.Equal(2, r.NextSteps.Count);
        var best = Assert.Single(r.NextSteps, n => n.IsBest);
        Assert.Equal(r.NextStep, best.Coord);
        Assert.Equal(MapNodeType.Elite, best.Type);
        Assert.Equal(r.Top[0].Total, best.BestScore, 9);
        Assert.True(r.NextSteps[0].BestScore >= r.NextSteps[1].BestScore);
    }

    [Fact]
    public void LowHp_PrefersRestSites()
    {
        // A: ? → monster → boss    B: rest → ? → boss
        var map = new MapBuilder(3, 1, (0, 1), (1, 1))
            .Add(0, 1, MapNodeType.Unknown, (0, 2))
            .Add(0, 2, MapNodeType.Monster, (0, 3))
            .Add(1, 1, MapNodeType.RestSite, (1, 2))
            .Add(1, 2, MapNodeType.Unknown, (0, 3))
            .Build();

        var r = Recommend(State(0.25), map);

        Assert.Equal(new MapCoord(1, 1), r.NextStep);
        Assert.Equal(1, r.Top[0].Rests);
        Assert.True(r.Top[0].ExpectedBossHp > r.Top[1].ExpectedBossHp);
        Assert.True(r.Top[0].Score.ValueOf(PathComponentKeys.Rest) > 0);
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(0, false)]
    public void Shop_IsWorthItOnlyWithGold(int gold, bool expectShop)
    {
        var map = new MapBuilder(2, 1, (0, 1), (1, 1))
            .Add(0, 1, MapNodeType.Shop, (0, 2))
            .Add(1, 1, MapNodeType.Monster, (0, 2))
            .Build();

        var r = Recommend(State(1.0, gold: gold), map);

        Assert.Equal(expectShop ? new MapCoord(0, 1) : new MapCoord(1, 1), r.NextStep);
        var shopPath = r.Top.Single(o => o.CountOf(MapNodeType.Shop) == 1);
        Assert.Contains(expectShop ? "상점 1 (골드 활용)" : "상점 1", shopPath.Chips);
    }

    [Fact]
    public void GoldEarnedOnThePath_MakesALaterShopValuable()
    {
        // Just short of the threshold now: shop right away is poor, after two fights (+2 × MonsterGold) it can buy.
        var w = Pack.Weights.Path;
        var gold = w.ShopGoldThreshold - 2 * w.MonsterGold;
        var map = new MapBuilder(4, 1, (0, 1))
            .Add(0, 1, MapNodeType.Monster, (0, 2))
            .Add(0, 2, MapNodeType.Monster, (0, 3))
            .Add(0, 3, MapNodeType.Shop, (0, 4))
            .Build();
        var state = State(1.0, gold: gold, deck: [new DeckCard(new CardRef(TestPacks.OrbCore))]); // nothing to remove
        var advisor = new PathAdvisor(Pack);

        var later = advisor.ScorePath(state, map, [new(0, 1), new(0, 2), new(0, 3), map.Boss]);
        var direct = advisor.ScorePath(state, new MapBuilder(2, 1, (0, 1)).Add(0, 1, MapNodeType.Shop, (0, 2)).Build(), [new(0, 1), new(0, 2)]);

        // Rewards are weighted by the chance to be alive; two hallway fights at full HP leave that at ~0.99999.
        Assert.Equal(Pack.Weights.Path.ShopValue, later.Score.ValueOf(PathComponentKeys.Shop), 3);
        Assert.Equal(Pack.Weights.Path.ShopPoorValue, direct.Score.ValueOf(PathComponentKeys.Shop), 9);
    }

    [Fact]
    public void ElitesAlreadyFoughtThisAct_LowerTheValueOfMore()
    {
        var w = new Weights();
        w.Path.TargetElites[1] = 2;
        w.Path.DamageShape = new PathDamageShape(); // uniform damage: no death chance at full HP, so rewards are exact
        w.Path.CardRewardDecay.Clear(); // only the relic part should change here (card-reward decay has its own test)
        var pack = TestPacks.DefectPack(w);
        var fresh = new MapBuilder(3, 1, (0, 1))
            .Add(0, 1, MapNodeType.Elite, (0, 2))
            .Add(0, 2, MapNodeType.Elite, (0, 3))
            .Build();
        var veteran = fresh with { Visited = [new(0, 0), new(0, 1), new(0, 2)] }; // pretend both elites were done
        var coords = new MapCoord[] { new(0, 1), new(0, 2), fresh.Boss };
        var advisor = new PathAdvisor(pack);

        var a = advisor.ScorePath(State(1.0), fresh, coords).Score.ValueOf(PathComponentKeys.Elite);
        var b = advisor.ScorePath(State(1.0), veteran, coords).Score.ValueOf(PathComponentKeys.Elite);

        Assert.Equal(2 * w.Path.EliteReward * (1 - w.Path.ExtraEliteFactor), a - b, 9);
    }

    [Fact]
    public void RiskPatterns_EliteChainAndNoRest()
    {
        var map = new MapBuilder(4, 1, (0, 1))
            .Add(0, 1, MapNodeType.Elite, (0, 2))
            .Add(0, 2, MapNodeType.Elite, (0, 3))
            .Add(0, 3, MapNodeType.Monster, (0, 4))
            .Build();

        var r = Recommend(State(0.5), map);
        var o = Assert.Single(r.Top);

        Assert.Contains(PathRisks.EliteChain, o.Risks);
        Assert.Contains(PathRisks.EliteNoRest, o.Risks);
        Assert.Contains("위험: 엘리트 연속", o.Chips);
        Assert.Contains("위험: 휴식 없이 엘리트", o.Chips);
        Assert.True(o.Score.ValueOf(PathComponentKeys.Patterns) < 0);
    }

    [Fact]
    public void RestBeforeBoss_Chip()
    {
        var map = new MapBuilder(3, 1, (0, 1))
            .Add(0, 1, MapNodeType.Monster, (0, 2))
            .Add(0, 2, MapNodeType.RestSite, (0, 3))
            .Build();

        Assert.Contains("휴식처 1 (보스 전 휴식 있음)", Recommend(State(), map).Top[0].Chips);
    }

    [Fact]
    public void Weights_ChangeTheRecommendation()
    {
        var w = new Weights();
        w.Path.EliteReward = 0;

        var r = Recommend(State(1.0), EliteOrMonster(), TestPacks.DefectPack(w));

        Assert.Equal(new MapCoord(1, 1), r.NextStep);
    }

    [Fact]
    public void PathWeights_LoadFromWeightsJson_PartialOverride()
    {
        var w = DataPackLoader.LoadWeights("""{ "path": { "eliteReward": 5, "targetElites": { "1": 3 } } }""");

        Assert.Equal(5, w.Path.EliteReward);
        Assert.Equal(3, w.Path.TargetElites[1]);
        Assert.Equal(2, w.Path.TargetElites[2]);                       // untouched act keeps its default
        Assert.Equal(new PathWeights().MonsterReward, w.Path.MonsterReward); // untouched value keeps its default
    }

    [Fact]
    public void EliteCap_OverridesTheSimulation_WhenLowerHpWouldWantMoreElites()
    {
        // Random maps where a rest-heavy, elite-heavy route out-scores a lean one at mid HP exist (HP and rest sites
        // are substitutes). Find such a case and check the guard: the recommendation stays within the cap, a higher-
        // scoring alternative is still listed (marked), and the chips explain it.
        var advisor = new PathAdvisor(Pack);
        foreach (var seed in Enumerable.Range(1, 80))
        {
            var map = TestMaps.Random(seed);
            foreach (var hp in new[] { 0.35, 0.5, 0.65 })
            {
                var r = advisor.Recommend(State(hp, gold: 0), map);
                PathAssert.WellFormed(map, r);
                if (!r.Top[0].Chips.Contains("HP 고려: 엘리트 수 제한")) continue;

                Assert.NotNull(r.EliteCap);
                Assert.True(r.Top[0].Elites <= r.EliteCap);
                var over = r.Top.Where(o => o.Elites > r.EliteCap).ToList();
                Assert.All(over, o => Assert.Contains("HP 대비 엘리트 많음", o.Chips));
                return;
            }
        }
        Assert.Fail("expected at least one random map where the elite cap binds");
    }

    // ---- Edge cases ----

    [Fact]
    public void CurrentIsBoss_Empty()
    {
        var map = EliteOrMonster(new MapCoord(0, 3));
        var r = Recommend(State(), map);

        Assert.True(r.IsEmpty);
        Assert.Null(r.NextStep);
        Assert.Empty(r.NextSteps);
        Assert.Equal(map.Boss, r.Origin);
    }

    [Fact]
    public void CurrentIsDeadEndOrOffMap_Empty()
    {
        var deadEnd = new MapBuilder(2, 1, (0, 1), (1, 1))
            .Add(0, 1, MapNodeType.Monster)
            .Add(1, 1, MapNodeType.Monster, (0, 2))
            .Build(new MapCoord(0, 1));

        Assert.True(Recommend(State(), deadEnd).IsEmpty);
        Assert.True(Recommend(State(), EliteOrMonster(new MapCoord(6, 6))).IsEmpty);
    }

    [Fact]
    public void BeforeFirstStep_ChoosesAmongStartChildren()
    {
        var map = EliteOrMonster(current: null);
        var r = Recommend(State(), map);

        Assert.Equal(map.Start, r.Origin);
        Assert.Contains(r.NextStep!.Value, map.Nodes[map.Start].Children);
        Assert.Equal(2, r.TotalPaths);
        Assert.True(r.Exhaustive);
    }

    [Fact]
    public void UnknownSaveType_IsTreatedAsQuestionMarkRoom()
    {
        var type = MapGraph.ParseType("some_new_room_type");
        Assert.Equal(MapNodeType.Unknown, type);

        var map = new MapBuilder(2, 1, (0, 1)).Add(0, 1, type, (0, 2)).Build();
        var r = Recommend(State(), map);

        Assert.Contains("? 1", r.Top[0].Chips);
        Assert.True(r.Top[0].Score.ValueOf(PathComponentKeys.Unknown) > 0);
    }

    [Fact]
    public void EmptyDataPack_StillWorks()
    {
        var r = Recommend(State(0.6), TestMaps.Random(5), new DataPack());

        Assert.False(r.IsEmpty);
        Assert.InRange(r.DeckStrength, 0, 1);
    }

    [Fact]
    public void TightBeam_StillScoresEveryNextStep()
    {
        var w = new Weights();
        w.Path.CandidateLimit = 1;
        var map = TestMaps.Random(9);

        var r = Recommend(State(), map, TestPacks.DefectPack(w));

        Assert.False(r.Exhaustive);
        Assert.Equal(MapPaths.ValidChildren(map, map.Start).Count(c => MapPaths.CountPaths(map, c) > 0), r.NextSteps.Count);
    }

    // ---- Real map fixture (act 2 map from a live save: coords/types/edges only) ----

    [Fact]
    public void Fixture_LoadsAsAConsistentGraph()
    {
        var map = MapFixture.Load("map_act2.json");

        Assert.Equal("ACT.HIVE", map.ActId);
        Assert.Equal(2, map.Act);
        Assert.Equal(49 + 2, map.Nodes.Count);
        Assert.Equal(MapNodeType.Ancient, map.Nodes[map.Start].Type);
        Assert.Equal(MapNodeType.Boss, map.Nodes[map.Boss].Type);
        Assert.Equal(map.Visited[^1], map.Current);
        Assert.All(map.Nodes.Values, n => Assert.All(n.Children, c => Assert.True(map.Nodes.ContainsKey(c), $"{n.Coord} → {c}")));
        Assert.Equal(TestMaps.BruteForcePaths(map, map.Start).Count, MapPaths.CountPaths(map, map.Start));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.7)]
    [InlineData(0.35)]
    [InlineData(0.15)]
    public void Fixture_RecommendsFromCurrentNode(double hp)
    {
        var map = MapFixture.Load("map_act2.json");

        var r = Recommend(State(hp, gold: 120, act: 2), map);

        Assert.Equal(map.Current, r.Origin);
        Assert.True(r.Exhaustive);
        Assert.Equal(TestMaps.BruteForcePaths(map, r.Origin).Count, r.TotalPaths);
        Assert.Equal(MapPaths.ValidChildren(map, r.Origin).ToHashSet(), r.NextSteps.Select(n => n.Coord).ToHashSet());
        Assert.Contains(r.Top[0].Chips, c => c.StartsWith("휴식처") && c.Contains("보스 전 휴식 있음"));

        _out.WriteLine($"HP {hp:P0}: {r.TotalPaths} paths, strength {r.DeckStrength:0.00}, next {r.NextStep}");
        foreach (var o in r.Top)
        {
            _out.WriteLine($"  {o.Total,7:0.0}  {string.Join(" ", o.Coords)}");
            _out.WriteLine($"           {string.Join(" · ", o.Chips)}");
            _out.WriteLine($"           {o.Score}");
        }
    }

    [Fact]
    public void Fixture_BeforeFirstStep()
    {
        var map = MapFixture.Load("map_act2.json") with { Current = null };

        var r = Recommend(State(1.0, act: 2), map);

        Assert.Equal(map.Start, r.Origin);
        Assert.Equal(52, r.TotalPaths);
        Assert.Equal(3, r.Top.Count);
    }
}

internal static class PathAssert
{
    /// <summary>Invariants every recommendation must satisfy.</summary>
    public static void WellFormed(MapGraph map, PathRecommendation r)
    {
        Assert.True(r.Top.Count <= 3);
        Assert.Equal(r.Top.Count == 0, r.NextStep is null);
        for (var i = 0; i < r.Top.Count; i++)
        {
            var o = r.Top[i];
            Assert.True(MapPaths.IsValidPath(map, r.Origin, o.Coords), $"invalid path {string.Join(" ", o.Coords)}");
            Assert.Equal(map.Boss, o.Coords[^1]);
            Assert.Equal(o.Coords.Count, o.Types.Count);
            Assert.Equal(o.Coords.Count, o.Counts.Values.Sum());
            Assert.InRange(o.ExpectedBossHp, 0, 1);
            Assert.NotEmpty(o.Chips);
            ScoreAssert.Consistent(o.Score);
            if (i > 0)
            {
                // In-cap paths come first; within the same group, highest score first.
                var prevIn = InCap(r, r.Top[i - 1]);
                Assert.True(prevIn || !InCap(r, o), "an over-cap path is ranked above an in-cap one");
                if (prevIn == InCap(r, o)) Assert.True(r.Top[i - 1].Total >= o.Total);
            }
        }
        if (r.Top.Count > 0) Assert.True(InCap(r, r.Top[0]), "the recommendation must respect the elite cap");
        Assert.Equal(r.Top.Count, r.Top.Select(o => string.Join(";", o.Coords)).Distinct().Count());
        if (r.Top.Count > 0)
        {
            Assert.Equal(r.Top[0].Coords[0], Assert.Single(r.NextSteps, n => n.IsBest).Coord);
            Assert.True(r.NextSteps[0].IsBest);
            Assert.Equal(r.NextSteps.Count, r.NextSteps.Select(n => n.Coord).Distinct().Count());
            Assert.All(r.NextSteps, n => Assert.Contains(n.Coord, MapPaths.ValidChildren(map, r.Origin)));
        }
        Assert.InRange(r.DeckStrength, 0, 1);
    }

    public static bool InCap(PathRecommendation r, PathOption o) => r.EliteCap is not { } k || o.Elites <= k;
}
