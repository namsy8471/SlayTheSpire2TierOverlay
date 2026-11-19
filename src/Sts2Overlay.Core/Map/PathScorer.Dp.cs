using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Map;

/// <summary>
/// The map DP (<see cref="PathDpWeights"/>): the whole act map as a DAG, a port of the simulator's <c>--path-rule dp</c>
/// (sts2-sim-lab <c>full/path.rs</c>, <c>Dp</c> and <c>dp_values</c>).
/// <para>
/// The rooms, rewards, HP losses and penalties are the scorer's (<see cref="Step"/>), but instead of scoring a list of
/// fixed paths the DP takes the best child at every node for the HP the run actually has there. HP is a random state:
/// each fight has a death chance and <see cref="PathDpWeights.LossPoints"/> equal-probability survived losses, rounded
/// to a 1/<see cref="PathDpWeights.HpSteps"/> grid, and
/// <c>V(node, state) = max over children of E[room value + V(child, next state)]</c>. So a branch that keeps a rest site
/// or a "?" open for the case that a fight goes badly is worth what that option is worth.
/// </para>
/// <para>
/// The map rule on top of the scorer: an elite entered while easy-pool hallway fights are left costs
/// <see cref="PathDpWeights.EarlyElitePenalty"/> per fight left, and a hard-pool hallway fight earns
/// <see cref="PathDpWeights.HardFightReward"/> × its card reward (0: only HP cost and death risk).
/// </para>
/// </summary>
public sealed partial class PathScorer
{
    /// <summary>
    /// DP value of stepping from the current node (or the act start) into each of <paramref name="options"/>; null for
    /// an option that is not a child of it. <paramref name="eliteValue"/> replaces the elite fight's card reward + relic
    /// (null: the scorer's reward).
    /// </summary>
    internal double?[] DpValues(IReadOnlyList<MapCoord> options, double? eliteValue)
    {
        var origin = _map.Current ?? _map.Start;
        var children = MapPaths.ValidChildren(_map, origin);
        var dp = new MapDp(this, eliteValue);
        var start = _start;
        start.Alive = 1;
        var values = new double?[options.Count];
        for (var i = 0; i < options.Count; i++)
        {
            if (children.Contains(options[i])) values[i] = dp.Q(origin, options[i], start);
        }
        return values;
    }

    /// <summary>
    /// <paramref name="n"/> equal-probability survived losses of one fight at <paramref name="hp"/> with mean loss
    /// <paramref name="expected"/> (damage multiplier applied), rescaled to the exact conditional mean
    /// <see cref="LossIfSurvived"/>. Empty when the fight is certain death.
    /// </summary>
    internal static double[] SurvivedLosses(double hp, double expected, int shape, int n)
    {
        n = Math.Max(1, n);
        if (expected <= 0) return new double[n];
        if (DeathChance(hp, expected, shape) >= 1) return [];
        var pts = new double[n];
        if (shape <= 0)
        {
            var top = Math.Min(hp, 2 * expected);
            for (var i = 0; i < n; i++) pts[i] = (i + 0.5) / n * top;
        }
        else
        {
            var scale = expected / shape;
            var below = 1 - ErlangTail(hp / scale, shape);
            for (var i = 0; i < n; i++)
            {
                var target = (i + 0.5) / n * below;
                double lo = 0, hi = hp;
                for (var it = 0; it < 40; it++)
                {
                    var mid = 0.5 * (lo + hi);
                    if (1 - ErlangTail(mid / scale, shape) < target) lo = mid; else hi = mid;
                }
                pts[i] = 0.5 * (lo + hi);
            }
        }
        var mean = pts.Sum() / n;
        var exact = LossIfSurvived(hp, expected, shape);
        if (mean > 1e-12)
        {
            for (var i = 0; i < n; i++) pts[i] = Math.Min(hp, pts[i] * exact / mean);
        }
        return pts;
    }

    private readonly record struct DpKey(MapCoord Node, int Hp, long Hall, int Elites, int Gold, int Removals, int Curses, int Basics, int Upgradable);

    private sealed class MapDp(PathScorer sc, double? eliteValue)
    {
        private readonly double _steps = Math.Max(1, sc._p.Dp.HpSteps);
        private readonly int _points = Math.Max(1, sc._p.Dp.LossPoints);
        private readonly Dictionary<DpKey, double> _memo = new();
        private readonly Dictionary<MapCoord, bool> _restNear = new();

        // Rust f64::round rounds half away from zero.
        private int Quantize(double hp) => (int)Math.Round(Math.Clamp(hp, 0, 1) * _steps, MidpointRounding.AwayFromZero);

        private MapNodeType? TypeOf(MapCoord c) => sc._map.Nodes.TryGetValue(c, out var n) ? n.Type : null;

        /// <summary>A rest site within <see cref="PathWeights.EliteRestLookahead"/> rooms after <paramref name="c"/> on some path.</summary>
        private bool RestNear(MapCoord c)
        {
            if (_restNear.TryGetValue(c, out var b)) return b;
            var frontier = new List<MapCoord> { c };
            var found = false;
            for (var i = 0; i < Math.Max(0, sc._p.EliteRestLookahead); i++)
            {
                var next = new List<MapCoord>();
                foreach (var n in frontier)
                {
                    foreach (var ch in MapPaths.ValidChildren(sc._map, n))
                    {
                        if (TypeOf(ch) == MapNodeType.RestSite) found = true;
                        if (!next.Contains(ch)) next.Add(ch);
                    }
                }
                frontier = next;
            }
            _restNear[c] = found;
            return found;
        }

