using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Tests.Scoring;

namespace Sts2Overlay.Core.Tests.Map;

/// <summary>Builds small maps by hand: start at (0,0), boss at (0, <c>bossRow</c>).</summary>
internal sealed class MapBuilder
{
    private readonly Dictionary<MapCoord, MapNode> _nodes = new();
    private readonly MapCoord _start = new(0, 0);
    private readonly MapCoord _boss;
    private readonly int _act;

    public MapBuilder(int bossRow, int act = 1, params (int Col, int Row)[] startChildren)
    {
        _boss = new MapCoord(0, bossRow);
        _act = act;
        _nodes[_start] = new MapNode(_start, MapNodeType.Ancient, startChildren.Select(C).ToArray());
        _nodes[_boss] = new MapNode(_boss, MapNodeType.Boss, []);
    }

    public MapBuilder Add(int col, int row, MapNodeType type, params (int Col, int Row)[] children)
    {
        _nodes[new MapCoord(col, row)] = new MapNode(new MapCoord(col, row), type, children.Select(C).ToArray());
        return this;
    }

    public MapGraph Build(MapCoord? current = null) => new()
    {
        Act = _act,
        Width = _nodes.Keys.Max(k => k.Col) + 1,
        Height = _boss.Row,
        Nodes = new Dictionary<MapCoord, MapNode>(_nodes),
        Start = _start,
        Boss = _boss,
        Current = current,
    };

    public static MapCoord C((int Col, int Row) c) => new(c.Col, c.Row);
}

internal static class TestMaps
{
    public static readonly DataPack Pack = TestPacks.DefectPack();

    /// <summary>A 10-card starter-ish Defect deck at the given HP ratio (max HP 100).</summary>
    public static RunState State(double hpRatio = 1.0, int gold = 99, int act = 1, IReadOnlyList<DeckCard>? deck = null) => new()
    {
        Character = TestPacks.Defect,
        Act = act,
        Hp = (int)Math.Round(hpRatio * 100),
        MaxHp = 100,
        Gold = gold,
        Deck = deck ?? Enumerable.Repeat(new DeckCard(new CardRef(TestPacks.Strike)), 5)
            .Concat(Enumerable.Repeat(new DeckCard(new CardRef(TestPacks.Defend)), 4))
            .Append(new DeckCard(new CardRef(TestPacks.OrbCore)))
            .ToArray(),
    };

    /// <summary>
    /// Random map shaped like the game's: rows 1..height-1 with 2..width nodes, each node linking to 1–2 nodes of the
    /// next row within ±1 column (nearest node if none), last row to the boss. Seeded, so failures reproduce.
    /// </summary>
    public static MapGraph Random(int seed, int width = 7, int height = 15)
    {
        var rng = new Random(seed);
        MapNodeType[] bag =
        [
            MapNodeType.Monster, MapNodeType.Monster, MapNodeType.Monster, MapNodeType.Monster,
            MapNodeType.Unknown, MapNodeType.Unknown, MapNodeType.Unknown,
            MapNodeType.Elite, MapNodeType.Elite, MapNodeType.RestSite, MapNodeType.RestSite,
            MapNodeType.Shop, MapNodeType.Treasure,
        ];

        var rows = new List<int[]>();
        for (var r = 1; r < height; r++)
        {
            var count = rng.Next(2, width + 1);
            rows.Add(Enumerable.Range(0, width).OrderBy(_ => rng.Next()).Take(count).OrderBy(c => c).ToArray());
        }

        var start = new MapCoord(width / 2, 0);
        var boss = new MapCoord(width / 2, height);
        var nodes = new Dictionary<MapCoord, MapNode>
        {
            [start] = new(start, MapNodeType.Ancient, rows[0].Select(c => new MapCoord(c, 1)).ToArray()),
            [boss] = new(boss, MapNodeType.Boss, []),
        };
        for (var i = 0; i < rows.Count; i++)
        {
            var row = i + 1;
            foreach (var col in rows[i])
            {
                IReadOnlyList<MapCoord> children;
                if (i == rows.Count - 1)
                {
                    children = [boss];
                }
                else
                {
                    var next = rows[i + 1];
                    var near = next.Where(c => Math.Abs(c - col) <= 1).OrderBy(_ => rng.Next()).Take(rng.Next(1, 3)).ToList();
                    if (near.Count == 0) near.Add(next.OrderBy(c => Math.Abs(c - col)).First());
                    children = near.Select(c => new MapCoord(c, row + 1)).ToArray();
                }
                // Row-specific rules like the game: last row before the boss is all rest sites.
                var type = i == rows.Count - 1 ? MapNodeType.RestSite : bag[rng.Next(bag.Length)];
                nodes[new MapCoord(col, row)] = new MapNode(new MapCoord(col, row), type, children);
            }
        }
        return new MapGraph { Act = 1, Width = width, Height = height, Nodes = nodes, Start = start, Boss = boss };
    }

    /// <summary>Every path from <paramref name="from"/> to the boss by plain DFS (reference implementation for tests).</summary>
    public static List<List<MapCoord>> BruteForcePaths(MapGraph map, MapCoord from)
    {
        var all = new List<List<MapCoord>>();
        Walk(from, []);
        return all;

        void Walk(MapCoord at, List<MapCoord> prefix)
        {
            if (at == map.Boss) { all.Add([.. prefix]); return; }
            foreach (var c in MapPaths.ValidChildren(map, at))
            {
                prefix.Add(c);
                Walk(c, prefix);
                prefix.RemoveAt(prefix.Count - 1);
            }
        }
    }
}

/// <summary>
/// Loads <c>Fixtures/*.json</c>: the <c>saved_map</c> and <c>visited_map_coords</c> of a real <c>current_run.save</c>
/// (coordinates, types and edges only). Deliberately independent of the real save parser in Sts2Overlay.GameState.
/// </summary>
internal static class MapFixture
{
    public static string PathOf(string fileName)
        => System.IO.Path.Combine(TestPaths.RepoRoot, "tests", "Sts2Overlay.Core.Tests", "Map", "Fixtures", fileName);

    public static MapGraph Load(string fileName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(PathOf(fileName)));
        var root = doc.RootElement;
        var map = root.GetProperty("saved_map");

        var nodes = new Dictionary<MapCoord, MapNode>();
        MapNode Add(JsonElement p)
        {
            var coord = Coord(p.GetProperty("coord"));
            var children = p.TryGetProperty("children", out var ch) ? ch.EnumerateArray().Select(Coord).ToArray() : [];
            var node = new MapNode(coord, MapGraph.ParseType(p.GetProperty("type").GetString()), children);
            nodes[coord] = node;
            return node;
        }

        var start = Add(map.GetProperty("start"));
        foreach (var p in map.GetProperty("points").EnumerateArray()) Add(p);
        var boss = Add(map.GetProperty("boss"));
        var visited = root.GetProperty("visited_map_coords").EnumerateArray().Select(Coord).ToArray();

        return new MapGraph
        {
            ActId = root.GetProperty("act_id").GetString() ?? "",
            Act = root.GetProperty("current_act_index").GetInt32() + 1,
            Width = map.GetProperty("width").GetInt32(),
            Height = map.GetProperty("height").GetInt32(),
            Nodes = nodes,
            Start = start.Coord,
            Boss = boss.Coord,
            Current = visited.Length > 0 ? visited[^1] : null,
            Visited = visited,
        };
    }

    private static MapCoord Coord(JsonElement e) => new(e.GetProperty("col").GetInt32(), e.GetProperty("row").GetInt32());
}
