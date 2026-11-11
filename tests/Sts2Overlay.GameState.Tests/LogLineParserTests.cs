using Sts2Overlay.Core.Map;
using Sts2Overlay.GameState.Log;

namespace Sts2Overlay.GameState.Tests;

public class LogLineParserTests
{
    private static T Parse<T>(string line) where T : LogEvent =>
        Assert.IsType<T>(LogLineParser.Parse(line));

    [Fact]
    public void Embark_single()
    {
        var e = Parse<RunStartedEvent>("[INFO] Embarking on a singleplayer IRONCLAD run. Ascension: 1 Seed: 77V3X5AF52");
        Assert.False(e.IsMultiplayer);
        Assert.Equal("CHARACTER.IRONCLAD", e.Character);
        Assert.Equal(1, e.Ascension);
        Assert.Equal("77V3X5AF52", e.Seed);
        Assert.Equal("standard", e.Mode);
    }

    [Fact]
    public void Embark_multi_lists_players_in_slot_order()
    {
        var e = Parse<RunStartedEvent>("[INFO] Embarking on a multiplayer run. Players: Player 76561190000000001, DEFECT,Player 76561190000000002, SILENT. Ascension: 4 Seed: ZSN877JR14");
        Assert.True(e.IsMultiplayer);
        Assert.Null(e.Character);
        Assert.Equal(4, e.Ascension);
        Assert.Equal([new LobbyPlayer(TestPaths.Me, "CHARACTER.DEFECT"), new LobbyPlayer(TestPaths.Friend, "CHARACTER.SILENT")], e.Players);
    }

    [Fact]
    public void Embark_custom_and_daily_variants()
    {
        Assert.Equal("custom", Parse<RunStartedEvent>("[INFO] Embarking on a CUSTOM DEFECT run. Ascension: 0 Seed: ABC Modifiers: x").Mode);
        var daily = Parse<RunStartedEvent>("[INFO] Embarking on a DAILY SILENT run with 2 players. Ascension: 3 Seed: D1");
        Assert.Equal("daily", daily.Mode);
        Assert.True(daily.IsMultiplayer);
    }

    [Fact]
    public void Map_vote_is_col_row_and_generation_is_act()
    {
        var e = Parse<MapVoteEvent>("[INFO] Player vote changed for 1: ->MapVote (gen: 2 coord: (4, 9))");
        Assert.Equal(1UL, e.PlayerId);
        Assert.Equal(2, e.Generation);
        Assert.Equal(new MapCoord(4, 9), e.Coord);
        // A changed vote prints the old one first.
        Assert.Equal(new MapCoord(0, 6), Parse<MapVoteEvent>("[INFO] Player vote changed for 7: MapVote (gen: 2 coord: (1, 6))->MapVote (gen: 2 coord: (0, 6))").Coord);
    }

    [Theory]
    [InlineData("Combat Room", RoomKind.Combat)]
    [InlineData("Merchant Room", RoomKind.Merchant)]
    [InlineData("RestSite Room", RoomKind.RestSite)]
    [InlineData("Event Room", RoomKind.Event)]
    [InlineData("Treasure Room", RoomKind.Treasure)]
    public void Room_preload(string name, RoomKind kind) =>
        Assert.Equal(kind, Parse<RoomEnteredEvent>($"[INFO] Preloading '{name}' assets... count=1 vfx=0").Room);

    [Fact]
    public void Preload_act_menu_and_noise()
    {
        Assert.Equal("ACT.HIVE", Parse<ActEnteredEvent>("[INFO] Preloading 'Act=HIVE' assets... count=6 vfx=0").ActId);
        Parse<MainMenuEvent>("[INFO] Preloading 'MainMenuEssentials' assets... count=5 vfx=0");
        Assert.Null(LogLineParser.Parse("[INFO] Preloading 'Combat Room' Complete: assets=11 time_elapsed=52ms"));
        Assert.Null(LogLineParser.Parse("[INFO] Preloading 'characters=IRONCLAD' assets... count=37 vfx=9"));
    }

