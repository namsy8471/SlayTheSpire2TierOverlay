using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

public class NeowOptionsTests
{
    /// <summary>
    /// Every v0.107.1 run in a real history folder (<c>ancient_choice</c> of the first map point lists the generated options
    /// in button order): seed, slot, players → the three relics. 16 single-player + 18 co-op player slots; all unlocked.
    /// </summary>
    public static TheoryData<string, int, int, string[]> HistoryRuns => new()
    {
        { "AKNC9YS1L3", 0, 1, ["LAVA_ROCK", "NUTRITIOUS_OYSTER", "HEFTY_TABLET"] },
        { "XM79D8HG16", 0, 1, ["BOOMING_CONCH", "LAVA_ROCK", "PRECARIOUS_SHEARS"] },
        { "3FCYMZNLDZ", 0, 1, ["NEW_LEAF", "KALEIDOSCOPE", "LARGE_CAPSULE"] },
        { "R9REQLA5XL", 0, 1, ["LEAD_PAPERWEIGHT", "NEOWS_TORMENT", "LEAFY_POULTICE"] },
        { "DSK3MDCHPH", 0, 1, ["FISHING_ROD", "WINGED_BOOTS", "LARGE_CAPSULE"] },
        { "77V3X5AF52", 0, 1, ["KALEIDOSCOPE", "STONE_HUMIDIFIER", "PRECARIOUS_SHEARS"] },
        { "H6VRKY8PFE", 0, 1, ["BOOMING_CONCH", "POMANDER", "HEFTY_TABLET"] },
        { "ZG50SGBCVV", 0, 1, ["LEAD_PAPERWEIGHT", "NEOWS_TORMENT", "LARGE_CAPSULE"] },
        { "AWDNRQE2RS", 0, 1, ["GOLDEN_PEARL", "SCROLL_BOXES", "LARGE_CAPSULE"] },
        { "JNYBPHT15H", 0, 1, ["WINGED_BOOTS", "LEAD_PAPERWEIGHT", "SILVER_CRUCIBLE"] },
        { "1NNYM78VXU", 0, 1, ["ARCANE_SCROLL", "KALEIDOSCOPE", "NEOWS_BONES"] },
        { "JQ57XZSB5X", 0, 1, ["NEW_LEAF", "KALEIDOSCOPE", "SILKEN_TRESS"] },
        { "P6T0GFM7EL", 0, 1, ["LEAD_PAPERWEIGHT", "LOST_COFFER", "PRECARIOUS_SHEARS"] },
        { "BYF4VFC70G", 0, 1, ["KALEIDOSCOPE", "LEAD_PAPERWEIGHT", "CURSED_PEARL"] },
        { "LX6BJDX882", 0, 1, ["PHIAL_HOLSTER", "NEOWS_TORMENT", "CURSED_PEARL"] },
        { "ETPB3KS9F8", 0, 1, ["LEAD_PAPERWEIGHT", "WINGED_BOOTS", "LARGE_CAPSULE"] },
        { "ZSN877JR14", 0, 2, ["NEW_LEAF", "NUTRITIOUS_OYSTER", "CURSED_PEARL"] },
        { "ZSN877JR14", 1, 2, ["LOST_COFFER", "SCROLL_BOXES", "PRECARIOUS_SHEARS"] },
        { "VRKW7CVYCB", 0, 2, ["NEOWS_TORMENT", "LOST_COFFER", "CURSED_PEARL"] },
        { "VRKW7CVYCB", 1, 2, ["NEOWS_TORMENT", "LEAD_PAPERWEIGHT", "NEOWS_BONES"] },
        { "16SWKJ2YDN", 0, 2, ["FISHING_ROD", "NEW_LEAF", "LARGE_CAPSULE"] },
        { "16SWKJ2YDN", 1, 2, ["KALEIDOSCOPE", "FISHING_ROD", "HEFTY_TABLET"] },
        { "95D4EM5N4P", 0, 2, ["BOOMING_CONCH", "POMANDER", "CURSED_PEARL"] },
        { "95D4EM5N4P", 1, 2, ["NEW_LEAF", "LEAD_PAPERWEIGHT", "SILKEN_TRESS"] },
        { "9JT41XU1VJ", 0, 2, ["FISHING_ROD", "LEAD_PAPERWEIGHT", "PRECARIOUS_SHEARS"] },
        { "9JT41XU1VJ", 1, 2, ["STONE_HUMIDIFIER", "GOLDEN_PEARL", "SILKEN_TRESS"] },
        { "K3E07209SX", 0, 2, ["NEOWS_TORMENT", "BOOMING_CONCH", "HEFTY_TABLET"] },
        { "K3E07209SX", 1, 2, ["PRECISE_SCISSORS", "ARCANE_SCROLL", "LARGE_CAPSULE"] },
        { "6F02PZVWCV", 0, 2, ["GOLDEN_PEARL", "PHIAL_HOLSTER", "PRECARIOUS_SHEARS"] },
        { "6F02PZVWCV", 1, 2, ["NEW_LEAF", "ARCANE_SCROLL", "NEOWS_BONES"] },
        { "6QALZF9K3L", 0, 2, ["PRECISE_SCISSORS", "NUTRITIOUS_OYSTER", "LEAFY_POULTICE"] },
        { "6QALZF9K3L", 1, 2, ["KALEIDOSCOPE", "LEAD_PAPERWEIGHT", "HEFTY_TABLET"] },
        { "EE91F4NNKT", 0, 2, ["PHIAL_HOLSTER", "MASSIVE_SCROLL", "NEOWS_BONES"] },
        { "EE91F4NNKT", 1, 2, ["LEAD_PAPERWEIGHT", "NEOWS_TORMENT", "NEOWS_BONES"] },
    };

