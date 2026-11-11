using Sts2Overlay.Core.Model;
using System.IO.Compression;
using Sts2Overlay.Core.History;
using Sts2Overlay.GameState.Diagnostics;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// Co-op guest deck rebuild (a guest has no save: the deck comes from godot.log alone).
/// Part 1 replays 9 real co-op runs (fixtures: only the log lines the parser understands, ids only, Steam IDs
/// anonymised) as each player and compares with the run's .run file. Part 2 pins each rule with a tiny synthetic log.
/// </summary>
public class CoopGuestDeckTests
{
    private const ulong Me = TestPaths.Me;         // seat 0, DEFECT (10 starting cards)
    private const ulong Friend = TestPaths.Friend; // seat 1, SILENT (12 starting cards)

    // ------------------------------------------------------------------------------------------
    // Part 1: real runs
    // ------------------------------------------------------------------------------------------

    private static string CoopFixtureDir => Path.Combine(TestPaths.Fixture("coop_runs"));

    private static List<string> ReadGz(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var lines = new List<string>();
        for (string? l; (l = reader.ReadLine()) != null;) lines.Add(l);
        return lines;
    }

    private static readonly Lazy<List<GuestDeckResult>> s_results = new(EvaluateFixtures);

    /// <summary>The tracker reads <c>&lt;saves&gt;/history/N.run</c> at run end: copy the shared run files into that layout.</summary>
    private static List<GuestDeckResult> EvaluateFixtures()
    {
        string saves = Path.Combine(Path.GetTempPath(), "sts2-coop-fixtures-" + Guid.NewGuid().ToString("N"));
        string history = Path.Combine(saves, "history");
        Directory.CreateDirectory(history);
        try
        {
            var results = new List<GuestDeckResult>();
            foreach (string f in Directory.GetFiles(CoopFixtureDir, "*.log.gz").Order(StringComparer.Ordinal))
            {
                string run = Path.GetFileName(f)[..^".log.gz".Length] + ".run";
                File.Copy(Path.Combine(TestPaths.RunsDir, run), Path.Combine(history, run));
                results.AddRange(GuestDeckEvaluation.EvaluateLog(Path.GetFileName(f), ReadGz(f), history));
            }
            return results;
        }
        finally
        {
            Directory.Delete(saves, recursive: true);
        }
    }

    [Fact]
    public void Real_coop_runs_every_card_is_right_or_flagged_and_run_end_is_exact()
    {
        List<GuestDeckResult> results = s_results.Value;
        Assert.Equal(18, results.Count); // 9 runs x 2 players

        // The overlay never shows a wrong card as certain: every difference is covered by a flag.
        Assert.All(results, r => Assert.True(r.SilentErrors == 0, r.ToString()));
        // At run end the .run file replaces the rebuild: exact for everyone.
        Assert.All(results, r => Assert.True(r.ReconciledExact, r.ToString()));

        // Card identities: right everywhere except one Pandora's Box result that never appears in the log
        // (1794079324, MULTI_CAST: never played, never picked) - the tracker reports it as 1 unidentified card.
        Assert.Equal(391, results.Sum(r => r.IdMatches));
        Assert.Equal(392, results.Sum(r => r.TrueDeck.Count));
        GuestDeckResult pandora = Assert.Single(results, r => r.IdMatches < r.TrueDeck.Count);
        Assert.Equal("1794079324.run", pandora.RunFile);
        Assert.Equal(1, pandora.Run!.UnidentifiedCards);
        Assert.Equal(pandora.TrueDeck.Count, pandora.RebuiltDeck.Count + pandora.Run.UnidentifiedCards);

        // Upgrades: reward-card upgrade rolls, Tablet of Truth and Doors of Light are replayed from the run seed; what
        // is left are relics that draw from the run's Niche stream (Whetstone, War Paint, Fragrant Mushroom, Fishing Rod),
        // whose position the log can't tell: 18 upgrades, +1 for the Pandora card above.
        Assert.True(results.Sum(r => r.ExactMatches) >= 373, $"exact {results.Sum(r => r.ExactMatches)}/392");
    }

    [Fact]
    public void Real_run_thieving_hopper_steal_not_returned_is_resolved_from_the_steal_rule()
    {
        // 1794065818: the hopper stole TACTICIAN from the friend, who skipped the "special card reward" that returns it.
        // EXPERTISE (the other Uncommon) was played after the steal, so TACTICIAN is the only candidate.
        GuestDeckResult friend = s_results.Value.Single(r => r.RunFile == "1794065818.run" && r.PlayerId == Friend);
        Assert.Equal(friend.TrueDeck.Count, friend.IdMatches);
        Assert.DoesNotContain(friend.RebuiltDeck, c => c.Id == "CARD.TACTICIAN");
    }

