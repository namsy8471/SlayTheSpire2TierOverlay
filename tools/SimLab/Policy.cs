using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.MonsterMoves.Intents;

namespace SimTrial;

/// <summary>
/// Deterministic card-selection prompts (discard / exhaust / pick-one-of-N ...).
/// If every candidate is in the player's hand the prompt is treated as a sacrifice (discard,
/// exhaust, put back): pick only the required minimum, worst cards first (status/curse, then
/// basic cards, then cheap commons). Otherwise (generated / pile choices) it is treated as a
/// gain: pick max(min, 1) cards, best first.
/// </summary>
public sealed class SimpleSelector : ICardSelectionDecisionSource
{
    public static readonly SimpleSelector Instance = new();
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Prompts = new();

    public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
    {
        IReadOnlyList<CardModel> cands = request.Candidates;
        int max = Math.Min(request.MaxCount, cands.Count);
        int min = Math.Min(request.MinCount, max);
        if (cands.Count == 0 || max <= 0) return Task.FromResult<IReadOnlyList<CardModel>>(Array.Empty<CardModel>());

        IReadOnlyList<CardModel>? hand = request.Player.PlayerCombatState?.Hand.Cards;
        bool allInHand = !request.IsBundleSelection && hand is not null && cands.All(c => hand.Contains(c));
        if (BeamPolicy.Profile)
            Prompts.AddOrUpdate((request.Source?.GetType().Name ?? "?") + (allInHand ? " hand" : " other") + $" min{request.MinCount} max{request.MaxCount}", 1, (_, v) => v + 1);
        IReadOnlyList<CardModel> picked;
        if (allInHand)
        {
            string src = request.Source?.GetType().Name ?? "";
            if (BestFromHand.Contains(src))
                picked = cands.OrderByDescending(Worth).Take(Math.Max(min, 1)).ToArray(); // upgrade / copy / make free
            else if (DiscardFromHand.Contains(src))
                picked = cands.OrderBy(c => IsSly(c) ? -20 : Worth(c)).Take(min).ToArray(); // Sly cards play when discarded
            else
                picked = cands.OrderBy(Worth).Take(min).ToArray();
        }
        else
        {
            int k = Math.Max(min, Math.Min(1, max));
            picked = request.IsBundleSelection
                ? cands.Take(k).ToArray()
                : cands.OrderByDescending(Worth).Take(k).ToArray();
        }
        return Task.FromResult(picked);
    }

    /// <summary>Hand prompts that improve the chosen card (upgrade, copy, make free): choose the best.</summary>
    public static readonly HashSet<string> BestFromHand = new() { "Armaments", "DualWield", "Nightmare", "TouchOfInsanity", "HeirloomHammer" };

    /// <summary>Hand prompts that discard (Sly cards are played for free when discarded).</summary>
    public static readonly HashSet<string> DiscardFromHand = new()
        { "Acrobatics", "DaggerThrow", "HiddenDaggers", "Prepared", "Survivor", "ToolsOfTheTradePower", "GamblingChip", "GamblersBrew" };

    public static bool IsSly(CardModel c) => c.HasKeyword(CardKeyword.Sly) || c.TemporarySlyThisTurn;

    /// <summary>Crude card worth used for sacrifice/gain choices.</summary>
    public static double Worth(CardModel c)
    {
        double w = c.Type switch
        {
            CardType.Curse => -10,
            CardType.Status => -8,
            _ => 0,
        };
        w += c.Rarity switch
        {
            CardRarity.Basic => 1,
            CardRarity.Common => 3,
            CardRarity.Uncommon => 4,
            CardRarity.Rare => 5,
            CardRarity.Ancient => 6,
            CardRarity.Status or CardRarity.Curse => 0,
            _ => 2,
        };
        if (c.IsUpgraded) w += 1;
        if (c.Type == CardType.Power) w += 1;
        return w;
    }
}

/// <summary>
/// One-step lookahead policy with a heuristic within-turn rollout:
/// for every legal action (each playable card x each legal target, deduplicated by identical
/// cards, plus End Turn) clone the combat, apply the action, finish the turn with the cheap
/// <see cref="RolloutTurn"/> heuristic, end the turn so the enemies act and the next player turn
/// starts, then score the resulting state with <see cref="Evaluate"/>. The best action is played
/// for real and the process repeats. Ties favour playing a card over ending the turn.
/// </summary>
public sealed class LookaheadPolicy : ICombatPolicy
{
    public int RolloutFailures;

