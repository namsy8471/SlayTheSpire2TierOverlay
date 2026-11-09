using System.Globalization;
using System.Text;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Hooks;
using Sts2Sim.Core.MonsterMoves.Intents;
using Sts2Sim.Core.ValueProps;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Potions;

namespace SimTrial;

/// <summary>A combat-playing policy: given the live combat, return the next action.</summary>
public interface ICombatPolicy
{
    LookaheadPolicy.Action Choose(CombatState state);
}

/// <summary>
/// Tunable numbers of the beam policy and its evaluation, settable from the command line
/// (<c>--set name=value</c>). Plain static fields: read in the hot loop, written once at start-up.
/// </summary>
public static class Tun
{
    // search
    public static int Beam = 8;          // partial turns kept per depth
    public static int MaxDepth = 12;     // card plays per turn
    public static int LeafK = 16;        // turn plans that get an exact enemy-turn simulation
    public static int LeafHorizon = 4;   // player turns looked at per plan (2 = also the next turn)
    public static int DeepK = 0;         // >0: only the DeepK best one-turn plans get the full horizon
    public static int NestBeam = 0;      // >0: next turns are planned by a nested beam search (else greedy)
    public static int NestLeafK = 3;
    public static int Rollout = 1;       // next-turn rollout: 0 = fixed card-type priorities, 1 = one-step greedy on Eval.Static

    // evaluation (units: player HP points)
    public static double EnemyW = 0.6;   // one enemy HP point (fixed weight, used when RaceK = 0)
    // Race model (RaceK > 0): enemy HP weight = RaceK * leak * D / O, from this fight's running averages:
    // D = enemies' attack damage per turn, O = enemy HP removed per player turn, leak = share of D not blocked.
    public static double RaceK = 0.7;
    public static double RaceMin = 0.2, RaceMax = 2.0;
    public static double PriorO = 14;    // prior damage output per turn (act 1; +PriorOAct per later act)
    public static double PriorOAct = 4;
    public static double PriorLeak = 0.4;
    public static double PriorTurns = 2; // prior strength, in turns
    public static double KillW = 8;      // a primary enemy killed
    public static double BlockW = 0.25;  // block carried into the next turn
    public static double ThreatW = 0.0;  // enemies' next-turn intended damage (after the enemy turn)
    public static double BuffW = 1.5;    // player buff stacks (each power capped at 10)
    public static double DebuffW = 1.5;  // player debuff stacks
    public static double EDebuffW = 1.0; // enemy debuff stacks other than poison/doom (capped 15)
    public static double EBuffW = 0.5;   // enemy buff stacks
    public static double PoisonW = 0.0;  // fraction of poison's remaining total damage counted as dealt
    public static double DoomW = 0.0;    // fraction of doom counted as dealt
    public static double OrbW = 2;
    public static double PetW = 0.3;
    public static double SandpitW = 80;  // The Insatiable's Sandpit on the player: SandpitW / turns left
    public static double LowHpW = 0.0;   // extra value per HP below LowHpAt * max HP (risk aversion)
    public static double LowHpAt = 0.3;
    public static double SurviveTurnW = 150; // lost lines: value per turn survived before dying (0 = old behaviour)
    public static double LostDmgW = 20;      // lost lines: value of the share of enemy HP removed (old: 100)
    public static int Backtracks = 0;          // >0: on a loss, rewind to an earlier turn and play its next-best plan (uses the exact future)
    public static double PotionHoldW = 6; // value of a potion still held (search uses one only if it gains more); <0 = never use

    /// <summary>
    /// <c>--quality high</c>: a wider search for when accuracy matters more than speed (about half the
    /// combats/s). Dev boss set: death 21.3% -> 16.2%, damage ratio 1.24x -> 1.16x.
    /// </summary>
    public static void Quality(string level)
    {
        if (level == "high") { LeafK = 40; Beam = 10; LeafHorizon = 4; }
        else if (level != "normal") throw new ArgumentException("quality: normal | high");
    }

    public static void Set(string kv)
    {
        int eq = kv.IndexOf('=');
        if (eq < 0) throw new ArgumentException("--set expects name=value: " + kv);
        string name = kv[..eq].Trim();
        string value = kv[(eq + 1)..].Trim();
        var f = typeof(Tun).GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase)
                ?? throw new ArgumentException("unknown tunable: " + name);
        object v = f.FieldType == typeof(int)
            ? (object)int.Parse(value, CultureInfo.InvariantCulture) // cast: else the ?: unifies to double
            : double.Parse(value, CultureInfo.InvariantCulture);
        f.SetValue(null, v);
    }

    public static string Describe()
    {
        var sb = new StringBuilder();
        foreach (var f in typeof(Tun).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            sb.Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(null), CultureInfo.InvariantCulture)).Append(' ');
        return sb.ToString().TrimEnd();
    }
}

