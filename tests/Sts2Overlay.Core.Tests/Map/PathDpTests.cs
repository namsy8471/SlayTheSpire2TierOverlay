using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Tests.Data;

namespace Sts2Overlay.Core.Tests.Map;

/// <summary>
/// The map DP (<see cref="PathScorer"/> <c>DpValues</c>) against the simulator's <c>--path-rule dp</c>: the expected
/// values are the simulator's <c>path_pick_dp</c> (sts2-sim-lab 82a9449) on the same map, states and data pack, with the
/// adopted elite values (A0–A9 50/40/25, A10 95/74/47).
/// </summary>
public class PathDpTests
{
    private static readonly DataPack RealPack = DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, DataPackLoader.PackRelativePath("v0.111.0")));

    private static MapCoord C(int col, int row) => new(col, row);

    private static MapGraph Big(int act, MapCoord? current, IReadOnlyList<MapCoord> visited, int done)
    {
        (MapCoord, MapNodeType, MapCoord[])[] spec =
        [
            (C(0, 0), MapNodeType.Ancient, [C(0, 1), C(1, 1), C(2, 1)]),
            (C(0, 1), MapNodeType.Monster, [C(0, 2), C(1, 2)]),
            (C(1, 1), MapNodeType.Unknown, [C(1, 2)]),
            (C(2, 1), MapNodeType.Monster, [C(1, 2), C(2, 2)]),
            (C(0, 2), MapNodeType.Elite, [C(0, 3)]),
            (C(1, 2), MapNodeType.Monster, [C(0, 3), C(1, 3)]),
            (C(2, 2), MapNodeType.Shop, [C(1, 3)]),
            (C(0, 3), MapNodeType.RestSite, [C(0, 4)]),
            (C(1, 3), MapNodeType.Elite, [C(0, 4), C(1, 4)]),
            (C(0, 4), MapNodeType.Unknown, [C(0, 5)]),
            (C(1, 4), MapNodeType.Treasure, [C(0, 5), C(1, 5)]),
            (C(0, 5), MapNodeType.RestSite, [C(0, 6)]),
            (C(1, 5), MapNodeType.Elite, [C(0, 6)]),
            (C(0, 6), MapNodeType.Boss, []),
        ];
        return new MapGraph
        {
            Act = act,
            Nodes = spec.ToDictionary(s => s.Item1, s => new MapNode(s.Item1, s.Item2, s.Item3)),
            Start = C(0, 0),
            Boss = C(0, 6),
            Current = current,
            Visited = visited,
            NormalFightsDone = done,
        };
    }

    // (asc, act, hp, gold, fights done, current col/row or -1, expected values for options (0,1) (1,1) (2,1) (0,3) (1,3); NaN = not a child)
    public static TheoryData<int, int, int, int, int, int, int, double[]> Cases() => new()
    {
        { 0, 1, 80, 99, 0, -1, -1, [77.0671613332, 70.1439113119, 79.3093465141, double.NaN, double.NaN] },
        { 0, 1, 30, 99, 0, -1, -1, [34.7541632079, 34.1726438172, 34.7541632079, double.NaN, double.NaN] },
        { 0, 2, 55, 160, 2, -1, -1, [8.1783291658, 15.4883856711, 19.5186411858, double.NaN, double.NaN] },
        { 8, 1, 70, 50, 1, -1, -1, [79.4491571757, 74.0900193578, 79.4491571757, double.NaN, double.NaN] },
        { 8, 3, 40, 300, 3, -1, -1, [-10.3593233253, 8.6195107043, -1.6850488909, double.NaN, double.NaN] },
        { 10, 1, 75, 99, 0, -1, -1, [113.9005610663, 110.1732808941, 124.5149663669, double.NaN, double.NaN] },
        { 10, 2, 25, 0, 3, -1, -1, [-47.4634551275, -10.2183223034, -47.4634551275, double.NaN, double.NaN] },
        { 10, 1, 60, 120, 1, 1, 2, [double.NaN, double.NaN, double.NaN, 14.6973919755, 78.3997606049] },
        { 0, 1, 12, 0, 4, 1, 2, [double.NaN, double.NaN, double.NaN, 21.7292494154, -95.3217787171] },
        { 4, 2, 90, 400, 0, -1, -1, [41.9515750357, 45.1870848836, 67.2803249073, double.NaN, double.NaN] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Dp_values_match_the_simulator(int asc, int act, int hp, int gold, int done, int col, int row, double[] want)
    {
        MapCoord? cur = col >= 0 ? C(col, row) : null;
        var map = Big(act, cur ?? C(0, 0), cur is { } x ? [C(0, 0), C(1, 1), x] : [C(0, 0)], done);
        CardRef[] cards = [.. Enumerable.Repeat(new CardRef("CARD.STRIKE_DEFECT"), 4), .. Enumerable.Repeat(new CardRef("CARD.DEFEND_DEFECT"), 4),
            new CardRef("CARD.ZAP"), new CardRef("CARD.DUALCAST")];
        var state = new RunState
        {
            Character = "CHARACTER.DEFECT", Ascension = asc, Act = act, Floor = 3, Hp = hp, MaxHp = 80, Gold = gold,
            Deck = cards.Select(c => new DeckCard(c)).ToArray(),
        };
        var pack = RealPack.ForRun(state);
        var p = pack.Weights.Path;
        var scorer = new PathScorer(pack, state, map, DeckAnalyzer.Analyze(state, pack), null, p.Dp.HpLossFor(asc));
        MapCoord[] options = [C(0, 1), C(1, 1), C(2, 1), C(0, 3), C(1, 3)];
        var got = scorer.DpValues(options, p.Dp.EliteValueFor(asc, act));
        for (var i = 0; i < options.Length; i++)
        {
            if (double.IsNaN(want[i])) Assert.True(got[i] is null, $"option {i}: {got[i]}");
            else Assert.True(got[i] is { } g && Math.Abs(g - want[i]) < 1e-8, $"asc {asc} act {act} hp {hp}: option {i} got {got[i]:R} want {want[i]}");
        }
    }

    [Fact]
    public void Defect_single_player_takes_the_dp_next_room_and_its_route_first()
    {
        var state = new RunState
        {
            Character = "CHARACTER.DEFECT", Ascension = 10, Act = 2, Floor = 3, Hp = 20, MaxHp = 80, Gold = 0,
            Deck = [new DeckCard(new CardRef("CARD.ZAP"))],
        };
        var map = Big(2, C(0, 0), [C(0, 0)], 3);
        var advisor = new PathAdvisor(RealPack);
        var pack = RealPack.ForRun(state);
        var scorer = new PathScorer(pack, state, map, DeckAnalyzer.Analyze(state, pack), null, pack.Weights.Path.Dp.HpLossFor(10));
        MapCoord[] children = [C(0, 1), C(1, 1), C(2, 1)];
        var values = scorer.DpValues(children, pack.Weights.Path.Dp.EliteValueFor(10, 2));
        var best = children[Enumerable.Range(0, 3).MaxBy(i => values[i]!.Value)];

        var r = advisor.Recommend(state, map);
        Assert.Equal(best, r.NextSteps[0].Coord);
        Assert.Equal(best, r.Top[0].Coords[0]);
        Assert.True(r.NextSteps.Select(n => n.Coord).SequenceEqual(children.OrderByDescending(c => values[Array.IndexOf(children, c)])));

        // Co-op and other characters keep the path list (no DP chip either way).
        Assert.False(pack.Weights.Path.Dp.AppliesTo(state with { IsMultiplayer = true }));
        Assert.False(pack.Weights.Path.Dp.AppliesTo(state with { Character = "CHARACTER.IRONCLAD" }));
    }
}
