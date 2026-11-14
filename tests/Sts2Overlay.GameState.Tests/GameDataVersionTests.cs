using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// Card and relic lookups use the catalogs for the run that produced the save or log.
/// </summary>
public class GameDataVersionTests
{
    [Fact]
    public void Scheme_picks_the_table_version()
    {
        Assert.Equal("v0.107.1", GameDataVersion.For(RngScheme.Legacy32));
        Assert.Equal("v0.111.0", GameDataVersion.For(RngScheme.Xx64));
        Assert.Equal("v0.111.0", GameDataVersion.For(RngSeeds.Default));
    }

    [Theory]
    [InlineData(0, "v0.108.0", GameDataVersion.V0107)]
    [InlineData(0, "v0.111.0", GameDataVersion.V0111)]
    [InlineData(0, "v0.113.0-beta", GameDataVersion.V0113)]
    [InlineData(20, "v0.113.0", GameDataVersion.V0111)]
    [InlineData(21, "v0.111.0", GameDataVersion.V0113)]
    public void Save_schema_precedes_log_version(int schema, string release, string expected)
        => Assert.Equal(expected, GameDataVersion.ForRun(schema, release).Version);

    [Fact]
    public void Event_deck_effects_follow_the_game_version()
    {
        const string doors = "DOORS_OF_LIGHT_AND_DARK.pages.INITIAL.options.LIGHT";
        var oldLight = DeckEffects.ForChoice("EVENT.DOORS_OF_LIGHT_AND_DARK", doors, RngScheme.Xx64)!;
        var light = DeckEffects.ForChoice("EVENT.DOORS_OF_LIGHT_AND_DARK", doors, GameDataVersion.Latest)!;
        Assert.Equal(2, oldLight.RandomUpgrades);
        Assert.Equal(SelectMeaning.Upgrade, light.Select);
        Assert.Equal(0, light.RandomUpgrades);

        const string help = "WUPPY_IN_TROUBLE.pages.INITIAL.options.HELP_IMMEDIATELY";
        Assert.Null(DeckEffects.ForChoice("EVENT.WUPPY_IN_TROUBLE", help, RngScheme.Xx64));
        Assert.Equal(["CARD.INJURY"], DeckEffects.ForChoice("EVENT.WUPPY_IN_TROUBLE", help, GameDataVersion.Latest)!.Adds);
    }

    [Fact]
    public void Fool_rewards_follow_pool_order_and_epoch_locks()
    {
        var pool = Assert.IsType<RewardCardPool>(RewardCardPool.For("CHARACTER.FOOL", false, GameDataVersion.Latest));
        Assert.Equal(8, pool.Hypotheses);
        OfferSlot[] offer = [new(0, "Common", 0, 0), new(3, "Rare", 0, 0)];
        Assert.Equal(["CARD.ARROW_STAB", "CARD.BOUNCE"], pool.Predict(offer, lockedMask: 0)!);
        Assert.Equal(["CARD.BOW_SMACK", "CARD.BOUNCE"], pool.Predict(offer, lockedMask: 1)!);
        Assert.Null(RewardCardPool.For("CHARACTER.FOOL", false, RngScheme.Xx64));
    }

    [Fact]
    public void Log_only_Fool_run_starts_with_its_v113_loadout()
    {
        using var tracker = new GameStateTracker();
        tracker.ProcessLines([
            "Release Version: v0.113.0",
            "[INFO] Embarking on a singleplayer FOOL run. Ascension: 0 Seed: TESTSEED",
        ]);
        var state = Assert.IsType<TrackedRun>(tracker.Current).State;
        Assert.Equal((77, 77, 99), (state.Hp, state.MaxHp, state.Gold));
        Assert.Equal(10, state.Deck.Count);
        Assert.Equal(5, state.Deck.Count(c => c.Id == "CARD.STRIKE_FOOL"));
        Assert.Equal(4, state.Deck.Count(c => c.Id == "CARD.DEFEND_FOOL"));
        Assert.Single(state.Deck, c => c.Id == "CARD.FOOLISH_SHOT");
        Assert.Equal(["RELIC.SONG_BOW"], state.Relics);
        var inputs = new AncientInputs
        {
            RunSeed = "TESTSEED",
            UsesV113Rules = true,
            Deck = state.Deck.Select(c => new AncientDeckCard(c.Id)).ToList(),
            Relics = state.Relics,
        };
        var options = AncientOptions.PossibleKeys("EVENT.OROBAS", inputs)!;
        Assert.Contains("OROBAS.pages.INITIAL.options.TOUCH_OF_OROBAS", options);
        Assert.Contains("OROBAS.pages.INITIAL.options.ARCHAIC_TOOTH", options);
        var deck = new DeckReplay(state.Deck, GameDataVersion.Latest);
        deck.Transcend();
        Assert.Equal("CARD.DIVINE_SHOT", deck.Cards[^1].Id);
    }

