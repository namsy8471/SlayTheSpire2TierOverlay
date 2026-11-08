using Sts2Overlay.Core.Map;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// <see cref="MapScreenGeometry"/>: the game's map layout (decompiled NMapScreen.SetMap) rebuilt from the save.
/// All maps here are synthetic 7-column maps; the expected numbers are worked out from the formula by hand.
/// </summary>
public class MapScreenGeometryTests
{
    private const string Seed = "ZSN877JR14";

    // Grid origin of node (0, 0) without jitter: (960 − 500 + 28, 540 + 740 + 28).
    private const float GridX0 = 488f, GridY0 = 1308f;

    /// <summary>
    /// A 7x16 map: start (3,0), grid rows 1..14 with 3..5 nodes each, boss (3,15) and optionally a second boss (3,16).
    /// <paramref name="reverse"/> inserts the nodes into the dictionary in the opposite order.
    /// </summary>
    private static MapGraph Map(bool secondBoss = false, bool ancient = true, MapCoord? current = null,
        int act = 1, bool reverse = false)
    {
        var start = new MapCoord(3, 0);
        var boss = new MapCoord(3, 15);
        MapCoord? second = secondBoss ? new MapCoord(3, 16) : null;
        var list = new List<MapNode>
        {
            new(start, ancient ? MapNodeType.Ancient : MapNodeType.Monster, []),
            new(boss, MapNodeType.Boss, []),
        };
        if (second is { } s) list.Add(new MapNode(s, MapNodeType.Boss, []));
        var rng = new Random(7);
        for (int row = 1; row <= 14; row++)
        {
            int count = rng.Next(3, 6);
            foreach (int col in Enumerable.Range(0, 7).OrderBy(_ => rng.Next()).Take(count))
                list.Add(new MapNode(new MapCoord(col, row), MapNodeType.Monster, []));
        }
        if (reverse) list.Reverse();
        return new MapGraph
        {
            Act = act,
            Width = 7,
            Height = 16,
            Nodes = list.ToDictionary(n => n.Coord),
            Start = start,
            Boss = boss,
            SecondBoss = second,
            Current = current,
        };
    }

    private static IEnumerable<MapCoord> GridNodes(MapGraph map) =>
        map.Nodes.Keys.Where(c => c != map.Start && c != map.Boss && c != map.SecondBoss);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_without_a_seed(string? seed) => Assert.Null(MapScreenGeometry.Compute(Map(), seed));

    [Fact]
    public void Null_for_a_degenerate_map()
    {
        Assert.Null(MapScreenGeometry.Compute(Map() with { Width = 0 }, Seed));
        Assert.Null(MapScreenGeometry.Compute(Map() with { Height = 1 }, Seed));
    }

    [Theory]
    // distY = 2325 / (16 − 1) = 155; with a second boss × 0.9 = 139.5.
    [InlineData(false, 155f)]
    [InlineData(true, 139.5f)]
    public void Row_spacing_follows_the_height_and_the_second_boss(bool secondBoss, float distY)
    {
        MapScreenLayout layout = MapScreenGeometry.Compute(Map(secondBoss), Seed)!;
        Assert.Equal(distY, layout.RowSpacing, 3);
    }

    [Theory]
    [InlineData(false, 155f)]
    [InlineData(true, 139.5f)]
    public void Every_grid_node_sits_on_its_grid_point_within_the_jitter_box(bool secondBoss, float distY)
    {
        MapGraph map = Map(secondBoss);
        MapScreenLayout layout = MapScreenGeometry.Compute(map, Seed)!;
        const float distX = 150f; // 1050 / 7
        const float eps = 1e-3f;  // float rounding of the sum, far below a pixel

        foreach (MapCoord c in GridNodes(map))
        {
            DesignPoint p = layout.Centers[c];
            float jx = p.X - (GridX0 + c.Col * distX);
            float jy = p.Y - (GridY0 - c.Row * distY);
            Assert.InRange(jx, -21f - eps, 21f + eps);
            Assert.InRange(jy, -25f - eps, 25f + eps);
        }
    }