    [Theory]
    [MemberData(nameof(HistoryRuns))]
    public void Replays_the_options_the_game_offered(string seed, int slot, int players, string[] expected)
    {
        Assert.Equal(expected, NeowOptions.Relics(seed, slot, players, allCharactersUnlocked: true));
    }

    [Fact]
    public void Keys_are_the_event_text_keys_in_button_order()
    {
        Assert.Equal(
            ["NEOW.pages.INITIAL.options.LAVA_ROCK", "NEOW.pages.INITIAL.options.NUTRITIOUS_OYSTER", "NEOW.pages.INITIAL.options.HEFTY_TABLET"],
            NeowOptions.Keys("AKNC9YS1L3", 0, 1, true));
    }

    private static IEnumerable<string> Seeds(int n)
    {
        const string chars = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var r = new Random(7);
        for (int i = 0; i < n; i++) yield return new string(Enumerable.Range(0, 10).Select(_ => chars[r.Next(chars.Length)]).ToArray());
    }

    private static readonly string[] Curses =
        ["CURSED_PEARL", "HEFTY_TABLET", "LARGE_CAPSULE", "LEAFY_POULTICE", "NEOWS_BONES", "PRECARIOUS_SHEARS", "SILKEN_TRESS", "SILVER_CRUCIBLE"];