    [Theory]
    // v0.111 moved these rarities (CardModel.Rarity in the two decompiles).
    [InlineData("CARD.BLOODLETTING", "Common", "Uncommon")]
    [InlineData("CARD.DOMINATE", "Uncommon", "Rare")]
    [InlineData("CARD.TAUNT", "Uncommon", "Common")]
    // Unchanged.
    [InlineData("CARD.BASH", "Basic", "Basic")]
    public void Rarity_follows_the_generation(string card, string v0107, string v0111)
    {
        Assert.Equal(v0107, CardRarities.Of(card, RngScheme.Legacy32));
        Assert.Equal(v0111, CardRarities.Of(card, RngScheme.Xx64));
    }

    [Fact]
    public void Sidestep_exists_only_in_v0111_and_an_old_Scare_id_reads_as_it()
    {
        Assert.True(CardRarities.Knows("CARD.SIDESTEP", RngScheme.Xx64));
        Assert.False(CardRarities.Knows("CARD.SIDESTEP", RngScheme.Legacy32));

        // SerializableRunV19ToV20 renames CARD.SCARE to CARD.SIDESTEP; a stale id still resolves on Xx64.
        Assert.True(CardRarities.Knows("CARD.SCARE", RngScheme.Xx64));
        Assert.Equal(CardRarities.Of("CARD.SIDESTEP", RngScheme.Xx64), CardRarities.Of("CARD.SCARE", RngScheme.Xx64));
        Assert.False(CardRarities.LeavesCombatWhenPlayed("CARD.SCARE", RngScheme.Xx64)); // Sidestep has no Exhaust
        Assert.True(CardRarities.LeavesCombatWhenPlayed("CARD.SCARE", RngScheme.Legacy32)); // Scare did
    }

    [Fact]
    public void Co_op_only_v0111_cards_are_filtered_in_single_player()
    {
        Assert.True(CardRarities.Knows("CARD.TUTOR", RngScheme.Xx64));
        Assert.False(CardRarities.AllowedWithPlayers("CARD.TUTOR", multiplayer: false, RngScheme.Xx64));
        Assert.True(CardRarities.AllowedWithPlayers("CARD.TUTOR", multiplayer: true, RngScheme.Xx64));
    }

    [Fact]
    public void Reward_pool_follows_the_generation()
    {
        var old = RewardCardPool.For("CHARACTER.SILENT", multiplayer: false, RngScheme.Legacy32);
        var now = RewardCardPool.For("CHARACTER.SILENT", multiplayer: false, RngScheme.Xx64);
        Assert.Equal(RngScheme.Legacy32, old!.Scheme);
        Assert.Equal(RngScheme.Xx64, now!.Scheme);
    }

    [Fact]
    public void Upgrade_that_drops_exhaust_follows_the_generation()
    {
        // Scare+ lost Exhaust in v0.107.1; Sidestep never had it. Synchronize lost Exhaust altogether in v0.111.
        Assert.True(EventPrediction.UpgradeDropsExhaust("CARD.SCARE", RngScheme.Legacy32));
        Assert.False(EventPrediction.UpgradeDropsExhaust("CARD.SIDESTEP", RngScheme.Xx64));
        Assert.True(EventPrediction.UpgradeDropsExhaust("CARD.SYNCHRONIZE", RngScheme.Legacy32));
        Assert.False(EventPrediction.UpgradeDropsExhaust("CARD.SYNCHRONIZE", RngScheme.Xx64));
        Assert.True(EventPrediction.UpgradeDropsExhaust("CARD.UNDERWORLD", RngScheme.Xx64));
    }

