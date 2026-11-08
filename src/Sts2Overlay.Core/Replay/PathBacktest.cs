using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Map;

namespace Sts2Overlay.Core.Replay;

/// <summary>The scorer's fight model applied to one recorded fight (actual HP on entry).</summary>
public sealed record FightPrediction(string RunKey, long PlayerId, bool Multiplayer, int Act, string Room,
    double HpIn, double ExpectedLoss, double ActualLoss, double DeathChance, bool Died);

/// <summary>The scorer's simulation of the path actually walked in one act, next to what happened.</summary>
public sealed record ActPrediction
{
    public required string RunKey { get; init; }
    public long PlayerId { get; init; }
    public bool Multiplayer { get; init; }
    public int Act { get; init; }
    public double StartHp { get; init; }

    /// <summary>Rooms walked after the act's ancient (boss included when reached).</summary>
    public required IReadOnlyList<MapNodeType> Path { get; init; }

    public bool ReachedBoss { get; init; }

    /// <summary>HP ratio on arrival at the boss (null when the boss was not reached).</summary>
    public double? ActualBossHp { get; init; }

    public bool DiedBeforeBoss { get; init; }

    /// <summary>Simulated HP ratio at the boss (or at the end of a path cut short by death).</summary>
    public double PredictedBossHp { get; init; }

    /// <summary>Simulated chance to survive the rooms walked before the boss (for a death: through the death room).</summary>
    public double PredictedArrival { get; init; }
}

/// <summary>Aggregate quality of one weight set (lower Brier / MAE = better).</summary>
public sealed record CalibrationSummary
{
    public required string Label { get; init; }
    public int Fights { get; init; }
    public int Deaths { get; init; }
    public double PredictedDeaths { get; init; }

    /// <summary>Mean of (P(die) − died)² over fights.</summary>
    public double FightBrier { get; init; }

    /// <summary>Per room: (fights, deaths, predicted deaths, mean expected loss, mean actual loss).</summary>
    public IReadOnlyList<RoomCalibration> ByRoom { get; init; } = [];

    public int Acts { get; init; }
    public int ReachedBoss { get; init; }

    /// <summary>Mean |predicted − actual| boss-arrival HP over acts that reached the boss.</summary>
    public double BossHpMae { get; init; }

    /// <summary>Mean (predicted − actual): positive = the model is too optimistic about HP.</summary>
    public double BossHpBias { get; init; }

    /// <summary>MAE of the "always predict the average" baseline, for scale.</summary>
    public double BaselineBossHpMae { get; init; }

    public int DiedBeforeBoss { get; init; }
    public double PredictedDiedBeforeBoss { get; init; }

    /// <summary>Mean of ((1 − arrival) − diedBeforeBoss)² over acts.</summary>
    public double ArrivalBrier { get; init; }
}

public sealed record RoomCalibration(string Room, int Fights, int Deaths, double PredictedDeaths, double ExpectedLossMean, double ActualLossMean);

/// <summary>One decision on a real map: where the player went vs what the advisor said.</summary>
public sealed record RealMapDecision(int Step, MapCoord From, MapCoord Taken, MapNodeType TakenType, MapCoord? Recommended,
    MapNodeType? RecommendedType, int Choices, double HpRatio);

public sealed record RealMapResult
{
    public required IReadOnlyList<MapCoord> Taken { get; init; }
    public required PathOption TakenScored { get; init; }
    public required PathRecommendation AtStart { get; init; }

    /// <summary>1-based rank of the taken path among all paths from the act start, by score at the start state.</summary>
    public int TakenRank { get; init; }

    public long TotalPaths { get; init; }
    public IReadOnlyList<RealMapDecision> Decisions { get; init; } = [];
    public double StartHp { get; init; }
    public double? ActualBossHp { get; init; }
    public bool SurvivedAct { get; init; }

    /// <summary>Decisions with more than one child, and how many of those matched the advisor's next step.</summary>
    public int RealChoices => Decisions.Count(d => d.Choices > 1);

    public int Agreed => Decisions.Count(d => d.Choices > 1 && d.Recommended == d.Taken);
}

/// <summary>
/// Backtests for <see cref="PathWeights"/>. The <c>.run</c> files keep only the path that was walked, not the map, so
/// "what would the advisor have chosen" can only be asked where a map was saved separately. What the history does
/// support is checking the scorer's <i>model</i>: its expected damage and death chance per fight, and its simulated
/// HP at the boss for the path that was walked.
/// </summary>
public static class PathBacktest
{
    /// <summary>Applies the fight model to every recorded fight (monster, elite, boss and "?" rooms).</summary>
    public static IReadOnlyList<FightPrediction> Fights(IReadOnlyList<PathFloor> floors, PathWeights w)
    {
        var result = new List<FightPrediction>();
        foreach (var f in floors)
        {
            if (f.Type is not (MapNodeType.Monster or MapNodeType.Elite or MapNodeType.Boss or MapNodeType.Unknown)) continue;
            var factor = double.IsFinite(f.DeckStrength) ? PathScorer.StrengthDamageFactor(f.DeckStrength, w) : 1.0;
            var loss = w.HpLossFor(f.Multiplayer).For(f.Type, f.Act) * factor;
            result.Add(new FightPrediction(f.RunKey, f.PlayerId, f.Multiplayer, f.Act, PathStats.RoomName(f.Type),
                f.HpRatioBefore, loss, f.DamageFraction, PathScorer.DeathChance(f.HpRatioBefore, loss), f.Died));
        }
        return result;
    }