/// <summary>State scoring used by the beam policy.</summary>
public static class Eval
{
    /// <summary>
    /// Value of a state at the start of a player turn (after the enemies acted).
    /// win: 1000 + HP; loss: -1000 + 100 * fraction of enemy HP removed.
    /// </summary>
    public static double Leaf(CombatState state, Player p)
    {
        CombatEngine engine = state.Engine!;
        if (!engine.IsInProgress && engine.Won) return 1000 + p.Creature.CurrentHp;
        if (p.Creature.IsDead || !engine.IsInProgress) return Lost(state, p);
        double threat = Tun.ThreatW != 0 ? Incoming(state, p) : 0;
        return Common(state, p, p.Creature.CurrentHp) + Tun.BlockW * p.Creature.Block - Tun.ThreatW * threat;
    }

    /// <summary>
    /// Cheap mid-turn estimate used to order partial turns: HP after the enemies' announced attacks hit
    /// the current block, plus the same non-HP terms as <see cref="Leaf"/>.
    /// </summary>
    public static double Static(CombatState state, Player p)
    {
        CombatEngine engine = state.Engine!;
        if (!engine.IsInProgress && engine.Won) return 1000 + p.Creature.CurrentHp;
        if (p.Creature.IsDead || !engine.IsInProgress) return Lost(state, p);
        (int blockable, int unblockable) = TurnEndSelfDamage(p);
        int unblocked = Math.Max(0, Incoming(state, p) + blockable - p.Creature.Block) + unblockable;
        double hp = p.Creature.CurrentHp - unblocked;
        if (hp <= 0) return -500 + hp;
        return Common(state, p, hp, midTurn: true);
    }

    /// <summary>
    /// Damage the player deals to itself at the end of this turn: status/curse cards still in hand
    /// (Burn, Wither, Regret, ...) and Disintegration. Without it the search happily ends a turn
    /// holding five Withers. Returns (blocked by block, ignores block).
    /// </summary>
    public static (int Blockable, int Unblockable) TurnEndSelfDamage(Player p)
    {
        int blockable = 0, unblockable = 0;
        var hand = p.PlayerCombatState!.Hand.Cards;
        foreach (CardModel c in hand)
        {
            if (!c.HasTurnEndInHandEffectForPrediction) continue;
            switch (c)
            {
                case Wither w: blockable += w.TurnEndDamage; break;
                case Burn: case Decay: blockable += 2; break;
                case Infection: blockable += 3; break;
                case Toxic: blockable += 5; break;
                case Regret: unblockable += hand.Count; break;
                case BadLuck: unblockable += 13; break;
                case Beckon: unblockable += 6; break;
            }
        }
        foreach (PowerModel pw in p.Creature.Powers)
            if (pw is DisintegrationPower) blockable += Math.Abs(pw.Amount);
        return (blockable, unblockable);
    }

    /// <summary>
    /// An enemy that is gone for scoring: dead, or the Waterfall Giant after its "death" (it is set to
    /// ~1e9 HP for one stunned turn and then explodes; its HP no longer matters, the blast is an intent).
    /// </summary>
    public static bool Spent(Creature e) => (!e.IsAlive && Reserve(e) == 0) || e.MaxHp >= 100_000_000;

    /// <summary>
    /// HP of forms still to come. Test Subject revives twice (111 -> 212 -> 313 HP at A10); without this a
    /// phase kill looks like the boss gaining HP, so the search avoided finishing phases.
    /// </summary>
    public static double Reserve(Creature e) =>
        e.Monster is TestSubject ts && e.GetPower<NemesisPower>() is null
            ? (e.MaxHp <= ts.FirstFormHp ? ts.SecondFormHp + ts.ThirdFormHp : ts.ThirdFormHp)
            : 0;

    /// <summary>
    /// Damage the enemies' announced attacks will deal to the player, with the game's damage hooks
    /// applied (strength, weak, vulnerable, intangible, relics). Intents store the raw number only.
    /// </summary>
    public static int Incoming(CombatState state, Player p)
    {
        decimal total = 0;
        foreach (Creature e in state.Enemies)
        {
            if (!e.IsAlive || e.Monster?.NextMove is not { } move) continue;
            foreach (AbstractIntent intent in move.Intents)
            {
                if (intent is not AttackIntent atk || atk.DamageCalc is null) continue;
                try
                {
                    decimal hit = Math.Max(0m, atk.DamageCalc());
                    hit = Hook.ModifyDamage(state, p.Creature, e, hit, ValueProp.Move, null, null, out _);
                    total += Math.Max(0m, Math.Floor(hit)) * Math.Max(1, atk.Repeats);
                }
                catch (Exception) { }
            }
        }
        return (int)total;
    }