    /// <summary>Player turns simulated per candidate: 1 = this turn + enemy turn; 2 = also the next
    /// turn with the heuristic rollout + the following enemy turn.</summary>
    public int Horizon { get; init; } = 1;

    /// <summary>Value of one enemy HP point relative to one player HP point.</summary>
    public static double EnemyHpWeight = 0.6;

    public abstract record Action;
    public sealed record Play(CardModel Card, Creature? Target) : Action;
    public sealed record End : Action;
    public sealed record UsePotion(PotionModel Potion, Creature? Target) : Action;

    public Action Choose(CombatState state)
    {
        Player player = state.Players[0];
        var candidates = LegalPlays(state, player);
        if (candidates.Count == 0) return new End();

        double best = double.NegativeInfinity;
        Action bestAction = new End();
        double endValue = Simulate(state, null, null);
        foreach ((CardModel card, Creature? target) in candidates)
        {
            double v = Simulate(state, card, target);
            if (v > best)
            {
                best = v;
                bestAction = new Play(card, target);
            }
        }
        // Prefer playing on (near-)ties: draw/setup cards look neutral at this horizon.
        if (best >= endValue - 0.01) return bestAction;
        return new End();
    }

    public static List<(CardModel Card, Creature? Target)> LegalPlays(CombatState state, Player player)
    {
        var result = new List<(CardModel, Creature?)>();
        var seen = new HashSet<string>();
        IReadOnlyList<CardModel> hand = player.PlayerCombatState!.Hand.Cards;
        foreach (CardModel card in hand)
        {
            if (!card.CanPlay(out _)) continue;
            string key = card.Id + "+" + card.CurrentUpgradeLevel + "#" + string.Join(",", card.Enchantments.Select(en => en.Id.ToString())) +
                         ":" + card.EnergyCost;
            if (card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer)
            {
                IReadOnlyList<Creature> targets = CombatTargetCandidates.ForCard(state, player, card.TargetType);
                for (int i = 0; i < targets.Count; i++)
                {
                    if (seen.Add(key + "@" + i)) result.Add((card, targets[i]));
                }
            }
            else if (seen.Add(key)) result.Add((card, null));
        }
        return result;
    }