    /// <summary>
    /// For every player and act: the deck/HP state after the act's ancient, then <see cref="PathAdvisor.ScorePath"/> on
    /// the rooms actually walked (as a straight-line map). Acts cut short without a death (abandoned) are skipped.
    /// </summary>
    public static IReadOnlyList<ActPrediction> Acts(IReadOnlyList<RunHistory> runs, DataPack pack)
    {
        var advisor = new PathAdvisor(pack);
        var result = new List<ActPrediction>();
        foreach (var run in runs)
        {
            var lastIndex = run.Nodes.Count - 1;
            var lost = !run.Win && !run.WasAbandoned;
            foreach (var player in run.Players)
            {
                var timeline = DeckTimeline.Build(run, player.Id);
                foreach (var act in run.Nodes.GroupBy(n => n.ActIndex))
                {
                    var nodes = act.Where(n => n.IndexInAct > 0 || n.MapPointType != "ancient").ToArray();
                    if (nodes.Length == 0) continue;
                    var firstIndex = nodes[0].Floor - 1;
                    var types = nodes.Select(n => MapGraph.ParseType(n.MapPointType)).ToArray();
                    var reachedBoss = types[^1] == MapNodeType.Boss;
                    var endIndex = nodes[^1].Floor - 1;
                    var died = lost && endIndex == lastIndex && (nodes[^1].StatsFor(player.Id)?.CurrentHp ?? 1) <= 0;
                    if (!reachedBoss && !died) continue;

                    var state = timeline.StateBefore(firstIndex);
                    var map = Chain(types, act.Key + 1);
                    var coords = map.Nodes.Keys.Where(c => c != map.Start).OrderBy(c => c.Row).ToArray();
                    var option = advisor.ScorePath(state, map, coords);

                    double? bossHp = reachedBoss ? timeline.StateBefore(endIndex).HpRatio : null;
                    result.Add(new ActPrediction
                    {
                        RunKey = PathFloorExtractor.KeyOf(run),
                        PlayerId = player.Id,
                        Multiplayer = run.IsMultiplayer,
                        Act = act.Key + 1,
                        StartHp = state.HpRatio,
                        Path = types,
                        ReachedBoss = reachedBoss,
                        ActualBossHp = bossHp,
                        DiedBeforeBoss = !reachedBoss && died,
                        PredictedBossHp = option.ExpectedBossHp,
                        PredictedArrival = option.BossArrivalChance,
                    });
                }
            }
        }
        return result;
    }

