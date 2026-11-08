using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Tests.Scoring;
using static Sts2Overlay.Core.Tests.Map.TestMaps;

namespace Sts2Overlay.Core.Tests.Map;

/// <summary>Invariants over many seeded random maps plus the real fixture.</summary>
public class PathAdvisorPropertyTests
{
    private static readonly double[] HpGrid = [0.05, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85, 1.0];

    public static IEnumerable<object[]> Maps =>
        Enumerable.Range(1, 50).Select(s => new object[] { $"random:{s}" }).Append(["fixture"]);

    private static MapGraph MapFor(string name) => name == "fixture"
        ? MapFixture.Load("map_act2.json")
        : TestMaps.Random(int.Parse(name["random:".Length..]));

    /// <summary>Starter deck, and a deck that meets act 1 role targets (strong) to vary deck strength.</summary>
    private static IEnumerable<IReadOnlyList<DeckCard>?> Decks()
    {
        yield return null;
        yield return new[] { TestPacks.Blocker, TestPacks.Blocker, TestPacks.Blocker, TestPacks.AoeHit, TestPacks.ShivGen,
                TestPacks.ShivGen, TestPacks.OrbPayoff, TestPacks.OrbPayoff, TestPacks.OrbPayoff, TestPacks.OrbPayoff,
                TestPacks.OrbPayoff, TestPacks.OrbPayoff, TestPacks.Drawer, TestPacks.OrbCore }
            .Select(id => new DeckCard(new CardRef(id, 1))).ToArray();
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void LowerHp_NeverIncreasesElitesInTheTopPath(string mapName)
    {
        var map = MapFor(mapName);
        var advisor = new PathAdvisor(Pack);
        foreach (var deck in Decks())
        {
            foreach (var gold in new[] { 0, 200 })
            {
                int? previous = null;
                double previousHp = 0;
                // Walk HP upward: elite count of the top path must be non-decreasing.
                foreach (var hp in HpGrid)
                {
                    var r = advisor.Recommend(State(hp, gold, map.Act, deck), map);
                    var elites = r.Top[0].Elites;
                    if (previous is { } p)
                    {
                        Assert.True(p <= elites,
                            $"{mapName} gold {gold} deck {(deck is null ? "starter" : "strong")}: HP {previousHp:P0} → {p} elites but HP {hp:P0} → {elites}");
                    }
                    previous = elites;
                    previousHp = hp;
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void EveryResultIsValid_FromEveryReachableNode(string mapName)
    {
        var map = MapFor(mapName);
        var advisor = new PathAdvisor(Pack);
        var reachable = new HashSet<MapCoord> { map.Start };
        var queue = new Queue<MapCoord>([map.Start]);
        while (queue.TryDequeue(out var at))
        {
            foreach (var c in MapPaths.ValidChildren(map, at)) if (reachable.Add(c)) queue.Enqueue(c);
        }

        foreach (var at in reachable)
        {
            var current = map with { Current = at };
            var r = advisor.Recommend(State(0.6, 100, map.Act), current);
            PathAssert.WellFormed(current, r);
            Assert.Equal(at == map.Boss || MapPaths.CountPaths(map, at) == 0, r.IsEmpty);
            Assert.Equal(at == map.Boss ? 0 : MapPaths.CountPaths(map, at), r.TotalPaths);
        }
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void TopPath_IsTheBestOfAllPaths(string mapName)
    {
        // Exhaustive check against brute force: among paths within the elite cap, none scores higher than Top[0];
        // at full HP there is no cap, so Top[0] is the best of all paths.
        var map = MapFor(mapName);
        var advisor = new PathAdvisor(Pack);
        foreach (var hp in new[] { 1.0, 0.5 })
        {
            var state = State(hp, 150, map.Act);
            var r = advisor.Recommend(state, map);
            var all = TestMaps.BruteForcePaths(map, r.Origin);
            if (all.Count > 5000) return; // keep the test quick on the rare huge random map

            var best = all.Select(p => advisor.ScorePath(state, map, p))
                .Where(o => PathAssert.InCap(r, o))
                .Max(o => o.Total);
            Assert.Equal(best, r.Top[0].Total, 9);
            if (hp == 1.0) Assert.Null(r.EliteCap);
        }
    }
}
