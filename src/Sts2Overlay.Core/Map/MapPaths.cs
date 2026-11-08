namespace Sts2Overlay.Core.Map;

/// <summary>
/// Graph algorithms over a <see cref="MapGraph"/>. The map is a DAG: every edge goes from row r to row r+1 and ends at
/// the boss, so "all paths to the boss" can be handled with memoised recursion instead of brute-force enumeration.
/// </summary>
public static class MapPaths
{
    /// <summary>Children of <paramref name="from"/> that exist in the graph, de-duplicated, in save order.</summary>
    public static IReadOnlyList<MapCoord> ValidChildren(MapGraph map, MapCoord from)
    {
        if (!map.Nodes.TryGetValue(from, out var node) || from == map.Boss) return [];
        var result = new List<MapCoord>(node.Children.Count);
        foreach (var c in node.Children)
        {
            if (map.Nodes.ContainsKey(c) && !result.Contains(c)) result.Add(c);
        }
        return result;
    }

    /// <summary>
    /// Number of distinct paths from <paramref name="from"/> to the boss (saturates at <see cref="long.MaxValue"/>).
    /// DP: <c>count(boss) = 1</c>, <c>count(n) = Σ count(child)</c>. Each node is solved once and remembered, so this
    /// is O(nodes + edges) even though the number of paths grows exponentially with the number of rows.
    /// </summary>
    public static long CountPaths(MapGraph map, MapCoord from)
    {
        var memo = new Dictionary<MapCoord, long>();
        var onStack = new HashSet<MapCoord>();
        return Count(from);

        long Count(MapCoord n)
        {
            if (memo.TryGetValue(n, out var known)) return known;
            if (n == map.Boss) return memo[n] = 1;
            if (!onStack.Add(n)) return 0; // malformed map with a cycle: ignore the back edge
            long total = 0;
            foreach (var c in ValidChildren(map, n))
            {
                var sub = Count(c);
                total = sub > long.MaxValue - total ? long.MaxValue : total + sub;
            }
            onStack.Remove(n);
            return memo[n] = total;
        }
    }

    /// <summary>True when <paramref name="path"/> walks child edges from <paramref name="origin"/> and its last step is the boss.</summary>
    public static bool IsValidPath(MapGraph map, MapCoord origin, IReadOnlyList<MapCoord> path)
    {
        if (path.Count == 0 || path[^1] != map.Boss) return false;
        var at = origin;
        foreach (var step in path)
        {
            if (!ValidChildren(map, at).Contains(step)) return false;
            at = step;
        }
        return true;
    }

    /// <summary>
    /// k-best paths to the boss under an additive node score, for every node at once (beam width <paramref name="beam"/>).
    /// <para>
    /// Why this works: if a path's score is a sum of per-node scores, the best suffix from a node is "best child score +
    /// best suffix of that child". Extending that to the k best: the k best suffixes of a node are among
    /// <c>{ score(child) + s : s in kBest(child) }</c> over all children, so each node only needs its children's lists.
    /// Every node stores at most k entries of (score, which child, which rank in the child's list), so memory is
    /// O(nodes × k) and paths are rebuilt by following those pointers, never stored as full lists.
    /// </para>
    /// <para>If a node has at most <paramref name="beam"/> paths, its list holds all of them (the result is exact).</para>
    /// </summary>
    public static KBestPaths BestPaths(MapGraph map, Func<MapNode, double> nodeScore, int beam)
        => new(map, nodeScore, Math.Max(1, beam));
}

/// <summary>Result of <see cref="MapPaths.BestPaths"/>: per-node lists of the best suffixes, solved lazily.</summary>
public sealed class KBestPaths
{
    /// <summary>One suffix: total score of the nodes after this one, and a pointer into a child's list (child = null at the boss).</summary>
    private readonly record struct Entry(double Score, MapCoord? Child, int ChildRank);

    private readonly MapGraph _map;
    private readonly Func<MapNode, double> _nodeScore;
    private readonly int _beam;
    private readonly Dictionary<MapCoord, Entry[]> _memo = new();
    private readonly HashSet<MapCoord> _onStack = new();

    internal KBestPaths(MapGraph map, Func<MapNode, double> nodeScore, int beam)
    {
        _map = map;
        _nodeScore = nodeScore;
        _beam = beam;
    }

    /// <summary>
    /// Up to <c>beam</c> suffixes from <paramref name="from"/> (excluding it, ending at the boss), best first by the
    /// additive score. Ties keep child order, so the result is deterministic.
    /// </summary>
    public IEnumerable<IReadOnlyList<MapCoord>> Suffixes(MapCoord from)
    {
        var list = Solve(from);
        for (var rank = 0; rank < list.Length; rank++) yield return Rebuild(from, rank);
    }

    /// <summary>Additive score of the best suffixes from <paramref name="from"/>, best first.</summary>
    public IReadOnlyList<double> Scores(MapCoord from) => Solve(from).Select(e => e.Score).ToArray();

    private Entry[] Solve(MapCoord n)
    {
        if (_memo.TryGetValue(n, out var known)) return known;
        if (n == _map.Boss) return _memo[n] = [new Entry(0, null, -1)];
        if (!_onStack.Add(n)) return []; // cycle guard (not expected in real maps)

        var merged = new List<Entry>();
        foreach (var child in MapPaths.ValidChildren(_map, n))
        {
            var sub = Solve(child);
            if (sub.Length == 0) continue; // dead end: no way to the boss through this child
            var s = _nodeScore(_map.Nodes[child]);
            for (var r = 0; r < sub.Length; r++) merged.Add(new Entry(s + sub[r].Score, child, r));
        }

        _onStack.Remove(n);
        // OrderByDescending is a stable sort, so equal scores keep child/rank order.
        return _memo[n] = merged.OrderByDescending(e => e.Score).Take(_beam).ToArray();
    }

    private IReadOnlyList<MapCoord> Rebuild(MapCoord from, int rank)
    {
        var path = new List<MapCoord>();
        var at = from;
        while (true)
        {
            var e = Solve(at)[rank];
            if (e.Child is not { } next) return path;
            path.Add(next);
            at = next;
            rank = e.ChildRank;
        }
    }
}