    // ------------------------------------------------------------------------------------------
    // Part 2: synthetic logs, one rule each
    // ------------------------------------------------------------------------------------------

    private static readonly string[] Header =
    [
        "Release Version: v0.107.1", // the replayed draws below were taken with v0.107.1's seed arithmetic
        "[INFO] [StartRunLobby (76561190000000001)] Local player 76561190000000001 is ready",
        "[INFO] Embarking on a multiplayer run. Players: Player 76561190000000001, DEFECT,Player 76561190000000002, SILENT. Ascension: 0 Seed: TESTSEED01",
        "[INFO] Preloading 'Act=OVERGROWTH' assets... count=8 vfx=0",
    ];

    private static TrackedRun Replay(ulong me, params string[] lines) => ReplayWithSaves(me, null, lines);

    private static TrackedRun ReplayWithSaves(ulong me, string? savesDir, params string[] lines)
    {
        var t = new GameStateTracker(new TrackerOptions
        {
            LocalNetIdOverride = me,
            Paths = savesDir is null ? null : new GamePaths(savesDir, null, null),
        });
        t.ProcessLines(Header.Concat(lines));
        return t.Current!;
    }

    private static int Count(TrackedRun run, string id) => run.State.Deck.Count(c => c.Id == id);

    private static string Room(string name) => $"[INFO] Preloading '{name}' assets... count=1 vfx=0";
    private static string Fight(string encounter) => $"[INFO] Creating NCombatRoom with mode=ActiveCombat encounter={encounter}.";
    private static string Won(string encounter) => $"[INFO] CHARACTER.DEFECT has won against encounter ENCOUNTER.{encounter}. That's 1 wins";
    private static string Play(ulong owner, string card, int index) =>
        $"[DEBUG] [ActionQueueSet] Enqueueing action PlayCardAction card: CARD.{card} (1) index: {index} targetid:  to player queue owned by {owner}";
    private static string Chose(ulong p, string cards) => $"[INFO] Player {p} chose cards [{cards}]";
    private static string Option(ulong p, string ev, string key) =>
        $"[DEBUG] [EventSynchronizer] Option index 0 chosen for player {p} in event EVENT.{ev}. Choice key: {key}";
    private static string Obtained(ulong p, string what, string from = "card reward") => $"[INFO] Player {p} obtained {what} from {from}";
    private static string Vote(ulong p, int row) => $"[INFO] Player vote changed for {p}: ->MapVote (gen: 1 coord: (0, {row}))";

    [Fact]
    public void Ancient_option_select_removes_the_chosen_cards()
    {
        TrackedRun run = Replay(Friend,
            Room("Event Room"),
            Option(Friend, "NEOW", "NEOW.pages.INITIAL.options.PRECARIOUS_SHEARS"),
            Chose(Friend, "STRIKE_SILENT, DEFEND_SILENT"));
        Assert.Equal(10, run.State.Deck.Count);
        Assert.Equal(4, Count(run, "CARD.STRIKE_SILENT"));
        Assert.Contains("RELIC.PRECARIOUS_SHEARS", run.State.Relics); // ancients log no "obtained" line for their relic
    }

    [Fact]
    public void Event_with_a_fixed_card_adds_it()
    {
        TrackedRun run = Replay(Friend,
            Room("Event Room"),
            Option(Friend, "THE_LEGENDS_WERE_TRUE", "THE_LEGENDS_WERE_TRUE.pages.INITIAL.options.NAB_THE_MAP"));
        Assert.Equal(1, Count(run, "CARD.SPOILS_MAP"));
    }

    [Fact]
    public void Merchant_sells_one_removal_per_visit_and_an_empty_pick_does_not_use_it()
    {
        TrackedRun run = Replay(Friend,
            Room("Merchant Room"),
            Chose(Friend, ""),                 // removal screen closed without a pick
            Chose(Friend, "STRIKE_SILENT"),    // the removal
            Chose(Friend, "DEFEND_SILENT"));   // a second pick in the same shop is something else: kept, flagged
        Assert.Equal(4, Count(run, "CARD.STRIKE_SILENT"));
        Assert.Equal(5, Count(run, "CARD.DEFEND_SILENT"));
        Assert.Equal(1, run.State.CardRemovalsBought);
        Assert.Contains(run.UncertainCards, u => u.CardId == "CARD.DEFEND_SILENT" && u.Reason == UncertainReason.UnknownCardSelect);
    }

