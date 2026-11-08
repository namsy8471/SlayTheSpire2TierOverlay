namespace Sts2Overlay.Core.Combat;

/// <summary>
/// One simulated fight against a target that never acts. Turn order, damage maths and orb rules follow the game's
/// <c>CombatManager</c>, power and orb classes (v0.107.1); see <c>data/packs/&lt;ver&gt;/combat/README.md</c>.
/// </summary>
internal sealed class SimFight
{
    private const int HandLimit = 10;
    private const int MaxOrbSlots = 10;
    private const double VulnerableMultiplier = 1.5; // VulnerablePower
    private const double WeakMultiplier = 0.75; // WeakPower
    private const string SovereignBlade = "CARD.SOVEREIGN_BLADE";
    private static readonly string[] OrbKinds = ["lightning", "frost", "dark", "plasma", "glass"];

    private readonly TurnSimulator.Setup _s;
    private readonly int _turns;
    private readonly bool _usePotions;

    // Not readonly: a rollout copy (CopyForRollout) gets its own piles, powers, orbs and random numbers.
    private Random _rng;
    private List<Inst> _draw = new(), _hand = new(), _discard = new(), _exhaust = new();
    private List<Inst> _returning = new();
    private List<Effect> _triggers = new(), _mods = new(), _perStart = new(), _perEnd = new();
    private List<Effect> _replays = new(), _toDrawTop = new();
    private List<(int Turn, IReadOnlyList<CombatOp> Ops)> _nextTurn = new();
    private List<Orb> _orbs = new();
    private Dictionary<string, int> _timesPlayed = new(StringComparer.Ordinal);

    private double _energy, _strength, _tempStrength, _vuln, _vulnBonus, _poison, _accel, _doom, _focus, _tempFocus;
    private double _stars, _forge, _ostyHp, _vigor, _selfWeak, _energySpentT, _block;
    private int _orbSlots, _turn;
    private int _cardsT, _attacksT, _skillsT, _discardedT, _drawnT, _nonHandDrawsT, _exhaustedT, _attackHitsT, _ostyAttacksT;
    private int _statusCreatedT, _cardsLastT, _attacksLastT;
    private double _starsGainedT, _hpLostT;
    private int _cardsC, _drawnC, _generatedC, _hpLossCountC, _etherealPlayedC, _lightningChanneledC;
    private bool _noDraw, _noEnergyGain, _retainHand;
    private double _dealt, _bestDoomCheck;
    private int _depth, _budget = 20000;
    private int _playSerial; // card plays started so far (Effect.Born)

    public double[] TurnDealt { get; private set; }
    public double Doom => _doom;

    private Action<string>? _trace;

    /// <summary>Debugging: one line per turn start, card played and turn end (tests print a fight with it).</summary>
    public Action<string>? Trace
    {
        get => _trace;
        init => _trace = value;
    }

    /// <summary>
    /// Look-ahead: before each card, every playable card is tried on a copy of the fight that the plain (greedy) choice
    /// then plays to the end, this many times each; the card whose copies dealt the most is played. 0 = greedy only.
    /// </summary>
    public int Rollouts
    {
        get => _rollouts;
        init => _rollouts = value;
    }

    private int _rollouts;

    /// <summary>The most HP the target could have had and still died: damage dealt, or damage + Doom at a Doom check.</summary>
    public double Effective => Math.Max(_dealt, _bestDoomCheck);

    public SimFight(TurnSimulator.Setup s, Random rng, int turns, bool usePotions)
    {
        _s = s;
        _rng = rng;
        _turns = turns;
        _usePotions = usePotions;
        TurnDealt = new double[turns];
        _orbSlots = s.Character.OrbSlots;
    }

    // ---- look-ahead -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A copy of the fight as it is now, for trying a card out. The draw pile is shuffled again with the copy's own random
    /// numbers: the player knows what is in it but not the order, so the try-out must not either.
    /// </summary>
    private SimFight CopyForRollout(Random rng)
    {
        var c = (SimFight)MemberwiseClone();
        c._rng = rng;
        c._trace = null;
        c._rollouts = 0;
        var insts = new Dictionary<Inst, Inst>(ReferenceEqualityComparer.Instance);
        var effects = new Dictionary<Effect, Effect>(ReferenceEqualityComparer.Instance);
        Inst Card(Inst i) => insts.TryGetValue(i, out var n) ? n : insts[i] = i.Copy();
        Inst? MaybeCard(Inst? i) => i is null ? null : Card(i);
        Effect Power(Effect e) => effects.TryGetValue(e, out var n) ? n : effects[e] = e.Copy(MaybeCard);
        c._draw = _draw.ConvertAll(Card);
        c._hand = _hand.ConvertAll(Card);
        c._discard = _discard.ConvertAll(Card);
        c._exhaust = _exhaust.ConvertAll(Card);
        c._returning = _returning.ConvertAll(Card);
        c._triggers = _triggers.ConvertAll(Power);
        c._mods = _mods.ConvertAll(Power);
        c._perStart = _perStart.ConvertAll(Power);
        c._perEnd = _perEnd.ConvertAll(Power);
        c._replays = _replays.ConvertAll(Power);
        c._toDrawTop = _toDrawTop.ConvertAll(Power);
        c._nextTurn = new(_nextTurn);
        c._orbs = _orbs.ConvertAll(o => new Orb(o.Kind, o.Value));
        c._timesPlayed = new(_timesPlayed, StringComparer.Ordinal);
        c.TurnDealt = (double[])TurnDealt.Clone();
        c.Shuffle(c._draw);
        c._handIndex = insts;
        return c;
    }

    /// <summary>The copy's card for one of this fight's hand cards (filled by <see cref="CopyForRollout"/>).</summary>
    private Dictionary<Inst, Inst>? _handIndex;

    /// <summary>Plays <paramref name="first"/> now, then the rest of the fight the plain way.</summary>
    private void FinishAfter(Inst first)
    {
        _hand.Remove(first);
        Play(first, free: false);
        PlayTurn();
        EndTurn();
        EnemyTurn();
        for (_turn++; _turn <= _turns; _turn++)
        {
            StartTurn(0);
            PlayTurn();
            EndTurn();
            EnemyTurn();
        }
    }

    /// <summary>
    /// The card to play next, by trying each one (<see cref="Rollouts"/>). Every candidate is tried against the same
    /// random futures, so the comparison is between the cards and not between lucky and unlucky draws. Ties keep the
    /// plain choice.
    /// </summary>
    private Inst ChooseByRollout(List<Inst> options, Inst plain)
    {
        var seeds = new int[Rollouts];
        for (var k = 0; k < seeds.Length; k++) seeds[k] = _rng.Next();
        // One try-out per different card: two copies of the same card in the same state play out the same.
        var tried = new List<Inst> { plain };
        foreach (var o in options)
        {
            if (!tried.Any(t => SameChoice(t, o))) tried.Add(o);
        }
        if (tried.Count == 1) return plain;
        var best = plain;
        var bestTotal = double.MinValue;
        foreach (var o in tried)
        {
            double total = 0;
            foreach (var seed in seeds)
            {
                var copy = CopyForRollout(new Random(seed));
                copy.FinishAfter(copy._handIndex![o]);
                total += copy.Effective;
            }
            if (total > bestTotal + 1e-6)
            {
                best = o;
                bestTotal = total;
            }
        }
        return best;
    }

    private bool SameChoice(Inst a, Inst b)
        => ReferenceEquals(a.Card, b.Card) && a.Ench == b.Ench && a.EnchUsed == b.EnchUsed && a.Momentum == b.Momentum
           && CurrentCost(a) == CurrentCost(b);

    // ---- types ------------------------------------------------------------------------------------------------------

    private sealed class Inst(CardCombat card)
    {
        public CardCombat Card = card;
        public int? CostCombat;
        public int? CostTurn;
        public int TimesDrawn;
        public bool ReturnNextTurn;

        /// <summary>The card's enchantment; its keyword/cost changes are already in <see cref="Card"/>.</summary>
        public Enchant? Ench;

        /// <summary>A once-per-combat enchantment (Swift, Sown, Glam, Vigorous) has been used up.</summary>
        public bool EnchUsed;

        /// <summary>Damage Momentum has built up.</summary>
        public double Momentum;

        /// <summary>Slumbering Essence: cost change until the card is played.</summary>
        public int CostUntilPlayed;

        public Inst Copy() => (Inst)MemberwiseClone();
    }

    /// <summary>A registered power: a trigger, a damage/cost modifier, a per-turn effect, a pending replay.</summary>
    private sealed class Effect(CombatOp op, int fromTurn, int toTurn)
    {
        public readonly CombatOp Op = op;
        public readonly int FromTurn = fromTurn;
        public int ToTurn = toTurn;
        public int Seen;
        public int FiredThisTurn;
        public int Left = int.MaxValue;

        /// <summary>The card instance that registered it (filter <c>self</c>; <c>costMod self</c> inside triggers).</summary>
        public Inst? Source;

        /// <summary>Modifier amount, read when it was registered.</summary>
        public double Value;

        public bool IsActive(int turn) => turn >= FromTurn && turn <= ToTurn && Left > 0;

        /// <summary>
        /// Card plays started before it was registered. A play only sees effects born before it started: the game fixes a
        /// card's play count and result pile before its effects run (<c>CardModel.OnPlayWrapper</c>), and its "after a card
        /// is played" powers only count cards they saw start (<c>BeforeCardPlayed</c>), so a power never fires on the card
        /// that made it.
        /// </summary>
        public int Born;

        /// <summary>A copy for a rollout, pointing at the copy's card instead of this fight's.</summary>
        public Effect Copy(Func<Inst?, Inst?> card)
        {
            var e = (Effect)MemberwiseClone();
            e.Source = card(Source);
            return e;
        }
    }

    private sealed class Orb(string kind, double value)
    {
        public readonly string Kind = kind;
        public double Value = value;
    }

    /// <summary>What an op list runs with: the card being played (if any), X, and the variables read before its first op.</summary>
    private sealed class Ctx
    {
        public CardCombat? Card;
        public Inst? Inst;
        public double X;
        public Dictionary<string, double>? Vars;
        public Inst? Drawn;
        public bool HandDraw;
        public int PlayerHits, OstyHits;