    [Theory]
    [InlineData("KNIGHTS_ELITE", CombatKind.Elite)]
    [InlineData("VANTOM_BOSS", CombatKind.Boss)]
    [InlineData("NIBBITS_WEAK", CombatKind.Normal)]
    [InlineData("CUBEX_CONSTRUCT_NORMAL", CombatKind.Normal)]
    [InlineData("OVERGROWTH_CRAWLERS", CombatKind.Unknown)]
    [InlineData("DENSE_VEGETATION_EVENT_ENCOUNTER", CombatKind.Unknown)]
    public void Combat_room_kind_from_suffix(string enc, CombatKind kind)
    {
        var e = Parse<CombatStartedEvent>($"[INFO] Creating NCombatRoom with mode=ActiveCombat encounter={enc}.");
        Assert.Equal("ENCOUNTER." + enc, e.EncounterId);
        Assert.Equal(kind, e.Kind);
        Assert.False(e.VisualOnly);
    }

    [Fact]
    public void Visual_only_combat_room_is_flagged() =>
        Assert.True(Parse<CombatStartedEvent>("[INFO] Creating NCombatRoom with mode=VisualOnly encounter=THE_ARCHITECT_EVENT_ENCOUNTER.").VisualOnly);

    [Fact]
    public void Combat_won_both_formats()
    {
        var a = Parse<CombatWonEvent>("[INFO] CHARACTER.DEFECT has won against encounter ENCOUNTER.NIBBITS_WEAK. That's 2 wins");
        Assert.Equal(("CHARACTER.DEFECT", "ENCOUNTER.NIBBITS_WEAK", CombatKind.Normal), (a.Character, a.EncounterId, a.Kind));
        var b = Parse<CombatWonEvent>("[INFO] CHARACTER.IRONCLAD fought ENCOUNTER.BYRDONIS_ELITE for the first time and WON >:)");
        Assert.Equal(CombatKind.Elite, b.Kind);
        // The monster variant of the same message is not a combat result.
        Assert.Null(LogLineParser.Parse("[INFO] CHARACTER.IRONCLAD fought MONSTER.FUZZY_WURM_CRAWLER for the first time and WON >:("));
    }

    [Fact]
    public void Combat_lost_and_run_end()
    {
        Assert.Equal(CombatKind.Boss, Parse<CombatLostEvent>("[INFO] CHARACTER.IRONCLAD has lost to encounter ENCOUNTER.VANTOM_BOSS. That's 1 losses").Kind);
        Parse<CombatLostEvent>("[INFO] CHARACTER.SILENT fought ENCOUNTER.X_BOSS for the first time and LOST :(");
        Parse<RunWonEvent>("[INFO] CHARACTER.IRONCLAD has won a run with ancient EVENT.NEOW. That's 2 wins");
        Assert.Equal("1794131872.run", Parse<RunHistorySavedEvent>("[INFO] Saved run history: 1794131872.run").FileName);
        Parse<RunAbandonedEvent>("[INFO] Abandoning an in-progress run (player-initiated)");
        Assert.Equal("CHARACTER.DEFECT", Parse<RunContinuedEvent>("[INFO] Continuing run with character: CHARACTER.DEFECT").Character);
    }

    [Fact]
    public void Rarity_roll_regular_with_pity_offset()
    {
        var e = Parse<CardRarityRolledEvent>("[INFO] Card rarity: Rolled 0.58266187, need < -0.020000001 for rare (offset = -0.05)");
        Assert.Equal(0.58266187, e.Roll, 6);
        Assert.Equal(-0.05, e.Offset, 6);
        Assert.False(e.IsRare);
        Assert.Equal(RaritySource.Regular, e.Source);
        Assert.False(e.IsScarcity);
    }

