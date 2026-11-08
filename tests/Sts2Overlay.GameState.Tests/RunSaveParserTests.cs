using System.Text.Json;
using Sts2Overlay.Core.Map;
using Sts2Overlay.GameState.Save;

namespace Sts2Overlay.GameState.Tests;

public class RunSaveParserTests
{
    private static string CoopJson => File.ReadAllText(TestPaths.Fixture("coop_save_handmade.json"));

    internal const string SingleJson = """
        {"schema_version":16,"ascension":1,"current_act_index":0,"save_time":0,
         "rng":{"seed":"SOLO1"},
         "acts":[{"id":"ACT.UNDERDOCKS"}],
         "visited_map_coords":[],
         "map_point_history":[],
         "players":[{"net_id":1,"character_id":"CHARACTER.IRONCLAD","current_hp":80,"max_hp":80,"gold":99,
           "deck":[{"id":"CARD.BASH","floor_added_to_deck":1}],
           "relics":[{"id":"RELIC.BURNING_BLOOD"}],
           "extra_fields":{"card_shop_removals_used":2},
           "odds":{"card_rarity_odds_value":-0.05}}]}
        """;

    [Fact]
    public void Coop_picks_my_player_by_net_id()
    {
        ParsedSave s = RunSaveParser.Parse(CoopJson, TestPaths.Me);
        Assert.True(s.MyPlayerMatched);
        Assert.Equal(TestPaths.Me, s.MyNetId);
        Assert.Equal("CHARACTER.DEFECT", s.State.Character);
        Assert.True(s.State.IsMultiplayer);
        Assert.Equal((51, 75, 120), (s.State.Hp, s.State.MaxHp, s.State.Gold));
        Assert.Equal([TestPaths.Me, TestPaths.Friend], s.PlayerNetIds);

        ParsedSave friend = RunSaveParser.Parse(CoopJson, TestPaths.Friend);
        Assert.Equal("CHARACTER.SILENT", friend.State.Character);
    }

    [Fact]
    public void Coop_unknown_id_falls_back_to_first_player_and_says_so()
    {
        ParsedSave s = RunSaveParser.Parse(CoopJson, 42);
        Assert.False(s.MyPlayerMatched);
        Assert.Equal("CHARACTER.DEFECT", s.State.Character);
    }

    [Fact]
    public void Deck_keeps_upgrades_enchantments_and_floor()
    {
        var deck = RunSaveParser.Parse(CoopJson, TestPaths.Me).State.Deck;
        Assert.Equal(3, deck.Count);
        Assert.Equal(1, deck[1].Upgrade);
        Assert.Equal(("ENCHANTMENT.SLITHER", 1, 12), (deck[2].EnchantmentId, deck[2].EnchantmentAmount, deck[2].FloorAdded));
    }

    [Fact]
    public void Act_floor_and_map()
    {
        ParsedSave s = RunSaveParser.Parse(CoopJson, TestPaths.Me);
        Assert.Equal(2, s.State.Act);
        Assert.Equal("ACT.HIVE", s.ActId);
        // 16 floors in act 1 + 3 visited coords in act 2 (the room being entered has no history entry yet).
        Assert.Equal(19, s.State.Floor);

        MapGraph map = Assert.IsType<MapGraph>(s.Map);
        Assert.Equal(2, map.Act);
        Assert.Equal(7, map.Nodes.Count); // start + 5 points + boss
        Assert.Equal(new MapCoord(3, 0), map.Start);
        Assert.Equal(new MapCoord(3, 4), map.Boss);
        Assert.Equal(new MapCoord(3, 2), map.Current);
        Assert.Equal(MapNodeType.Shop, map.Nodes[new MapCoord(3, 2)].Type);
        Assert.Equal([new MapCoord(2, 1), new MapCoord(4, 1)], map.Nodes[map.Start].Children);
        Assert.Empty(map.Nodes[map.Boss].Children);
    }

    [Fact]
    public void Removals_from_extra_fields_else_from_shop_history()
    {
        // Me: no extra_fields → count shop removals in history; the shop was a "?" node (room_type shop).
        Assert.Equal(1, RunSaveParser.Parse(CoopJson, TestPaths.Me).State.CardRemovalsBought);
        // Friend: extra_fields.card_shop_removals_used wins.
        Assert.Equal(3, RunSaveParser.Parse(CoopJson, TestPaths.Friend).State.CardRemovalsBought);
    }

    [Fact]
    public void Extras_odds_potions_seed()
    {
        ParsedSave s = RunSaveParser.Parse(CoopJson, TestPaths.Me);
        Assert.Equal("HANDMADE01", s.Seed);
        Assert.Equal(16, s.SchemaVersion);
        Assert.Equal(0.07, s.CardRarityOffset);
        Assert.Equal(0.4, s.PotionRewardOdds);
        Assert.Equal(0.03, s.UnknownMapPointOdds["unknown_map_point_shop_odds_value"]);
        Assert.Equal([new PotionSlot("POTION.CLARITY", 1)], s.Potions);
        Assert.Equal(2, s.MaxPotionSlots);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794137138), s.SaveTime);
    }

    [Fact]
    public void Single_player_ignores_net_id_and_has_no_map_without_saved_map()
    {
        ParsedSave s = RunSaveParser.Parse(SingleJson, localNetId: 76561190000000001);
        Assert.Equal(1UL, s.MyNetId);
        Assert.False(s.State.IsMultiplayer);
        Assert.Null(s.Map);
        Assert.Equal(1, s.State.Floor); // never below 1
        Assert.Equal(2, s.State.CardRemovalsBought);
        Assert.Equal(["RELIC.BURNING_BLOOD"], s.State.Relics);
    }

    [Fact]
    public void Truncated_json_throws_so_the_watcher_can_retry() =>
        Assert.ThrowsAny<JsonException>(() => RunSaveParser.Parse(CoopJson[..200], TestPaths.Me));
}