    [Fact]
    public void Jitter_is_actually_applied_and_spread_over_the_box()
    {
        MapGraph map = Map();
        MapScreenLayout layout = MapScreenGeometry.Compute(map, Seed)!;
        var jx = GridNodes(map).Select(c => layout.Centers[c].X - (GridX0 + c.Col * 150f)).ToList();
        var jy = GridNodes(map).Select(c => layout.Centers[c].Y - (GridY0 - c.Row * 155f)).ToList();

        // ~55 uniform draws: both signs occur and the range covers most of the box.
        Assert.True(jx.Min() < -10 && jx.Max() > 10, $"x jitter {jx.Min()}..{jx.Max()}");
        Assert.True(jy.Min() < -12 && jy.Max() > 12, $"y jitter {jy.Min()}..{jy.Max()}");
    }

    [Fact]
    public void Same_seed_gives_the_same_layout_and_another_seed_a_different_one()
    {
        MapGraph map = Map();
        MapScreenLayout a = MapScreenGeometry.Compute(map, Seed)!;
        MapScreenLayout b = MapScreenGeometry.Compute(map, Seed)!;
        MapScreenLayout c = MapScreenGeometry.Compute(map, "OTHERSEED1")!;

        Assert.Equal(a.Centers.Count, b.Centers.Count);
        foreach ((MapCoord k, DesignPoint p) in a.Centers) Assert.Equal(p, b.Centers[k]);

        // Only the jitter depends on the seed: grid nodes move, start and boss don't.
        int moved = GridNodes(map).Count(k => a.Centers[k] != c.Centers[k]);
        Assert.Equal(GridNodes(map).Count(), moved);
        Assert.Equal(a.Centers[map.Start], c.Centers[map.Start]);
        Assert.Equal(a.Centers[map.Boss], c.Centers[map.Boss]);
    }

    [Fact]
    public void Each_act_has_its_own_jitter_stream()
    {
        MapScreenLayout act1 = MapScreenGeometry.Compute(Map(act: 1), Seed)!;
        MapScreenLayout act2 = MapScreenGeometry.Compute(Map(act: 2), Seed)!;
        Assert.Equal(0, act1.ActIndex);
        Assert.Equal(1, act2.ActIndex);
        Assert.Equal(Seed, act1.Seed);
        Assert.Contains(act1.Centers.Keys, k => act1.Centers[k] != act2.Centers[k]);
    }

    [Fact]
    public void Jitter_order_does_not_depend_on_dictionary_insertion_order()
    {
        MapScreenLayout forward = MapScreenGeometry.Compute(Map(reverse: false), Seed)!;
        MapScreenLayout backward = MapScreenGeometry.Compute(Map(reverse: true), Seed)!;
        Assert.Equal(forward.Centers.Count, backward.Centers.Count);
        foreach ((MapCoord k, DesignPoint p) in forward.Centers) Assert.Equal(p, backward.Centers[k]);
    }

    [Theory]
    // Ancient: 208x208 scene at (−80, 720 − row·distY) → centre (960 − 80 + 104, 540 + 720 + 104).
    [InlineData(true, 984f, 1364f)]
    // Normal 56x56 point at (−80, 800 − row·distY) → centre (960 − 80 + 28, 540 + 800 + 28).
    [InlineData(false, 908f, 1368f)]
    public void Start_node_has_a_fixed_position(bool ancient, float x, float y)
    {
        MapGraph map = Map(ancient: ancient);
        DesignPoint p = MapScreenGeometry.Compute(map, Seed)!.Centers[map.Start];
        Assert.Equal(x, p.X, 3);
        Assert.Equal(y, p.Y, 3);
    }

    [Fact]
    public void Boss_has_a_fixed_position()
    {
        // 374x306 scene, pivot (187, 153), at (−200, −1980) → centre (960 − 200 + 187, 540 − 1980 + 153).
        MapGraph map = Map();
        DesignPoint p = MapScreenGeometry.Compute(map, Seed)!.Centers[map.Boss];
        Assert.Equal(947f, p.X, 3);
        Assert.Equal(-1287f, p.Y, 3);
    }