    /// <summary>
    /// A lost line: -1000, plus SurviveTurnW per player turn survived since the plan started (a later
    /// death may be avoidable by better play than the rollout's), plus LostDmgW * share of enemy HP removed.
    /// </summary>
    private static double Lost(CombatState state, Player p)
    {
        double hp = 0, max = 0;
        foreach (Creature e in state.Enemies)
        {
            if (e.MaxHp >= 100_000_000) continue;
            double reserve = Reserve(e);
            max += e.Monster is TestSubject t ? t.FirstFormHp + t.SecondFormHp + t.ThirdFormHp : e.MaxHp;
            if (e.IsAlive) hp += e.CurrentHp;
            hp += reserve;
        }
        int survived = Math.Max(0, (p.PlayerCombatState?.TurnNumber ?? CurPlanTurn) - CurPlanTurn);
        return -1000 + Tun.SurviveTurnW * survived + (max > 0 ? Tun.LostDmgW * (1 - hp / max) : 0);
    }

    /// <summary>Player turn number at which the plan being searched on this thread started.</summary>
    [ThreadStatic] public static int CurPlanTurn;

    /// <summary>Enemy HP weight for the plan being searched on this thread (race model or fixed).</summary>
    [ThreadStatic] public static double CurEnemyW;

    /// <summary>Summed enemy HP (+half block) of living enemies.</summary>
    public static double EnemyHp(CombatState state)
    {
        double hp = 0;
        foreach (Creature e in state.Enemies)
            if (!Spent(e)) hp += (e.IsAlive ? e.CurrentHp + 0.5 * e.Block : 0) + Reserve(e);
        return hp;
    }

    private static double Common(CombatState state, Player p, double hp, bool midTurn = false)
    {
        double enemyHp = 0, kills = 0, enemyDebuffs = 0, enemyBuffs = 0, sandpit = 0;
        foreach (Creature e in state.Enemies)
        {
            if (Spent(e))
            {
                if (e.IsPrimaryEnemy) kills++;
                continue;
            }
            if (!e.IsAlive) // between forms: only the forms to come are left
            {
                enemyHp += Reserve(e);
                continue;
            }
            // 2. Mid-turn (Static) the enemy's block only counts if it survives into its turn: most
            // enemies lose it at their turn start, so a kill-adjacent line should not look worse
            // because the other enemy just gained block (Kaiser Crab's rage).
            double eff = e.CurrentHp + (midTurn && Hook.ShouldClearBlock(state, e) ? 0 : 0.5) * e.Block;
            foreach (PowerModel pw in e.Powers)
            {
                int a = Math.Abs(pw.Amount);
                if (pw is PoisonPower)
                {
                    if (Tun.PoisonW != 0) eff -= Tun.PoisonW * Math.Min(e.CurrentHp, a * (a + 1) / 2.0);
                    else enemyDebuffs += Math.Min(a, 15);
                }
                else if (pw is DoomPower)
                {
                    if (Tun.DoomW != 0) eff -= Tun.DoomW * Math.Min(e.CurrentHp, a);
                    else enemyDebuffs += Math.Min(a, 15);
                }
                else if (pw is SandpitPower sand)
                {
                    // The Insatiable: the counter drops each enemy turn and kills its target at 0;
                    // Frantic Escape adds 1. A countdown to death, not a buff to shrink.
                    if (sand.Target == p.Creature) sandpit += Tun.SandpitW / Math.Max(1, a);
                }
                else if (pw.Type == PowerType.Debuff) enemyDebuffs += Math.Min(a, 15);
                else if (pw.Type == PowerType.Buff) enemyBuffs += Math.Min(a, 15);
            }
            enemyHp += Math.Max(0, eff) + Reserve(e);
        }

        double buffs = 0, debuffs = 0;
        foreach (PowerModel pw in p.Creature.Powers)
        {
            int a = Math.Min(Math.Abs(pw.Amount), 10);
            if (pw.Type == PowerType.Buff) buffs += a;
            else if (pw.Type == PowerType.Debuff) debuffs += a;
        }
        double pets = 0;
        foreach (Creature pet in p.Creature.Pets) if (pet.IsAlive) pets += pet.CurrentHp;
        int orbs = 0;
        try { orbs = p.PlayerCombatState?.OrbQueue.Orbs.Count ?? 0; } catch (Exception) { }

        double held = 0;
        foreach (PotionModel? pot in p.PotionSlots) if (pot is not null && pot.Usage != PotionUsage.Automatic) held++;

        double low = 0;
        if (Tun.LowHpW != 0)
        {
            double at = Tun.LowHpAt * p.Creature.MaxHp;
            if (hp < at) low = -Tun.LowHpW * (at - hp);
        }

        return hp + low + Tun.PotionHoldW * held
               - CurEnemyW * enemyHp
               + Tun.KillW * kills
               + Tun.BuffW * buffs - Tun.DebuffW * debuffs
               + Tun.EDebuffW * enemyDebuffs - Tun.EBuffW * enemyBuffs
               + Tun.OrbW * orbs
               + Tun.PetW * pets
               - sandpit;
    }
}