    /// <summary>Clone, apply the action (null card = end turn), roll out the turn, run the enemy turn, score.</summary>
    private double Simulate(CombatState state, CardModel? card, Creature? target)
    {
        try
        {
            CombatState clone = state.Clone(out CombatCloneMap map);
            clone.CardSelectionSource = SimpleSelector.Instance;
            CombatEngine engine = clone.Engine!;
            Player p = clone.Players[0];
            int startTurn = p.PlayerCombatState!.TurnNumber;
            if (card is not null)
            {
                CardModel cc = map.Card(card);
                Creature? ct = target is null ? null : map.Creature(target);
                engine.PlayCardAsync(p, cc, ct).GetAwaiter().GetResult();
                engine.CheckWinCondition();
                if (engine.IsInProgress && clone.CurrentSide == CombatSide.Player &&
                    p.PlayerCombatState!.TurnNumber == startTurn)
                    RolloutTurn(clone, p, startTurn);
            }
            if (engine.IsInProgress && clone.CurrentSide == CombatSide.Player &&
                p.PlayerCombatState!.TurnNumber == startTurn)
            {
                engine.EndPlayerTurnAsync().GetAwaiter().GetResult();
                engine.CheckWinCondition();
            }
            for (int h = 1; h < Horizon && engine.IsInProgress && clone.CurrentSide == CombatSide.Player; h++)
            {
                int t = p.PlayerCombatState!.TurnNumber;
                RolloutTurn(clone, p, t);
                if (engine.IsInProgress && clone.CurrentSide == CombatSide.Player && p.PlayerCombatState!.TurnNumber == t)
                {
                    engine.EndPlayerTurnAsync().GetAwaiter().GetResult();
                    engine.CheckWinCondition();
                }
            }
            return Evaluate(clone, p);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref RolloutFailures);
            return double.NegativeInfinity;
        }
    }

    /// <summary>Cheap in-place greedy completion of the current turn (no further cloning).</summary>
    public static void RolloutTurn(CombatState state, Player p, int turn)
    {
        CombatEngine engine = state.Engine!;
        for (int step = 0; step < 15; step++)
        {
            if (!engine.IsInProgress || state.CurrentSide != CombatSide.Player ||
                p.PlayerCombatState!.TurnNumber != turn) return;
            int unblocked = IncomingDamage(state, p) - p.Creature.Block;
            CardModel? bestCard = null;
            double bestScore = 0;
            foreach (CardModel c in p.PlayerCombatState.Hand.Cards)
            {
                if (!c.CanPlay(out _)) continue;
                if (c.TargetType is TargetType.AnyAlly or TargetType.AnyPlayer) continue;
                double s = c.Type switch
                {
                    CardType.Power => 50,
                    CardType.Attack => 30,
                    CardType.Skill => c.GainsBlock ? (unblocked > 0 ? 40 : 8) : 25,
                    _ => 0, // status / curse: never in the rollout
                };
                if (s <= 0) continue;
                if (c.EnergyCost == 0) s += 5;
                if (s > bestScore)
                {
                    bestScore = s;
                    bestCard = c;
                }
            }
            if (bestCard is null) return;
            Creature? tgt = null;
            if (bestCard.TargetType == TargetType.AnyEnemy)
            {
                tgt = state.HittableEnemies.OrderBy(e => e.CurrentHp + e.Block).FirstOrDefault();
                if (tgt is null) return;
            }
            int handBefore = p.PlayerCombatState.Hand.Cards.Count;
            int energyBefore = p.PlayerCombatState.Energy;
            var played = engine.PlayCardWithResultAsync(p, bestCard, tgt).GetAwaiter().GetResult();
            engine.CheckWinCondition();
            if (played is null && handBefore == p.PlayerCombatState?.Hand.Cards.Count &&
                energyBefore == p.PlayerCombatState?.Energy) return; // could not play; avoid a loop
        }
    }

    public static int IncomingDamage(CombatState state, Player p)
    {
        int total = 0;
        var targets = new[] { p.Creature };
        foreach (Creature e in state.Enemies)
        {
            if (!e.IsAlive || e.Monster?.NextMove is not { } move) continue;
            foreach (AbstractIntent intent in move.Intents)
            {
                if (intent is AttackIntent atk)
                {
                    try { total += atk.GetTotalDamage(targets, e); } catch (Exception) { }
                }
            }
        }
        return total;
    }

    /// <summary>
    /// State value after the enemy turn. Units are roughly "player HP points".
    /// win: 1000 + HP; loss: -1000 + fraction of enemy HP removed.
    /// otherwise: HP - 0.6*enemy HP(+block/2) + 8/kill + buffs/debuffs + orbs + pet HP.
    /// </summary>
    public static double Evaluate(CombatState state, Player p)
    {
        CombatEngine engine = state.Engine!;
        double enemyHp = 0, enemyMax = 0, kills = 0, enemyDebuffs = 0, enemyBuffs = 0;
        foreach (Creature e in state.Enemies)
        {
            enemyMax += e.MaxHp;
            if (!e.IsAlive)
            {
                if (e.IsPrimaryEnemy) kills++;
                continue;
            }
            enemyHp += e.CurrentHp + 0.5 * e.Block;
            foreach (PowerModel pw in e.Powers)
            {
                int a = Math.Min(Math.Abs(pw.Amount), 15);
                if (pw.Type == PowerType.Debuff) enemyDebuffs += a;
                else if (pw.Type == PowerType.Buff) enemyBuffs += a;
            }
        }
        if (!engine.IsInProgress && engine.Won) return 1000 + p.Creature.CurrentHp;
        if (p.Creature.IsDead || (!engine.IsInProgress && !engine.Won))
            return -1000 + (enemyMax > 0 ? 100 * (1 - enemyHp / enemyMax) : 0);

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

        return p.Creature.CurrentHp
               + 0.25 * p.Creature.Block
               - EnemyHpWeight * enemyHp
               + 8 * kills
               + 1.5 * buffs - 1.5 * debuffs
               + 1.0 * enemyDebuffs - 0.5 * enemyBuffs
               + 2 * orbs
               + 0.3 * pets;
    }
}