    public static CalibrationSummary Summarize(string label, IReadOnlyList<FightPrediction> fights, IReadOnlyList<ActPrediction> acts)
    {
        var reached = acts.Where(a => a.ReachedBoss && a.ActualBossHp is not null).ToArray();
        var meanActual = reached.Length == 0 ? 0 : reached.Average(a => a.ActualBossHp!.Value);
        return new CalibrationSummary
        {
            Label = label,
            Fights = fights.Count,
            Deaths = fights.Count(f => f.Died),
            PredictedDeaths = fights.Sum(f => f.DeathChance),
            FightBrier = fights.Count == 0 ? double.NaN : fights.Average(f => Math.Pow(f.DeathChance - (f.Died ? 1 : 0), 2)),
            ByRoom = fights.GroupBy(f => f.Room).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new RoomCalibration(g.Key, g.Count(), g.Count(f => f.Died), g.Sum(f => f.DeathChance),
                    g.Average(f => f.ExpectedLoss), g.Average(f => f.ActualLoss)))
                .ToArray(),
            Acts = acts.Count,
            ReachedBoss = reached.Length,
            BossHpMae = reached.Length == 0 ? double.NaN : reached.Average(a => Math.Abs(a.PredictedBossHp - a.ActualBossHp!.Value)),
            BossHpBias = reached.Length == 0 ? double.NaN : reached.Average(a => a.PredictedBossHp - a.ActualBossHp!.Value),
            BaselineBossHpMae = reached.Length == 0 ? double.NaN : reached.Average(a => Math.Abs(meanActual - a.ActualBossHp!.Value)),
            DiedBeforeBoss = acts.Count(a => a.DiedBeforeBoss),
            PredictedDiedBeforeBoss = acts.Sum(a => 1 - a.PredictedArrival),
            ArrivalBrier = acts.Count == 0 ? double.NaN : acts.Average(a => Math.Pow(1 - a.PredictedArrival - (a.DiedBeforeBoss ? 1 : 0), 2)),
        };
    }

    /// <summary>A straight-line map start → rooms → last room (row = step), used to score a walked path.</summary>
    public static MapGraph Chain(IReadOnlyList<MapNodeType> types, int act)
    {
        var nodes = new Dictionary<MapCoord, MapNode>();
        for (var i = 0; i <= types.Count; i++)
        {
            var coord = new MapCoord(0, i);
            var type = i == 0 ? MapNodeType.Ancient : types[i - 1];
            IReadOnlyList<MapCoord> children = i < types.Count ? [new MapCoord(0, i + 1)] : [];
            nodes[coord] = new MapNode(coord, type, children);
        }
        return new MapGraph
        {
            Act = act,
            Width = 1,
            Height = types.Count + 1,
            Nodes = nodes,
            Start = new MapCoord(0, 0),
            Boss = new MapCoord(0, types.Count),
        };
    }

    /// <summary>Every path from the map's start whose node types equal <paramref name="types"/> in order.</summary>
    public static IReadOnlyList<IReadOnlyList<MapCoord>> MatchPaths(MapGraph map, IReadOnlyList<MapNodeType> types)
    {
        var result = new List<IReadOnlyList<MapCoord>>();
        var stack = new List<MapCoord>();
        Walk(map.Start, 0);
        return result;

        void Walk(MapCoord at, int i)
        {
            if (i == types.Count)
            {
                result.Add(stack.ToArray());
                return;
            }
            foreach (var c in MapPaths.ValidChildren(map, at))
            {
                if (map.Nodes[c].Type != types[i]) continue;
                stack.Add(c);
                Walk(c, i + 1);
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    /// <summary>
    /// Replays one act on its real map: the advisor at the act start (top path vs the walked path, and the walked
    /// path's rank among all paths), then at every step the advisor's next node vs the node actually taken.
    /// Returns null when the walked rooms do not fit the map (or fit it in more than one way).
    /// </summary>
    public static RealMapResult? RealMap(RunHistory run, long playerId, int actIndex, MapGraph map, DataPack pack)
    {
        var nodes = run.Nodes.Where(n => n.ActIndex == actIndex && (n.IndexInAct > 0 || n.MapPointType != "ancient")).ToArray();
        if (nodes.Length == 0) return null;
        var types = nodes.Select(n => MapGraph.ParseType(n.MapPointType)).ToArray();
        var matches = MatchPaths(map, types);
        if (matches.Count != 1) return null;
        var taken = matches[0];

        var advisor = new PathAdvisor(pack);
        var timeline = DeckTimeline.Build(run, playerId);
        var baseMap = map with { Current = null, Visited = [map.Start] };
        var startState = timeline.StateBefore(nodes[0].Floor - 1);
        var atStart = advisor.Recommend(startState, baseMap);

        // Rank of the walked path among every path, all scored at the start state (no elite cap: plain score order).
        var all = AllPaths(baseMap, baseMap.Start);
        var takenTotal = advisor.ScorePath(startState, baseMap, taken).Total;
        var rank = 1 + all.Count(p => advisor.ScorePath(startState, baseMap, p).Total > takenTotal + 1e-9);

        var decisions = new List<RealMapDecision>();
        for (var k = 0; k < taken.Count; k++)
        {
            var from = k == 0 ? map.Start : taken[k - 1];
            var state = timeline.StateBefore(nodes[k].Floor - 1);
            var current = map with { Current = k == 0 ? null : from, Visited = [map.Start, .. taken.Take(k)] };
            var rec = advisor.Recommend(state, current);
            decisions.Add(new RealMapDecision(k + 1, from, taken[k], map.Nodes[taken[k]].Type, rec.NextStep,
                rec.NextStep is { } n ? map.Nodes[n].Type : null, MapPaths.ValidChildren(map, from).Count, state.HpRatio));
        }

        var reachedBoss = types[^1] == MapNodeType.Boss;
        var bossIndex = nodes[^1].Floor - 1;
        var survived = (nodes[^1].StatsFor(playerId)?.CurrentHp ?? 0) > 0;
        return new RealMapResult
        {
            Taken = taken,
            TakenScored = advisor.ScorePath(startState, baseMap, taken),
            AtStart = atStart,
            TakenRank = rank,
            TotalPaths = all.Count,
            Decisions = decisions,
            StartHp = startState.HpRatio,
            ActualBossHp = reachedBoss ? timeline.StateBefore(bossIndex).HpRatio : null,
            SurvivedAct = reachedBoss && survived,
        };
    }

    private static List<IReadOnlyList<MapCoord>> AllPaths(MapGraph map, MapCoord from)
    {
        var result = new List<IReadOnlyList<MapCoord>>();
        var stack = new List<MapCoord>();
        Walk(from);
        return result;

        void Walk(MapCoord at)
        {
            if (at == map.Boss)
            {
                result.Add(stack.ToArray());
                return;
            }
            foreach (var c in MapPaths.ValidChildren(map, at))
            {
                stack.Add(c);
                Walk(c);
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }
}