    [Fact]
    public void Draws_respect_the_player_count_unlocks_and_twin_rules()
    {
        foreach (var seed in Seeds(2000))
        {
            var solo = NeowOptions.Relics(seed, 0, 1, allCharactersUnlocked: false);
            var coop = NeowOptions.Relics(seed, 1, 2, allCharactersUnlocked: true);
            foreach (var o in new[] { solo, coop })
            {
                Assert.Equal(3, o.Count);
                Assert.Equal(3, o.Distinct().Count());
                Assert.Contains(o[2], Curses);
                Assert.DoesNotContain(o[0], Curses);
                Assert.DoesNotContain(o[1], Curses);
                // A curse relic keeps its positive twin out; Large Capsule keeps both capsule-slot relics out.
                if (o[2] == "CURSED_PEARL") Assert.DoesNotContain("GOLDEN_PEARL", o);
                if (o[2] == "HEFTY_TABLET") Assert.DoesNotContain("ARCANE_SCROLL", o);
                if (o[2] == "LEAFY_POULTICE") Assert.DoesNotContain("NEW_LEAF", o);
                if (o[2] == "PRECARIOUS_SHEARS") Assert.DoesNotContain("PRECISE_SCISSORS", o);
                if (o[2] == "LARGE_CAPSULE") { Assert.DoesNotContain("LAVA_ROCK", o); Assert.DoesNotContain("SMALL_CAPSULE", o); }
            }
            Assert.DoesNotContain("MASSIVE_SCROLL", solo);
            Assert.DoesNotContain("KALEIDOSCOPE", solo);
            Assert.DoesNotContain("WINGED_BOOTS", coop);
            Assert.DoesNotContain("SILVER_CRUCIBLE", coop);
        }
    }

    [Fact]
    public void Kaleidoscope_unlock_changes_some_draws()
    {
        // Removing Kaleidoscope shifts the shuffle, so the unlock state must be read, not guessed.
        Assert.Contains(Seeds(500), s => !NeowOptions.Relics(s, 0, 1, true).SequenceEqual(NeowOptions.Relics(s, 0, 1, false)));
    }

    [Fact]
    public void Possible_keys_are_the_pool_this_run_can_draw_from()
    {
        var solo = NeowOptions.PossibleKeys(1, allCharactersUnlocked: true);
        var coop = NeowOptions.PossibleKeys(2, allCharactersUnlocked: false);
        Assert.Equal(27, solo.Count); // 28 minus Massive Scroll
        Assert.DoesNotContain("NEOW.pages.INITIAL.options.MASSIVE_SCROLL", solo);
        Assert.Contains("NEOW.pages.INITIAL.options.WINGED_BOOTS", solo);
        Assert.Equal(25, coop.Count); // minus Winged Boots, Silver Crucible, Kaleidoscope
        Assert.DoesNotContain("NEOW.pages.INITIAL.options.KALEIDOSCOPE", coop);
        // Every replayed draw comes from that pool.
        foreach (var seed in Seeds(300))
        {
            Assert.All(NeowOptions.Keys(seed, 0, 1, true), k => Assert.Contains(k, solo));
            Assert.All(NeowOptions.Keys(seed, 1, 2, false), k => Assert.Contains(k, coop));
        }
    }

    [Fact]
    public void Slot_moves_the_stream()
    {
        Assert.Equal(NeowOptions.StreamSeed("ZSN877JR14", 0) + 1, NeowOptions.StreamSeed("ZSN877JR14", 1));
    }

    [Theory]
    [InlineData("""{"epochs":[{"id":"SILENT1_EPOCH","state":"revealed"},{"id":"REGENT1_EPOCH","state":"revealed"},{"id":"NECROBINDER1_EPOCH","state":"revealed"},{"id":"DEFECT1_EPOCH","state":"revealed"},{"id":"NECROBINDER2_EPOCH","state":"not_obtained"}]}""", true)]
    [InlineData("""{"epochs":[{"id":"SILENT1_EPOCH","state":"revealed"},{"id":"REGENT1_EPOCH","state":"revealed"},{"id":"NECROBINDER1_EPOCH","state":"obtained"},{"id":"DEFECT1_EPOCH","state":"revealed"}]}""", false)]
    [InlineData("""{"epochs":[]}""", false)]
    [InlineData("""{"schema_version":1}""", null)]
    [InlineData("""not json""", null)]
    public void Progress_save_tells_whether_every_character_is_unlocked(string json, bool? expected)
    {
        Assert.Equal(expected, ProgressSave.AllCharactersUnlocked(json));
    }
}
