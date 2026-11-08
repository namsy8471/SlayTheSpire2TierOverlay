using Sts2Overlay.Core.Map;

namespace Sts2Overlay.Core.Tests.Map;

public class MapPathsTests
{
    public static IEnumerable<object[]> Seeds => Enumerable.Range(1, 12).Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void CountPaths_MatchesBruteForce(int seed)
    {
        var map = TestMaps.Random(seed);
        Assert.Equal(TestMaps.BruteForcePaths(map, map.Start).Count, MapPaths.CountPaths(map, map.Start));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void BestPaths_WithLargeBeam_ListsEveryPathInScoreOrder(int seed)
    {
        var map = TestMaps.Random(seed, width: 5, height: 9);
        // Arbitrary but deterministic additive score per node.
        double Score(MapNode n) => (n.Coord.Col * 7 + n.Coord.Row * 3) % 11 - (int)n.Type;

        var expected = TestMaps.BruteForcePaths(map, map.Start)
            .Select(p => p.Sum(c => Score(map.Nodes[c])))
            .OrderByDescending(s => s)
            .ToArray();
        var kbest = MapPaths.BestPaths(map, Score, beam: 100_000);
        var suffixes = kbest.Suffixes(map.Start).ToArray();

        Assert.Equal(expected.Length, suffixes.Length);
        Assert.Equal(expected, suffixes.Select(p => p.Sum(c => Score(map.Nodes[c]))).ToArray());
        Assert.Equal(expected, kbest.Scores(map.Start));
        Assert.All(suffixes, p => Assert.True(MapPaths.IsValidPath(map, map.Start, p)));
        Assert.Equal(suffixes.Length, suffixes.Select(p => string.Join(";", p)).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void BestPaths_WithSmallBeam_KeepsTheTrueTopK(int seed)
    {
        // For an additive score the beam is exact for the top k: the k best paths overall are made only of
        // each node's k best suffixes.
        var map = TestMaps.Random(seed);
        double Score(MapNode n) => ((n.Coord.Col + 1) * (n.Coord.Row + 2)) % 13;
        var expected = TestMaps.BruteForcePaths(map, map.Start)
            .Select(p => p.Sum(c => Score(map.Nodes[c])))
            .OrderByDescending(s => s)
            .Take(3)
            .ToArray();

        var top = MapPaths.BestPaths(map, Score, beam: 3).Suffixes(map.Start).ToArray();

        Assert.Equal(expected, top.Select(p => p.Sum(c => Score(map.Nodes[c]))).ToArray());
    }

    [Fact]
    public void DeadEndsAndMissingChildren_AreSkipped()
    {
        // (0,1) leads nowhere; (1,1) points at a node that does not exist and at (1,2) → boss.
        var map = new MapBuilder(3, 1, (0, 1), (1, 1))
            .Add(0, 1, MapNodeType.Monster)
            .Add(1, 1, MapNodeType.Monster, (5, 5), (1, 2), (1, 2))
            .Add(1, 2, MapNodeType.RestSite, (0, 3))
            .Build();

        Assert.Equal([new MapCoord(1, 2)], MapPaths.ValidChildren(map, new MapCoord(1, 1)));
        Assert.Equal(1, MapPaths.CountPaths(map, map.Start));
        var only = Assert.Single(MapPaths.BestPaths(map, _ => 1, 10).Suffixes(map.Start));
        Assert.Equal([new MapCoord(1, 1), new MapCoord(1, 2), map.Boss], only);
    }

    [Fact]
    public void Cycle_DoesNotHang()
    {
        var map = new MapBuilder(3, 1, (0, 1))
            .Add(0, 1, MapNodeType.Monster, (0, 2))
            .Add(0, 2, MapNodeType.Monster, (0, 1), (0, 3))
            .Build();

        Assert.Equal(1, MapPaths.CountPaths(map, map.Start));
        Assert.Single(MapPaths.BestPaths(map, _ => 0, 10).Suffixes(map.Start));
    }

    [Fact]
    public void IsValidPath_RejectsBrokenEdgesAndMissingBoss()
    {
        var map = TestMaps.Random(3);
        var good = TestMaps.BruteForcePaths(map, map.Start)[0];
        Assert.True(MapPaths.IsValidPath(map, map.Start, good));
        Assert.False(MapPaths.IsValidPath(map, map.Start, good.Take(good.Count - 1).ToArray()));
        Assert.False(MapPaths.IsValidPath(map, map.Start, good.Skip(1).ToArray()));
        Assert.False(MapPaths.IsValidPath(map, map.Start, []));
    }

    [Fact]
    public void CountPaths_FullLattice_IsLargeButCheap()
    {
        // 7 wide, 15 rows, every node linked to col-1..col+1: tens of millions of paths, solved in O(nodes).
        var b = new MapBuilder(16, 1, Enumerable.Range(0, 7).Select(c => (c, 1)).ToArray());
        for (var r = 1; r <= 15; r++)
        {
            for (var c = 0; c < 7; c++)
            {
                var children = r == 15 ? [(0, 16)] : Enumerable.Range(c - 1, 3).Where(x => x is >= 0 and < 7).Select(x => (x, r + 1)).ToArray();
                b.Add(c, r, MapNodeType.Monster, children);
            }
        }
        var count = MapPaths.CountPaths(b.Build(), new MapCoord(0, 0));
        Assert.True(count > 10_000_000, $"count = {count}");
    }
}
