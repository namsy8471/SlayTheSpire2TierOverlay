using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.Core.Map;

/// <summary>
/// Scores one path for one run state by walking it room by room with an expected-value simulation (PLAN §7.6):
/// <code>
/// score = Σ room rewards − HP cost − death risk − boss risk − risk patterns
/// </code>
/// <list type="bullet">
/// <item><b>HP is simulated, not counted.</b> Each fight removes an expected fraction of max HP (measured per room type
/// and act from run history, <see cref="PathHpLoss"/>, separately for co-op, then scaled by deck strength); rest sites heal or upgrade, whichever is worth more right then. That is why the same "elite" costs
/// little at 90% HP and a lot at 30%: the cost depends on the HP you walk in with.</item>
/// <item><b>HP is worth more when low.</b> Losing 10% at full HP costs <c>HpValue × 0.1</c>; at 20% HP it costs up to
/// twice that (<see cref="PathWeights.LowHpValueBoost"/>).</item>
/// <item><b>Death risk.</b> Actual damage varies, so we model it as uniform on [0, 2 × expected]. Then
/// <c>P(die) = max(0, 1 − hp / (2 × expected))</c>: zero while HP is at least twice the expected hit, rising linearly
/// below that. It is tiny for hallway fights and dominant for elites at low HP.</item>
/// <item><b>Rewards need survival.</b> The simulation carries P(still alive); every later reward is multiplied by it,
/// so an elite you would probably not survive also wipes out the value of the rooms after it.</item>
/// </list>
/// All numbers come from <see cref="PathWeights"/>: room HP loss, gold and heal are measured from run history ([측정]), point values are estimates ([추정]).
/// </summary>
public sealed class PathScorer
{
    private readonly PathWeights _p;
    private readonly ShopWeights _shop;
    private readonly MapGraph _map;
    private readonly double _cardReward;
    private readonly double _eliteReward;
    private readonly int _targetElites;
    private readonly double _monsterLoss;
    private readonly double _eliteLoss;
    private readonly double _unknownLoss;
    private readonly double _bossLoss;
    private readonly int _removalPrice0;
    private readonly int _removalStep;
    private Sim _start; // not readonly: WithHp() changes it on a clone

    public PathScorer(DataPack pack, RunState state, MapGraph map, DeckProfile profile, PathWeights? path = null)
    {
        _p = path ?? pack.Weights.Path;
        _shop = pack.Weights.Shop;
        _map = map;
        var act = map.Act > 0 ? map.Act : Math.Max(1, state.Act);

        DeckStrength = ComputeDeckStrength(profile, _p);
        // Strong decks take less damage: ×0.5 at strength 1, ×1.5 at strength 0 (default slope).
        DamageMultiplier = StrengthDamageFactor(DeckStrength, _p);

        // Act and player count pick the measured per-room loss (co-op fights are longer and hit harder).
        var loss = _p.HpLossFor(state.IsMultiplayer);
        _monsterLoss = loss.For(MapNodeType.Monster, act);
        _eliteLoss = loss.For(MapNodeType.Elite, act);
        _unknownLoss = loss.For(MapNodeType.Unknown, act);
        _bossLoss = loss.For(MapNodeType.Boss, act);

        // A weak deck needs card rewards more; later acts need them less.
        _cardReward = (_p.MonsterReward + _p.WeakDeckCardBonus * (1 - DeckStrength)) * Weights.ForAct(_p.CardRewardActFactor, act, 1.0);
        _eliteReward = _p.EliteReward * Weights.ForAct(_p.EliteActFactor, act, 1.0);
        _targetElites = Weights.ForAct(_p.TargetElites, act, 2);

        _removalPrice0 = ShopScorer.RemovalPrice(state, pack.Weights);
        _removalStep = ShopScorer.RemovalPrice(state with { CardRemovalsBought = state.CardRemovalsBought + 1 }, pack.Weights) - _removalPrice0;

        _start = new Sim
        {
            Hp = state.HpRatio,
            Alive = 1,
            Gold = Math.Max(0, state.Gold),
            RemovalsHere = 0,
            // Only curses a shop can take: Eternal ones (Ascender's Bane, Greed, …) cannot be removed.
            Curses = state.Deck.Count(c => DeckAnalyzer.IsCurse(pack, state.Character, c.Id) && !pack.IsEternal(c.Id)),
            Basics = profile.BasicStrikes + profile.BasicDefends,
            Upgradable = state.Deck.Count(c => UpgradeAdvisor.CanUpgrade(pack, state.Character, c)),
            Elites = map.Visited.Count(c => map.Nodes.TryGetValue(c, out var n) && n.Type == MapNodeType.Elite),
        };
    }