    [Fact]
    public void Rest_site_pick_followed_by_COOK_is_a_removal_not_an_upgrade()
    {
        TrackedRun run = Replay(Friend,
            Room("RestSite Room"),
            Chose(Friend, "STRIKE_SILENT, DEFEND_SILENT"),
            $"[DEBUG] [RestSiteSynchronizer] Rest site option index 2 chosen for player {Friend} with success True. Option: COOK");
        Assert.Equal(10, run.State.Deck.Count);
        Assert.All(run.State.Deck, c => Assert.Equal(0, c.Upgrade));
    }

    [Fact]
    public void Fight_plays_reveal_cards_the_log_never_named()
    {
        // Combat ids: seat 0's deck gets 0..9, seat 1's 10..21, cards made in the fight come after both decks.
        // The friend (seat 1) plays BACKFLIP with id 21, a card we never saw (e.g. a local shop purchase).
        TrackedRun run = Replay(Friend,
            Room("Combat Room"), Fight("SLIMES_WEAK"),
            Play(Me, "DUALCAST", 9),            // seat 0's last deck card: the friend's deck starts at 10
            Play(Friend, "STRIKE_SILENT", 10),
            Play(Friend, "BACKFLIP", 21),
            Play(Friend, "SHIV", 23),           // a token: made in the fight, never a deck card ...
            Play(Friend, "BLADE_DANCE", 24),    // ... so ids above it are made too
            Won("SLIMES_WEAK"));
        Assert.Equal(1, Count(run, "CARD.BACKFLIP"));
        Assert.Equal(0, Count(run, "CARD.SHIV"));
        Assert.Equal(0, Count(run, "CARD.BLADE_DANCE"));
        // No merchant visited: where it came from (and so its upgrade) is unknown.
        Assert.Contains(run.UncertainCards, u => u.CardId == "CARD.BACKFLIP" && u.Reason == UncertainReason.Inferred);
    }

    [Fact]
    public void A_card_first_seen_after_a_merchant_visit_is_a_plain_purchase()
    {
        // My own shop purchases are never logged. Merchant cards never come upgraded (CardFactory.CreateForMerchant:
        // upgrade roll base -999999999), so after a merchant visit an unexplained common/uncommon/rare is taken as plain.
        TrackedRun run = Replay(Friend,
            Room("Merchant Room"),
            Room("Combat Room"), Fight("SLIMES_WEAK"),
            Play(Me, "DUALCAST", 9),
            Play(Friend, "STRIKE_SILENT", 10),
            Play(Friend, "BACKFLIP", 21),
            Won("SLIMES_WEAK"));
        DeckCard backflip = Assert.Single(run.State.Deck, c => c.Id == "CARD.BACKFLIP");
        Assert.Equal(0, backflip.Upgrade);
        Assert.DoesNotContain(run.UncertainCards, u => u.CardId == "CARD.BACKFLIP");
    }

    [Fact]
    public void Exact_deck_size_from_combat_ids_counts_cards_that_never_show()
    {
        // Seat 0 (me) plays id 10 and the friend's lowest id is 11: my deck is exactly 11 cards, one more than the
        // 10 starting cards, but the extra card is never played: it is reported as unidentified.
        TrackedRun run = Replay(Me,
            Room("Combat Room"), Fight("SLIMES_WEAK"),
            Play(Me, "ZAP", 0),
            Play(Me, "DUALCAST", 10),
            Play(Friend, "NEUTRALIZE", 11),
            Won("SLIMES_WEAK"));
        Assert.Equal(10, run.State.Deck.Count);
        Assert.Equal(1, run.UnidentifiedCards);
    }

    [Fact]
    public void Thieving_hopper_card_returned_by_the_special_reward_is_not_a_new_card()
    {
        TrackedRun run = Replay(Friend,
            Room("Combat Room"), Fight("THIEVING_HOPPER_WEAK"),
            "[INFO] Monster THIEVING_HOPPER performing move THIEVERY_MOVE",
            Won("THIEVING_HOPPER_WEAK"),
            Obtained(Friend, "CARD.NEUTRALIZE", "special card reward"));
        Assert.Equal(12, run.State.Deck.Count);
        Assert.Equal(1, Count(run, "CARD.NEUTRALIZE"));
    }