/// <summary>
/// Turn-level beam search. At the start of a turn (or when the live state stops matching the plan)
/// it searches sequences of card plays on cloned states: every partial turn is a possible place to
/// end the turn. Partial turns are ordered by <see cref="Eval.Static"/> and the best
/// <see cref="Tun.Beam"/> are expanded per depth (identical states merged). The
/// <see cref="Tun.LeafK"/> best partial turns (plus "end now") are finished exactly: end the turn on a
/// clone, let the enemies act, score with <see cref="Eval.Leaf"/>. The winning plan is then played
/// step by step. One instance per fight (it caches the plan).
/// </summary>
public sealed class BeamPolicy : ICombatPolicy
{
    public static long Failures;

    /// <summary>Rough time split (Stopwatch ticks, summed over threads) for SIMLAB_PROFILE=1 runs.</summary>
    public static readonly bool Profile = Environment.GetEnvironmentVariable("SIMLAB_PROFILE") == "1";
    public static long TClone, TPlay, TStatic, TSig, TFinish, NApply, NFinish;
    public static string ProfileReport()
    {
        double f = 1.0 / System.Diagnostics.Stopwatch.Frequency;
        return $"profile: apply n={NApply} clone={TClone * f:F1}s play={TPlay * f:F1}s static={TStatic * f:F1}s sig={TSig * f:F1}s | finish n={NFinish} {TFinish * f:F1}s";
    }

    /// <summary>A planned action: a hand card (HandIndex >= 0) or a potion (HandIndex = -1 - slot).</summary>
    private readonly record struct Step(int HandIndex, int TargetIndex, string CardId)
    {
        public bool IsPotion => HandIndex < 0;
        public int Slot => -1 - HandIndex;
    }

    private sealed class Node
    {
        public required CombatState State;
        public required List<Step> Steps;
        public double Static;
        public bool Closed; // turn already over (card ended it / fight over) - cannot be expanded
    }

    private List<Step>? _plan;

    // race-model running sums over finished turns of this fight
    private int _lastTurn = -1, _act = 1;
    private double _lastEnemyHp, _lastHp, _lastIncoming;
    private double _turns, _sumDealt, _sumIncoming, _sumTaken;

    public BeamPolicy(int act = 1) { _act = act; }

    private void UpdateRace(CombatState state, Player player, int turn)
    {
        if (turn == _lastTurn) return;
        double enemyHp = Eval.EnemyHp(state), hp = player.Creature.CurrentHp;
        if (_lastTurn >= 0)
        {
            _turns++;
            _sumDealt += Math.Max(0, _lastEnemyHp - enemyHp);
            _sumTaken += Math.Max(0, _lastHp - hp);
            _sumIncoming += _lastIncoming;
        }
        _lastTurn = turn;
        _lastEnemyHp = enemyHp;
        _lastHp = hp;
        _lastIncoming = Eval.Incoming(state, player);
        if (Tun.RaceK <= 0)
        {
            Eval.CurEnemyW = Tun.EnemyW;
            return;
        }
        double pt = Tun.PriorTurns;
        double o = (_sumDealt + pt * (Tun.PriorO + Tun.PriorOAct * (_act - 1))) / (_turns + pt);
        double d = (_sumIncoming + _lastIncoming + pt * _lastIncoming) / (_turns + 1 + pt);
        double leak = (_sumTaken + pt * Tun.PriorLeak * Math.Max(1, _lastIncoming)) / (_sumIncoming + pt * Math.Max(1, _lastIncoming));
        Eval.CurEnemyW = Math.Clamp(Tun.RaceK * leak * d / Math.Max(1, o), Tun.RaceMin, Tun.RaceMax);
    }
    private int _planTurn = -1;
    private int _planPos;

