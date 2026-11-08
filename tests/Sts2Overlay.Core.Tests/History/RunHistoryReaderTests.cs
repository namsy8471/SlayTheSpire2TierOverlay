using System.Text;
using System.Text.Json;
using Sts2Overlay.Core.History;

namespace Sts2Overlay.Core.Tests.History;

public class RunHistoryReaderTests
{
    public static IEnumerable<object[]> Runs => TestPaths.RunFileNames;

    [Fact]
    public void FixtureSetIsComplete()
    {
        Assert.Equal(24, TestPaths.AllRuns.Count);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public void EveryFixtureParses(string file)
    {
        var run = RunHistoryReader.ReadFile(TestPaths.Run(file));

        Assert.Contains(run.SchemaVersion, new[] { 8, 9 });
        Assert.StartsWith("v0.", run.BuildId);
        Assert.NotEmpty(run.Acts);
        Assert.NotEmpty(run.Players);
        Assert.NotEmpty(run.Nodes);
        for (var i = 0; i < run.Nodes.Count; i++)
        {
            var node = run.Nodes[i];
            Assert.Equal(i + 1, node.Floor);
            Assert.NotEmpty(node.MapPointType);
            Assert.Equal(run.Players.Count, node.Stats.Count);
            foreach (var p in run.Players)
            {
                Assert.NotNull(node.StatsFor(p.Id));
            }
        }

        foreach (var p in run.Players)
        {
            Assert.StartsWith("CHARACTER.", p.Character);
            Assert.NotEmpty(p.Deck);
            Assert.All(p.Deck, c => Assert.StartsWith("CARD.", c.Id));
            Assert.All(p.Relics, r => Assert.StartsWith("RELIC.", r.Id));
        }
    }

    [Fact]
    public void SinglePlayerIroncladRun()
    {
        var run = RunHistoryReader.ReadFile(TestPaths.Run("1794130978.run"));

        Assert.Equal(9, run.SchemaVersion);
        Assert.Equal("v0.107.1", run.BuildId);
        Assert.False(run.IsMultiplayer);
        Assert.False(run.Win);
        Assert.Equal("ENCOUNTER.VANTOM_BOSS", run.KilledByEncounter);
        Assert.Equal(new[] { "ACT.OVERGROWTH", "ACT.HIVE", "ACT.GLORY" }, run.Acts);
        Assert.Equal(17, run.Nodes.Count);

        var me = LocalPlayer.Resolve(run);
        Assert.Equal(1, me.Id);
        Assert.Equal("CHARACTER.IRONCLAD", me.Character);
        Assert.Equal(17, me.Deck.Count);
        Assert.Contains(me.Relics, r => r.Id == "RELIC.BURNING_BLOOD" && r.FloorAdded == 1);

        var fightMe = Assert.Single(me.Deck, c => c.Id == "CARD.FIGHT_ME");
        Assert.Equal(1, fightMe.Upgrade);

        var first = run.Nodes[0];
        Assert.Equal("ancient", first.MapPointType);
        Assert.Equal("EVENT.NEOW", first.Rooms[0].ModelId);
        var s0 = first.StatsFor(1)!;
        Assert.Equal(2, s0.CardsTransformed.Count);
        Assert.Equal("CARD.STRIKE_IRONCLAD", s0.CardsTransformed[0].Original.Id);
        Assert.Equal("CARD.FLAME_BARRIER", s0.CardsTransformed[0].Final.Id);
        Assert.Contains(s0.RelicChoices, c => c.Id == "RELIC.LEAFY_POULTICE" && c.WasPicked);

        var s1 = run.Nodes[1].StatsFor(1)!;
        Assert.Equal(3, s1.CardChoices.Count);
        Assert.True(s1.CardChoices[0].WasPicked);
        Assert.Equal("CARD.ANGER", s1.CardChoices[0].Card.Id);
        Assert.Equal(2, s1.CardChoices[0].Card.FloorAdded);
        Assert.Equal(0, s1.CardChoices[1].Card.FloorAdded);
        Assert.Equal("CARD.ANGER", Assert.Single(s1.CardsGained).Id);
        Assert.Equal(68, s1.CurrentHp);
        Assert.Equal(110, s1.CurrentGold);
        Assert.Equal(11, s1.GoldGained);
    }

    [Fact]
    public void MultiplayerIroncladRun()
    {
        var run = RunHistoryReader.ReadFile(TestPaths.Run("1773379374.run"));

        Assert.Equal(8, run.SchemaVersion);
        Assert.True(run.IsMultiplayer);
        Assert.True(run.Win);
        Assert.Equal(45, run.Nodes.Count);
        Assert.Equal(new long[] { LocalPlayer.FixtureSelfId, 76561190000000002 }, run.Players.Select(p => p.Id));
        Assert.All(run.Players, p => Assert.Equal("CHARACTER.IRONCLAD", p.Character));
        Assert.Equal(LocalPlayer.FixtureSelfId, LocalPlayer.Resolve(run).Id);
        Assert.Equal(76561190000000002, LocalPlayer.Resolve(run, 76561190000000002).Id);
        Assert.Equal(LocalPlayer.FixtureSelfId, LocalPlayer.Resolve(run, 12345).Id);

        // Act boundaries: 16 + 15 + 14 nodes.
        Assert.Equal(16, run.Nodes.Count(n => n.ActIndex == 0));
        var act2First = run.Nodes.First(n => n.ActIndex == 1);
        Assert.Equal(17, act2First.Floor);
        Assert.Equal(0, act2First.IndexInAct);
        Assert.Equal(2, act2First.Act);
        Assert.Equal(31, run.Nodes.Last(n => n.ActIndex == 1).Floor);
        Assert.Equal(13, run.Nodes[^1].IndexInAct);

        var enchanted = run.Nodes.SelectMany(n => n.Stats).SelectMany(s => s.CardsEnchanted).First();
        Assert.Equal("CARD.SETUP_STRIKE", enchanted.Id);
        Assert.Equal("ENCHANTMENT.SOWN", enchanted.EnchantmentId);
        Assert.Equal(1, enchanted.EnchantmentAmount);
    }

    [Fact]
    public void ShopInsideUnknownPointIsDetected()
    {
        var shops = TestPaths.AllRuns
            .Select(RunHistoryReader.ReadFile)
            .SelectMany(r => r.Nodes)
            .Where(n => n.MapPointType == "unknown" && n.HasShop)
            .ToList();
        Assert.NotEmpty(shops);
    }

    [Fact]
    public void ToleratesMissingAndUnknownFields()
    {
        const string json = """
        {
          "schema_version": 9,
          "something_new": {"x": [1, 2]},
          "ascension": "not a number",
          "players": [ { "id": 1, "character": "CHARACTER.DEFECT", "deck": [ {"id": "CARD.ZAP"}, {"id": "CARD.DUALCAST", "current_upgrade_level": 1, "enchantment": {"id": "ENCHANTMENT.X", "amount": 2}} ] } ],
          "map_point_history": [
            [ { "map_point_type": "monster", "player_stats": [ { "player_id": 1, "card_choices": [ {"card": {"id": "CARD.BEAM_CELL"}}, {"was_picked": true} ] } ] } ],
            [ { } ]
          ]
        }
        """;

        var run = RunHistoryReader.Parse(json);

        Assert.Equal(9, run.SchemaVersion);
        Assert.Equal(0, run.Ascension);
        Assert.Equal("", run.Seed);
        Assert.Empty(run.Acts);
        Assert.Null(run.KilledByEncounter);
        Assert.Equal(2, run.Nodes.Count);
        Assert.Equal(2, run.Nodes[1].Floor);
        Assert.Equal(1, run.Nodes[1].ActIndex);
        Assert.Empty(run.Nodes[1].Stats);

        var p = Assert.Single(run.Players);
        Assert.Empty(p.Relics);
        Assert.Equal(1, p.Deck[1].Upgrade);
        Assert.Equal("ENCHANTMENT.X", p.Deck[1].EnchantmentId);
        Assert.Equal(2, p.Deck[1].EnchantmentAmount);

        var choice = Assert.Single(run.Nodes[0].Stats[0].CardChoices);
        Assert.False(choice.WasPicked);
        Assert.Equal(0, choice.Card.FloorAdded);
    }

    [Fact]
    public void ReadFromStreamMatchesReadFile()
    {
        var path = TestPaths.Run("1794130978.run");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path)));
        var a = RunHistoryReader.Read(stream);
        var b = RunHistoryReader.ReadFile(path);
        Assert.Equal(b.Nodes.Count, a.Nodes.Count);
        Assert.Equal(b.Players[0].Deck, a.Players[0].Deck);
    }

    [Fact]
    public void InvalidJsonThrows()
    {
        Assert.ThrowsAny<JsonException>(() => RunHistoryReader.Parse("{ not json"));
        Assert.ThrowsAny<JsonException>(() => RunHistoryReader.Parse("[1, 2]"));
    }

    [Fact]
    public void ResolveWithoutPlayersThrows()
    {
        Assert.Throws<InvalidOperationException>(() => LocalPlayer.Resolve(new RunHistory()));
    }
}
