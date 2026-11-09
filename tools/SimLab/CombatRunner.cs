using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace SimTrial;

public sealed record SimOutcome(bool Won, bool Died, bool TimedOut, int Damage, int Turns, string? MonsterNote);

public static class CombatRunner
{
    public const int MaxTurns = 30;
    public static bool Trace = Environment.GetEnvironmentVariable("SIMTRIAL_TRACE") == "1";

    /// <summary>Give the player the potions the real player used in this fight (--potions).</summary>
    public static bool GivePotions;

    /// <summary>
    /// Picks a monster batch for the encounter. Encounters with random composition are generated with
    /// successive monster RNG seeds until the multiset of monster ids equals the real fight's list.
    /// </summary>
    public static (IReadOnlyList<(MonsterModel Monster, string? SlotName)> Monsters, string? Note) CreateMonsters(
        EncounterDefinition enc, IReadOnlyList<string> realMonsters, ulong baseSeed)
    {
        string want = string.Join(",", realMonsters.OrderBy(s => s, StringComparer.Ordinal));
        IReadOnlyList<(MonsterModel, string?)>? first = null;
        IReadOnlyList<(MonsterModel, string?)>? subset = null;
        for (ulong i = 0; i < 64; i++)
        {
            IReadOnlyList<(MonsterModel Monster, string? SlotName)> batch;
            try { batch = enc.CreateMonsters(new Rng(baseSeed + i)); }
            catch (InvalidOperationException) { batch = enc.CreateMonsters(); }
            first ??= batch;
            string got = string.Join(",", batch.Select(m => m.Monster.Id.ToString()).OrderBy(s => s, StringComparer.Ordinal));
            if (got == want) return (batch, null);
            // Real lists can include monsters summoned mid-fight; accept a sub-multiset as a fallback.
            if (subset is null)
            {
                var pool = realMonsters.ToList();
                if (batch.All(m => pool.Remove(m.Monster.Id.ToString()))) subset = batch;
            }
        }
        if (subset is not null) return (subset, null);
        string firstGot = string.Join("|", first!.Select(m => m.Item1.Id.Entry));
        return (first!, "monsters_differ(sim=" + firstGot + ")");
    }