    [Fact]
    public void Relic_deck_effects_follow_the_generation()
    {
        // Distinguished Cape: v0.107.1 3 Apparition + max HP loss; v0.111 3 Apparition + 2 random curses.
        Assert.Equal(0, DeckEffects.ForRelic("RELIC.DISTINGUISHED_CAPE", RngScheme.Legacy32)?.UnknownAdds ?? 0);
        Assert.Equal(2, DeckEffects.ForRelic("RELIC.DISTINGUISHED_CAPE", RngScheme.Xx64)!.UnknownAdds);
        // Sere Talon swapped the other way.
        Assert.Equal(2, DeckEffects.ForRelic("RELIC.SERE_TALON", RngScheme.Legacy32)!.UnknownAdds);
        Assert.Equal(0, DeckEffects.ForRelic("RELIC.SERE_TALON", RngScheme.Xx64)!.UnknownAdds);
        // New in v0.111.
        Assert.Equal(["CARD.DOWSING"], DeckEffects.ForRelic("RELIC.DOWSING_ROD", RngScheme.Xx64)!.Adds);
        Assert.Null(DeckEffects.ForRelic("RELIC.DOWSING_ROD", RngScheme.Legacy32));
    }

    [Fact]
    public void Starting_decks_load_for_both_generations()
    {
        Assert.Equal(
            StartingLoadouts.EmbeddedFor(RngScheme.Legacy32).For("CHARACTER.IRONCLAD")!.Deck.Count,
            StartingLoadouts.EmbeddedFor(RngScheme.Xx64).For("CHARACTER.IRONCLAD")!.Deck.Count);
    }

    [Theory]
    // v0.111.0 EventModel.BeginEvent: runSeed + (IsShared ? 0 : slot) + hash(id). Jungle Maze and Morphic Grove are shared.
    [InlineData("EVENT.JUNGLE_MAZE_ADVENTURE", true)]
    [InlineData("MORPHIC_GROVE", true)]
    [InlineData("EVENT.AROMA_OF_CHAOS", false)]
    [InlineData("EVENT.NEOW", false)]
    public void Shared_events_use_the_slot_0_stream_on_xx64(string eventId, bool shared)
    {
        const string seed = "TESTSEED01";
        Assert.Equal(shared, GameDataVersion.IsSharedEvent(RngScheme.Xx64, eventId));
        ulong guest = GameStateTracker.EventRngSeed(RngScheme.Xx64, seed, 1, eventId);
        ulong host = GameStateTracker.EventRngSeed(RngScheme.Xx64, seed, 0, eventId);
        Assert.Equal(shared, guest == host);
        Assert.Equal(RngSeeds.EventStream(RngScheme.Xx64, seed, shared ? 0 : 1, eventId), guest);
    }

    [Fact]
    public void Legacy32_keeps_adding_the_slot_for_every_event()
    {
        // The v0.107.1 seed rule for shared events is not known: the slot is still added.
        const string seed = "TESTSEED01";
        Assert.False(GameDataVersion.IsSharedEvent(RngScheme.Legacy32, "EVENT.MORPHIC_GROVE"));
        Assert.Equal(RngSeeds.EventStream(RngScheme.Legacy32, seed, 1, "EVENT.MORPHIC_GROVE"),
            GameStateTracker.EventRngSeed(RngScheme.Legacy32, seed, 1, "EVENT.MORPHIC_GROVE"));
        Assert.NotEqual(GameStateTracker.EventRngSeed(RngScheme.Legacy32, seed, 0, "EVENT.MORPHIC_GROVE"),
            GameStateTracker.EventRngSeed(RngScheme.Legacy32, seed, 1, "EVENT.MORPHIC_GROVE"));
    }
}