    /// <summary>Deck strength 0..1 used to scale damage and card-reward value.</summary>
    public double DeckStrength { get; }

    /// <summary>Deck-strength multiplier applied to every expected HP loss (the act is already in <see cref="PathHpLoss"/>).</summary>
    public double DamageMultiplier { get; }

    /// <summary><c>1 + slope × (0.5 − strength)</c>, at least 0.1. Shared with the run-history statistics so both agree.</summary>
    public static double StrengthDamageFactor(double strength, PathWeights p)
        => Math.Max(0.1, 1 + p.StrengthDamageSlope * (0.5 - strength));

    /// <summary>
    /// PLAN §7.6: <c>(1 − mean role gap) × 0.6 + max commit × 0.4</c>, clamped to 0..1. "Role gap" is how far each
    /// role (frontload, block, AoE, ...) is below its act target, so a deck that meets its targets and has committed to
    /// an archetype counts as strong.
    /// </summary>
    public static double ComputeDeckStrength(DeckProfile profile, PathWeights p)
    {
        var gaps = profile.RoleGap.Values.Where(double.IsFinite).ToArray();
        var coverage = gaps.Length == 0 ? p.StrengthUnknown : 1 - gaps.Average();
        var s = coverage * p.StrengthGapWeight + Math.Clamp(profile.MaxCommit, 0, 1) * p.StrengthCommitWeight;
        return double.IsFinite(s) ? Math.Clamp(s, 0, 1) : p.StrengthUnknown;
    }

    /// <summary>
    /// The same scorer with a different starting HP ratio (everything else unchanged). Cheap: no deck re-analysis.
    /// <see cref="PathAdvisor"/> uses it to re-score candidates over an HP grid.
    /// </summary>
    public PathScorer WithHp(double hpRatio)
    {
        var clone = (PathScorer)MemberwiseClone();
        clone._start.Hp = double.IsFinite(hpRatio) ? Math.Clamp(hpRatio, 0, 1) : _start.Hp;
        return clone;
    }

    /// <summary>Starting HP ratio of the simulation.</summary>
    public double StartHp => _start.Hp;

    /// <summary>
    /// Score of entering <paramref name="node"/> straight from the current state, ignoring the rest of the path.
    /// This is the additive per-node score the k-best DP ranks candidates by; the full <see cref="Score"/> then
    /// re-scores those candidates with HP carried along the path.
    /// </summary>
    public double NodeEstimate(MapNode node)
    {
        var sim = _start;
        var acc = new Accumulator();
        Step(ref sim, node.Type, acc);
        return acc.Sum;
    }

    /// <summary>Node types along <paramref name="coords"/>; coordinates missing from the map count as "?" rooms.</summary>
    public MapNodeType[] TypesOf(IReadOnlyList<MapCoord> coords)
        => coords.Select(c => _map.Nodes.TryGetValue(c, out var n) ? n.Type : MapNodeType.Unknown).ToArray();

    /// <summary>Total score only (no breakdown objects): the fast path for re-scoring many candidates.</summary>
    public double Total(IReadOnlyList<MapNodeType> types)
    {
        var acc = new Accumulator();
        Run(types, acc, risks: null, out _, out _);
        return acc.Sum;
    }