    public LookaheadPolicy.Action Choose(CombatState state)
    {
        Player player = state.Players[0];
        int turn = player.PlayerCombatState!.TurnNumber;
        UpdateRace(state, player, turn);
        if (_plan is null || _planTurn != turn || !StepMatches(state, player))
        {
            _plan = Plan(state, player);
            _planTurn = turn;
            _planPos = 0;
        }
        if (_planPos >= _plan.Count) return new LookaheadPolicy.End();
        Step s = _plan[_planPos++];
        if (s.IsPotion)
        {
            PotionModel potion = player.PotionSlots[s.Slot]!;
            return new LookaheadPolicy.UsePotion(potion, PotionTarget(state, player, potion, s.TargetIndex));
        }
        CardModel card = player.PlayerCombatState.Hand.Cards[s.HandIndex];
        Creature? target = s.TargetIndex < 0 ? null : CombatTargetCandidates.ForCard(state, player, card.TargetType)[s.TargetIndex];
        return new LookaheadPolicy.Play(card, target);
    }

    /// <summary>Potion targets: each hittable enemy for enemy-targeted potions, else the player (or none).</summary>
    public static IReadOnlyList<Creature?> PotionTargets(CombatState state, Player p, PotionModel potion) => potion.TargetType switch
    {
        TargetType.AnyEnemy => state.HittableEnemies,
        TargetType.Self or TargetType.AnyPlayer or TargetType.AnyAlly => new Creature?[] { p.Creature },
        TargetType.Osty => p.Osty is { IsAlive: true } o ? new Creature?[] { o } : Array.Empty<Creature?>(),
        _ => new Creature?[] { null },
    };

    private static Creature? PotionTarget(CombatState state, Player p, PotionModel potion, int ti) =>
        PotionTargets(state, p, potion)[Math.Max(0, ti)];

    /// <summary>Plays one planned step on <paramref name="state"/>; false if it could not be done.</summary>
    private static bool Execute(CombatState state, Player p, Step s)
    {
        CombatEngine engine = state.Engine!;
        if (s.IsPotion)
        {
            if (s.Slot >= p.PotionSlots.Count || p.PotionSlots[s.Slot] is not { } potion || potion.Id.ToString() != s.CardId) return false;
            var targets = PotionTargets(state, p, potion);
            if (s.TargetIndex >= targets.Count) return false;
            Creature? pt = targets[Math.Max(0, s.TargetIndex)];
            if (!PotionCmd.CanUseManually(state, p, potion, pt)) return false;
            PotionCmd.Use(potion, p, pt).GetAwaiter().GetResult();
            engine.CheckWinCondition();
            return true;
        }
        var hand = p.PlayerCombatState!.Hand.Cards;
        if (s.HandIndex >= hand.Count) return false;
        CardModel card = hand[s.HandIndex];
        if (card.Id.ToString() != s.CardId) return false;
        Creature? target = null;
        if (s.TargetIndex >= 0)
        {
            var ts = CombatTargetCandidates.ForCard(state, p, card.TargetType);
            if (s.TargetIndex >= ts.Count) return false;
            target = ts[s.TargetIndex];
        }
        int handBefore = hand.Count, energyBefore = p.PlayerCombatState.Energy;
        var res = engine.PlayCardWithResultAsync(p, card, target).GetAwaiter().GetResult();
        engine.CheckWinCondition();
        return !(res is null && engine.IsInProgress && handBefore == p.PlayerCombatState?.Hand.Cards.Count &&
                 energyBefore == p.PlayerCombatState?.Energy);
    }

    private bool StepMatches(CombatState state, Player player)
    {
        if (_plan is null || _planPos >= _plan.Count) return _plan is not null;
        Step s = _plan[_planPos];
        if (s.IsPotion)
            return s.Slot < player.PotionSlots.Count && player.PotionSlots[s.Slot]?.Id.ToString() == s.CardId;
        var hand = player.PlayerCombatState!.Hand.Cards;
        if (s.HandIndex >= hand.Count || hand[s.HandIndex].Id.ToString() != s.CardId || !hand[s.HandIndex].CanPlay(out _)) return false;
        if (s.TargetIndex >= 0 && s.TargetIndex >= CombatTargetCandidates.ForCard(state, player, hand[s.HandIndex].TargetType).Count) return false;
        return true;
    }

    /// <summary>Search parameters of one search level (the top level, or a nested next-turn search).</summary>
    private readonly record struct Level(int Beam, int LeafK, int DeepK, int Horizon, HashSet<string>? Banned = null);

