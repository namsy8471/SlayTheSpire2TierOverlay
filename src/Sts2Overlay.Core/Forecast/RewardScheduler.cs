using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Forecast;

/// <summary>
/// Samples the card-reward chances left in a run: the current act from the map (a random walk to the boss) or from a
/// typical act layout, then acts 2–3 from the typical layout. One call = one simulated future.
/// </summary>
public sealed class RewardScheduler
{
    /// <summary>
    /// Typical act layout from the user's 51 runs (acts that were finished): floors between the act's ancient and boss,
    /// and the share of those floors that are monster / elite / "?" / shop. The rest are rest sites and the treasure room.
    /// </summary>
    /// <remarks>Act lengths seen: 16.3 / 15.2 / 14.2 map points (incl. ancient and boss). [추정, 표본 29/16/11 막]</remarks>
    internal static readonly ActLayout[] TypicalActs =
    [
        new(Act: 1, StartFloor: 1, ContentFloors: 14, Monster: 0.326, Elite: 0.079, Unknown: 0.215, Shop: 0.103),
        new(Act: 2, StartFloor: 17, ContentFloors: 13, Monster: 0.261, Elite: 0.109, Unknown: 0.246, Shop: 0.109),
        new(Act: 3, StartFloor: 32, ContentFloors: 12, Monster: 0.283, Elite: 0.075, Unknown: 0.261, Shop: 0.112),
    ];

    public const int LastAct = 3;

    private readonly int _act;
    private readonly int _floor;
    private readonly ForecastOptions _options;
    private readonly MapWalk? _walk;

    public RewardScheduler(RunState state, MapGraph? map, ForecastOptions options)
    {
        _act = Math.Clamp(state.Act, 1, LastAct);
        _floor = state.Floor;
        _options = options;
        if (map is not null && map.Nodes.Count > 0 && map.Act == state.Act)
        {
            _walk = MapWalk.Build(map);
        }
    }

    public bool UsesMap => _walk is not null;

    /// <summary>Clears <paramref name="into"/> and fills it with one sampled future, in order.</summary>
    public void Sample(ref ForecastRng rng, List<RewardEvent> into)
    {
        into.Clear();
        if (_walk is not null) _walk.Sample(ref rng, _act, _options, into);
        else SampleTypical(ref rng, _act, RemainingContentFloors(_act, _floor), into);

        for (var act = _act + 1; act <= LastAct; act++)
        {
            SampleTypical(ref rng, act, TypicalActs[act - 1].ContentFloors, into);
        }
    }

    /// <summary>Content floors (between ancient and boss) still ahead in the act, from the global floor number. [추정]</summary>
    internal static int RemainingContentFloors(int act, int floor)
    {
        var layout = TypicalActs[Math.Clamp(act, 1, LastAct) - 1];
        var indexInAct = floor - layout.StartFloor; // 0 = the ancient
        return Math.Clamp(layout.ContentFloors - Math.Max(0, indexInAct), 0, layout.ContentFloors);
    }

    private void SampleTypical(ref ForecastRng rng, int act, int floors, List<RewardEvent> into)
    {
        var l = TypicalActs[act - 1];
        for (var i = 0; i < floors; i++)
        {
            var r = rng.NextDouble();
            if ((r -= l.Monster) < 0) into.Add(new RewardEvent(RewardKind.Normal, act));
            else if ((r -= l.Elite) < 0) into.Add(new RewardEvent(RewardKind.Elite, act));
            else if ((r -= l.Unknown) < 0) AddUnknown(ref rng, act, _options, into);
            else if ((r -= l.Shop) < 0) into.Add(new RewardEvent(RewardKind.Shop, act));
        }
        into.Add(BossEvent(act));
    }

    internal static RewardEvent BossEvent(int act) => new(act >= LastAct ? RewardKind.FinalBoss : RewardKind.Boss, act);

    internal static void AddUnknown(ref ForecastRng rng, int act, ForecastOptions options, List<RewardEvent> into)
    {
        var r = rng.NextDouble();
        if (r < options.UnknownFightChance) into.Add(new RewardEvent(RewardKind.Normal, act));
        else if (r < options.UnknownFightChance + options.UnknownShopChance) into.Add(new RewardEvent(RewardKind.Shop, act));
        else if (options.EventCardChance > 0 && rng.NextDouble() < options.EventCardChance) into.Add(new RewardEvent(RewardKind.EventCard, act));
    }

    internal sealed record ActLayout(int Act, int StartFloor, int ContentFloors, double Monster, double Elite, double Unknown, double Shop);

    /// <summary>The map flattened to arrays so a random walk allocates nothing.</summary>
    private sealed class MapWalk
    {
        private MapNodeType[] _types = [];
        private int[][] _children = [];
        private int _origin;

        public static MapWalk Build(MapGraph map)
        {
            var coords = map.Nodes.Keys.OrderBy(c => c.Row).ThenBy(c => c.Col).ToArray();
            var index = new Dictionary<MapCoord, int>();
            for (var i = 0; i < coords.Length; i++) index[coords[i]] = i;
            var walk = new MapWalk
            {
                _types = coords.Select(c => map.Nodes[c].Type).ToArray(),
                _children = coords.Select(c => MapPaths.ValidChildren(map, c).Select(ch => index[ch]).ToArray()).ToArray(),
            };
            var origin = map.Current is { } cur && index.ContainsKey(cur) ? cur : map.Start;
            walk._origin = index.TryGetValue(origin, out var o) ? o : 0;
            return walk;
        }

        /// <summary>Walks from the current node (not counted: its reward is already decided) to the boss, uniform over children.</summary>
        public void Sample(ref ForecastRng rng, int act, ForecastOptions options, List<RewardEvent> into)
        {
            var at = _origin;
            var reachedBoss = _types[at] == MapNodeType.Boss;
            for (var guard = 0; guard < 64 && !reachedBoss; guard++)
            {
                var ch = _children[at];
                if (ch.Length == 0) break;
                at = ch.Length == 1 ? ch[0] : ch[rng.NextInt(ch.Length)];
                switch (_types[at])
                {
                    case MapNodeType.Monster: into.Add(new RewardEvent(RewardKind.Normal, act)); break;
                    case MapNodeType.Elite: into.Add(new RewardEvent(RewardKind.Elite, act)); break;
                    case MapNodeType.Shop: into.Add(new RewardEvent(RewardKind.Shop, act)); break;
                    case MapNodeType.Unknown: AddUnknown(ref rng, act, options, into); break;
                    case MapNodeType.Boss: into.Add(BossEvent(act)); reachedBoss = true; break;
                }
            }
            if (!reachedBoss && _types[_origin] != MapNodeType.Boss) into.Add(BossEvent(act));
        }
    }
}

/// <summary>
/// SplitMix64: a tiny, fast, seedable generator. Same seed → same sequence on every machine and .NET version,
/// which <see cref="Random"/> does not promise. A struct passed by <c>ref</c> so the hot loop has no allocations.
/// </summary>
public struct ForecastRng(ulong seed)
{
    private ulong _state = seed;

    public ulong NextULong()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1) with 53 random bits.</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform in [0, n).</summary>
    public int NextInt(int n) => (int)((NextULong() >> 33) * (ulong)n >> 31);

    /// <summary>Standard normal (Box–Muller).</summary>
    public double NextGaussian()
    {
        var u1 = 1.0 - NextDouble();
        var u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
