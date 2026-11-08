using Sts2Overlay.GameState.Log;

namespace Sts2Overlay.GameState.Tests;

/// <summary>Co-op Debug lines used to rebuild a guest's deck (synthetic, ids only).</summary>
public class LogLineParserCoopTests
{
    private static T Parse<T>(string line) where T : LogEvent =>
        Assert.IsType<T>(LogLineParser.Parse(line));

    [Fact]
    public void Event_option_with_choice_key()
    {
        var e = Parse<EventOptionChosenEvent>(
            "[DEBUG] [EventSynchronizer] Option index 2 chosen for player 76561190000000002 in event EVENT.NEOW. Choice key: NEOW.pages.INITIAL.options.PRECARIOUS_SHEARS");
        Assert.Equal(TestPaths.Friend, e.PlayerId);
        Assert.Equal("EVENT.NEOW", e.EventId);
        Assert.Equal("NEOW.pages.INITIAL.options.PRECARIOUS_SHEARS", e.ChoiceKey);
    }

    [Theory]
    [InlineData("[DEBUG] [ActionQueueSet] Enqueueing action PlayCardAction card: CARD.ZAP (12345678) index: 2 targetid:  to player queue owned by 76561190000000001", "CARD.ZAP", 2)]
    [InlineData("[DEBUG] [ActionQueueSet] Enqueueing action PlayCardAction card: CARD.STRIKE_SILENT (42965608) index: 21 targetid: 2 to player queue owned by 76561190000000001", "CARD.STRIKE_SILENT", 21)]
    public void Card_play_with_combat_index(string line, string card, int index)
    {
        var e = Parse<CardPlayedEvent>(line);
        Assert.Equal(TestPaths.Me, e.OwnerId);
        Assert.Equal(card, e.CardId);
        Assert.Equal(index, e.CombatIndex);
    }

    [Fact]
    public void Synchronizer_copy_of_a_play_is_ignored()
    {
        // The same play is also logged by ActionQueueSynchronizer ("from owner"); only the ActionQueueSet line counts.
        Assert.Null(LogLineParser.Parse(
            "[DEBUG] [ActionQueueSynchronizer] Enqueueing action PlayCardAction card: CARD.ZAP (12345678) index: 2 targetid:  from owner 76561190000000001"));
    }

    [Fact]
    public void Remote_shop_purchase()
    {
        var e = Parse<RemoteShopPurchaseEvent>(
            "[VERYDEBUG] [RunLocationTargetedMessageBuffer] Handling map-targeted message RewardObtainedMessage type: Card location: act 1 coord (0, 4) room 0 skipped: False Card: CARD.NEGATIVE_PULSE (63668645) from 76561190000000002 for location act 1 coord (0, 4) room 0");
        Assert.Equal(TestPaths.Friend, e.PlayerId);
        Assert.Equal("CARD.NEGATIVE_PULSE", e.ItemId);
    }

    [Fact]
    public void Thieving_hopper_steal_is_the_only_monster_move_reported()
    {
        var e = Parse<MonsterMoveEvent>("[INFO] Monster THIEVING_HOPPER performing move THIEVERY_MOVE");
        Assert.Equal("THIEVERY_MOVE", e.Move);
        Assert.Null(LogLineParser.Parse("[INFO] Monster THIEVING_HOPPER performing move NAB_MOVE"));
        Assert.Null(LogLineParser.Parse("[INFO] Monster TWIG_SLIME_S performing move TACKLE_MOVE"));
    }
}