        /// <summary><c>V(node, s)</c>: the best expected value of the rooms after <paramref name="node"/>, alive there with <paramref name="s"/>.</summary>
        private double Value(MapCoord node, Sim s)
        {
            var key = new DpKey(node, Quantize(s.Hp), (long)Math.Round(s.HallwayFights * 1000, MidpointRounding.AwayFromZero),
                s.Elites, s.Gold, s.RemovalsHere, s.Curses, s.Basics, s.Upgradable);
            if (_memo.TryGetValue(key, out var v)) return v;
            s.Hp = key.Hp / _steps;
            var children = MapPaths.ValidChildren(sc._map, node);
            var best = children.Count == 0 ? 0 : double.NegativeInfinity;
            foreach (var c in children)
            {
                var q = Q(node, c, s);
                if (q > best) best = q;
            }
            _memo[key] = best;
            return best;
        }

        /// <summary>The expected value of stepping from <paramref name="from"/> (alive, state <paramref name="s"/>) into <paramref name="c"/> and on.</summary>
        public double Q(MapCoord from, MapCoord c, Sim s)
        {
            var p = sc._p;
            var dp = p.Dp;
            var t = TypeOf(c) ?? MapNodeType.Unknown;
            double v = 0;
            if (t == MapNodeType.Elite)
            {
                if (TypeOf(from) == MapNodeType.Elite) v -= p.EliteChainPenalty;
                if (s.Hp < p.EliteHpThreshold && !RestNear(c)) v -= p.EliteNoRestPenalty;
                v -= dp.EarlyElitePenalty * Math.Max(0, sc._easyFights - s.HallwayFights);
            }
            double lossA, lossB, pA;
            int shape;
            switch (t)
            {
                case MapNodeType.Monster:
                    pA = Math.Clamp(sc._easyFights - s.HallwayFights, 0, 1);
                    (lossA, lossB, shape) = (sc._monsterLoss, sc._hardMonsterLoss, p.DamageShape.Monster);
                    break;
                case MapNodeType.Elite:
                    (lossA, lossB, pA, shape) = (sc._eliteLoss, sc._eliteLoss, 1, p.DamageShape.Elite);
                    break;
                case MapNodeType.Unknown:
                    (lossA, lossB, pA, shape) = (sc._unknownLoss, sc._unknownLoss, 1, p.DamageShape.Unknown);
                    break;
                default:
                {
                    // No fight: the scorer's room step, one outcome.
                    var n0 = s;
                    n0.Alive = 1;
                    var acc = new Accumulator();
                    sc.Step(ref n0, t, acc);
                    return v + acc.Sum + Value(c, n0);
                }
            }

            // The fight (Fight() with alive = 1), split into outcomes.
            var a = Math.Max(0, lossA * sc.DamageMultiplier);
            var b = Math.Max(0, lossB * sc.DamageMultiplier);
            var dieA = DeathChance(s.Hp, a, shape);
            var dieB = pA >= 1 ? 0 : DeathChance(s.Hp, b, shape);
            var die = pA * dieA + (1 - pA) * dieB;
            v -= p.DeathCost * die;
            if (die >= 1) return v;
            var lostA = LossIfSurvived(s.Hp, a, shape);
            var lost = pA >= 1 ? lostA
                : (pA * (1 - dieA) * lostA + (1 - pA) * (1 - dieB) * LossIfSurvived(s.Hp, b, shape)) / (1 - die);
            v -= p.HpValue * (1 - die) * lost * sc.HpWeight(s.Hp);
            var outcomes = new List<(double W, double Loss)>();
            foreach (var (share, mean) in new[] { (pA * (1 - dieA), a), ((1 - pA) * (1 - dieB), b) })
            {
                if (share <= 0) continue;
                var pts = SurvivedLosses(s.Hp, mean, shape, _points);
                var w = share / (1 - die) / Math.Max(1, pts.Length);
                foreach (var l in pts) outcomes.Add((w, l));
            }

            // The room's reward and bookkeeping (Step()), HP set per outcome below.
            var n = s;
            double reward, heal;
            switch (t)
            {
                case MapNodeType.Monster:
                    n.HallwayFights += 1;
                    reward = sc.CardReward(n.HallwayFights - 1 + n.Elites) * (pA + (1 - pA) * dp.HardFightReward);
                    n.Gold += p.MonsterGold;
                    heal = p.RoomHeal.Monster;
                    break;
                case MapNodeType.Elite:
                    var relic = sc._eliteReward * (n.Elites < sc._targetElites ? 1.0 : p.ExtraEliteFactor);
                    reward = eliteValue ?? sc.CardReward(n.HallwayFights + n.Elites) + relic;
                    n.Elites++;
                    n.Gold += p.EliteGold;
                    heal = p.RoomHeal.Elite;
                    break;
                default:
                    reward = p.UnknownReward + p.UnknownLowHpBonus * (1 - s.Hp);
                    n.HallwayFights += p.UnknownFightChance;
                    n.Gold += p.UnknownGold;
                    heal = p.RoomHeal.Unknown;
                    break;
            }
            var cont = reward;
            foreach (var (w, l) in outcomes)
            {
                var m = n;
                m.Hp = Math.Max(0, s.Hp - l);
                Heal(ref m, heal);
                cont += w * Value(c, m);
            }
            return v + (1 - die) * cont;
        }
    }
}
