using System.Text.RegularExpressions;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Xunit.Abstractions;

namespace Sts2Overlay.Core.Tests.History;

public class DeckTimelineTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Runs => TestPaths.RunFileNames;

    private static RunHistory Load(string file) => RunHistoryReader.ReadFile(TestPaths.Run(file));

    private static string[] Multiset(IEnumerable<DeckCard> deck) =>
        deck.Select(c => c.Card.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    [Theory]
    [MemberData(nameof(Runs))]
    public void TimelineIsConsistentForEveryPlayer(string file)
    {
        var run = Load(file);
        foreach (var player in run.Players)
        {
            var t = DeckTimeline.Build(run, player.Id);
            var last = run.Nodes.Count - 1;

            Assert.Equal(player.Character, t.Character);
            Assert.Equal(Multiset(player.Deck), Multiset(t.DeckAfter(last)));
            Assert.Equal(Multiset(player.Deck), Multiset(t.DeckBefore(run.Nodes.Count)));
            for (var i = 0; i < last; i++)
            {
                Assert.Equal(Multiset(t.DeckAfter(i)), Multiset(t.DeckBefore(i + 1)));
            }

            var start = t.StartingDeck;
            Assert.InRange(start.Count, 9, 13);
            Assert.Contains(start, c => c.Id.StartsWith("CARD.STRIKE_", StringComparison.Ordinal));
            Assert.Contains(start, c => c.Id.StartsWith("CARD.DEFEND_", StringComparison.Ordinal));
            Assert.All(start, c => Assert.Equal(0, c.Upgrade));
            Assert.Equal(Multiset(start), Multiset(t.DeckBefore(0)));
        }
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public void StateBeforeIsPlausible(string file)
    {
        var run = Load(file);
        foreach (var player in run.Players)
        {
            var t = DeckTimeline.Build(run, player.Id);
            for (var i = 0; i < run.Nodes.Count; i++)
            {
                var s = t.StateBefore(i);
                Assert.Equal(player.Character, s.Character);
                Assert.Equal(run.Ascension, s.Ascension);
                Assert.Equal(run.Nodes[i].Floor, s.Floor);
                Assert.Equal(run.Nodes[i].Act, s.Act);
                Assert.Equal(run.IsMultiplayer, s.IsMultiplayer);
                Assert.Same(t.DeckBefore(i), s.Deck);
                Assert.True(s.MaxHp > 0, $"node {i}: max hp {s.MaxHp}");
                Assert.InRange(s.Hp, 0, s.MaxHp);
                Assert.True(s.Gold >= 0);
                Assert.NotEmpty(s.Relics);
                if (i > 0)
                {
                    Assert.True(s.CardRemovalsBought >= t.StateBefore(i - 1).CardRemovalsBought);
                }
            }

            // Every final relic is held before the last node unless it was obtained there.
            var lastFloor = run.Nodes[^1].Floor;
            var held = t.StateBefore(run.Nodes.Count - 1).Relics;
            Assert.All(player.Relics.Where(r => r.FloorAdded < lastFloor), r => Assert.Contains(r.Id, held));
        }
    }

    [Fact]
    public void SinglePlayerIroncladStartsWithStandardDeck()
    {
        var run = Load("1794130978.run");
        var t = DeckTimeline.Build(run, 1);

        Assert.Empty(t.Warnings);
        Assert.Equal(
            new[] { "CARD.BASH", "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD",
                    "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD" },
            Multiset(t.StartingDeck));

        // Neow transformed a Strike and a Defend.
        var afterNeow = Multiset(t.DeckAfter(0));
        Assert.Contains("CARD.FLAME_BARRIER", afterNeow);
        Assert.Contains("CARD.FIGHT_ME", afterNeow);
        Assert.Equal(4, afterNeow.Count(c => c == "CARD.STRIKE_IRONCLAD"));

        // Floor 2 card reward added Anger.
        Assert.DoesNotContain(t.DeckBefore(1), c => c.Id == "CARD.ANGER");
        Assert.Contains(t.DeckAfter(1), c => c.Id == "CARD.ANGER" && c.FloorAdded == 2);
    }

    [Fact]
    public void StateBeforeTracksVitalsRelicsAndRemovals()
    {
        var run = Load("1794130978.run");
        var t = DeckTimeline.Build(run, 1);

        var s0 = t.StateBefore(0);
        Assert.Equal(80, s0.MaxHp);
        Assert.Equal(80, s0.Hp);
        Assert.Equal(99, s0.Gold);
        Assert.Equal(new[] { "RELIC.BURNING_BLOOD" }, s0.Relics);

        var s1 = t.StateBefore(1);
        Assert.Equal(2, s1.Floor);
        Assert.Equal(68, s1.Hp);
        Assert.Equal(68, s1.MaxHp);
        Assert.Equal(99, s1.Gold);
        Assert.Equal(new[] { "RELIC.BURNING_BLOOD", "RELIC.LEAFY_POULTICE" }, s1.Relics);

        var vajra = t.StateBefore(9); // floor 10 is where Vajra was obtained
        Assert.DoesNotContain("RELIC.VAJRA", vajra.Relics);
        Assert.Contains("RELIC.VAJRA", t.StateBefore(10).Relics);

        // Removals bought at shops accumulate.
        var multi = Load("1773379374.run");
        var mt = DeckTimeline.Build(multi, LocalPlayer.FixtureSelfId);
        var shopIndex = Enumerable.Range(0, multi.Nodes.Count)
            .First(i => multi.Nodes[i].HasShop && multi.Nodes[i].StatsFor(LocalPlayer.FixtureSelfId)!.CardsRemoved.Count > 0);
        Assert.Equal(0, mt.StateBefore(shopIndex).CardRemovalsBought);
        Assert.Equal(1, mt.StateBefore(shopIndex + 1).CardRemovalsBought);
        Assert.True(mt.StateBefore(multi.Nodes.Count - 1).IsMultiplayer);
    }

    [Fact]
    public void RemovedRelicIsHeldUntilItIsRemoved()
    {
        var run = Load("1773404818.run");
        var (index, playerId) = Enumerable.Range(0, run.Nodes.Count)
            .SelectMany(i => run.Nodes[i].Stats.Where(s => s.RelicsRemoved.Contains("RELIC.CRACKED_CORE")).Select(s => (i, s.PlayerId)))
            .First();
        var t = DeckTimeline.Build(run, playerId);

        Assert.Contains("RELIC.CRACKED_CORE", t.StateBefore(0).Relics);
        Assert.Contains("RELIC.CRACKED_CORE", t.StateBefore(index).Relics);
        Assert.DoesNotContain("RELIC.CRACKED_CORE", t.StateBefore(index + 1).Relics);
    }

    [Fact]
    public void StolenAndReturnedCardCancelsOut()
    {
        // Floor 19: a thief stole RAGE (floor 12) and it came back; the deck must keep it before the node.
        var run = Load("1773379374.run");
        var t = DeckTimeline.Build(run, LocalPlayer.FixtureSelfId);
        Assert.Contains(t.DeckBefore(18), c => c.Id == "CARD.RAGE");
        Assert.Contains(t.DeckBefore(12), c => c.Id == "CARD.RAGE");
        Assert.DoesNotContain(t.DeckBefore(11), c => c.Id == "CARD.RAGE");
    }

    [Fact]
    public void UnmatchedUndoIsWarnedAndWalkContinues()
    {
        const string json = """
        {
          "players": [ { "id": 1, "character": "CHARACTER.DEFECT", "deck": [
            {"id": "CARD.STRIKE_DEFECT", "floor_added_to_deck": 1},
            {"id": "CARD.ZAP", "floor_added_to_deck": 1, "current_upgrade_level": 1},
            {"id": "CARD.COOLHEADED", "floor_added_to_deck": 3} ] } ],
          "map_point_history": [ [
            { "map_point_type": "monster", "player_stats": [ { "player_id": 1, "cards_gained": [ {"id": "CARD.GHOST"} ] } ] },
            { "map_point_type": "rest_site", "player_stats": [ { "player_id": 1, "upgraded_cards": ["CARD.ZAP", "CARD.DUALCAST"] } ] },
            { "map_point_type": "shop", "rooms": [ {"room_type": "shop"} ], "player_stats": [ { "player_id": 1,
                "cards_gained": [ {"id": "CARD.COOLHEADED"} ],
                "cards_removed": [ {"id": "CARD.DEFEND_DEFECT", "floor_added_to_deck": 1} ] } ] }
          ] ]
        }
        """;

        var run = RunHistoryReader.Parse(json);
        var t = DeckTimeline.Build(run, 1);

        Assert.Equal(2, t.Warnings.Count);
        Assert.Contains("floor 1", t.Warnings[0]);
        Assert.Contains("CARD.GHOST", t.Warnings[0]);
        Assert.Contains("floor 2", t.Warnings[1]);
        Assert.Contains("CARD.DUALCAST", t.Warnings[1]);
        Assert.Equal(5, t.UndoOperationCount);
        Assert.Equal(new[] { "CARD.DEFEND_DEFECT", "CARD.STRIKE_DEFECT", "CARD.ZAP" }, Multiset(t.StartingDeck));
        Assert.Equal(new[] { "CARD.DEFEND_DEFECT", "CARD.STRIKE_DEFECT", "CARD.ZAP+1" }, Multiset(t.DeckBefore(2)));
        Assert.Equal(3, t.StateBefore(2).Floor);
        Assert.Equal(0, t.StateBefore(2).CardRemovalsBought);
    }

    [Fact]
    public void UnknownPlayerThrows()
    {
        var run = Load("1794130978.run");
        Assert.Throws<ArgumentException>(() => DeckTimeline.Build(run, 42));
    }

    [Fact]
    public void WarningsStayWithinBound()
    {
        var totalOps = 0;
        var all = new List<string>();
        foreach (var path in TestPaths.AllRuns)
        {
            var run = RunHistoryReader.ReadFile(path);
            foreach (var p in run.Players)
            {
                var t = DeckTimeline.Build(run, p.Id);
                totalOps += t.UndoOperationCount;
                all.AddRange(t.Warnings.Select(w => $"{Path.GetFileName(path)} p{p.Id % 100}: {w}"));
            }
        }

        output.WriteLine($"undo operations: {totalOps}, warnings: {all.Count} ({100.0 * all.Count / totalOps:F2}%)");
        foreach (var group in all.GroupBy(w => Regex.Replace(w, @"^.*?: floor \d+: (\w+).*$", "$1")).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  {group.Key}: {group.Count()}");
        }

        foreach (var w in all)
        {
            output.WriteLine("  " + w);
        }

        Assert.True(totalOps > 500, $"only {totalOps} undo operations");
        Assert.True(all.Count < totalOps * 0.05, $"{all.Count} warnings for {totalOps} undo operations");
    }
}
