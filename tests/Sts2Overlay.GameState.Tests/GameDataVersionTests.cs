using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// <see cref="GameDataVersion"/>: the embedded tables follow the run's <see cref="RngScheme"/> (Legacy32 → v0.107.1,
/// Xx64 → v0.111.0), so a v0.107.1 run keeps its own rarities and a v0.111 run sees the moved ones and the new cards.
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
