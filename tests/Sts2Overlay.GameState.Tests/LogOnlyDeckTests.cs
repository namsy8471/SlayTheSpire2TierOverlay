using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// Log-only deck rebuild rules found on a real v0.111.0 single-player run (seed PZ4J3CV19SSW): single player logs no
/// event option, so event picks are flags, and the flags must survive later picks of the same card.
/// </summary>
public class LogOnlyDeckTests
{
    private static readonly string[] Solo =
    [
        "[INFO] [Sentry.NET] Initialized: env=unknown, release=v0.111.0",
        "[INFO] Embarking on a singleplayer IRONCLAD run. Ascension: 0 Seed: TESTSEED01",
        "[INFO] Preloading 'Act=OVERGROWTH' assets... count=8 vfx=0",
    ];

    private const ulong Friend = 76561190000000002;

    private static readonly string[] Coop =
    [
        "[INFO] [Sentry.NET] Initialized: env=unknown, release=v0.111.0",
        "[INFO] [StartRunLobby (76561190000000001)] Local player 76561190000000001 is ready",
        "[INFO] Embarking on a multiplayer run. Players: Player 76561190000000001, DEFECT,Player 76561190000000002, IRONCLAD. Ascension: 0 Seed: TESTSEED01",
        "[INFO] Preloading 'Act=OVERGROWTH' assets... count=8 vfx=0",
    ];

    private static TrackedRun Replay(string[] header, ulong? me, params string[] lines)
    {
        var t = new GameStateTracker(new TrackerOptions { LocalNetIdOverride = me });
        t.ProcessLines(header.Concat(lines));
        return t.Current!;
    }

    private static string Room(string name) => $"[INFO] Preloading '{name}' assets... count=1 vfx=0";
    private static string Chose(ulong p, string cards) => $"[INFO] Player {p} chose cards [{cards}]";
    private static string Screen(string scene) => $"[WARN] Asset not cached: res://scenes/screens/card_selection/{scene}.tscn";
    private static string Option(ulong p, string ev, string key) =>
        $"[DEBUG] [EventSynchronizer] Option index 0 chosen for player {p} in event EVENT.{ev}. Choice key: {key}";

    private static int Count(TrackedRun run, string id) => run.State.Deck.Count(c => c.Id == id);
    private static int Flagged(TrackedRun run, string id) =>
        run.UncertainCards.Count(u => u.CardId == id && u.Reason == UncertainReason.UnknownCardSelect);

    [Fact]
    public void A_shop_removal_takes_a_certain_copy_and_leaves_the_flag_on_the_doubtful_one()
    {
        TrackedRun run = Replay(Solo, null,
            Room("Event Room"), Chose(1, "STRIKE_IRONCLAD"),      // an event we can't name: maybe transformed
            Room("Merchant Room"), Chose(1, "STRIKE_IRONCLAD"));  // the removal service
        Assert.Equal(4, Count(run, "CARD.STRIKE_IRONCLAD"));
        Assert.Equal(1, Flagged(run, "CARD.STRIKE_IRONCLAD"));
    }

    [Fact]
    public void Each_card_an_unknown_select_names_flags_its_own_copy()
    {
        TrackedRun run = Replay(Solo, null,
            Room("Event Room"), Chose(1, "STRIKE_IRONCLAD"),
            Room("Event Room"), Chose(1, "STRIKE_IRONCLAD,STRIKE_IRONCLAD")); // e.g. Amalgamator: two different Strikes
        Assert.Equal(5, Count(run, "CARD.STRIKE_IRONCLAD"));
        Assert.Equal(3, Flagged(run, "CARD.STRIKE_IRONCLAD"));
    }

    [Fact]
    public void A_logged_transform_screen_makes_the_pick_a_transform()
    {
        TrackedRun run = Replay(Solo, null,
            Room("Event Room"), Screen("deck_transform_select_screen"), Chose(1, "STRIKE_IRONCLAD"));
        Assert.Equal(4, Count(run, "CARD.STRIKE_IRONCLAD"));
        Assert.Equal(0, Flagged(run, "CARD.STRIKE_IRONCLAD"));
        Assert.Equal(1, run.UnidentifiedCards);
        Assert.Equal(9, run.State.Deck.Count); // the unnamed result is counted, not listed
    }

    [Fact]
    public void A_logged_upgrade_screen_makes_the_pick_an_upgrade_and_the_hint_ends_with_the_pick()
    {
        TrackedRun run = Replay(Solo, null,
            Room("Event Room"), Screen("deck_upgrade_select_screen"), Chose(1, "BASH"),
            Room("Event Room"), Chose(1, "DEFEND_IRONCLAD"));
        Assert.Equal(1, Assert.Single(run.State.Deck, c => c.Id == "CARD.BASH").Upgrade);
        Assert.DoesNotContain(run.UncertainCards, u => u.CardId == "CARD.BASH");
        Assert.Equal(1, Flagged(run, "CARD.DEFEND_IRONCLAD"));
    }

    [Fact]
    public void Large_capsule_adds_the_characters_basic_strike_and_defend()
    {
        TrackedRun run = Replay(Coop, Friend,
            Room("Event Room"), Option(Friend, "NEOW", "NEOW.pages.INITIAL.options.LARGE_CAPSULE"));
        Assert.Equal(6, Count(run, "CARD.STRIKE_IRONCLAD"));
        Assert.Equal(5, Count(run, "CARD.DEFEND_IRONCLAD"));
        Assert.Contains("RELIC.LARGE_CAPSULE", run.State.Relics);
    }

    [Fact]
    public void Paels_claw_makes_every_defend_goopy()
    {
        TrackedRun run = Replay(Coop, Friend,
            Room("Event Room"), Option(Friend, "PAEL", "PAEL.pages.INITIAL.options.PAELS_CLAW"));
        Assert.All(run.State.Deck.Where(c => c.Id == "CARD.DEFEND_IRONCLAD"), c => Assert.Equal("ENCHANTMENT.GOOPY", c.EnchantmentId));
        Assert.All(run.State.Deck.Where(c => c.Id != "CARD.DEFEND_IRONCLAD"), c => Assert.Null(c.EnchantmentId));
    }

    [Theory]
    [InlineData("RELIC.PAELS_HORN", "CARD.RELAX", 2)]
    [InlineData("RELIC.NEOWS_BONES", null, 0)]
    public void Relic_pickup_card_counts(string relic, string? card, int adds)
    {
        DeckEffect effect = DeckEffects.ForRelic(relic, RngScheme.Xx64)!;
        Assert.Equal(adds, card is null ? 0 : effect.Adds.Count(a => a == card));
        if (relic == "RELIC.NEOWS_BONES") Assert.Equal(1, effect.UnknownAdds); // one random curse
    }
}