    private static Level Top => new(Tun.Beam, Tun.LeafK, Tun.DeepK, Tun.LeafHorizon);
    private static Level Nested(int horizon) => new(Tun.NestBeam, Tun.NestLeafK, 0, horizon);

    private List<Step> Plan(CombatState state, Player player)
    {
        int turn = player.PlayerCombatState!.TurnNumber;
        // Only the first plan of a backtracked turn avoids the banned end states.
        HashSet<string>? banned = _bans.Remove(turn, out var b) ? b : null;
        return Plan(state, player, Top with { Banned = banned });
    }

    // turn -> end-of-turn state signatures that already led to a loss (set by backtracking)
    private Dictionary<int, HashSet<string>> _bans = new();

    /// <summary>At the start of <paramref name="turn"/>, take the best plan whose end state is not in <paramref name="endStates"/>.</summary>
    public void Ban(int turn, HashSet<string> endStates) => _bans[turn] = new HashSet<string>(endStates);

    /// <summary>The state key the search uses to merge identical partial turns.</summary>
    public static string EndStateKey(CombatState state) => Signature(state, state.Players[0]);

    /// <summary>Copy of the per-fight state (race averages) for a backtracking checkpoint.</summary>
    public BeamPolicy Copy()
    {
        var c = (BeamPolicy)MemberwiseClone();
        c._bans = new Dictionary<int, HashSet<string>>();
        c._plan = null;
        c._planTurn = -1;
        return c;
    }

    private static List<Step> Plan(CombatState state, Player player, Level lv)
    {
        int saved = Eval.CurPlanTurn;
        try { return PlanCore(state, player, lv); }
        finally { Eval.CurPlanTurn = saved; }
    }

    private static List<Step> PlanCore(CombatState state, Player player, Level lv)
    {
        int turn = player.PlayerCombatState!.TurnNumber;
        Eval.CurPlanTurn = turn;
        var root = new Node { State = state, Steps = new List<Step>() };
        root.Static = Eval.Static(state, player);
        var all = new List<Node> { root };
        var frontier = new List<Node> { root };
        var seen = new HashSet<string> { Signature(state, player) };

        for (int depth = 0; depth < Tun.MaxDepth && frontier.Count > 0; depth++)
        {
            var children = new List<Node>();
            foreach (Node node in frontier)
            {
                if (node.Closed) continue;
                Player np = node.State.Players[0];
                foreach ((int hi, int ti) in Moves(node.State, np))
                {
                    Node? child = Apply(node, hi, ti, turn);
                    if (child is null) continue;
                    long ts = Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    bool dup = !child.Closed && !seen.Add(Signature(child.State, child.State.Players[0]));
                    if (Profile) Interlocked.Add(ref TSig, System.Diagnostics.Stopwatch.GetTimestamp() - ts);
                    if (dup) continue;
                    children.Add(child);
                }
            }
            if (children.Count == 0) break;
            all.AddRange(children);
            children.Sort((a, b) => b.Static.CompareTo(a.Static));
            frontier = children.Count > lv.Beam ? children.GetRange(0, lv.Beam) : children;
        }

        // Exact finish for the most promising end points (root always included).
        IEnumerable<Node> pool = all;
        if (lv.Banned is { } ban)
        {
            // A backtrack: drop end states that already lost (a closed node's key is its post-turn state).
            var open = all.Where(n => n.Closed || !ban.Contains(Signature(n.State, n.State.Players[0]))).ToList();
            if (open.Count > 0) pool = open;
        }
        var cands = pool.Where(n => n != root).OrderByDescending(n => n.Static).Take(lv.LeafK).ToList();
        if (pool.Contains(root)) cands.Add(root);
        // Two stages when DeepK > 0: score every candidate one turn deep, then re-score the DeepK best
        // with the full horizon (nested search for the following turns) and pick among those.
        bool twoStage = lv.DeepK > 0 && lv.Horizon > 1;
        var scored = cands.Select(n => (Node: n, V: Finish(n, turn, twoStage ? 1 : lv.Horizon))).ToList();
        if (twoStage)
        {
            scored = scored.OrderByDescending(x => x.V).Take(lv.DeepK)
                .Select(x => (x.Node, V: x.V >= 900 || x.V <= -900 ? x.V : Finish(x.Node, turn, lv.Horizon))).ToList();
        }
        // Best first; on near-ties prefer longer plans (setup / draw cards look neutral at this horizon).
        var ranked = scored.OrderByDescending(x => Math.Round(x.V, 6)).ThenByDescending(x => x.Node.Steps.Count).ToList();
        if (Debug && ranked.Count > 0 && lv.Horizon == Tun.LeafHorizon)
            Console.WriteLine("   plan: " + string.Join("  ", ranked.Take(6).Select(x => $"{x.V:F1}[{string.Join(",", x.Node.Steps.Select(st => st.CardId.Replace("CARD.", "")))}]")) + $"  (nodes {all.Count})");
        return ranked[0].Node.Steps;
    }