    [Theory]
    [InlineData("Rolled 0.110755, need < 0.1 for rare (offset = 0.01)", RaritySource.Shop, false)]
    [InlineData("Rolled 0.055243134, need < 0.07000001 for rare (offset = -0.02)", RaritySource.Shop, true)]
    [InlineData("Rolled 0.15843602, need < 0.120000005 for rare (offset = 0.02)", RaritySource.Elite, false)]
    [InlineData("Rolled 0.9, need < 1 for rare (offset = 0)", RaritySource.Boss, true)]
    [InlineData("Rolled 1E-05, need < 0.03 for rare (offset = 0)", RaritySource.Regular, true)]
    [InlineData("Rolled 0.5, need < 0.0249 for rare (offset = 0.01)", RaritySource.Regular, false)] // A7 base 1.49%
    public void Rarity_source_from_need_minus_offset(string tail, RaritySource source, bool rare)
    {
        var e = Parse<CardRarityRolledEvent>("[INFO] Card rarity: " + tail);
        Assert.Equal(source, e.Source);
        Assert.Equal(rare, e.IsRare);
    }

    [Fact]
    public void Obtained_variants()
    {
        var card = Parse<CardObtainedEvent>("[INFO] Player 1 obtained CARD.ANGER from card reward");
        Assert.Equal((1UL, "CARD.ANGER", false), (card.PlayerId!.Value, card.CardId, card.Special));
        Assert.True(Parse<CardObtainedEvent>("[INFO] Player 7 obtained CARD.X from special card reward").Special);
        Assert.Null(Parse<CardObtainedEvent>("[INFO] Obtained CARD.DRAIN_POWER from card reward").PlayerId); // v0.99 format
        Assert.Equal(17, Parse<GoldObtainedEvent>("[INFO] Player 1 obtained 17 gold from reward").Amount);
        Assert.Equal(7, Parse<GoldObtainedEvent>("[DEBUG] Player 2 obtained 7 gold from gold reward").Amount);
        Assert.Equal("POTION.CLARITY", Parse<PotionObtainedEvent>("[INFO] Player 76561190000000001 obtained POTION.CLARITY from potion reward").PotionId);
        Assert.Equal("RELIC.HORN_CLEAT", Parse<RelicObtainedEvent>("[INFO] Player 1 obtained RELIC.HORN_CLEAT from relic reward").RelicId);
        var chest = Parse<RelicObtainedEvent>("[DEBUG] [TreasureRoomRelicSynchronizer] Relic index 0 (RELIC.WAR_PAINT (21823123)) is being picked by local player 76561190000000001");
        Assert.Equal((TestPaths.Me, "RELIC.WAR_PAINT"), (chest.PlayerId!.Value, chest.RelicId));
        Assert.Null(LogLineParser.Parse("[DEBUG] [TreasureRoomRelicSynchronizer] Player MegaCrit.Sts2.Core.Entities.Players.Player picked relic at index 0: RELIC.WAR_PAINT (21823123)"));
        Assert.Equal(1UL, Parse<CardRemovalRewardEvent>("[INFO] Player 1 obtained card removal from reward").PlayerId);
        Assert.Null(LogLineParser.Parse("[INFO] Player 1 selected card reward"));
    }

    [Fact]
    public void Chose_cards_adds_card_prefix()
    {
        var e = Parse<CardsChosenEvent>("[INFO] Player 76561190000000002 chose cards [STRIKE_SILENT,DEFEND_SILENT]");
        Assert.Equal(TestPaths.Friend, e.PlayerId);
        Assert.Equal(["CARD.STRIKE_SILENT", "CARD.DEFEND_SILENT"], e.CardIds);
        Assert.Empty(Parse<CardsChosenEvent>("[INFO] Player 1 chose cards []").CardIds);
    }

