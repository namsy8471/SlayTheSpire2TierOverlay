using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary><see cref="EventRandomPages"/>: the Trial's defendant, replayed from the event's stream.</summary>
public class EventRandomPagesTests
{
    [Fact]
    public void The_trial_scenario_is_the_first_draw_of_the_event_stream()
    {
        // Trial.Accept: switch (Rng.NextInt(3)) { 0: MERCHANT, 1: NOBLE, 2: NONDESCRIPT }.
        string[] pages = ["MERCHANT", "NOBLE", "NONDESCRIPT"];
        var seen = new HashSet<string>();
        for (int slot = 0; slot < 40; slot++)
        {
            ulong seed = GameStateTracker.EventRngSeed(RngScheme.Xx64, "ZSN877JR14", slot, EventRandomPages.TrialEventId);
            string page = EventRandomPages.TrialScenario(seed);
            Assert.Equal(pages[new GameRng(seed).NextInt(3)], page);
            seen.Add(page);
        }
        Assert.Equal(3, seen.Count); // every scenario comes up over 40 streams
    }

    [Fact]
    public void Only_the_trial_has_a_replayed_page_and_it_maps_the_placeholder()
    {
        Assert.True(EventRandomPages.Handles("EVENT.TRIAL"));
        Assert.False(EventRandomPages.Handles("EVENT.NEOW"));
        Assert.Null(EventRandomPages.For("EVENT.NEOW", 1));
        var map = EventRandomPages.For("EVENT.TRIAL", 12345)!;
        Assert.Equal(EventRandomPages.TrialScenario(12345), Assert.Single(map).Value);
        Assert.Equal("RANDOM_SCENARIO", map.Keys.Single());
    }

    [Fact]
    public void The_placeholder_is_the_next_page_the_pack_names()
    {
        // The data and the code meet on this name: the pack's Trial must still say RANDOM_SCENARIO on its Accept options.
        string json = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.111.0", "events.json"));
        int at = json.IndexOf("\"TRIAL.pages.INITIAL.options.ACCEPT\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        int next = json.IndexOf("\"next\":", at, StringComparison.Ordinal);
        Assert.StartsWith($"\"next\": \"{EventRandomPages.TrialPlaceholder}\"", json[next..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tinker_time_offers_two_types_and_two_of_each_types_riders(bool childShuffle)
    {
        // TinkerTime.TakeTwo: Rng.Shuffle, then the first two; the riders' shuffle follows the types' on the same stream.
        string[] riders = ["SAPPING", "VIOLENCE", "CHOKING", "ENERGIZED", "WISDOM", "CHAOS", "EXPERTISE", "CURIOUS", "IMPROVEMENT"];
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var map = EventOfferedOptions.For(EventOfferedOptions.TinkerTimeEventId, seed, childShuffle)!;
            var types = map["TINKER_TIME.pages.INITIAL.options.CHOOSE_CARD_TYPE"].Select(k => k.Split('.')[^1]).ToArray();
            Assert.Equal(2, types.Distinct().Count());
            Assert.Equal(3, map.Count);
            foreach (var t in types)
            {
                int start = Array.IndexOf(["ATTACK", "SKILL", "POWER"], t) * 3;
                var offered = map["TINKER_TIME.pages.CHOOSE_CARD_TYPE.options." + t];
                Assert.Equal(2, offered.Distinct().Count());
                Assert.All(offered, k => Assert.Contains(k.Split('.')[^1], riders[start..(start + 3)]));
                Assert.All(offered, k => Assert.StartsWith("TINKER_TIME.pages.CHOOSE_RIDER.options.", k));
            }
        }
        Assert.Null(EventOfferedOptions.For("EVENT.TRIAL", 1, childShuffle));
    }
}