    public static SimOutcome RunOne(FightSpec f, int seedIndex, ICombatPolicy policy, TimeSpan wallCap)
    {
        FightRow row = f.Row;
        EncounterInfo enc = f.Encounter!;
        string seed = $"{row.run_id}_{row.node_index}_{seedIndex}";

        ActDefinition act1 = enc.ActType == typeof(Underdocks) ? new Underdocks() : new Overgrowth();
        var acts = new ActDefinition[] { act1, new Hive(), new Glory() };
        var runState = new RunState(seed, acts, ascensionLevel: 10);
        Player player = Player.CreateForNewRun(f.Character!, runState);
        runState.AddPlayer(player);

        // Deck: exactly the recorded deck, with upgrades and enchantments.
        foreach (CardModel c in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(c);
        foreach (CardSpec cs in f.Deck)
        {
            var card = (CardModel)cs.Canonical.MutableClone();
            card.AssignOwner(player);
            card.FloorAddedToDeck = 1;
            if (cs.Enchantment is not null)
            {
                var ench = (EnchantmentModel)cs.Enchantment.MutableClone();
                ench.AssignMagnitude(cs.EnchantAmount);
                card.AttachEnchantment(ench);
                ench.OnAttached(card);
            }
            for (int u = 0; u < cs.Upgrades; u++) card.Upgrade();
            player.Deck.AddInternal(card);
        }
        // Relics: exactly the recorded relics (unmapped ones were skipped in mapping).
        foreach (RelicModel r in player.Relics.ToArray()) player.RemoveRelicInternal(r);
        foreach (RelicModel canonical in f.Relics)
        {
            var relic = (RelicModel)canonical.MutableClone();
            relic.AssignOwner(player);
            player.AddRelicInternal(relic);
            if (row.relic_state is not null && row.relic_state.TryGetValue(relic.Id.Entry, out int st)) SetRelicState(relic, st);
        }
        // Potions: none, or (--potions) exactly the ones the real player used in this fight.
        foreach (PotionModel? old in player.PotionSlots.ToArray()) if (old is not null) player.RemovePotionInternal(old);
        if (GivePotions && f.Potions.Count > 0)
        {
            if (player.MaxPotionCount < f.Potions.Count) player.GrowPotionSlots(f.Potions.Count - player.MaxPotionCount);
            foreach (PotionModel pm in f.Potions) player.AddPotionInternal(pm);
        }
        // HP.
        player.Creature.SetMaxHpInternal(row.max_hp_before);
        player.Creature.SetCurrentHpInternal(Math.Clamp(row.hp_before, 1, row.max_hp_before));

        for (int a = 1; a < Math.Clamp(row.act, 1, 3); a++) runState.AdvanceToNextAct();

        ulong monsterSeed = (ulong)(uint)StringHelperHash(seed);
        var (monsters, note) = CreateMonsters(enc.Definition, row.monsters, monsterSeed);
        var room = new CombatRoom(() => (enc.Definition, monsters), enc.RoomType);
        room.ConfigureCardSelectionSource(SimpleSelector.Instance);
        runState.PushRoom(room);
        room.Enter(runState).GetAwaiter().GetResult();

        CombatEngine engine = room.Engine;
        CombatState state = engine.State;
        var sw = Stopwatch.StartNew();
        bool timedOut = false;
        int guard = 0;
        // Backtracking (Tun.Backtracks > 0, beam policy only): checkpoint every player-turn start; when
        // the player dies, rewind to the turn before and take that turn's next-best plan (up to 3 per
        // turn, then one turn further back). The simulator replays the same future exactly, so this is
        // a search with full knowledge of draws and enemy rolls.
        var checkpoints = new Dictionary<int, (CombatState State, BeamPolicy Policy)>();
        var tries = new Dictionary<int, int>();
        var bans = new Dictionary<int, HashSet<string>>();   // turn -> end-of-turn states that lost
        var endState = new Dictionary<int, string>();        // turn -> this line's end-of-turn state
        int backtracksLeft = policy is BeamPolicy ? Tun.Backtracks : 0;
        int lastCheckpoint = -1;
        while (true)
        {
            if (!engine.IsInProgress)
            {
                if (!player.Creature.IsDead || backtracksLeft <= 0 || timedOut) break;
                int deathTurn = player.PlayerCombatState?.TurnNumber ?? 1;
                int bt = -1;
                // the turn whose enemy phase killed us first, then up to three turns earlier
                for (int t = deathTurn; t >= Math.Max(1, deathTurn - 3); t--)
                {
                    if (checkpoints.ContainsKey(t) && tries.GetValueOrDefault(t) < 3) { bt = t; break; }
                }
                if (bt < 0) break;
                backtracksLeft--;
                tries[bt] = tries.GetValueOrDefault(bt) + 1;
                if (endState.TryGetValue(bt, out string? lost))
                {
                    if (!bans.TryGetValue(bt, out var set)) bans[bt] = set = new HashSet<string>();
                    set.Add(lost);
                }
                foreach (int t in checkpoints.Keys.Where(t => t > bt).ToList()) { checkpoints.Remove(t); tries.Remove(t); bans.Remove(t); }
                endState.Clear();
                (CombatState cs, BeamPolicy cp) = checkpoints[bt];
                state = cs.Clone(out _);
                state.CardSelectionSource = SimpleSelector.Instance;
                engine = state.Engine!;
                player = state.Players[0];
                BeamPolicy np = cp.Copy();
                if (bans.TryGetValue(bt, out var banned)) np.Ban(bt, banned);
                policy = np;
                lastCheckpoint = bt;
                if (Trace) Console.WriteLine($"-- backtrack to T{bt} (try {tries[bt]}), {backtracksLeft} left");
                continue;
            }
            engine.CheckWinCondition();
            if (!engine.IsInProgress) continue;
            if (backtracksLeft > 0 && state.CurrentSide == CombatSide.Player && player.PlayerCombatState!.TurnNumber != lastCheckpoint)
            {
                lastCheckpoint = player.PlayerCombatState.TurnNumber;
                CombatState snap = state.Clone(out _);
                snap.CardSelectionSource = SimpleSelector.Instance;
                checkpoints[lastCheckpoint] = (snap, ((BeamPolicy)policy).Copy());
            }
            if (player.PlayerCombatState!.TurnNumber > MaxTurns || sw.Elapsed > wallCap || ++guard > 2000)
            {
                timedOut = true;
                break;
            }
            LookaheadPolicy.Action action = policy.Choose(state);
            if (Trace)
            {
                var pcs = player.PlayerCombatState;
                string enemies = string.Join(" ", state.Enemies.Where(e => e.IsAlive).Select(e =>
                    $"{e.Monster!.Id.Entry}:{e.CurrentHp}hp/{e.Block}b"));
                string hand = string.Join(" ", pcs.Hand.Cards.Select(c => c.Id.Entry + (c.IsUpgraded ? "+" : "")));
                string act = action switch
                {
                    LookaheadPolicy.Play pl => $"PLAY {pl.Card.Id.Entry}{(pl.Target is null ? "" : " -> " + pl.Target.Monster?.Id.Entry)}",
                    LookaheadPolicy.UsePotion up => $"POTION {up.Potion.Id.Entry}{(up.Target?.Monster is null ? "" : " -> " + up.Target.Monster.Id.Entry)}",
                    _ => "END",
                };
                Console.WriteLine($"T{pcs.TurnNumber} hp={player.Creature.CurrentHp} blk={player.Creature.Block} e={pcs.Energy} " +
                                  $"inc={LookaheadPolicy.IncomingDamage(state, player)}/{Eval.Incoming(state, player)} | {enemies} | hand: {hand} => {act}");
            }
            if (action is LookaheadPolicy.UsePotion use)
            {
                Sts2Sim.Core.Commands.PotionCmd.Use(use.Potion, player, use.Target).GetAwaiter().GetResult();
            }
            else if (action is LookaheadPolicy.Play play)
            {
                int handBefore = player.PlayerCombatState.Hand.Cards.Count;
                int energyBefore = player.PlayerCombatState.Energy;
                var res = engine.PlayCardWithResultAsync(player, play.Card, play.Target).GetAwaiter().GetResult();
                engine.CheckWinCondition();
                if (res is null && engine.IsInProgress && handBefore == player.PlayerCombatState.Hand.Cards.Count &&
                    energyBefore == player.PlayerCombatState.Energy)
                {
                    engine.EndPlayerTurnAsync().GetAwaiter().GetResult(); // unplayable after all
                }
            }
            else
            {
                if (backtracksLeft > 0) endState[player.PlayerCombatState.TurnNumber] = BeamPolicy.EndStateKey(state);
                engine.EndPlayerTurnAsync().GetAwaiter().GetResult();
            }
            engine.CheckWinCondition();
        }

        bool died = player.Creature.IsDead;
        int damage = died ? row.hp_before : Math.Max(0, player.Creature.CumulativeHpLost);
        int turns = player.PlayerCombatState?.TurnNumber ?? 0;
        return new SimOutcome(!timedOut && engine.Won, died, timedOut, damage, turns, note);
    }

    /// <summary>
    /// Restores run-long relic state that the relic list alone does not carry (relics are added without
    /// their obtain effects): Girya lifts, Pumpkin Candle combats left lit, tea sets armed by a rest site.
    /// The fields are private in the simulator, so they are set by reflection (offline tool only).
    /// </summary>
    public static void SetRelicState(RelicModel relic, int value)
    {
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        Type t = relic.GetType();
        switch (relic.Id.Entry)
        {
            case "GIRYA":
                t.GetProperty("TimesLifted", F)!.SetValue(relic, Math.Clamp(value, 0, 3));
                break;
            case "PUMPKIN_CANDLE":
                t.GetProperty("KindleCount", F)!.SetValue(relic, Math.Max(0, value));
                break;
            case "VENERABLE_TEA_SET":
            case "FAKE_VENERABLE_TEA_SET":
                t.GetField("_isArmed", F)!.SetValue(relic, value != 0);
                break;
        }
    }

    private static int StringHelperHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (char ch in s) h = h * 31 + ch;
            return h;
        }
    }
}