    private static IEnumerable<(int HandIndex, int TargetIndex)> Moves(CombatState state, Player p)
    {
        var seen = new HashSet<string>();
        IReadOnlyList<CardModel> hand = p.PlayerCombatState!.Hand.Cards;
        var result = new List<(int, int)>();
        for (int i = 0; i < hand.Count; i++)
        {
            CardModel card = hand[i];
            if (!card.CanPlay(out var why))
            {
                if (Debug) Console.WriteLine($"   cannot play {card.Id.Entry}: {why}");
                continue;
            }
            string key = CardKey(card);
            if (card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer)
            {
                IReadOnlyList<Creature> targets = CombatTargetCandidates.ForCard(state, p, card.TargetType);
                for (int t = 0; t < targets.Count; t++)
                    if (seen.Add(key + "@" + t)) result.Add((i, t));
            }
            else if (seen.Add(key)) result.Add((i, -1));
        }
        if (Tun.PotionHoldW >= 0 && p.CanUseOrRemovePotions)
        {
            for (int slot = 0; slot < p.PotionSlots.Count; slot++)
            {
                if (p.PotionSlots[slot] is not { } potion || potion.Usage == PotionUsage.Automatic) continue;
                if (!seen.Add("potion:" + potion.Id)) continue;
                var targets = PotionTargets(state, p, potion);
                for (int t = 0; t < targets.Count; t++)
                    if (PotionCmd.CanUseManually(state, p, potion, targets[t])) result.Add((-1 - slot, t));
            }
        }
        return result;
    }

    private static string CardKey(CardModel card) =>
        card.Id + "+" + card.CurrentUpgradeLevel + "#" + string.Join(",", card.Enchantments.Select(en => en.Id.ToString())) + ":" + card.EnergyCost;