    /// <summary>Scores a path (origin excluded). Unknown coordinates count as "?" rooms. Chips are left empty.</summary>
    public PathOption Score(IReadOnlyList<MapCoord> coords)
    {
        var types = TypesOf(coords);
        var acc = new Accumulator();
        var risks = new List<string>();
        var bossHp = Run(types, acc, risks, out var end, out var bossAlive);

        return new PathOption
        {
            Coords = coords.ToArray(),
            Types = types,
            Counts = types.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()),
            Score = acc.ToBreakdown(),
            ExpectedBossHp = bossHp,
            BossArrivalChance = bossAlive,
            ShopPurchases = end.ShopPurchases,
            Risks = risks,
        };
    }

    /// <summary>Walks the path; returns the HP ratio on arrival at the boss (or at the end if there is no boss).</summary>
    private double Run(IReadOnlyList<MapNodeType> types, Accumulator acc, List<string>? risks, out Sim end, out double bossAlive)
    {
        var sim = _start;
        double? bossHp = null;
        double? alive = null;
        for (var i = 0; i < types.Count; i++)
        {
            var t = types[i];
            if (t == MapNodeType.Elite)
            {
                // Path patterns need to look around the node, so they live here and not in Step().
                if (i > 0 && types[i - 1] == MapNodeType.Elite)
                {
                    acc.Add(PathComponentKeys.Patterns, -_p.EliteChainPenalty);
                    AddOnce(risks, PathRisks.EliteChain);
                }
                if (sim.Hp < _p.EliteHpThreshold && !RestWithin(types, i + 1, _p.EliteRestLookahead))
                {
                    acc.Add(PathComponentKeys.Patterns, -_p.EliteNoRestPenalty);
                    AddOnce(risks, PathRisks.EliteNoRest);
                }
            }
            if (t == MapNodeType.Boss)
            {
                bossHp = sim.Hp;
                alive = sim.Alive;
            }
            Step(ref sim, t, acc);
        }
        end = sim;
        bossAlive = alive ?? sim.Alive;
        return bossHp ?? sim.Hp;
    }

    /// <summary>Simulation state carried along a path (a value type, so copying it forks the simulation).</summary>
    private struct Sim
    {
        public double Hp;

        /// <summary>Probability of still being alive at this point of the path (1 at the start).</summary>
        public double Alive;

        public int Gold;
        public int RemovalsHere;
        public int Curses;
        public int Basics;
        public int Upgradable;
        public int Elites;
        public int ShopPurchases;
    }

    private void Step(ref Sim s, MapNodeType type, Accumulator acc)
    {
        switch (type)
        {
            case MapNodeType.Monster:
                Fight(ref s, _monsterLoss, _p.DamageShape.Monster, acc);
                Heal(ref s, _p.RoomHeal.Monster);
                // Rewards are multiplied by s.Alive: you only collect them if you survived to this room.
                acc.Add(PathComponentKeys.Monster, s.Alive * _cardReward);
                s.Gold += _p.MonsterGold;
                break;

            case MapNodeType.Elite:
                Fight(ref s, _eliteLoss, _p.DamageShape.Elite, acc);
                Heal(ref s, _p.RoomHeal.Elite);
                // Diminishing returns past the act's target elite count.
                var relic = _eliteReward * (s.Elites < _targetElites ? 1.0 : _p.ExtraEliteFactor);
                acc.Add(PathComponentKeys.Elite, s.Alive * (_cardReward + relic));
                s.Elites++;
                s.Gold += _p.EliteGold;
                break;

            case MapNodeType.Unknown:
                // "?" is low-variance, which matters more when HP is low.
                var lowness = 1 - s.Hp;
                Fight(ref s, _unknownLoss, _p.DamageShape.Unknown, acc);
                Heal(ref s, _p.RoomHeal.Unknown);
                acc.Add(PathComponentKeys.Unknown, s.Alive * (_p.UnknownReward + _p.UnknownLowHpBonus * lowness));
                s.Gold += _p.UnknownGold;
                break;

            case MapNodeType.RestSite:
                // Heal or upgrade. Healing is worth more the lower HP is (HpWeight). Which one the player picks:
                // below RestHealBelowHp they heal (measured from run history); without a threshold, the better value.
                var heal = Math.Min(_p.RestHeal, 1 - s.Hp);
                var healValue = _p.HpValue * heal * HpWeight(s.Hp);
                var upgradeValue = _p.UpgradeValue * Math.Min(1.0, s.Upgradable / (double)Math.Max(1, _p.UpgradeSaturation));
                // pHeal = chance the player heals here; the simulation takes the expectation of both outcomes.
                var pHeal = upgradeValue <= 0 ? 1.0
                    : _p.RestHealBelowHp is { } below ? HealChance(s.Hp, below, _p.RestHealSoftness)
                    : healValue >= upgradeValue ? 1.0 : 0.0;
                s.Hp += pHeal * heal;
                if (pHeal < 0.5) s.Upgradable = Math.Max(0, s.Upgradable - 1);
                acc.Add(PathComponentKeys.Rest, s.Alive * (pHeal * healValue + (1 - pHeal) * upgradeValue));
                break;

            case MapNodeType.Shop:
                acc.Add(PathComponentKeys.Shop, s.Alive * ShopVisit(ref s));
                Heal(ref s, _p.RoomHeal.Shop);
                break;

            case MapNodeType.Treasure:
                acc.Add(PathComponentKeys.Treasure, s.Alive * _p.TreasureReward);
                s.Gold += _p.TreasureGold;
                Heal(ref s, _p.RoomHeal.Treasure);
                break;

            case MapNodeType.Boss:
                acc.Add(PathComponentKeys.BossRisk, -_p.BossDeathCost * s.Alive * DeathChance(s.Hp, _bossLoss * DamageMultiplier, _p.DamageShape.Boss));
                break;

            default: // Ancient (act start) or anything new: no value either way.
                break;
        }
    }

    /// <summary>
    /// One fight with expected loss <c>loss</c>. Damage follows <see cref="PathDamageShape"/>: a gamma (Erlang)
    /// distribution with that mean and an integer shape, or the old uniform on [0, 2 × loss] when the shape is 0.
    /// <list type="bullet">
    /// <item>P(die) = P(damage ≥ hp); the death cost is charged once, weighted by the chance we were still alive.</item>
    /// <item>The HP we keep is the expectation <i>given that we survived</i>: hp − E[damage | damage &lt; hp]. Near
    /// death that is less than the mean loss, which keeps HP above zero without an arbitrary floor.</item>
    /// </list>
    /// </summary>
    private void Fight(ref Sim s, double baseLoss, int shape, Accumulator acc)
    {
        var loss = Math.Max(0, baseLoss * DamageMultiplier);
        var die = DeathChance(s.Hp, loss, shape);
        acc.Add(PathComponentKeys.DeathRisk, -_p.DeathCost * s.Alive * die);
        s.Alive *= 1 - die;

        var lost = LossIfSurvived(s.Hp, loss, shape);
        acc.Add(PathComponentKeys.HpLoss, -_p.HpValue * s.Alive * lost * HpWeight(s.Hp));
        s.Hp = Math.Max(0, s.Hp - lost);
    }

    /// <summary>Chance to heal at a rest site: a logistic step around <paramref name="below"/> (hard step when softness is 0).</summary>
    internal static double HealChance(double hp, double below, double softness)
        => softness <= 0 ? (hp < below ? 1.0 : 0.0) : 1 / (1 + Math.Exp((hp - below) / softness));

    /// <summary>
    /// Healing outside rest sites (post-combat relics, events, potions bought…): an expected amount per room, capped at
    /// full HP. It only moves HP; its value shows up through lower HP cost and death risk later on the path.
    /// </summary>
    private static void Heal(ref Sim s, double amount)
    {
        if (amount > 0) s.Hp = Math.Min(1, s.Hp + amount);
    }

    /// <summary>Removal first (if useful and affordable), then a purchase if gold is still above the threshold.</summary>
    private double ShopVisit(ref Sim s)
    {
        double value = 0;
        var removalValue = s.Curses > 0 ? _shop.CurseRemovalValue : s.Basics > 0 ? _shop.BasicRemovalValue : 0;
        var price = _removalPrice0 + s.RemovalsHere * _removalStep;
        if (removalValue > 0 && s.Gold >= price)
        {
            value += removalValue;
            s.Gold -= price;
            s.RemovalsHere++;
            if (s.Curses > 0) s.Curses--; else s.Basics--;
        }
        if (s.Gold >= _p.ShopGoldThreshold)
        {
            value += _p.ShopValue;
            s.Gold -= _p.ShopGoldThreshold;
        }
        if (value > 0) s.ShopPurchases++;
        return value > 0 ? value : _p.ShopPoorValue;
    }

    private double HpWeight(double hp) => 1 + _p.LowHpValueBoost * (1 - Math.Clamp(hp, 0, 1));

    /// <summary>P(damage ≥ hp) for damage uniform on [0, 2 × expected].</summary>
    internal static double DeathChance(double hp, double expectedLoss)
        => expectedLoss <= 0 ? 0 : Math.Clamp(1 - hp / (2 * expectedLoss), 0, 1);

    /// <summary>
    /// P(damage ≥ hp) when damage has mean <paramref name="expectedLoss"/> and gamma shape <paramref name="shape"/>
    /// (shape 0 = the uniform model above). For an integer shape k with scale θ = mean / k the tail is
    /// <c>e^(−x) × Σ_{i&lt;k} x^i / i!</c> with x = hp / θ (the chance that fewer than k Poisson events happen).
    /// </summary>
    internal static double DeathChance(double hp, double expectedLoss, int shape)
    {
        if (shape <= 0) return DeathChance(hp, expectedLoss);
        if (expectedLoss <= 0) return 0;
        if (hp <= 0) return 1;
        return ErlangTail(hp / (expectedLoss / shape), shape);
    }

    /// <summary>
    /// E[damage | damage &lt; hp]. Uniform model: the mean loss, or hp / 2 when the fight can kill. Gamma model:
    /// <c>mean × F_{k+1}(hp) / F_k(hp)</c>, because ∫₀ʰᵖ t·f_k(t) dt = kθ·F_{k+1}(hp) for the Erlang density f_k.
    /// </summary>
    internal static double LossIfSurvived(double hp, double expectedLoss, int shape)
    {
        if (expectedLoss <= 0) return 0;
        if (shape <= 0) return hp < 2 * expectedLoss ? hp / 2 : expectedLoss;
        var x = hp / (expectedLoss / shape);
        var below = 1 - ErlangTail(x, shape);
        if (below <= 1e-12) return hp / 2;
        return Math.Min(hp, expectedLoss * (1 - ErlangTail(x, shape + 1)) / below);
    }

    /// <summary>P(Erlang(k, scale 1) ≥ x) = e^(−x) Σ_{i=0}^{k−1} x^i / i!.</summary>
    private static double ErlangTail(double x, int k)
    {
        double term = 1, sum = 1;
        for (var i = 1; i < k; i++)
        {
            term *= x / i;
            sum += term;
        }
        return Math.Clamp(Math.Exp(-x) * sum, 0, 1);
    }

    private static bool RestWithin(IReadOnlyList<MapNodeType> types, int from, int count)
    {
        for (var i = from; i < Math.Min(types.Count, from + count); i++)
        {
            if (types[i] == MapNodeType.RestSite) return true;
        }
        return false;
    }

    private static void AddOnce(List<string>? list, string key)
    {
        if (list is not null && !list.Contains(key)) list.Add(key);
    }

    /// <summary>Sums per component key; turns into a <see cref="ScoreBreakdown"/> whose total is exactly the sum.</summary>
    private sealed class Accumulator
    {
        private static readonly (string Key, string Label)[] Order =
        [
            (PathComponentKeys.Elite, "엘리트"),
            (PathComponentKeys.Monster, "몬스터"),
            (PathComponentKeys.Unknown, "? 방"),
            (PathComponentKeys.Treasure, "보물"),
            (PathComponentKeys.Shop, "상점"),
            (PathComponentKeys.Rest, "휴식처"),
            (PathComponentKeys.HpLoss, "HP 손실"),
            (PathComponentKeys.DeathRisk, "사망 위험"),
            (PathComponentKeys.BossRisk, "보스 위험"),
            (PathComponentKeys.Patterns, "위험 패턴"),
        ];

        // A small array instead of a dictionary: the advisor re-scores thousands of paths per call.
        private readonly double[] _values = new double[Order.Length];

        public void Add(string key, double value) => _values[IndexOf(key)] += value;

        public double Sum => _values.Sum();

        public ScoreBreakdown ToBreakdown()
        {
            var parts = new List<ScoreComponent>();
            for (var i = 0; i < Order.Length; i++)
            {
                if (_values[i] != 0) parts.Add(new ScoreComponent(Order[i].Key, Order[i].Label, _values[i]));
            }
            if (parts.Count == 0) parts.Add(new ScoreComponent(PathComponentKeys.BossRisk, "보스 위험", 0));
            return new ScoreBreakdown(parts);
        }

        private static int IndexOf(string key)
        {
            for (var i = 0; i < Order.Length; i++)
            {
                if (ReferenceEquals(Order[i].Key, key) || Order[i].Key == key) return i;
            }
            throw new ArgumentException($"Unknown path component '{key}'.", nameof(key));
        }
    }
}