    [Fact]
    public void Thieving_hopper_card_not_returned_is_removed_when_the_steal_rule_leaves_one_candidate()
    {
        // The hopper takes from the best class first: Uncommon (EXPERTISE, TACTICIAN). EXPERTISE is played after the
        // steal, so it was still in the fight: TACTICIAN was taken. The special reward is never taken.
        TrackedRun run = Replay(Friend,
            Room("Combat Room"), Fight("SLIMES_WEAK"), Won("SLIMES_WEAK"),
            Obtained(Friend, "CARD.EXPERTISE"),
            Room("Combat Room"), Fight("SLIMES_WEAK"), Won("SLIMES_WEAK"),
            Obtained(Friend, "CARD.TACTICIAN"),
            Room("Combat Room"), Fight("THIEVING_HOPPER_WEAK"),
            "[INFO] Monster THIEVING_HOPPER performing move THIEVERY_MOVE",
            Play(Friend, "EXPERTISE", 22),
            Won("THIEVING_HOPPER_WEAK"),
            Vote(Friend, 3),
            Room("Event Room"));
        Assert.Equal(1, Count(run, "CARD.EXPERTISE"));
        Assert.Equal(0, Count(run, "CARD.TACTICIAN"));
        Assert.Equal(0, run.UnknownRemovals);
    }

    [Fact]
    public void Guilty_leaves_the_deck_after_five_fights()
    {
        // Wellspring BATHE: remove a card, gain Guilty (a curse that removes itself after 5 fights, Guilty.cs).
        var lines = new List<string>
        {
            Room("Event Room"),
            Option(Friend, "WELLSPRING", "WELLSPRING.pages.INITIAL.options.BATHE"),
            Chose(Friend, "STRIKE_SILENT"),
        };
        for (int i = 0; i < 4; i++) lines.AddRange([Room("Combat Room"), Fight("SLIMES_WEAK"), Won("SLIMES_WEAK")]);
        Assert.Equal(1, Count(Replay(Friend, [.. lines]), "CARD.GUILTY"));
        lines.AddRange([Room("Combat Room"), Fight("SLIMES_WEAK"), Won("SLIMES_WEAK")]);
        TrackedRun run = Replay(Friend, [.. lines]);
        Assert.Equal(0, Count(run, "CARD.GUILTY"));
        Assert.Equal(4, Count(run, "CARD.STRIKE_SILENT"));
    }

    [Fact]
    public void Fifth_tablet_decipher_upgrades_every_card()
    {
        TrackedRun run = Replay(Friend,
            Room("Event Room"),
            Option(Friend, "TABLET_OF_TRUTH", "TABLET_OF_TRUTH.pages.INITIAL.options.DECIPHER_1"),
            Option(Friend, "TABLET_OF_TRUTH", "TABLET_OF_TRUTH.pages.DECIPHER_1.options.DECIPHER"),
            Option(Friend, "TABLET_OF_TRUTH", "TABLET_OF_TRUTH.pages.DECIPHER_2.options.DECIPHER"),
            Option(Friend, "TABLET_OF_TRUTH", "TABLET_OF_TRUTH.pages.DECIPHER_3.options.DECIPHER"),
            Option(Friend, "TABLET_OF_TRUTH", "TABLET_OF_TRUTH.pages.DECIPHER_4.options.DECIPHER"));
        Assert.All(run.State.Deck, c => Assert.Equal(1, c.Upgrade));
        Assert.Equal(0, run.UnknownUpgrades); // the 4 random upgrades are covered by "everything upgraded"
    }

    [Fact]
    public void Random_upgrades_from_the_niche_stream_are_counted_not_placed()
    {
        // Whetstone picks 2 attacks with the run's Niche stream, which every monster spawn also draws from: not replayable.
        TrackedRun run = Replay(Friend,
            Room("Treasure Room"),
            Obtained(Friend, "RELIC.WHETSTONE", "relic reward"));
        Assert.All(run.State.Deck, c => Assert.Equal(0, c.Upgrade));
        Assert.Equal(2, run.UnknownUpgrades);
        Assert.True(run.DeckApproximate);
    }

    [Fact]
    public void Tablet_of_truth_decipher_is_replayed_from_the_event_stream()
    {
        // Event stream = (uint)((int)hash("TESTSEED01") + slot 1) + hash("TABLET_OF_TRUTH"); its first NextInt(12) is 7
        // (worked out independently with a Python port): the 8th upgradable card in deck order, the 3rd Defend.
        TrackedRun run = Replay(Friend,
            Room("Event Room"),
            Option(Friend, "TABLET_OF_TRUTH", "TABLET_OF_TRUTH.pages.INITIAL.options.DECIPHER_1"));
        Assert.Equal(0, run.UnknownUpgrades);
        DeckCard upgraded = Assert.Single(run.State.Deck, c => c.Upgrade > 0);
        Assert.Equal("CARD.DEFEND_SILENT", upgraded.Id);
    }