    private static Node? Apply(Node parent, int handIndex, int targetIndex, int turn)
    {
        try
        {
            long t0 = Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            CombatState clone = parent.State.Clone(out _);
            clone.CardSelectionSource = SimpleSelector.Instance;
            CombatEngine engine = clone.Engine!;
            Player p = clone.Players[0];
            long t1 = Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            string id = handIndex >= 0 ? p.PlayerCombatState!.Hand.Cards[handIndex].Id.ToString() : p.PotionSlots[-1 - handIndex]!.Id.ToString();
            var step = new Step(handIndex, targetIndex, id);
            if (!Execute(clone, p, step))
            {
                if (Debug) Console.WriteLine($"   not playable: {id} t{targetIndex}");
                return null; // not actually playable
            }
            var steps = new List<Step>(parent.Steps.Count + 1);
            steps.AddRange(parent.Steps);
            steps.Add(step);
            bool closed = !engine.IsInProgress || clone.CurrentSide != CombatSide.Player ||
                          p.PlayerCombatState?.TurnNumber != turn;
            var node = new Node { State = clone, Steps = steps, Closed = closed };
            long t2 = Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            node.Static = closed && engine.IsInProgress ? Eval.Leaf(clone, p) : Eval.Static(clone, p);
            if (Profile)
            {
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Add(ref TClone, t1 - t0); Interlocked.Add(ref TPlay, t2 - t1); Interlocked.Add(ref TStatic, t3 - t2);
                Interlocked.Increment(ref NApply);
            }
            return node;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref Failures);
            if (Debug) Console.WriteLine($"   apply failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static readonly bool Debug = Environment.GetEnvironmentVariable("SIMLAB_DEBUG") == "1";

    /// <summary>
    /// End the turn from this node on a clone, let the enemies act; for a horizon above 1 play the
    /// following turns (nested beam search when NestBeam > 0, else the cheap greedy rollout); score.
    /// </summary>
    private static double Finish(Node n, int turn, int horizon)
    {
        if (!Profile) return FinishCore(n, turn, horizon);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        double v = FinishCore(n, turn, horizon);
        Interlocked.Add(ref TFinish, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        Interlocked.Increment(ref NFinish);
        return v;
    }

    private static double FinishCore(Node n, int turn, int horizon)
    {
        try
        {
            CombatState clone = n.State.Clone(out _);
            clone.CardSelectionSource = SimpleSelector.Instance;
            CombatEngine engine = clone.Engine!;
            Player p = clone.Players[0];
            if (engine.IsInProgress && clone.CurrentSide == CombatSide.Player && p.PlayerCombatState!.TurnNumber == turn)
            {
                engine.EndPlayerTurnAsync().GetAwaiter().GetResult();
                engine.CheckWinCondition();
            }
            for (int h = 1; h < horizon && engine.IsInProgress && clone.CurrentSide == CombatSide.Player; h++)
            {
                int t = p.PlayerCombatState!.TurnNumber;
                if (Tun.NestBeam > 0)
                {
                    // Plan the next turn with a small search whose own leaves look one enemy turn ahead,
                    // then play it on this clone.
                    foreach (Step s in Plan(clone, p, Nested(1)))
                    {
                        if (!engine.IsInProgress || clone.CurrentSide != CombatSide.Player || p.PlayerCombatState!.TurnNumber != t) break;
                        if (!Execute(clone, p, s)) break;
                    }
                }
                else
                {
                    if (Tun.Rollout == 1) GreedyTurn(clone, p, t);
                    else LookaheadPolicy.RolloutTurn(clone, p, t);
                }
                if (engine.IsInProgress && clone.CurrentSide == CombatSide.Player && p.PlayerCombatState!.TurnNumber == t)
                {
                    engine.EndPlayerTurnAsync().GetAwaiter().GetResult();
                    engine.CheckWinCondition();
                }
            }
            return Eval.Leaf(clone, p);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref Failures);
            return double.NegativeInfinity;
        }
    }

    /// <summary>
    /// One-step greedy turn: try every legal move on a clone, keep the one with the best
    /// <see cref="Eval.Static"/>, repeat while that beats stopping. Plays on <paramref name="state"/> in place.
    /// </summary>
    private static void GreedyTurn(CombatState state, Player p, int turn)
    {
        CombatEngine engine = state.Engine!;
        for (int step = 0; step < Tun.MaxDepth; step++)
        {
            if (!engine.IsInProgress || state.CurrentSide != CombatSide.Player || p.PlayerCombatState!.TurnNumber != turn) return;
            double stop = Eval.Static(state, p);
            Step? best = null;
            double bestV = stop - 1e-6;
            foreach ((int hi, int ti) in Moves(state, p))
            {
                if (hi < 0) continue; // potions are only planned at the top level
                var probe = new Node { State = state, Steps = new List<Step>() };
                Node? child = Apply(probe, hi, ti, turn);
                if (child is null) continue;
                if (child.Static > bestV)
                {
                    bestV = child.Static;
                    best = child.Steps[0];
                }
            }
            if (best is null || !Execute(state, p, best.Value)) return;
        }
    }

    /// <summary>Identity of a mid-turn state for merging transpositions (A then B == B then A).</summary>
    private static string Signature(CombatState state, Player p)
    {
        var sb = new StringBuilder(256);
        var pcs = p.PlayerCombatState!;
        sb.Append(pcs.Energy).Append('|').Append(p.Creature.CurrentHp).Append('/').Append(p.Creature.Block).Append('|');
        foreach (string k in pcs.Hand.Cards.Select(CardKey).OrderBy(s => s, StringComparer.Ordinal)) sb.Append(k).Append(',');
        sb.Append('|').Append(pcs.DrawPile.Cards.Count).Append('/').Append(pcs.DiscardPile.Cards.Count).Append('/').Append(pcs.ExhaustPile.Cards.Count);
        AppendPowers(sb, p.Creature);
        try { sb.Append("|o").Append(pcs.OrbQueue.Orbs.Count); } catch (Exception) { }
        foreach (Creature e in state.Enemies)
        {
            sb.Append("|e").Append(e.IsAlive ? e.CurrentHp : -1).Append('/').Append(e.Block);
            AppendPowers(sb, e);
        }
        foreach (Creature pet in p.Creature.Pets) sb.Append("|p").Append(pet.CurrentHp).Append('/').Append(pet.Block);
        foreach (PotionModel? pot in p.PotionSlots) sb.Append("|q").Append(pot is null ? "-" : pot.Id.Entry);
        return sb.ToString();
    }

    private static void AppendPowers(StringBuilder sb, Creature c)
    {
        foreach (PowerModel pw in c.Powers)
        {
            sb.Append(';').Append(pw.Id.Entry).Append(pw.Amount);
            if (pw is SurroundedPower s) sb.Append('f').Append((int)s.Facing); // which crab is behind you
        }
    }
}