    [Fact]
    public void Potion_use_and_rest_option_and_ids()
    {
        var p = Parse<PotionUsedEvent>("[INFO] Player 1 using potion EXPLOSIVE_AMPOULE (no target)");
        Assert.Equal(("POTION.EXPLOSIVE_AMPOULE", false), (p.PotionId, p.Discarded));
        Assert.True(Parse<PotionUsedEvent>("[INFO] Player 1 discarding potion FIRE_POTION").Discarded);

        var r = Parse<RestSiteOptionEvent>("[DEBUG] [RestSiteSynchronizer] Rest site option index 1 chosen for player 76561190000000001 with success True. Option: SMITH");
        Assert.Equal((TestPaths.Me, "SMITH", true), (r.PlayerId, r.Option, r.Success));

        Assert.Equal(TestPaths.Me, Parse<LocalPlayerIdEvent>("[INFO] [SteamHost] Initializing Steam host. Our player id: 76561190000000001").NetId);
        Assert.Equal(TestPaths.Friend, Parse<LocalPlayerIdEvent>("[INFO] [SteamClient] Attempting to connect to lobby. Our player id: 76561190000000002. Other player's id: 76561190000000001.").NetId);
        Assert.Equal(TestPaths.Me, Parse<LocalPlayerIdEvent>("[INFO] [StartRunLobby (76561190000000001)] Local player 76561190000000001 is ready").NetId);
        var prof = Parse<LocalPlayerIdEvent>("[INFO] Profile-scoped data path initialized: user://steam/76561190000000001/profile1");
        Assert.True(prof.FromProfilePath);
    }

    [Fact]
    public void Save_written_only_for_run_saves()
    {
        var e = Parse<RunSaveWrittenEvent>(@"[INFO] Wrote 87444 bytes to path=user://steam/76561190000000001/profile1\saves\current_run.save save_dir=user://steam/76561190000000001");
        Assert.Equal(("current_run.save", 87444L), (e.FileName, e.Bytes));
        Assert.Equal("current_run_mp.save", Parse<RunSaveWrittenEvent>(@"[INFO] Wrote 1 bytes to path=user://steam/1/profile1\saves\current_run_mp.save save_dir=x").FileName);
        Assert.Null(LogLineParser.Parse(@"[INFO] Wrote 126286 bytes to path=user://steam/1/profile1\saves\progress.save save_dir=x"));
        Assert.Null(LogLineParser.Parse("[INFO] Wrote 87444 bytes to profile1/saves/current_run.save in steam remote store"));
    }

    [Theory]
    [InlineData("[INFO] [Sentry.NET] Initialized: env=unknown, release=v0.111.0", "v0.111.0")]
    [InlineData("Release Version: v0.111.0", "v0.111.0")]
    [InlineData("[INFO] [Sentry.NET] Initialized: env=development, release=v0.107.1", "v0.107.1")]
    public void Game_version_from_the_startup_lines(string line, string version) =>
        Assert.Equal(version, Parse<GameVersionEvent>(line).Version);

    [Fact]
    public void Version_lookalikes_are_ignored()
    {
        Assert.Null(LogLineParser.Parse("[INFO] [Sentry.NET] Capturing event"));
        Assert.Null(LogLineParser.Parse("Release Version: unknown"));
    }

    [Fact]
    public void Noise_is_ignored()
    {
        Assert.Null(LogLineParser.Parse("[INFO] Player 1 playing card STRIKE_IRONCLAD (no target)"));
        Assert.Null(LogLineParser.Parse("[INFO] Monster NIBBIT performing move BUTT_MOVE"));
        Assert.Null(LogLineParser.Parse("   at: pipeline_cache_create (drivers/d3d12/rendering_device_driver_d3d12.cpp:4469)"));
        Assert.Null(LogLineParser.Parse(""));
    }

    [Fact]
    public void Every_fixture_line_parses_without_throwing()
    {
        foreach (string name in new[] { "single_ironclad_run.log", "coop_host_run.log" })
            foreach (string line in TestPaths.FixtureLines(name))
                _ = LogLineParser.Parse(line);
    }
}