    [Fact]
    public void Reward_card_upgrade_roll_is_replayed_from_the_run_seed()
    {
        // My (seat 0, DEFECT) Rewards stream for TESTSEED01 holds these three rarity rolls at counters 54, 57, 60. The
        // cards they show are FERAL, GO_FOR_THE_EYES, UPROAR under every epoch-lock hypothesis, and UPROAR's upgrade
        // roll (counter 62) is 0.188 <= act index 1 x 25%: it comes upgraded. Nothing in the log says so.
        TrackedRun run = Replay(Me,
            "[INFO] Preloading 'Act=HIVE' assets... count=8 vfx=0",
            Room("Combat Room"), Fight("SLIMES_WEAK"), Won("SLIMES_WEAK"),
            "[INFO] Card rarity: Rolled 0.1554522, need < -0.02 for rare (offset = -0.05)",
            "[INFO] Card rarity: Rolled 0.8230557, need < -0.01 for rare (offset = -0.04)",
            "[INFO] Card rarity: Rolled 0.69397694, need < 0 for rare (offset = -0.03)",
            Obtained(Me, "CARD.UPROAR"));
        DeckCard uproar = Assert.Single(run.State.Deck, c => c.Id == "CARD.UPROAR");
        Assert.Equal(1, uproar.Upgrade);
        Assert.DoesNotContain(run.UncertainCards, u => u.CardId == "CARD.UPROAR");
    }

    [Fact]
    public void Reward_card_upgrade_falls_back_to_the_screen_when_replay_has_nothing()
    {
        // No rarity roll found (e.g. a continued run: no seed in the log): the recognizer's reading decides.
        var t = new GameStateTracker(new TrackerOptions { LocalNetIdOverride = Me });
        t.ProcessLines([.. Header, "[INFO] Preloading 'Act=HIVE' assets... count=8 vfx=0", Room("Combat Room"), Won("SLIMES_WEAK")]);
        t.OnCardOfferSeen([new("CARD.HOTFIX", 1), new("CARD.UPROAR", 0)]);
        t.ProcessLines([Obtained(Me, "CARD.HOTFIX")]);
        DeckCard hotfix = Assert.Single(t.Current!.State.Deck, c => c.Id == "CARD.HOTFIX");
        Assert.Equal(1, hotfix.Upgrade);
        Assert.DoesNotContain(t.Current!.UncertainCards, u => u.CardId == "CARD.HOTFIX");
    }

    [Fact]
    public void Run_end_reads_the_exact_deck_from_the_history_file()
    {
        string saves = Path.Combine(Path.GetTempPath(), "sts2-coop-guest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(saves, "history"));
        try
        {
            File.WriteAllText(Path.Combine(saves, "history", "123.run"),
                $$"""{"players":[{"id":{{Me}},"character":"CHARACTER.DEFECT","deck":[{"id":"CARD.ZAP"}]},""" +
                $$"""{"id":{{Friend}},"character":"CHARACTER.SILENT","deck":[{"id":"CARD.SURVIVOR","current_upgrade_level":1},{"id":"CARD.BACKFLIP"}]}]}""");
            TrackedRun run = ReplayWithSaves(Friend, saves, Room("Event Room"), "[INFO] Saved run history: 123.run");
            Assert.True(run.DeckFromRunHistory);
            Assert.False(run.DeckApproximate);
            Assert.Equal(["CARD.BACKFLIP", "CARD.SURVIVOR+1"], run.State.Deck.Select(c => c.Card.ToString()).Order(StringComparer.Ordinal));
            Assert.Empty(run.UncertainCards);
        }
        finally
        {
            Directory.Delete(saves, recursive: true);
        }
    }

    [Fact]
    public void History_reader_is_only_used_for_a_guest_without_save()
    {
        // The same lines with no saves folder: the rebuild stays a rebuild (no file read, no exception).
        TrackedRun run = Replay(Friend, Room("Event Room"), "[INFO] Saved run history: 123.run");
        Assert.False(run.DeckFromRunHistory);
        Assert.Equal(12, run.State.Deck.Count);
        _ = RunHistoryReader.Parse("""{"players":[]}"""); // reader accepts minimal files (used above)
    }
}