        /// <summary>Limited modifiers (limitPerTurn) this card's hits used; counted once the card is done.</summary>
        public HashSet<Effect>? Used;
    }

    private struct Mods
    {
        public double Flat;
        public double Mult;
    }

    // ---- fight loop -------------------------------------------------------------------------------------------------

    public void Run()
    {
        for (var k = 0; k < _s.Cards.Count; k++)
        {
            var e = _s.Enchants[k];
            _draw.Add(new Inst(_s.Enchanted(_s.Cards[k], e)) { Ench = e });
        }
        Shuffle(_draw);
        // CombatManager turn 1: Imbued cards go to the bottom, then Innate ones (not Imbued) on top (the list's end is the top).
        var imbued = _draw.Where(IsImbued).ToList();
        foreach (var i in imbued)
        {
            _draw.Remove(i);
            _draw.Insert(0, i);
        }
        var innate = _draw.Where(i => i.Card.Has("Innate") && !IsImbued(i)).ToList();
        foreach (var i in innate)
        {
            _draw.Remove(i);
            _draw.Add(i);
        }
        // Combat start is "turn 0": a perTurn start registered here also runs on turn 1, a nextTurn on turn 1.
        _turn = 0;
        foreach (var r in _s.Relics) Exec(r.CombatStart, new Ctx());

        for (_turn = 1; _turn <= _turns; _turn++)
        {
            StartTurn(innate.Count);
            PlayTurn();
            EndTurn();
            EnemyTurn();
        }
    }

    private void StartTurn(int innateCount)
    {
        // Turn 1's counters keep what combat-start effects did (Divine Right's stars count for Radiate).
        if (_turn > 1)
        {
            _cardsLastT = _cardsT;
            _attacksLastT = _attacksT;
            _cardsT = _attacksT = _skillsT = _discardedT = _drawnT = _nonHandDrawsT = _exhaustedT = _attackHitsT = 0;
            _ostyAttacksT = _statusCreatedT = 0;
            _starsGainedT = _hpLostT = _energySpentT = 0;
        }
        _block = 0;
        _noDraw = _noEnergyGain = _retainHand = false;
        foreach (var t in _triggers)
        {
            t.FiredThisTurn = 0;
            if (t.Op.ResetEachTurn) t.Seen = 0;
        }
        foreach (var m in _mods) m.FiredThisTurn = 0;
        // A "this turn" cost set at combat start (Jeweled Mask's free Power) is for turn 1.
        if (_turn > 1)
        {
            foreach (var i in _hand) i.CostTurn = null;
        }

        _energy = _s.Character.Energy + _s.Relics.Sum(r => r.EnergyPerTurn);
        foreach (var i in _returning) AddToHand(i);
        _returning.Clear();

        var handDraw = _s.Character.Draw + _s.Relics.Sum(r => r.DrawPerTurn);
        foreach (var r in _s.Relics)
        {
            if (!RunsOn(r.Turns, _turn)) continue;
            foreach (var op in r.TurnStart)
            {
                if (IsHandDraw(op)) handDraw += Count(op.N, new Ctx(), 0);
            }
        }
        foreach (var (turn, ops) in _nextTurn.ToArray())
        {
            if (turn == _turn) Exec(ops, new Ctx { HandDraw = true });
        }
        _nextTurn.RemoveAll(n => n.Turn <= _turn);
        if (_turn == 1) handDraw = Math.Max(handDraw, Math.Min(innateCount, HandLimit));
        Draw(Math.Max(0, handDraw), handDraw: true);
        Trace?.Invoke($"turn {_turn}: energy {_energy}, hand {string.Join(", ", _hand.Select(h => h.Card.Id + (h.Card.Upgraded ? "+" : "")))}");

        foreach (var r in _s.Relics)
        {
            if (!RunsOn(r.Turns, _turn)) continue;
            foreach (var op in r.TurnStart)
            {
                if (IsHandDraw(op)) continue;
                Exec(op, new Ctx());
            }
        }
        foreach (var e in _perStart.ToArray())
        {
            if (e.IsActive(_turn)) Exec(e.Op.Effects, new Ctx { Inst = e.Source });
        }
        foreach (var orb in _orbs)
        {
            if (orb.Kind == "plasma") GainEnergy(1);
        }
        if (_turn == 1)
        {
            // Imbued (AfterAutoPrePlayPhaseEntered, turn 1): played for free from wherever it is.
            foreach (var i in _draw.Concat(_hand).Where(IsImbued).ToList())
            {
                if (!_draw.Remove(i)) _hand.Remove(i);
                PlayFree(i, exhaust: false);
            }
        }
        if (_turn == 1 && _usePotions)
        {
            foreach (var p in _s.Potions)
            {
                Exec(p.Use, new Ctx());
                Fire(CombatOps.OnPotionUsed, null, null);
            }
        }
    }

    private static bool RunsOn(IReadOnlyList<int> turns, int turn) => turns.Count == 0 || turns.Contains(turn);

    /// <summary>A relic's turn-start draw that is part of the hand draw (<c>handDraw</c>, or a fixed "draw fewer").</summary>
    private static bool IsHandDraw(CombatOp op)
        => op.Op == CombatOps.Draw && (op.HandDraw || (op.N.IsConstant && op.N.Constant < 0));