    [Fact]
    public void Two_bosses_are_squeezed_by_the_same_factor()
    {
        // k = 0.9: boss at 540 − 1782 + 153, second boss at 540 − 2052 + 153.
        MapGraph map = Map(secondBoss: true);
        MapScreenLayout layout = MapScreenGeometry.Compute(map, Seed)!;
        DesignPoint boss = layout.Centers[map.Boss], second = layout.Centers[map.SecondBoss!.Value];
        Assert.Equal(947f, boss.X, 3);
        Assert.Equal(-1089f, boss.Y, 3);
        Assert.Equal(947f, second.X, 3);
        Assert.Equal(-1359f, second.Y, 3);
    }

    [Fact]
    public void Every_node_gets_a_centre()
    {
        MapGraph map = Map(secondBoss: true);
        MapScreenLayout layout = MapScreenGeometry.Compute(map, Seed)!;
        Assert.Equal(map.Nodes.Keys.OrderBy(k => k.Col).ThenBy(k => k.Row), layout.Centers.Keys.OrderBy(k => k.Col).ThenBy(k => k.Row));
    }

    [Theory]
    // −600 + row × 155, clamped to the game's −600..1800.
    [InlineData(-1, -600f)]   // before the first step: the start row (0)
    [InlineData(0, -600f)]
    [InlineData(8, 640f)]
    [InlineData(14, 1570f)]
    [InlineData(15, 1725f)]   // the boss row: −600 + 2325, still inside the limit
    public void Default_scroll_follows_the_current_row(int row, float expected)
    {
        MapGraph map = Map();
        MapCoord? current = row < 0 ? null : GridNodes(map).Concat([map.Start, map.Boss]).First(c => c.Row == row);
        MapScreenLayout layout = MapScreenGeometry.Compute(map with { Current = current }, Seed)!;
        Assert.Equal(expected, layout.DefaultScroll, 3);
    }

    [Fact]
    public void Default_scroll_is_clamped_to_the_maximum()
    {
        // Height 4 → distY 775; current row 4 → −600 + 3100 = 2500, clamped to 1800.
        MapGraph map = Map() with { Height = 4, Current = new MapCoord(0, 4) };
        Assert.Equal(MapScreenLayout.MaxScroll, MapScreenGeometry.Compute(map, Seed)!.DefaultScroll, 3);

        // A (corrupt) negative row would go below −600: clamped to the minimum.
        MapGraph below = Map() with { Current = new MapCoord(0, -2) };
        Assert.Equal(MapScreenLayout.MinScroll, MapScreenGeometry.Compute(below, Seed)!.DefaultScroll, 3);
    }

    /// <summary>
    /// Golden test: the jitter re-derived here by hand from <see cref="GameRng"/> (stream "map_jitter_{act−1}" on the
    /// hashed run seed, two doubles per grid node, columns left to right, each column bottom to top) must match exactly.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Jitter_matches_an_independent_column_major_derivation(int act, bool secondBoss)
    {
        MapGraph map = Map(secondBoss, act: act, reverse: true);
        MapScreenLayout layout = MapScreenGeometry.Compute(map, Seed)!;

        uint runSeed = unchecked((uint)GameRng.DeterministicHash(Seed));
        var rng = new GameRng(runSeed, "map_jitter_" + (act - 1));
        float distX = 1050f / 7;
        float distY = 2325f / 15 * (secondBoss ? 0.9f : 1f);
        int checkedNodes = 0;
        for (int col = 0; col < 7; col++)
        {
            for (int row = 0; row <= 16; row++)
            {
                var c = new MapCoord(col, row);
                if (!map.Nodes.ContainsKey(c) || c == map.Start || c == map.Boss || c == map.SecondBoss) continue;
                float jx = (float)(rng.NextDouble() * 42.0 + -21.0);
                float jy = (float)(rng.NextDouble() * 50.0 + -25.0);
                var expected = new DesignPoint(960f + col * distX - 500f + jx + 28f, 540f + 740f - row * distY + jy + 28f);
                Assert.Equal(expected, layout.Centers[c]);
                checkedNodes++;
            }
        }
        Assert.Equal(GridNodes(map).Count(), checkedNodes);
        Assert.Equal(2 * checkedNodes, rng.Counter);
    }
}
