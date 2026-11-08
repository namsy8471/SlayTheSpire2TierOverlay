using System.Text.Json;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

public class GameStateTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static GameStateTracker NewTracker(ulong? netId = null) =>
        new(new TrackerOptions { LocalNetIdOverride = netId, Clock = () => T0 });

    /// <summary>Feeds fixture lines up to and including the first line matching <paramref name="until"/>; returns the rest.</summary>
    private static string[] FeedUntil(GameStateTracker t, string[] lines, string until)
    {
        int i = Array.FindIndex(lines, l => l.Contains(until, StringComparison.Ordinal));
        Assert.True(i >= 0, $"fixture has no line containing '{until}'");
        t.ProcessLines(lines[..(i + 1)]);
        return lines[(i + 1)..];
    }

    private static int Count(TrackedRun run, string cardId) => run.State.Deck.Count(c => c.Id == cardId);

    // ------------------------------------------------------------------------------------------
    // Single player, log only (no save fed): exercises every log signal on a real (anonymised) run.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Single_run_screen_hints_rewards_and_pity_follow_the_log()
    {
        var t = NewTracker();
        string[] rest = TestPaths.FixtureLines("single_ironclad_run.log");

        rest = FeedUntil(t, rest, "Embarking on a singleplayer IRONCLAD");
        TrackedRun run = t.Current!;
        Assert.False(run.FromSave);
        Assert.Equal(("CHARACTER.IRONCLAD", 0, 10), (run.State.Character, run.State.Ascension, run.State.Deck.Count));
        Assert.Equal(-0.05, run.RarityPityOffset);

        // Combat won → reward screen; the three rarity lines that follow are this reward's card slots.
        rest = FeedUntil(t, rest, "has won against encounter ENCOUNTER.SLIMES_WEAK");
        rest = FeedUntil(t, rest, "Card rarity: Rolled 0.19927086");
        run = t.Current!;
        Assert.Equal(ScreenHint.RewardScreenLikely, run.Screen);
        Assert.Equal(OfferContext.CardReward, run.OfferContext);
        Assert.Equal(3, run.LastRewardRolls.Count);
        Assert.All(run.LastRewardRolls, r => Assert.False(r.IsRare));
        Assert.Equal(-0.02, run.RarityPityOffset!.Value, 6); // -0.05 → -0.04 → -0.03 → next -0.02

        rest = FeedUntil(t, rest, "obtained CARD.ANGER");
        run = t.Current!;
        Assert.Equal(ScreenHint.Map, run.Screen); // card picked
        Assert.Equal(1, Count(run, "CARD.ANGER"));
        Assert.Equal(2, run.State.Floor);

        // Shop: inventory rarity rolls come right before the room and must NOT move the pity counter.
        rest = FeedUntil(t, rest, "obtained CARD.BLOODLETTING"); // 2nd fight: offsets -0.02, -0.01, 0
        double pityBeforeShop = t.Current!.RarityPityOffset!.Value;
        Assert.Equal(0.01, pityBeforeShop, 6);
        rest = FeedUntil(t, rest, "Preloading 'Merchant Room'");
        run = t.Current!;
        Assert.Equal(ScreenHint.ShopLikely, run.Screen);
        Assert.Equal(OfferContext.Shop, run.OfferContext);
        Assert.Equal(pityBeforeShop, run.RarityPityOffset);

        rest = FeedUntil(t, rest, "has won against encounter ENCOUNTER.BYRDONIS_ELITE");
        Assert.Equal(OfferContext.EliteReward, t.Current!.OfferContext);
        FeedUntil(t, rest, "Card rarity: Rolled 0.6890903");
        Assert.All(t.Current!.LastRewardRolls, r => Assert.Equal(RaritySource.Elite, r.Source));
    }

    [Fact]
    public void Single_run_full_replay_rebuilds_deck_floor_and_outcome()
    {
        var t = NewTracker();
        t.ProcessLines(TestPaths.FixtureLines("single_ironclad_run.log"));
        TrackedRun run = t.Current!;

        // Floors counted from room preloads must equal the game's own count in the run-history file.
        using (JsonDocument history = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RunsDir, "1794130978.run"))))
            Assert.Equal(MapPointHistory.TotalFloors(history.RootElement.GetProperty("map_point_history")), run.State.Floor);
        Assert.Equal(17, run.State.Floor);
        Assert.Equal(1, run.State.Act);

        // Starting 10 + 6 reward cards - 1 shop removal + FIGHT_ME. Single player logs no event options, so event
        // "chose cards" lines can't be interpreted: those cards are kept and flagged.
        Assert.Equal(16, run.State.Deck.Count);
        Assert.Equal(4, Count(run, "CARD.STRIKE_IRONCLAD"));
        Assert.Contains(run.UncertainCards, u => u.CardId == "CARD.STRIKE_IRONCLAD" && u.Reason == UncertainReason.UnknownCardSelect);
        Assert.Equal(1, run.State.CardRemovalsBought);
        // Rest-site smith: one JUGGLING upgraded, the other (picked later) not. FIGHT_ME came from Neow (not logged),
        // but smithing it proves it is in the deck: it is added (Inferred) with the upgrade.
        Assert.Equal([0, 1], run.State.Deck.Where(c => c.Id == "CARD.JUGGLING").Select(c => c.Upgrade).Order());
        Assert.Equal(1, Assert.Single(run.State.Deck, c => c.Id == "CARD.FIGHT_ME").Upgrade);
        Assert.Contains(run.UncertainCards, u => u.CardId == "CARD.FIGHT_ME" && u.Reason == UncertainReason.Inferred);
        Assert.Equal(99 + 11 + 11 + 17 + 14 + 20 + 36, run.State.Gold);
        Assert.Equal(["RELIC.BURNING_BLOOD", "RELIC.HORN_CLEAT"], run.State.Relics);
        Assert.Contains(run.Potions, p => p.Id == "POTION.DEXTERITY_POTION");

        Assert.Equal(RunOutcome.Lost, run.Outcome);
        Assert.Equal(ScreenHint.RunEnded, run.Screen);
        Assert.Equal(CombatKind.Boss, run.LastCombatKind);
        Assert.False(run.HpGoldReliable);
        Assert.True(run.DeckApproximate);
    }

    // ------------------------------------------------------------------------------------------
    // Save + log patches
    // ------------------------------------------------------------------------------------------

    private static SaveSnapshot Save(string seed, long bytes, params string[] deck) => new(
        @"C:\saves\current_run.save",
        new ParsedSave
        {
            State = new RunState
            {
                Character = "CHARACTER.IRONCLAD", Floor = 5, Hp = 50, MaxHp = 80, Gold = 100,
                Deck = deck.Select(id => new DeckCard(new CardRef(id))).ToList(),
            },
            Seed = seed,
            PlayerNetIds = [1],
            MyNetId = 1,
            CardRarityOffset = 0.02,
        },
        T0, bytes);

    private const string Embark = "[INFO] Embarking on a singleplayer IRONCLAD run. Ascension: 0 Seed: S1";
    private static string Wrote(long bytes) => $@"[INFO] Wrote {bytes} bytes to path=user://steam/1/profile1\saves\current_run.save save_dir=user://steam/1";

    [Fact]
    public void Log_obtain_after_save_is_patched_and_not_doubled_by_the_next_save()
    {
        var t = NewTracker();
        t.ProcessLines([Embark, Wrote(100)]);
        t.OnSave(Save("S1", 100, "CARD.BASH"));
        Assert.True(t.Current!.FromSave);
        Assert.Equal(0, t.Current.AppliedLogPatches);
        Assert.Equal(0.02, t.Current.RarityPityOffset); // from the save

        t.ProcessLines([
            "[INFO] CHARACTER.IRONCLAD has won against encounter ENCOUNTER.NIBBITS_WEAK. That's 2 wins",
            "[INFO] Card rarity: Rolled 0.5, need < 0.05 for rare (offset = 0.02)",
            "[INFO] Card rarity: Rolled 0.01, need < 0.06 for rare (offset = 0.03)",
            "[INFO] Player 1 obtained 10 gold from reward",
            "[INFO] Player 1 obtained CARD.ANGER from card reward",
        ]);
        TrackedRun run = t.Current!;
        Assert.Equal(["CARD.BASH", "CARD.ANGER"], run.State.Deck.Select(c => c.Id));
        Assert.Equal(110, run.State.Gold);
        Assert.Equal(-0.05, run.RarityPityOffset); // second slot was rare → reset
        Assert.True(run.LastRewardRolls[1].IsRare);
        Assert.Equal(2, run.AppliedLogPatches);
        Assert.True(run.DeckApproximate);

        // Next room: the log line arrives first, then the watcher delivers the file.
        t.ProcessLine(Wrote(200));
        t.OnSave(Save("S1", 200, "CARD.BASH", "CARD.ANGER") with { FileWriteUtc = T0.AddSeconds(5) });
        run = t.Current!;
        Assert.Equal(2, run.State.Deck.Count);
        Assert.Equal(0, run.AppliedLogPatches);
        Assert.Equal(100, run.State.Gold); // the (fake) save says 100; the save is authoritative
    }

    [Fact]
    public void Save_delivered_before_its_log_line_self_corrects()
    {
        var t = NewTracker();
        t.ProcessLines([Embark, Wrote(100)]);
        t.OnSave(Save("S1", 100, "CARD.BASH"));
        t.ProcessLine("[INFO] Player 1 obtained CARD.ANGER from card reward");

        // Watcher wins the race: the new save (which already has ANGER) arrives before "Wrote 200".
        t.OnSave(Save("S1", 200, "CARD.BASH", "CARD.ANGER"));
        Assert.Equal(3, t.Current!.State.Deck.Count); // transient double count (documented)

        t.ProcessLine(Wrote(200)); // the matching log line → ANGER is known to be inside the save
        Assert.Equal(2, t.Current!.State.Deck.Count);
    }

    [Fact]
    public void Shop_removal_and_rest_smith_patch_the_save_deck()
    {
        var t = NewTracker();
        t.ProcessLines([Embark, Wrote(100)]);
        t.OnSave(Save("S1", 100, "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.BASH"));
        t.ProcessLines([
            "[INFO] Preloading 'Merchant Room' assets... count=1 vfx=0",
            "[INFO] Player 1 chose cards [STRIKE_IRONCLAD]",
            "[INFO] Player vote changed for 1: ->MapVote (gen: 1 coord: (1, 6))",
            "[INFO] Preloading 'RestSite Room' assets... count=3 vfx=2",
            "[INFO] Player 1 chose cards [BASH]",
            "[INFO] Preloading 'Combat Room' assets... count=3 vfx=2",
            "[INFO] Player 1 chose cards [STRIKE_IRONCLAD]", // in-combat pick: ignored
        ]);
        RunState s = t.Current!.State;
        Assert.Equal(1, s.Deck.Count(c => c.Id == "CARD.STRIKE_IRONCLAD"));
        Assert.Equal(1, s.Deck.Single(c => c.Id == "CARD.BASH").Upgrade);
        Assert.Equal(1, s.CardRemovalsBought);
    }

    [Fact]
    public void Coop_rest_option_other_than_smith_is_not_an_upgrade()
    {
        var t = NewTracker(TestPaths.Me);
        t.ProcessLines([
            "[INFO] Embarking on a multiplayer run. Players: Player 76561190000000001, DEFECT,Player 76561190000000002, SILENT. Ascension: 0 Seed: M1",
            "[INFO] Preloading 'RestSite Room' assets... count=3 vfx=2",
            "[DEBUG] [RestSiteSynchronizer] Rest site option index 3 chosen for player 76561190000000001 with success True. Option: CLONE",
            "[INFO] Player 76561190000000001 chose cards [ZAP]",
        ]);
        Assert.All(t.Current!.State.Deck, c => Assert.Equal(0, c.Upgrade));
    }

    [Fact]
    public void Map_vote_moves_current_position_before_the_room_save()
    {
        var map = new MapGraph
        {
            Nodes = new Dictionary<MapCoord, MapNode>
            {
                [new(3, 0)] = new(new(3, 0), MapNodeType.Ancient, [new(3, 1)]),
                [new(3, 1)] = new(new(3, 1), MapNodeType.Elite, []),
            },
            Start = new(3, 0), Boss = new(3, 1), Current = new(3, 0),
        };
        var t = NewTracker();
        t.ProcessLines([Embark, Wrote(100)]);
        SaveSnapshot s = Save("S1", 100);
        t.OnSave(s with { Save = s.Save with { Map = map } });
        t.ProcessLine("[INFO] Player vote changed for 1: ->MapVote (gen: 1 coord: (3, 1))");
        Assert.Equal(new MapCoord(3, 1), t.Current!.Map!.Current);

        // An event fight without a suffix takes its kind from the map node we stand on.
        t.ProcessLine(Wrote(150));
        t.OnSave(s with { FileBytes = 150, Save = s.Save with { Map = map with { Current = new(3, 1) } } });
        t.ProcessLine("[INFO] Creating NCombatRoom with mode=ActiveCombat encounter=OVERGROWTH_CRAWLERS.");
        Assert.Equal(CombatKind.Elite, t.Current!.LastCombatKind);
    }

    /// <summary>A single-player Neow room: the run-start save stands on the act's ancient node.</summary>
    private static SaveSnapshot NeowSave(string seed, int modifiers)
    {
        var start = new MapCoord(3, 0);
        SaveSnapshot s = Save(seed, 100);
        return s with
        {
            Save = s.Save with
            {
                ModifierCount = modifiers,
                Map = new MapGraph
                {
                    Nodes = new Dictionary<MapCoord, MapNode> { [start] = new(start, MapNodeType.Ancient, []) },
                    Start = start, Boss = start, Current = start,
                },
                Events = new EventRoomState { AncientId = "EVENT.NEOW", VisitedInAct = 1, HistoryInAct = 0 },
            },
        };
    }

    private static GameStateTracker NeowTracker(string progressJson, string seed, int modifiers = 0)
    {
        string dir = Directory.CreateTempSubdirectory("sts2-neow-").FullName;
        File.WriteAllText(Path.Combine(dir, ProgressSave.FileName), progressJson);
        var t = new GameStateTracker(new TrackerOptions { Paths = new GamePaths(dir, null, null), Clock = () => T0 });
        t.ProcessLines([$"[INFO] Embarking on a singleplayer IRONCLAD run. Ascension: 0 Seed: {seed}", Wrote(100)]);
        t.OnSave(NeowSave(seed, modifiers));
        t.ProcessLine("[INFO] Preloading 'Event Room' assets... count=2 vfx=0");
        return t;
    }

    private const string AllUnlocked =
        """{"epochs":[{"id":"SILENT1_EPOCH","state":"revealed"},{"id":"REGENT1_EPOCH","state":"revealed"},{"id":"NECROBINDER1_EPOCH","state":"revealed"},{"id":"DEFECT1_EPOCH","state":"revealed"}]}""";

    [Fact]
    public void Single_player_neow_options_are_replayed_from_the_seed()
    {
        // R9REQLA5XL: the real run behind single_ironclad_run.log; its history file lists these three options.
        var ev = NeowTracker(AllUnlocked, "R9REQLA5XL").Current!.CurrentEvent!;
        Assert.Equal("EVENT.NEOW", ev.EventId);
        Assert.Equal(EventSource.SavePrediction, ev.Source);
        Assert.True(ev.OptionsReplayed);
        Assert.Equal(
            ["NEOW.pages.INITIAL.options.LEAD_PAPERWEIGHT", "NEOW.pages.INITIAL.options.NEOWS_TORMENT", "NEOW.pages.INITIAL.options.LEAFY_POULTICE"],
            ev.OptionKeys);
        Assert.Equal(NeowOptions.PossibleKeys(1, allCharactersUnlocked: true), ev.PossibleKeys);
    }

    [Fact]
    public void Neow_options_follow_the_unlocks_in_progress_save()
    {
        string seed = "3FCYMZNLDZ"; // all unlocked: NEW_LEAF, KALEIDOSCOPE, LARGE_CAPSULE
        Assert.Contains("NEOW.pages.INITIAL.options.KALEIDOSCOPE", NeowTracker(AllUnlocked, seed).Current!.CurrentEvent!.OptionKeys!);
        var locked = NeowTracker("""{"epochs":[{"id":"SILENT1_EPOCH","state":"revealed"}]}""", seed).Current!.CurrentEvent!.OptionKeys!;
        Assert.DoesNotContain("NEOW.pages.INITIAL.options.KALEIDOSCOPE", locked);
        Assert.Equal(NeowOptions.Keys(seed, 0, 1, allCharactersUnlocked: false), locked);
    }

    [Fact]
    public void Neow_with_run_modifiers_is_not_replayed()
    {
        var ev = NeowTracker(AllUnlocked, "R9REQLA5XL", modifiers: 1).Current!.CurrentEvent!;
        Assert.Equal("EVENT.NEOW", ev.EventId);
        Assert.Null(ev.OptionKeys);
        Assert.False(ev.OptionsReplayed);
    }

    /// <summary>
    /// The real act-2 Orobas visit of run H6VRKY8PFE (tests/fixtures/ancient-visits.v0.107.1.json): the save written on the
    /// ancient's node holds the deck and relics the draw reads. The game offered Sand Castle, Driftwood, Touch of Orobas.
    /// </summary>
    private static GameStateTracker OrobasTracker(bool onAncientNode = true, string[]? relics = null)
    {
        const string seed = "H6VRKY8PFE";
        var start = new MapCoord(3, 0);
        SaveSnapshot s = Save(seed, 100,
            "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD",
            "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.BASH", "CARD.FIEND_FIRE", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD",
            "CARD.COLOSSUS", "CARD.THUNDERCLAP", "CARD.TREMBLE", "CARD.CRIMSON_MANTLE", "CARD.TREMBLE", "CARD.WHIRLWIND", "CARD.ANGER",
            "CARD.ARMAMENTS", "CARD.AGGRESSION", "CARD.MOLTEN_FIST", "CARD.BATTLE_TRANCE", "CARD.CONFLAGRATION");
        s = s with
        {
            Save = s.Save with
            {
                ModifierCount = 0,
                State = s.Save.State with
                {
                    Act = 2,
                    Relics = relics ?? ["RELIC.BURNING_BLOOD", "RELIC.HEFTY_TABLET", "RELIC.HAPPY_FLOWER", "RELIC.FESTIVE_POPPER", "RELIC.BOOK_OF_FIVE_RINGS", "RELIC.GREMLIN_HORN"],
                },
                UnlockedEpochs = ["SILENT1_EPOCH", "REGENT1_EPOCH", "NECROBINDER1_EPOCH", "DEFECT1_EPOCH"],
                Map = new MapGraph
                {
                    Nodes = new Dictionary<MapCoord, MapNode> { [start] = new(start, onAncientNode ? MapNodeType.Ancient : MapNodeType.Monster, []) },
                    Start = start, Boss = start, Current = start,
                },
                Events = new EventRoomState { ActIndex = 1, AncientId = "EVENT.OROBAS", VisitedInAct = 1, HistoryInAct = 0 },
            },
        };
        var t = new GameStateTracker(new TrackerOptions { Clock = () => T0 });
        t.ProcessLines([$"[INFO] Embarking on a singleplayer IRONCLAD run. Ascension: 0 Seed: {seed}", Wrote(100)]);
        t.OnSave(s);
        t.ProcessLine("[INFO] Preloading 'Event Room' assets... count=2 vfx=0");
        return t;
    }

    [Fact]
    public void Act_start_ancient_options_are_replayed_from_the_seed_and_the_node_save()
    {
        var ev = OrobasTracker().Current!.CurrentEvent!;
        Assert.Equal("EVENT.OROBAS", ev.EventId);
        Assert.True(ev.OptionsReplayed);
        Assert.Equal(
            ["OROBAS.pages.INITIAL.options.SAND_CASTLE", "OROBAS.pages.INITIAL.options.DRIFTWOOD", "OROBAS.pages.INITIAL.options.TOUCH_OF_OROBAS"],
            ev.OptionKeys);
        Assert.Contains("OROBAS.pages.INITIAL.options.ARCHAIC_TOOTH", ev.PossibleKeys!); // Bash is in the deck
        Assert.DoesNotContain("OROBAS.pages.INITIAL.options.OPTION_POOL_3_LOCKED", ev.PossibleKeys!);
    }

    [Fact]
    public void Ancient_draw_follows_the_relics_in_the_save()
    {
        // Without a starter relic Touch of Orobas can't be offered: the third slot draws from Archaic Tooth alone.
        var ev = OrobasTracker(relics: ["RELIC.HEFTY_TABLET"]).Current!.CurrentEvent!;
        Assert.Equal("OROBAS.pages.INITIAL.options.ARCHAIC_TOOTH", ev.OptionKeys![2]);
        Assert.DoesNotContain("OROBAS.pages.INITIAL.options.TOUCH_OF_OROBAS", ev.PossibleKeys!);
    }

    [Fact]
    public void Ancient_is_not_replayed_from_a_save_off_its_node()
    {
        // The deck/relics must be the ones at the ancient's node; a save standing elsewhere doesn't predict the room either.
        var ev = OrobasTracker(onAncientNode: false).Current!.CurrentEvent;
        Assert.True(ev is null || !ev.OptionsReplayed);
    }

    [Fact]
    public void Stale_save_with_another_seed_is_ignored()
    {
        var t = NewTracker(TestPaths.Friend);
        t.OnSave(Save("OLD_RUN", 100, "CARD.BASH"));
        Assert.True(t.Current!.FromSave);
        t.ProcessLine("[INFO] Embarking on a multiplayer run. Players: Player 76561190000000001, DEFECT,Player 76561190000000002, SILENT. Ascension: 5 Seed: NEW_RUN");
        TrackedRun run = t.Current!;
        Assert.False(run.FromSave);
        Assert.Equal("CHARACTER.SILENT", run.State.Character);
        // Ascension 5+ adds Ascender's Bane to the starting deck.
        Assert.Equal(13, run.State.Deck.Count);
        Assert.Contains(run.State.Deck, c => c.Id == StartingLoadouts.AscendersBane);
    }

    [Fact]
    public void Log_rotation_forgets_the_old_log_but_keeps_the_save()
    {
        var t = NewTracker();
        t.ProcessLines([Embark, Wrote(100), "[INFO] Preloading 'Combat Room' assets... count=3 vfx=2"]);
        t.OnSave(Save("S1", 100, "CARD.BASH"));
        t.ProcessLine("[INFO] Player 1 obtained CARD.ANGER from card reward");
        Assert.Equal(2, t.Current!.State.Deck.Count);

        t.OnLogRotated();
        TrackedRun run = t.Current!;
        Assert.True(run.FromSave);
        Assert.Single(run.State.Deck);
        Assert.Equal(ScreenHint.Unknown, run.Screen);
    }

    [Fact]
    public void Scarcity_pity_grows_half_as_fast()
    {
        var t = NewTracker();
        t.ProcessLines([
            "[INFO] Embarking on a singleplayer DEFECT run. Ascension: 7 Seed: A7",
            "[INFO] CHARACTER.DEFECT has won against encounter ENCOUNTER.NIBBITS_WEAK. That's 1 wins",
            "[INFO] Card rarity: Rolled 0.5, need < -0.0351 for rare (offset = -0.05)", // base 1.49%
        ]);
        Assert.Equal(-0.045, t.Current!.RarityPityOffset!.Value, 6);
    }

    // ------------------------------------------------------------------------------------------
    // Co-op (real anonymised host log): rolls/obtains are filtered to "me"; guest rebuild from the log.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Coop_host_view_uses_only_my_events()
    {
        var t = NewTracker(); // my id comes from "[SteamHost] ... Our player id: ...001" in the log
        string[] lines = TestPaths.FixtureLines("coop_host_run.log");
        string[] rest = FeedUntil(t, lines, "Card rarity: Rolled 0.4610016");
        TrackedRun run = t.Current!;
        Assert.Equal(TestPaths.Me, run.LocalPlayerId);
        Assert.Equal("CHARACTER.DEFECT", run.State.Character);
        // Six rolls were logged (3 per player); mine are the first three (slot 0).
        Assert.Equal([0.6362035, 0.70350623, 0.4104836], run.LastRewardRolls.Select(r => Math.Round(r.Roll, 8)));

        t.ProcessLines(rest);
        run = t.Current!;
        Assert.Equal(1, Count(run, "CARD.COOLHEADED"));          // mine
        Assert.Equal(0, Count(run, "CARD.LEADING_STRIKE"));      // the friend's
        Assert.Equal(RunOutcome.Lost, run.Outcome);

        using JsonDocument history = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RunsDir, "1794060064.run")));
        Assert.Equal(MapPointHistory.TotalFloors(history.RootElement.GetProperty("map_point_history")), run.State.Floor);
    }

    [Fact]
    public void Coop_guest_rebuild_from_log_alone()
    {
        // Same log, seen as the friend (a guest has no save file at all).
        var t = NewTracker(TestPaths.Friend);
        t.ProcessLines(TestPaths.FixtureLines("coop_host_run.log"));
        TrackedRun run = t.Current!;
        Assert.False(run.FromSave);
        Assert.False(run.HpGoldReliable);
        Assert.Equal("CHARACTER.SILENT", run.State.Character);
        Assert.Equal(70, run.State.MaxHp); // Silent's starting HP
        // One from a card reward, one bought in a shop (a remote player's purchase is logged on the host).
        Assert.Equal(2, Count(run, "CARD.LEADING_STRIKE"));
        Assert.Equal(0, Count(run, "CARD.COOLHEADED"));
        Assert.Contains("RELIC.RING_OF_THE_SNAKE", run.State.Relics);
        // The other player: character from the lobby line; no save, so no deck.
        Assert.Equal(2, run.PlayerCount);
        var partner = Assert.Single(run.Partners);
        Assert.Equal((TestPaths.Me, "CHARACTER.DEFECT"), (partner.NetId, partner.Character));
        Assert.Empty(partner.Deck);

        // How close is the log-only deck to the truth? Compare with the final deck in the run history.
        using JsonDocument history = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RunsDir, "1794060064.run")));
        var finalDeck = history.RootElement.GetProperty("players").EnumerateArray()
            .Single(p => p.GetProperty("id").GetUInt64() == TestPaths.Friend)
            .GetProperty("deck").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();
        int common = run.State.Deck.Select(c => c.Id).GroupBy(id => id)
            .Sum(g => Math.Min(g.Count(), finalDeck.Count(id => id == g.Key)));
        // This excerpt is trimmed (no event options, no fight plays), so it can't be exact; the full-run check is
        // CoopGuestDeckTests.Real_coop_runs_every_card_is_right_or_flagged_and_run_end_is_exact.
        Assert.True(common >= finalDeck.Count / 2, $"only {common}/{finalDeck.Count} cards match");
    }
}