    private void PlayTurn()
    {
        for (var guard = 0; guard < 80 && _budget > 0; guard++)
        {
            Inst? best = null;
            double bestScore = 0, bestValue = 0;
            var options = new List<Inst>();
            var forced = _hand.Where(h => h.Card.MustPlayFirst && Playable(h, ignoreForced: true)).ToList();
            foreach (var i in forced.Count > 0 ? forced : _hand)
            {
                if (!Playable(i)) continue;
                var v = Value(i);
                if (forced.Count > 0) v = Math.Max(v, 1);
                if (v > 0.01) options.Add(i);
                var cost = i.Card.CostX ? _energy : CurrentCost(i);
                var score = v / Math.Max(cost, 0.5);
                if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && v > bestValue))
                {
                    best = i;
                    bestScore = score;
                    bestValue = v;
                }
            }
            if (best is null || bestValue <= 0.01) return;
            if (Rollouts > 0 && options.Count > 1)
            {
                var plain = best;
                best = ChooseByRollout(options, best);
                if (best != plain && Trace is not null) bestValue = Value(best);
            }
            Trace?.Invoke($"  play {best.Card.Id}{(best.Card.Upgraded ? "+" : "")} (value {bestValue:0.0}, energy {_energy}), dealt so far {_dealt:0}");
            _hand.Remove(best);
            Play(best, free: false);
        }
    }

    private void EndTurn()
    {
        if (_selfWeak > 0) _selfWeak--;
        foreach (var i in _hand.ToArray())
        {
            if (i.Card.EndOfTurnInHand.Count > 0) Exec(i.Card.EndOfTurnInHand, CardCtx(i, 0));
        }
        foreach (var orb in _orbs.ToArray()) OrbPassive(orb);
        foreach (var e in _perEnd.ToArray())
        {
            if (e.IsActive(_turn)) Exec(e.Op.Effects, new Ctx { Inst = e.Source });
        }
        foreach (var r in _s.Relics)
        {
            if (RunsOn(r.TurnEndTurns, _turn)) Exec(r.TurnEnd, new Ctx());
        }
        foreach (var i in _hand)
        {
            if (i.Ench?.Id == Enchantments.SlumberingEssence) i.CostUntilPlayed--;
        }
        foreach (var i in _hand.ToArray())
        {
            if (_retainHand || i.Card.Has("Retain")) continue;
            _hand.Remove(i);
            if (i.Card.Has("Ethereal")) ExhaustCard(i);
            else _discard.Add(i);
        }
        _tempStrength = 0;
        _tempFocus = 0;
        _replays.Clear();
        _toDrawTop.Clear();
    }

    private void EnemyTurn()
    {
        // PoisonPower.AfterSideTurnStart: triggers min(amount, 1 + Accelerant) times, each dealing the current amount.
        var triggers = Math.Min(_poison, 1 + _accel);
        for (var k = 0; k < triggers && _poison > 0; k++)
        {
            Deal(_poison);
            _poison--;
        }
        // DoomPower.BeforeSideTurnEnd: dies when HP ≤ Doom, i.e. when damage so far + Doom reaches its HP.
        DoomCheck();
        if (_vuln > 0) _vuln--;
        Trace?.Invoke($"  after enemy turn {_turn}: dealt {_dealt:0}, poison {_poison}, doom {_doom}, strength {_strength}, focus {_focus}, "
                      + $"orbs [{string.Join(", ", _orbs.Select(o => o.Kind))}], osty {_ostyHp}");
    }

    private void DoomCheck()
    {
        if (_doom > 0) _bestDoomCheck = Math.Max(_bestDoomCheck, _dealt + _doom);
    }

    // ---- cards -------------------------------------------------------------------------------------------------------

    private bool Playable(Inst i, bool ignoreForced = false)
    {
        var c = i.Card;
        if (c.Unplayable) return false;
        if (!ignoreForced && _hand.Any(h => h != i && h.Card.MustPlayFirst)) return false;
        foreach (var h in _hand)
        {
            if (h.Card.InHandMaxCardsPerTurn is int max && _cardsT >= max) return false;
        }
        if (!c.CostX && CurrentCost(i) > _energy) return false;
        if (c.StarCost > 0 && c.StarCost > _stars) return false;
        if (c.PlayableIf is { } cond && Amount(cond, CardCtx(i, c.CostX ? _energy : 0)) <= 0) return false;
        return true;
    }

    /// <summary>The energy the card costs right now (cost modifiers, free-this-turn, cost formulas).</summary>
    private int CurrentCost(Inst i)
    {
        var c = i.Card;
        if (c.CostX) return 0;
        double cost = i.CostCombat ?? c.Cost;
        if (c.CostExpr is { } expr) cost = Amount(expr, CardCtx(i, 0));
        cost += i.CostUntilPlayed;
        foreach (var m in _mods)
        {
            if (m.Op.Op != CombatOps.CostMod || !m.IsActive(_turn) || !Matches(m.Op.Filter, c, i, m.Source)) continue;
            if (m.Op.Count is int perTurn && m.Op.Duration == "eachTurn" && m.FiredThisTurn >= perTurn) continue;
            cost = ApplyCostMod(m.Op, cost);
        }
        if (i.CostTurn is int t) cost = Math.Min(cost, t);
        return (int)Math.Max(0, cost);
    }

    private static double ApplyCostMod(CombatOp op, double cost)
    {
        var next = op.Set is double set ? set : cost + op.Delta;
        return op.LowerOnly ? Math.Min(cost, next) : next;
    }

    private Ctx CardCtx(Inst i, double x) => new() { Card = i.Card, Inst = i, X = x };

    /// <summary>Plays a card that has already left its pile.</summary>
    private void Play(Inst i, bool free, int times = 1)
    {
        if (--_budget < 0 || _depth > 12) return;
        var serial = ++_playSerial;
        var c = i.Card;
        double x = 0;
        if (c.CostX)
        {
            x = _energy;
            if (!free)
            {
                _energySpentT += _energy;
                _energy = 0;
            }
        }
        else if (!free)
        {
            var cost = CurrentCost(i);
            _energy -= cost;
            _energySpentT += cost;
            ConsumeCostMods(i);
        }
        if (c.StarCostX)
        {
            x = _stars;
            SpendStars(_stars);
        }
        else if (c.StarCost > 0 && !free) SpendStars(Math.Min(_stars, c.StarCost));

        var ctx = CardCtx(i, x);
        if (HasFormula(c)) ctx.Vars = Snapshot(ctx);

        var plays = times + (i.Ench is { } en ? Enchantments.ExtraPlays(en, i.EnchUsed) : 0);
        _depth++;
        for (var t = 0; t < plays; t++)
        {
            Exec(c.Play, ctx);
            EnchantOnPlay(i);
        }
        foreach (var r in _replays)
        {
            if (r.Left <= 0 || r.Born >= serial || !Matches(r.Op.Filter, c, i, r.Source)) continue;
            r.Left--;
            Exec(c.Play, ctx);
            EnchantOnPlay(i);
            break;
        }
        _replays.RemoveAll(r => r.Left <= 0);
        _depth--;
        if (i.Ench?.Id is Enchantments.Glam or Enchantments.Vigorous) i.EnchUsed = true;
        i.CostUntilPlayed = 0;

        _cardsT++;
        _cardsC++;
        if (c.IsAttack) _attacksT++;
        if (c.Type == "Skill") _skillsT++;
        if (c.Has("Ethereal")) _etherealPlayedC++;
        _timesPlayed[c.Id] = _timesPlayed.GetValueOrDefault(c.Id) + 1;
        if (c.IsAttack && ctx.PlayerHits > 0) _vigor = 0;
        if (ctx.Used is not null)
        {
            foreach (var e in ctx.Used) e.FiredThisTurn++;
        }

        if (c.Type != "Power")
        {
            if (i.ReturnNextTurn)
            {
                i.ReturnNextTurn = false;
                _returning.Add(i);
            }
            else if (c.Has("Exhaust") || ExhaustedByMod(i)) ExhaustCard(i);
            else if (TakeDrawTop(i, serial)) _draw.Add(i);
            else _discard.Add(i);
        }
        Fire(CombatOps.OnPlay, c, i, bornBefore: serial);
        if (_hand.Count == 0) Fire(CombatOps.OnHandEmpty, null, null);
    }

    private void SpendStars(double n)
    {
        if (n <= 0) return;
        _stars -= n;
        Fire(CombatOps.OnStarsSpent, null, null);
    }

    private void ConsumeCostMods(Inst i)
    {
        foreach (var m in _mods)
        {
            if (m.Op.Op != CombatOps.CostMod || !m.IsActive(_turn) || !Matches(m.Op.Filter, i.Card, i, m.Source)) continue;
            if (m.Op.Duration == "next") m.Left--;
            else if (m.Op.Duration == "eachTurn") m.FiredThisTurn++;
        }
        _mods.RemoveAll(m => m.Left <= 0);
    }

    private bool ExhaustedByMod(Inst i)
        => _mods.Any(m => m.Op.Op == CombatOps.CostMod && m.Op.Exhaust && m.IsActive(_turn) && Matches(m.Op.Filter, i.Card, i, m.Source));

    private bool TakeDrawTop(Inst i, int serial)
    {
        foreach (var e in _toDrawTop)
        {
            if (e.Left <= 0 || e.Born >= serial || !Matches(e.Op.Filter, i.Card, i, e.Source)) continue;
            e.Left--;
            return true;
        }
        return false;
    }

    private void AddToHand(Inst i)
    {
        if (_hand.Count < HandLimit) _hand.Add(i);
        else _discard.Add(i);
    }

    private void Draw(double n, bool handDraw)
    {
        for (var k = 0; k < n; k++)
        {
            if (_noDraw) return;
            if (_draw.Count == 0)
            {
                if (_discard.Count == 0) return;
                _draw.AddRange(_discard);
                _discard.Clear();
                Shuffle(_draw);
                // Perfect Fit (ModifyShuffleOrder, not the first shuffle): on top.
                foreach (var pf in _draw.Where(d => d.Ench?.Id == Enchantments.PerfectFit).ToList())
                {
                    _draw.Remove(pf);
                    _draw.Add(pf);
                }
                Fire(CombatOps.OnShuffle, null, null);
            }
            var top = _draw[^1];
            _draw.RemoveAt(_draw.Count - 1);
            if (_hand.Count >= HandLimit)
            {
                _discard.Add(top);
                continue;
            }
            _hand.Add(top);
            top.TimesDrawn++;
            if (top.Ench?.Id == Enchantments.Slither) top.CostCombat = _rng.Next(4); // EnergyCost.SetThisCombat(0..3)
            _drawnT++;
            _drawnC++;
            if (!handDraw) _nonHandDrawsT++;
            if (top.Card.OnDraw.Count > 0) Exec(top.Card.OnDraw, CardCtx(top, 0));
            FireDraw(top, handDraw);
        }
    }

    private void ExhaustCard(Inst i)
    {
        _exhaust.Add(i);
        _exhaustedT++;
        if (i.Card.OnExhaust.Count > 0) Exec(i.Card.OnExhaust, CardCtx(i, 0));
        Fire(CombatOps.OnExhaust, i.Card, i);
    }

    /// <summary>A card discarded by an effect: Sly cards are played for free.</summary>
    private void DiscardByEffect(Inst i)
    {
        _discard.Add(i);
        _discardedT++;
        Fire(CombatOps.OnDiscard, i.Card, i);
        if (i.Card.Has("Sly") && _discard.Remove(i)) Play(i, free: true);
    }

    private void Shuffle(List<Inst> list)
    {
        for (var n = list.Count - 1; n > 0; n--)
        {
            var j = _rng.Next(n + 1);
            (list[n], list[j]) = (list[j], list[n]);
        }
    }

    // ---- damage -----------------------------------------------------------------------------------------------------

    private void Deal(double amount)
    {
        if (amount <= 0) return;
        _dealt += amount;
        TurnDealt[Math.Clamp(_turn - 1, 0, TurnDealt.Length - 1)] += amount;
    }

    /// <summary>
    /// Flat bonuses and multipliers of the active modifiers that match a player / Osty hit of this card. Read at each
    /// attack op, so a modifier the card registers before its attack (Hang, Conqueror) applies to that attack.
    /// </summary>
    private Mods HitMods(Ctx ctx, bool osty, bool record)
    {
        var card = ctx.Card;
        var m = new Mods { Mult = 1 };
        foreach (var e in _mods)
        {
            if (e.Op.Op is not (CombatOps.AttackBonus or CombatOps.DamageMultiplier) || !e.IsActive(_turn)) continue;
            if (!MatchesHit(e.Op.Filter, card, osty, ctx.Inst, e.Source)) continue;
            if (e.Op.LimitPerTurn is int lim && e.FiredThisTurn >= lim) continue;
            if (e.Op.Op == CombatOps.AttackBonus) m.Flat += e.Value;
            else m.Mult *= e.Value;
            if (record && e.Op.LimitPerTurn is not null) (ctx.Used ??= new()).Add(e);
        }
        if (!osty && card is { IsAttack: true }) m.Flat += _vigor;
        return m;
    }

    private double PlayerHit(double dmg, double strMult, CardCombat? card, in Mods mods)
    {
        var d = dmg + (_strength + _tempStrength) * strMult + mods.Flat;
        if (card?.Id == SovereignBlade) d += _forge;
        var mult = mods.Mult;
        if (_vuln > 0) mult *= VulnerableMultiplier + _vulnBonus;
        if (_selfWeak > 0) mult *= WeakMultiplier;
        return Math.Max(0, Math.Floor(d * mult));
    }

    private double OstyHit(double dmg, in Mods mods)
    {
        // Osty's attacks are its own: the player's Strength and Weak don't apply, Vulnerable does.
        var mult = mods.Mult;
        if (_vuln > 0) mult *= VulnerableMultiplier + _vulnBonus;
        return Math.Max(0, Math.Floor((dmg + mods.Flat) * mult));
    }

    // ---- effects ----------------------------------------------------------------------------------------------------

    private double Amount(CombatAmount a, Ctx ctx)
    {
        if (a.IsConstant) return a.Constant;
        ctx.Vars ??= Snapshot(ctx);
        return a.Evaluate(ctx.Vars);
    }

    private int Count(CombatAmount a, Ctx ctx, int all)
        => a.IsAll ? all : (int)Math.Max(0, Math.Floor(Amount(a, ctx)));

    private void Exec(IReadOnlyList<CombatOp> ops, Ctx ctx)
    {
        foreach (var op in ops) Exec(op, ctx);
    }

    private void Exec(CombatOp op, Ctx ctx)
    {
        if (--_budget < 0) return;
        if (CombatOps.IsTrigger(op.Op))
        {
            var (from, to) = Span(op.Duration);
            _triggers.Add(new Effect(op, from, to) { Source = ctx.Inst, Born = _playSerial });
            return;
        }
        switch (op.Op)
        {
            case CombatOps.Attack:
            {
                var hits = Count(op.Hits, ctx, 1);
                var dmg = EnchantDamage(ctx, Amount(op.Dmg, ctx));
                var mods = HitMods(ctx, osty: false, record: hits > 0);
                for (var h = 0; h < hits; h++)
                {
                    var d = PlayerHit(dmg, op.StrMult, ctx.Card, mods);
                    Deal(d);
                    _attackHitsT++;
                    ctx.PlayerHits++;
                    FireHit(ctx, osty: false, d);
                }
                break;
            }
            case CombatOps.OstyAttack:
            {
                if (_ostyHp <= 0) break;
                var hits = Count(op.Hits, ctx, 1);
                var dmg = EnchantDamage(ctx, Amount(op.Dmg, ctx));
                var mods = HitMods(ctx, osty: true, record: hits > 0);
                for (var h = 0; h < hits; h++)
                {
                    var d = OstyHit(dmg, mods);
                    Deal(d);
                    ctx.OstyHits++;
                    FireHit(ctx, osty: true, d);
                }
                _ostyAttacksT++;
                break;
            }
            case CombatOps.Damage: Deal(Amount(op.N, ctx)); break;
            case CombatOps.Draw: Draw(Amount(op.N, ctx), ctx.HandDraw); break;
            case CombatOps.Energy: GainEnergy(Amount(op.N, ctx)); break;
            case CombatOps.Strength: _strength += Amount(op.N, ctx); break;
            case CombatOps.TempStrength: _tempStrength += Amount(op.N, ctx); break;
            case CombatOps.LoseStrength: _strength -= Amount(op.N, ctx); break;
            case CombatOps.Vulnerable: Apply("vulnerable", Amount(op.N, ctx), n => _vuln += n); break;
            case CombatOps.Weak: Apply("weak", Amount(op.N, ctx), _ => { }); break;
            case CombatOps.Poison: Apply("poison", Amount(op.N, ctx), n => _poison += n); break;
            case CombatOps.Doom: Apply("doom", Amount(op.N, ctx), n => _doom += n); break;
            case CombatOps.Accelerant: _accel += Amount(op.N, ctx); break;
            case CombatOps.DoomCheck: DoomCheck(); break;
            case CombatOps.AddCard: AddCards(op, ctx); break;
            case CombatOps.PerTurn:
                (op.When == "end" ? _perEnd : _perStart).Add(new Effect(op, op.When == "end" ? _turn : _turn + 1, int.MaxValue) { Source = ctx.Inst });
                break;
            case CombatOps.NextTurn: _nextTurn.Add((_turn + 1, op.Effects)); break;
            case CombatOps.ReplayNext:
                _replays.Add(new Effect(op, _turn, _turn) { Left = Math.Max(1, Count(op.N, ctx, 1)), Source = ctx.Inst, Born = _playSerial });
                break;
            case CombatOps.Vigor: _vigor += Amount(op.N, ctx); break;
            case CombatOps.AttackBonus:
            case CombatOps.DamageMultiplier:
                AddModifier(op, ctx);
                break;
            case CombatOps.VulnerableBonus: _vulnBonus += Amount(op.N, ctx); break;
            case CombatOps.CostMod: CostMod(op, ctx); break;
            case CombatOps.PlayTop:
            case CombatOps.AutoPlayTop:
            {
                var n = Count(op.N, ctx, _draw.Count);
                for (var k = 0; k < n; k++)
                {
                    if (_draw.Count == 0)
                    {
                        if (_discard.Count == 0) break;
                        _draw.AddRange(_discard);
                        _discard.Clear();
                        Shuffle(_draw);
                        Fire(CombatOps.OnShuffle, null, null);
                    }
                    var top = _draw[^1];
                    _draw.RemoveAt(_draw.Count - 1);
                    PlayFree(top, op.Exhaust);
                }
                break;
            }
            case CombatOps.PlayFromHand:
            {
                var pick = Pick(_hand, op.Filter, op.Pick, ctx.Inst);
                if (pick is null) break;
                _hand.Remove(pick);
                PlayFree(pick, op.Exhaust, Math.Max(1, Count(op.Times, ctx, 1)));
                break;
            }
            case CombatOps.PlayFromDiscard:
            {
                var n = Count(op.N, ctx, _discard.Count);
                for (var k = 0; k < n; k++)
                {
                    var pick = Pick(_discard, op.Filter, op.Pick ?? "random", ctx.Inst);
                    if (pick is null) break;
                    _discard.Remove(pick);
                    PlayFree(pick, op.Exhaust);
                }
                break;
            }
            case CombatOps.PlayDrawn:
                if (ctx.Drawn is { } drawn && _hand.Remove(drawn)) PlayFree(drawn, op.Exhaust);
                break;
            case CombatOps.UpgradeInHand: Upgrade(_hand, Count(op.N, ctx, _hand.Count), "best"); break;
            case CombatOps.UpgradeCards:
                foreach (var pile in Piles(op.From ?? "hand"))
                {
                    Upgrade(pile, Count(op.N, ctx, pile.Count), op.Pick ?? "random");
                }
                break;
            case CombatOps.TransformInHand:
            {
                if (op.Card is null || _s.Data.Card(op.Card, op.Upgraded ? 1 : 0) is not { } to) break;
                // Without n every matching card changes (Primal Force); with n, the least useful ones.
                var targets = _hand.Where(i => i != ctx.Inst && Matches(op.Filter, i.Card, i, ctx.Inst)).ToList();
                if (!op.N.IsConstant || op.N.Constant > 0)
                {
                    targets = targets.OrderBy(i => i.Card.Unplayable ? 0 : 1).ThenBy(Value).Take(Count(op.N, ctx, targets.Count)).ToList();
                }
                foreach (var i in targets)
                {
                    i.Card = to; // a new card: the enchantment is gone
                    i.Ench = null;
                }
                break;
            }
            case CombatOps.NoDraw: _noDraw = true; break;
            case CombatOps.NoEnergyGain: _noEnergyGain = true; break;
            case CombatOps.LoseHp:
            {
                var n = Amount(op.N, ctx);
                if (n <= 0) break;
                _hpLostT += n;
                _hpLossCountC++;
                Fire(CombatOps.OnLoseHp, null, null);
                break;
            }
            case CombatOps.GainBlock:
            {
                var n = Amount(op.N, ctx);
                if (n <= 0) break;
                _block += n;
                Fire(CombatOps.OnGainBlock, null, null);
                break;
            }
            case CombatOps.Channel:
            {
                var n = Count(op.N, ctx, 1);
                for (var k = 0; k < n; k++)
                {
                    var kind = op.Orb is null or "random" ? (op.Orb is null ? "lightning" : OrbKinds[_rng.Next(OrbKinds.Length)]) : op.Orb;
                    Channel(kind);
                }
                break;
            }
            case CombatOps.Evoke:
            {
                var n = Count(op.N, ctx, _orbs.Count);
                var times = Count(op.Times, ctx, 1);
                if (times <= 0) break; // Multi-Cast with X = 0
                for (var k = 0; k < n && _orbs.Count > 0; k++) Evoke(op.From == "last" ? _orbs.Count - 1 : 0, times);
                break;
            }
            case CombatOps.Focus: _focus += Amount(op.N, ctx); break;
            case CombatOps.TempFocus: _tempFocus += Amount(op.N, ctx); break;
            case CombatOps.OrbSlots:
            {
                _orbSlots = Math.Clamp(_orbSlots + (int)Amount(op.N, ctx), 0, MaxOrbSlots);
                while (_orbs.Count > _orbSlots) _orbs.RemoveAt(_orbs.Count - 1);
                break;
            }
            case CombatOps.TriggerOrbPassives:
            {
                var n = Count(op.N, ctx, 1);
                for (var k = 0; k < n; k++)
                {
                    switch (op.Which ?? "front")
                    {
                        case "front":
                            if (_orbs.Count > 0) OrbPassive(_orbs[0]);
                            break;
                        case "all":
                            foreach (var orb in _orbs.ToArray()) OrbPassive(orb);
                            break;
                        default:
                            foreach (var orb in _orbs.Where(o => o.Kind == op.Which).ToArray()) OrbPassive(orb);
                            break;
                    }
                }
                break;
            }
            case CombatOps.Stars:
            {
                var n = Amount(op.N, ctx);
                _stars += n;
                if (n > 0)
                {
                    _starsGainedT += n;
                    FireApply("stars");
                }
                break;
            }
            case CombatOps.Forge:
            {
                if (!_draw.Concat(_hand).Concat(_discard).Any(i => i.Card.Id == SovereignBlade)
                    && _s.Data.Card(SovereignBlade) is { } blade)
                {
                    AddToHand(new Inst(blade));
                }
                _forge += Amount(op.N, ctx);
                break;
            }
            case CombatOps.Summon: _ostyHp += Math.Max(0, Amount(op.N, ctx)); break;
            case CombatOps.KillOsty:
            {
                var lost = _ostyHp;
                _ostyHp = 0;
                if (lost > 0) Fire(CombatOps.OnOstyLoseHp, null, null, ("lostHp", lost));
                break;
            }
            case CombatOps.ExhaustCards:
            {
                // "choice" / up-to amounts parse to 0: nothing is exhausted unless the data says how many.
                foreach (var pile in Piles(op.From ?? "hand"))
                {
                    var n = Count(op.N, ctx, pile.Count);
                    for (var k = 0; k < n && pile.Count > 0; k++)
                    {
                        var pick = op.Pick == "random"
                            ? RandomOf(pile.Where(i => i != ctx.Inst && Matches(op.Filter, i.Card, i, ctx.Inst)).ToList())
                            : Worst(pile, op.Filter, ctx.Inst);
                        if (pick is null) break;
                        pile.Remove(pick);
                        ExhaustCard(pick);
                    }
                }
                break;
            }
            case CombatOps.DiscardCards:
            {
                var n = Count(op.N, ctx, _hand.Count);
                for (var k = 0; k < n && _hand.Count > 0; k++)
                {
                    // Sly cards first (discarding plays them), then unplayable, then the least useful.
                    var pick = _hand.OrderBy(i => i.Card.Has("Sly") ? 0 : i.Card.Unplayable ? 1 : 2).ThenBy(Value).First();
                    _hand.Remove(pick);
                    DiscardByEffect(pick);
                }
                break;
            }
            case CombatOps.ReturnFromDiscard:
            {
                var n = Count(op.N, ctx, _discard.Count);
                for (var k = 0; k < n && _hand.Count < HandLimit; k++)
                {
                    var pick = Best(_discard, op.Filter, ctx.Inst);
                    if (pick is null) break;
                    _discard.Remove(pick);
                    _hand.Add(pick);
                }
                break;
            }
            case CombatOps.Fetch:
            {
                var pile = op.From == "discard" ? _discard : _draw;
                var n = Count(op.N, ctx, pile.Count);
                for (var k = 0; k < n && _hand.Count < HandLimit; k++)
                {
                    var pool = pile.Where(i => Matches(op.Filter, i.Card, i, ctx.Inst)).ToList();
                    if (op.Choose is int choose && choose > 0 && pool.Count > choose)
                    {
                        Shuffle(pool);
                        pool = pool.Take(choose).ToList();
                    }
                    var pick = Best(pool, null, null);
                    if (pick is null) break;
                    pile.Remove(pick);
                    _hand.Add(pick);
                }
                break;
            }
            case CombatOps.RetainHand: _retainHand = true; break;
            case CombatOps.RedrawHand:
            {
                _draw.AddRange(_hand);
                _hand.Clear();
                Shuffle(_draw);
                Draw(Amount(op.N, ctx), handDraw: false);
                break;
            }
            case CombatOps.NextToDrawTop:
                _toDrawTop.Add(new Effect(op, _turn, _turn) { Left = Math.Max(1, Count(op.N, ctx, 1)), Source = ctx.Inst, Born = _playSerial });
                break;
            case CombatOps.CopyInHand:
            {
                var src = Best(_hand.Where(h => h != ctx.Inst).ToList(), op.Filter, ctx.Inst);
                if (src is null) break;
                var n = Count(op.N, ctx, 1);
                for (var k = 0; k < n; k++) AddToHand(new Inst(src.Card) { CostCombat = src.CostCombat });
                break;
            }
            case CombatOps.SelfWeak: _selfWeak += Amount(op.N, ctx); break;
            case CombatOps.ReturnNextTurn:
                if (ctx.Inst is { } self) self.ReturnNextTurn = true;
                break;
            case CombatOps.GainPower:
                if (op.Power is { } p && p.Contains("ACCELERANT", StringComparison.OrdinalIgnoreCase)) _accel += Amount(op.N, ctx);
                break;
        }
    }

    private void GainEnergy(double n)
    {
        if (n > 0 && _noEnergyGain) return;
        _energy = Math.Max(0, _energy + n);
    }

    private (int From, int To) Span(string? duration) => duration switch
    {
        "turn" => (_turn, _turn),
        "nextTurn" => (_turn + 1, _turn + 1),
        _ => (_turn, int.MaxValue),
    };

    private void AddModifier(CombatOp op, Ctx ctx)
    {
        var (from, to) = Span(op.Duration);
        // A second copy of the same card's short multiplier extends it instead of stacking (Shadow Step, Conqueror).
        var selfOnly = op.Filter?.Split('|').Contains("self") == true;
        if (op.Op == CombatOps.DamageMultiplier && !selfOnly && op.Duration is "turn" or "nextTurn"
            && _mods.FirstOrDefault(m => ReferenceEquals(m.Op, op) && m.ToTurn >= _turn) is { } same)
        {
            same.ToTurn++;
            return;
        }
        _mods.Add(new Effect(op, from, to) { Source = ctx.Inst, Value = Amount(op.N, ctx) });
    }

    private void CostMod(CombatOp op, Ctx ctx)
    {
        if (op.Filter == "hand")
        {
            foreach (var i in _hand)
            {
                var now = CurrentCost(i);
                var cost = (int)Math.Max(0, ApplyCostMod(op, now));
                if (op.Duration == "combat") i.CostCombat = cost;
                else i.CostTurn = cost;
            }
            return;
        }
        if (op.Filter == "self")
        {
            if (ctx.Inst is { } self)
            {
                double baseCost = self.CostCombat ?? self.Card.Cost;
                var cost = (int)Math.Max(0, ApplyCostMod(op, baseCost));
                if (op.Duration is "turn" or "next") self.CostTurn = cost;
                else self.CostCombat = cost;
            }
            return;
        }
        var (from, to) = op.Duration switch
        {
            "turn" => (_turn, _turn),
            _ => (_turn, int.MaxValue),
        };
        var e = new Effect(op, from, to) { Source = ctx.Inst };
        if (op.Duration == "next") e.Left = Math.Max(1, op.Count ?? 1);
        _mods.Add(e);
    }

    private void Apply(string power, double n, Action<double> add)
    {
        if (n <= 0) return;
        add(n);
        FireApply(power);
    }

    private void FireApply(string power)
    {
        foreach (var t in _triggers.ToArray())
        {
            if (t.Op.Op != CombatOps.OnApply) continue;
            var want = t.Op.Power ?? "debuff";
            var match = want == power || (want == "debuff" && power is "vulnerable" or "weak" or "poison" or "doom");
            if (match) TryFire(t, new Ctx());
        }
    }

    private void AddCards(CombatOp op, Ctx ctx)
    {
        var n = Count(op.N, ctx, 1);
        for (var k = 0; k < n; k++)
        {
            CardCombat? card;
            if (op.Random is { } random)
            {
                card = RandomCard(random, op.Choose)
                       ?? (op.Card is null ? null : _s.Data.Card(op.Card));
                if (card is not null && op.Upgraded) card = _s.Data.Card(card.Id, 1) ?? card;
            }
            else card = op.Card is null ? null : _s.Data.Card(op.Card, op.Upgraded ? 1 : 0);
            if (card is null) continue;
            var inst = new Inst(card);
            if (op.Free) inst.CostTurn = 0;
            if (op.SetCost is int sc) inst.CostCombat = sc;
            switch (op.To)
            {
                case "draw": _draw.Insert(_rng.Next(_draw.Count + 1), inst); break;
                case "drawTop": _draw.Add(inst); break;
                case "discard": _discard.Add(inst); break;
                default: AddToHand(inst); break;
            }
            _generatedC++;
            if (card.Type == "Status")
            {
                _statusCreatedT++;
                Fire(CombatOps.OnStatusCreated, card, inst);
            }
        }
    }

    /// <summary><c>class|attack</c>, <c>colorless|any</c>, <c>class|common</c>: a random card; with choose, the best of that many.</summary>
    private CardCombat? RandomCard(string spec, int? choose)
    {
        var parts = spec.Split('|');
        var poolId = parts[0] switch
        {
            "class" or "" => _s.CharacterPool,
            "colorless" => CombatData.ColorlessPool,
            var p when p.StartsWith("CARD_POOL.", StringComparison.Ordinal) => p,
            var p => $"CARD_POOL.{p.ToUpperInvariant()}_CARD_POOL",
        };
        var pool = _s.Data.RandomPool(poolId);
        var kind = parts.Length > 1 ? parts[1] : "any";
        var options = pool.Where(c => kind switch
        {
            "any" => true,
            "common" or "uncommon" or "rare" => string.Equals(c.Rarity, kind, StringComparison.OrdinalIgnoreCase),
            "zeroCost" => c.Cost == 0 && !c.CostX,
            _ => string.Equals(c.Type, kind, StringComparison.OrdinalIgnoreCase),
        }).ToList();
        if (options.Count == 0) return null;
        var k = Math.Max(1, choose ?? 1);
        CardCombat? best = null;
        double bestValue = double.NegativeInfinity;
        for (var j = 0; j < k; j++)
        {
            var c = options[_rng.Next(options.Count)];
            if (k == 1) return c;
            var v = Value(new Inst(c));
            if (v > bestValue)
            {
                best = c;
                bestValue = v;
            }
        }
        return best;
    }

    private void PlayFree(Inst i, bool exhaust, int times = 1)
    {
        if (i.Card.Unplayable)
        {
            if (exhaust) ExhaustCard(i);
            else _discard.Add(i);
            return;
        }
        Play(i, free: true, times);
        if (exhaust && _discard.Remove(i)) ExhaustCard(i);
    }

    private Inst? RandomOf(List<Inst> options) => options.Count == 0 ? null : options[_rng.Next(options.Count)];

    private IEnumerable<List<Inst>> Piles(string from) => from switch
    {
        "draw" => [_draw],
        "discard" => [_discard],
        "deck" or "all" => [_hand, _draw, _discard],
        _ => [_hand],
    };

    private void Upgrade(List<Inst> pile, int n, string pick)
    {
        var candidates = pile.Where(i => !i.Card.Upgraded && _s.Data.Card(i.Card.Id, 1) is not null).ToList();
        if (pick == "random") Shuffle(candidates);
        else candidates = candidates.OrderByDescending(i => UpgradeGain(i)).ToList();
        foreach (var i in candidates.Take(n)) i.Card = _s.Enchanted(_s.Data.Card(i.Card.Id, 1)!, i.Ench);
    }

    private double UpgradeGain(Inst i)
    {
        if (_s.Data.Card(i.Card.Id, 1) is not { } up) return 0;
        return Value(new Inst(up)) - Value(i);
    }

    private Inst? Pick(List<Inst> pile, string? filter, string? pick, Inst? self)
    {
        var options = pile.Where(i => i != self && Matches(filter, i.Card, i, self) && !i.Card.Unplayable).ToList();
        if (options.Count == 0) return null;
        return pick == "random" ? options[_rng.Next(options.Count)] : options.MaxBy(Value);
    }

    private Inst? Best(List<Inst> pile, string? filter, Inst? self)
        => pile.Where(i => Matches(filter, i.Card, i, self) && !i.Card.Unplayable).MaxBy(Value)
           ?? pile.FirstOrDefault(i => Matches(filter, i.Card, i, self));

    private Inst? Worst(List<Inst> pile, string? filter, Inst? self)
        => pile.Where(i => i != self && Matches(filter, i.Card, i, self)).OrderBy(i => i.Card.Unplayable ? 0 : 1).ThenBy(Value).FirstOrDefault();

    // ---- triggers ---------------------------------------------------------------------------------------------------

    private void Fire(string kind, CardCombat? card, Inst? inst, (string Name, double Value)? extra = null, int bornBefore = int.MaxValue)
    {
        if (_triggers.Count == 0) return;
        foreach (var t in _triggers.ToArray())
        {
            if (t.Op.Op != kind || !t.IsActive(_turn) || t.Born >= bornBefore) continue;
            if (kind is CombatOps.OnPlay or CombatOps.OnExhaust or CombatOps.OnDiscard or CombatOps.OnStatusCreated
                && !Matches(t.Op.Filter, card, inst, t.Source)) continue;
            var ctx = new Ctx();
            if (extra is { } x)
            {
                ctx.Vars = Snapshot(ctx);
                ctx.Vars[x.Name] = x.Value;
            }
            TryFire(t, ctx);
        }
    }

    private void FireDraw(Inst drawn, bool handDraw)
    {
        foreach (var t in _triggers.ToArray())
        {
            if (t.Op.Op != CombatOps.OnDraw || !t.IsActive(_turn)) continue;
            if (t.Op.ExcludeTurnDraw && handDraw) continue;
            if (!Matches(t.Op.Filter, drawn.Card, drawn, t.Source)) continue;
            TryFire(t, new Ctx { Drawn = drawn });
        }
    }

    private void FireHit(Ctx hit, bool osty, double damage)
    {
        foreach (var t in _triggers.ToArray())
        {
            if (t.Op.Op != CombatOps.OnHit || !t.IsActive(_turn)) continue;
            if (!MatchesHit(t.Op.Filter, hit.Card, osty, hit.Inst, t.Source)) continue;
            var ctx = new Ctx();
            ctx.Vars = Snapshot(ctx);
            ctx.Vars["lastHitDamage"] = damage;
            TryFire(t, ctx);
        }
    }

    private void OnEvoked(string kind)
    {
        foreach (var t in _triggers.ToArray())
        {
            if (t.Op.Op != CombatOps.OnEvoke || !t.IsActive(_turn)) continue;
            if (t.Op.Orb is { } want && want != kind) continue;
            TryFire(t, new Ctx());
        }
    }

    private void OnChanneled(string kind)
    {
        foreach (var t in _triggers.ToArray())
        {
            if (t.Op.Op != CombatOps.OnChannel || !t.IsActive(_turn)) continue;
            if (t.Op.Orb is { } want && want != kind) continue;
            TryFire(t, new Ctx());
        }
    }

    private void TryFire(Effect t, Ctx ctx)
    {
        if (_depth > 12) return;
        t.Seen++;
        if (t.Op.Every is int every && every > 1 && t.Seen % every != 0) return;
        if (t.Op.LimitPerTurn is int lim && t.FiredThisTurn >= lim) return;
        t.FiredThisTurn++;
        ctx.Inst ??= t.Source;
        _depth++;
        Exec(t.Op.Effects, ctx);
        _depth--;
    }

    // ---- orbs (Lightning/Frost/Dark/Plasma/Glass orb classes) -------------------------------------------------------

    private double Focus => _focus + _tempFocus;

    private void Channel(string kind)
    {
        // A character without orb slots gets one on its first channel (OrbQueue).
        if (_orbSlots <= 0) _orbSlots = 1;
        if (_orbs.Count >= _orbSlots) Evoke(0, 1);
        _orbs.Add(new Orb(kind, kind switch { "dark" => 6, "glass" => 4, _ => 0 }));
        if (kind == "lightning") _lightningChanneledC++;
        OnChanneled(kind);
    }

    private void Evoke(int index, int times)
    {
        if (index < 0 || index >= _orbs.Count) return;
        var orb = _orbs[index];
        for (var t = 0; t < times; t++)
        {
            switch (orb.Kind)
            {
                case "lightning": Deal(Math.Max(0, 8 + Focus)); break;
                case "dark": Deal(orb.Value); break;
                case "plasma": GainEnergy(2); break;
                case "glass": Deal(Math.Max(0, 2 * (orb.Value + Focus))); break;
            }
            OnEvoked(orb.Kind);
        }
        _orbs.Remove(orb);
    }

    private void OrbPassive(Orb orb)
    {
        switch (orb.Kind)
        {
            case "lightning": Deal(Math.Max(0, 3 + Focus)); break;
            case "dark": orb.Value += Math.Max(0, 6 + Focus); break;
            case "glass":
                Deal(Math.Max(0, orb.Value + Focus));
                orb.Value = Math.Max(0, orb.Value - 1);
                break;
        }
    }

    // ---- filters ----------------------------------------------------------------------------------------------------

    internal static bool HasTag(CardCombat c, string tag)
    {
        foreach (var t in c.Tags)
        {
            if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasKeyword(CardCombat c, string keyword)
    {
        foreach (var k in c.Keywords)
        {
            if (string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// <c>any|attack|skill|power|status|curse|colorless|zeroCost|upgraded|tag:X|keyword:X|rarity:X|CARD.X|self|enchanted</c>,
    /// alternatives joined by <c>|</c>. <c>self</c> is the card instance that registered the effect; <c>enchanted</c> a card with an enchantment.
    /// </summary>
    private static bool Matches(string? filter, CardCombat? c, Inst? candidate = null, Inst? self = null)
    {
        if (string.IsNullOrEmpty(filter) || filter == "any") return true;
        if (c is null) return false;
        foreach (var token in filter.Split('|'))
        {
            if (token == "self")
            {
                if (candidate is not null && candidate == self) return true;
                continue;
            }
            if (token == "enchanted")
            {
                if (candidate?.Ench is not null) return true;
                continue;
            }
            if (MatchesToken(token, c)) return true;
        }
        return false;
    }

    private static bool MatchesToken(string token, CardCombat c)
    {
        if (token == "any") return true;
        if (token.StartsWith("tag:", StringComparison.Ordinal)) return HasTag(c, token[4..]);
        if (token.StartsWith("keyword:", StringComparison.Ordinal)) return HasKeyword(c, token[8..]);
        if (token.StartsWith("rarity:", StringComparison.Ordinal)) return string.Equals(c.Rarity, token[7..], StringComparison.OrdinalIgnoreCase);
        if (token.StartsWith("type:", StringComparison.Ordinal)) return string.Equals(c.Type, token[5..], StringComparison.OrdinalIgnoreCase);
        if (token.StartsWith("CARD.", StringComparison.Ordinal)) return c.Id == token;
        return token switch
        {
            "zeroCost" => c.Cost == 0 && !c.CostX,
            "upgraded" => c.Upgraded,
            "colorless" => c.Pool == CombatData.ColorlessPool,
            "osty" => false,
            _ => string.Equals(token, c.Type, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>A hit matches <c>osty</c> when Osty deals it; other tokens match the card that caused it.</summary>
    private static bool MatchesHit(string? filter, CardCombat? card, bool osty, Inst? candidate = null, Inst? self = null)
    {
        if (string.IsNullOrEmpty(filter)) return card is { IsAttack: true };
        if (filter == "any") return true;
        foreach (var token in filter.Split('|'))
        {
            if (token == "osty")
            {
                if (osty) return true;
                continue;
            }
            if (token == "self")
            {
                if (candidate is not null && candidate == self) return true;
                continue;
            }
            if (token == "enchanted")
            {
                // Mystic Lighter: the card that deals the hit has an enchantment (Osty's hits from such a card too).
                if (candidate?.Ench is not null) return true;
                continue;
            }
            if (card is not null && MatchesToken(token, card)) return true;
        }
        return false;
    }

    // ---- variables --------------------------------------------------------------------------------------------------

    /// <summary>Every variable a formula can read (data tests check that each formula only uses these).</summary>
    internal static IReadOnlyCollection<string> VariableNames(TurnSimulator.Setup setup)
        => new SimFight(setup, new Random(0), 3, false).Snapshot(new Ctx()).Keys.ToArray();

    private static readonly Dictionary<CardCombat, bool> FormulaCache = new(ReferenceEqualityComparer.Instance);

    private static readonly Dictionary<CardCombat, bool> BlockCache = new(ReferenceEqualityComparer.Instance);

    /// <summary>Cards whose numbers read the <c>block</c> variable (Body Slam).</summary>
    private static bool UsesBlock(CardCombat c)
    {
        lock (BlockCache)
        {
            if (BlockCache.TryGetValue(c, out var uses)) return uses;
            uses = c.Play.Any(op => Reads(op.Dmg) || Reads(op.Hits) || Reads(op.N));
            BlockCache[c] = uses;
            return uses;
        }

        static bool Reads(CombatAmount a) => a.Formula?.Contains("block", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool HasFormula(CardCombat c)
    {
        lock (FormulaCache)
        {
            if (FormulaCache.TryGetValue(c, out var has)) return has;
            has = c.Play.Any(op => !op.Dmg.IsConstant || !op.Hits.IsConstant || !op.N.IsConstant);
            FormulaCache[c] = has;
            return has;
        }
    }

    /// <summary>The state variables a formula can read, taken now (the card being played is not counted).</summary>
    private Dictionary<string, double> Snapshot(Ctx ctx)
    {
        var self = ctx.Inst;
        var piles = _hand.Concat(_draw).Concat(_discard);
        int status = 0, ostyCards = 0, starCards = 0;
        foreach (var i in piles)
        {
            if (i == self) continue;
            if (i.Card.Type == "Status") status++;
            if (HasTag(i.Card, "OstyAttack")) ostyCards++;
            if (i.Card.StarCost > 0 || i.Card.StarCostX) starCards++;
        }
        // Crescent Spear counts itself; Squeeze does not.
        if (self is not null && (self.Card.StarCost > 0 || self.Card.StarCostX)) starCards++;
        var hand = _hand.Where(h => h != self).ToList();
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["X"] = ctx.X,
            ["turn"] = _turn,
            ["attacksPlayedThisTurn"] = _attacksT,
            ["cardsPlayedThisTurn"] = _cardsT,
            ["skillsPlayedThisTurn"] = _skillsT,
            ["cardsPlayedThisCombat"] = _cardsC,
            ["cardsPlayedLastTurn"] = _cardsLastT,
            ["attacksPlayedLastTurn"] = _attacksLastT,
            ["handSize"] = hand.Count,
            ["skillsInHand"] = hand.Count(h => h.Card.Type == "Skill"),
            ["attacksInHand"] = hand.Count(h => h.Card.IsAttack),
            ["drawPileSize"] = _draw.Count,
            ["discardPileSize"] = _discard.Count,
            ["exhaustPileSize"] = _exhaust.Count,
            ["strikesInDeck"] = _s.StrikesInDeck,
            ["poisonOnTarget"] = _poison,
            ["doomOnTarget"] = _doom,
            ["vulnerableOnTarget"] = _vuln,
            ["strength"] = _strength + _tempStrength,
            ["energyLeft"] = _energy,
            ["stars"] = _stars,
            ["starsGainedThisTurn"] = _starsGainedT,
            ["starCostCardsInDeck"] = starCards,
            ["orbCount"] = _orbs.Count,
            ["orbSlots"] = _orbSlots,
            ["distinctOrbTypes"] = _orbs.Select(o => o.Kind).Distinct().Count(),
            ["lightningOrbs"] = _orbs.Count(o => o.Kind == "lightning"),
            ["frostOrbs"] = _orbs.Count(o => o.Kind == "frost"),
            ["darkOrbs"] = _orbs.Count(o => o.Kind == "dark"),
            ["lightningChanneledThisCombat"] = _lightningChanneledC,
            ["focus"] = Focus,
            ["ostyAlive"] = _ostyHp > 0 ? 1 : 0,
            ["ostyHp"] = _ostyHp,
            ["ostyAttacksThisTurn"] = _ostyAttacksT,
            ["ostyAttackCardsInDeck"] = ostyCards,
            ["etherealPlayedThisCombat"] = _etherealPlayedC,
            ["cardsDiscardedThisTurn"] = _discardedT,
            ["cardsDrawnThisCombat"] = _drawnC,
            ["cardsDrawnThisTurn"] = _drawnT,
            ["nonHandDrawsThisTurn"] = _nonHandDrawsT,
            ["exhaustedThisTurn"] = _exhaustedT,
            ["timesPlayedThisCombat"] = ctx.Card is { } c ? _timesPlayed.GetValueOrDefault(c.Id) : 0,
            ["timesDrawnThisCombat"] = self?.TimesDrawn ?? 0,
            ["cardsGeneratedThisCombat"] = _generatedC,
            ["attackHitsThisTurn"] = _attackHitsT,
            ["hpLostThisTurn"] = _hpLostT,
            ["hpLossCountThisCombat"] = _hpLossCountC,
            ["shivsInExhaust"] = _exhaust.Count(i => i.Card.Id == "CARD.SHIV"),
            ["soulsInExhaust"] = _exhaust.Count(i => i.Card.Id == "CARD.SOUL"),
            ["statusCardsOwned"] = status,
            ["statusCreatedThisTurn"] = _statusCreatedT,
            ["energySpentThisTurn"] = _energySpentT,
            ["block"] = _block,
            ["lostHp"] = 0,
            ["lastHitDamage"] = 0,
        };
    }

    // ---- the policy's value of playing a card now -------------------------------------------------------------------

    private int RemainingTurns => _turns - _turn;

    /// <summary>How deep the value of one card is asking for the value of another (a card that adds a copy of itself).</summary>
    private int _valueDepth;

    private const int MaxValueDepth = 3;

    private double Value(Inst i)
    {
        // A card that adds a copy of itself, or two cards that play each other, would ask forever: past a few levels,
        // count the card as an average one.
        if (_valueDepth >= MaxValueDepth) return _s.AvgValuePerCard * 0.5;
        _valueDepth++;
        try
        {
            return ValueOfPlay(i);
        }
        finally
        {
            _valueDepth--;
        }
    }

    private double ValueOfPlay(Inst i)
    {
        var c = i.Card;
        var x = c.CostX ? _energy : 0;
        var energyAfter = c.CostX ? 0 : _energy - CurrentCost(i);
        var ctx = CardCtx(i, x);
        if (HasFormula(c)) ctx.Vars = Snapshot(ctx);
        var v = Value(c.Play, ctx, energyAfter);
        // A pending Double-Tap-style replay doubles the card.
        if (_replays.Any(r => r.Left > 0 && Matches(r.Op.Filter, c, i, r.Source))) v *= 2;
        foreach (var t in _triggers)
        {
            if (t.Op.Op != CombatOps.OnPlay || !t.IsActive(_turn) || !Matches(t.Op.Filter, c, i, t.Source)) continue;
            if (t.Op.LimitPerTurn is int lim && t.FiredThisTurn >= lim) continue;
            if (t.Op.Every is int every && every > 1 && (t.Seen + 1) % every != 0) continue;
            v += Value(t.Op.Effects, new Ctx(), energyAfter);
        }
        if (c.Has("Ethereal") && !c.IsAttack) v += 0.5; // it would be exhausted unplayed anyway
        return v;
    }

    private double Value(IReadOnlyList<CombatOp> ops, Ctx ctx, double energyAfter)
    {
        double v = 0;
        foreach (var op in ops) v += Value(op, ctx, energyAfter);
        return v;
    }

    private double Value(CombatOp op, Ctx ctx, double energyAfter)
    {
        var r = RemainingTurns;
        var futureCards = r * _s.Character.Draw;
        switch (op.Op)
        {
            case CombatOps.Attack:
                return Count(op.Hits, ctx, 1) * PlayerHit(EnchantDamage(ctx, Amount(op.Dmg, ctx)), op.StrMult, ctx.Card, HitMods(ctx, osty: false, record: false));
            case CombatOps.OstyAttack:
                return _ostyHp > 0 ? Count(op.Hits, ctx, 1) * OstyHit(EnchantDamage(ctx, Amount(op.Dmg, ctx)), HitMods(ctx, osty: true, record: false)) : 0;
            case CombatOps.GainBlock:
            {
                var bodySlam = _hand.Any(h => h != ctx.Inst && UsesBlock(h.Card)) ? Amount(op.N, ctx) : 0;
                var triggers = _triggers.Where(t => t.Op.Op == CombatOps.OnGainBlock && t.IsActive(_turn)).Sum(t => Value(t.Op.Effects, new Ctx(), energyAfter));
                return bodySlam + triggers;
            }
            case CombatOps.LoseHp:
                return _triggers.Where(t => t.Op.Op == CombatOps.OnLoseHp && t.IsActive(_turn)).Sum(t => Value(t.Op.Effects, new Ctx(), energyAfter));
            case CombatOps.Damage:
                return Amount(op.N, ctx);
            case CombatOps.Draw:
            {
                if (_draw.Count + _discard.Count == 0 || _noDraw) return 0;
                var n = Math.Min(Amount(op.N, ctx), HandLimit - _hand.Count + 1);
                var useful = energyAfter > 0 ? 1.0 : 0.25;
                return n * _s.AvgValuePerCard * useful;
            }
            case CombatOps.Energy:
            {
                var n = Amount(op.N, ctx);
                if (n < 0) return n * _s.DamagePerEnergy;
                if (_noEnergyGain || n == 0) return 0;
                // An X-cost card in hand turns every extra energy into its own damage (Multi-Cast: one more evoke).
                var sink = XSinkPerEnergy(ctx.Inst, energyAfter, n);
                if (sink > 0) return n * Math.Max(sink, _s.DamagePerEnergy);
                var stuck = _hand.Any(h => h != ctx.Inst && !h.Card.Unplayable && CurrentCost(h) > energyAfter);
                return n * _s.DamagePerEnergy * (stuck ? 1 : 0.3);
            }
            case CombatOps.Strength:
                return Amount(op.N, ctx) * (HandHits(ctx.Inst) + _s.AvgHitsPerCard * futureCards);
            case CombatOps.TempStrength:
                return Amount(op.N, ctx) * HandHits(ctx.Inst);
            case CombatOps.LoseStrength:
                return -Amount(op.N, ctx) * (HandHits(ctx.Inst) + _s.AvgHitsPerCard * futureCards);
            case CombatOps.Vigor:
                return Amount(op.N, ctx) * Math.Max(1, HandHits(ctx.Inst) > 0 ? 1.5 : _s.AvgHitsPerCard * 2);
            case CombatOps.Vulnerable:
            {
                var turnsCovered = Math.Min(r + 1, _vuln + Amount(op.N, ctx)) - Math.Min(r + 1, _vuln);
                if (turnsCovered <= 0) return 0;
                var now = _vuln > 0 ? 0 : HandAttackDamage(ctx.Inst);
                var later = Math.Max(0, turnsCovered - (_vuln > 0 ? 0 : 1)) * _s.AvgAttackDamagePerCard * _s.Character.Draw;
                return (0.5 + _vulnBonus) * (now + later);
            }
            case CombatOps.Poison:
            {
                var n = Amount(op.N, ctx);
                return n * (r + 1) * Math.Max(1, Math.Min(1 + _accel, _poison + n)) + TriggerBonus("poison");
            }
            case CombatOps.Doom:
                return Amount(op.N, ctx) + TriggerBonus("doom");
            case CombatOps.Accelerant:
                return Math.Max(0, Math.Min(_poison, 1 + _accel + Amount(op.N, ctx)) - Math.Min(_poison, 1 + _accel)) * Math.Max(1, _poison - 1) * (r + 1) + Amount(op.N, ctx);
            case CombatOps.DoomCheck:
                return 0;
            case CombatOps.AddCard:
            {
                CardCombat? card = op.Card is null ? null : _s.Data.Card(op.Card, op.Upgraded ? 1 : 0);
                double each = card is null ? (op.Random is null ? 0 : _s.AvgValuePerCard * 0.8) : CardValue(card);
                var cost = op.Free ? 0 : op.SetCost ?? card?.Cost ?? 1;
                var affordable = cost <= 0 ? 1.0 : energyAfter >= cost ? 0.7 : 0.2;
                var where = op.To switch { null or "hand" => affordable, "drawTop" => 0.6, _ => 0.25 };
                return Count(op.N, ctx, 1) * each * where;
            }
            case CombatOps.PerTurn:
                return Value(op.Effects, new Ctx(), _s.Character.Energy) * (op.When == "end" ? r + 1 : r);
            case CombatOps.NextTurn:
                return r > 0 ? Value(op.Effects, new Ctx(), _s.Character.Energy) : 0;
            case CombatOps.OnPlay:
            case CombatOps.OnDraw:
            case CombatOps.OnExhaust:
            case CombatOps.OnApply:
            case CombatOps.OnHit:
            case CombatOps.OnDiscard:
            case CombatOps.OnStatusCreated:
            case CombatOps.OnEvoke:
            case CombatOps.OnChannel:
            case CombatOps.OnShuffle:
            case CombatOps.OnLoseHp:
            case CombatOps.OnGainBlock:
            case CombatOps.OnOstyLoseHp:
            case CombatOps.OnHandEmpty:
            case CombatOps.OnStarsSpent:
            case CombatOps.OnPotionUsed:
            {
                var turnsLeft = op.Duration == "turn" ? 0 : r;
                var matchesNow = _hand.Count(h => h != ctx.Inst && Matches(op.Filter, h.Card));
                var expected = op.Op == CombatOps.OnPlay || op.Op == CombatOps.OnHit
                    ? matchesNow + turnsLeft * _s.Character.Draw * 0.5
                    : 0.5 + turnsLeft;
                if (op.Every is int every && every > 1) expected /= every;
                return Value(op.Effects, new Ctx(), energyAfter) * expected;
            }
            case CombatOps.ReplayNext:
                return _hand.Where(h => h != ctx.Inst && Matches(op.Filter, h.Card) && Playable(h)).Select(Value).DefaultIfEmpty(0).Max();
            case CombatOps.AttackBonus:
            {
                var turns = op.Duration == "turn" ? 0 : r;
                var hits = HandHitsMatching(ctx.Inst, op.Filter) + turns * _s.AvgHitsPerCard * _s.Character.Draw;
                if (op.LimitPerTurn is int lim) hits = Math.Min(hits, lim * (turns + 1));
                return Amount(op.N, ctx) * hits;
            }
            case CombatOps.DamageMultiplier:
            {
                var turns = op.Duration switch { "turn" => 1, "nextTurn" => r > 0 ? 1 : 0, _ => r + 1 };
                var perTurn = op.Duration == "nextTurn" ? _s.AvgAttackDamagePerCard * _s.Character.Draw : Math.Max(HandAttackDamage(ctx.Inst), _s.AvgAttackDamagePerCard * _s.Character.Draw * 0.5);
                if (op.LimitPerTurn is int) perTurn = _s.AvgAttackDamagePerCard * 2;
                return (Amount(op.N, ctx) - 1) * perTurn * turns;
            }
            case CombatOps.VulnerableBonus:
                return Amount(op.N, ctx) * _s.AvgAttackDamagePerCard * futureCards * 0.5;
            case CombatOps.CostMod:
            {
                if (op.Filter == "self") return op.Set is 0 || op.Delta < 0 ? 1 : -0.5;
                var saved = op.Set is double set ? Math.Max(0, 1 - set) : -op.Delta;
                var count = op.Filter == "hand" ? _hand.Count(h => h != ctx.Inst) : op.Duration == "next" ? op.Count ?? 1 : 2 + r * 2;
                return saved * count * _s.DamagePerEnergy * 0.6;
            }
            case CombatOps.PlayTop:
            case CombatOps.AutoPlayTop:
            case CombatOps.PlayFromDiscard:
                return Count(op.N, ctx, 1) * _s.AvgValuePerCard;
            case CombatOps.PlayFromHand:
                return _hand.Where(h => h != ctx.Inst && Matches(op.Filter, h.Card)).Select(Value).DefaultIfEmpty(0).Max();
            case CombatOps.Fetch:
            case CombatOps.ReturnFromDiscard:
                return energyAfter > 0 ? Count(op.N, ctx, 2) * _s.AvgValuePerCard * 0.9 : Count(op.N, ctx, 2) * 0.5;
            case CombatOps.UpgradeInHand:
            case CombatOps.UpgradeCards:
                return Count(op.N, ctx, 3) * 1.0;
            case CombatOps.Channel:
                return Count(op.N, ctx, 1) * OrbValue(op.Orb ?? "lightning", r);
            case CombatOps.Evoke:
            {
                if (_orbs.Count == 0) return 0;
                var n = Count(op.N, ctx, _orbs.Count);
                var times = Count(op.Times, ctx, 1);
                if (times <= 0) return 0;
                var orbs = op.From == "last" ? _orbs.AsEnumerable().Reverse() : _orbs;
                return orbs.Take(n).Sum(o => times * EvokeValue(o)) - n * 0.5;
            }
            case CombatOps.Focus:
                return Amount(op.N, ctx) * Math.Max(1, _orbs.Count) * (r + 1);
            case CombatOps.TempFocus:
                return Amount(op.N, ctx) * _orbs.Count;
            case CombatOps.OrbSlots:
                return Amount(op.N, ctx) * 2;
            case CombatOps.TriggerOrbPassives:
                return Count(op.N, ctx, 1) * _orbs.Sum(o => OrbValue(o.Kind, 0));
            case CombatOps.Stars:
                return Amount(op.N, ctx) * 3;
            case CombatOps.Forge:
                return Amount(op.N, ctx) * (1 + r * 0.5);
            case CombatOps.Summon:
                return _ostyHp > 0 ? 0.5 : 4;
            case CombatOps.KillOsty:
                return _triggers.Any(t => t.Op.Op == CombatOps.OnOstyLoseHp) ? _ostyHp : -2;
            case CombatOps.DiscardCards:
                return _hand.Where(h => h != ctx.Inst && h.Card.Has("Sly")).Take(Count(op.N, ctx, 10)).Sum(h => Math.Max(0, Value(h)));
            case CombatOps.RetainHand:
                return 0.5;
            case CombatOps.NoDraw:
                return -0.5;
            case CombatOps.CopyInHand:
                return _hand.Where(h => h != ctx.Inst && Matches(op.Filter, h.Card)).Select(Value).DefaultIfEmpty(0).Max() * 0.6;
            case CombatOps.ReturnNextTurn:
                return 0.5;
            default:
                return 0;
        }
    }

    private double TriggerBonus(string power)
    {
        double v = 0;
        foreach (var t in _triggers)
        {
            if (t.Op.Op != CombatOps.OnApply || !t.IsActive(_turn)) continue;
            var want = t.Op.Power ?? "debuff";
            if (want != power && !(want == "debuff" && power is "poison" or "doom" or "vulnerable")) continue;
            var each = Value(t.Op.Effects, new Ctx(), _energy);
            v += t.Op.Every is int every && every > 1 ? each / every : each;
        }
        return v;
    }

    private double CardValue(CardCombat card) => Value(new Inst(card));

    // ---- enchantments ------------------------------------------------------------------------------------------------

    private static bool IsImbued(Inst i) => i.Ench?.Id == Enchantments.Imbued;

    /// <summary>The card's own attack damage with its enchantment (not a trigger's damage that merely names the card).</summary>
    private static double EnchantDamage(Ctx ctx, double dmg)
        => ctx.Inst is { Ench: { } e } i && ReferenceEquals(ctx.Card, i.Card) ? Enchantments.Damage(e, dmg, i.Momentum, i.EnchUsed) : dmg;

    private static readonly CombatOp CorruptedSelfDamage = new() { Op = CombatOps.LoseHp, N = new CombatAmount(2, null) };
    private static readonly CombatOp InkyWeak = new() { Op = CombatOps.Weak, N = CombatAmount.One };

    /// <summary><c>Enchantment.OnPlay</c>: after each play of the card (a replay included).</summary>
    private void EnchantOnPlay(Inst i)
    {
        if (i.Ench is not { } e) return;
        switch (e.Id)
        {
            case Enchantments.Swift when !i.EnchUsed:
                i.EnchUsed = true;
                Draw(e.Amount, handDraw: false);
                break;
            case Enchantments.Sown when !i.EnchUsed:
                i.EnchUsed = true;
                GainEnergy(e.Amount);
                break;
            case Enchantments.Adroit:
                Exec(new CombatOp { Op = CombatOps.GainBlock, N = new CombatAmount(e.Amount, null) }, new Ctx());
                break;
            case Enchantments.Corrupted:
                Exec(CorruptedSelfDamage, new Ctx());
                break;
            case Enchantments.Inky:
                Exec(InkyWeak, new Ctx());
                break;
            case Enchantments.Momentum:
                i.Momentum += e.Amount;
                break;
        }
    }

    /// <summary>
    /// What one more energy is worth to the best X-cost card in hand: its value at X = energy left + n, minus at the
    /// energy left, per energy. 0 when there is none.
    /// </summary>
    private double XSinkPerEnergy(Inst? self, double energyAfter, double n)
    {
        if (_valueDepth >= MaxValueDepth) return 0;
        double best = 0;
        foreach (var h in _hand)
        {
            if (h == self || !h.Card.CostX || h.Card.Unplayable) continue;
            var before = XValue(h, Math.Max(0, energyAfter));
            var after = XValue(h, Math.Max(0, energyAfter) + n);
            best = Math.Max(best, (after - before) / n);
        }
        return best;
    }

    private double XValue(Inst i, double x)
    {
        _valueDepth++;
        try
        {
            var ctx = CardCtx(i, x);
            if (HasFormula(i.Card)) ctx.Vars = Snapshot(ctx);
            return Value(i.Card.Play, ctx, 0);
        }
        finally
        {
            _valueDepth--;
        }
    }

    private double EvokeValue(Orb o) => o.Kind switch
    {
        "lightning" => 8 + Focus,
        "dark" => o.Value,
        "glass" => 2 * (o.Value + Focus),
        "plasma" => 2 * _s.DamagePerEnergy * 0.5,
        _ => 0,
    };

    private double OrbValue(string kind, int r) => kind switch
    {
        "lightning" => (3 + Focus) * (r + 1),
        "dark" => (6 + Focus) * r + (r > 0 ? 3 : 0),
        "plasma" => _s.DamagePerEnergy * r,
        "glass" => Math.Max(0, 4 + Focus) * Math.Min(r + 1, 4),
        "random" => 3 * (r + 1),
        _ => 0.5,
    };

    private double HandHits(Inst? self) => HandHitsMatching(self, null);

    private double HandHitsMatching(Inst? self, string? filter)
    {
        double h = 0;
        foreach (var i in _hand)
        {
            if (i == self || !i.Card.IsAttack || (filter is not null && !MatchesHit(filter, i.Card, false, i, self))) continue;
            foreach (var op in i.Card.Play)
            {
                if (op.Op == CombatOps.Attack) h += op.Hits.IsConstant ? op.Hits.Constant : 1;
            }
        }
        return h;
    }

    private double HandAttackDamage(Inst? self)
    {
        double d = 0;
        foreach (var i in _hand)
        {
            if (i == self || !i.Card.IsAttack) continue;
            foreach (var op in i.Card.Play)
            {
                if (op.Op is CombatOps.Attack or CombatOps.OstyAttack)
                    d += (op.Hits.IsConstant ? op.Hits.Constant : 1) * (op.Dmg.IsConstant ? op.Dmg.Constant : 6);
            }
        }
        return d;
    }
}
