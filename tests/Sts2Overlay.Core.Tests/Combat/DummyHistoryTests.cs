using System.Text.Json;
using Sts2Overlay.Core.Combat;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Xunit.Abstractions;

namespace Sts2Overlay.Core.Tests.Combat;

/// <summary>
/// The training dummy fights in the recorded runs (tests/fixtures/runs): the dummy's HP the game showed, and whether the
/// players killed it, against the HP rule and the simulator's forecast for the decks they had at that floor.
/// </summary>
public class DummyHistoryTests(ITestOutputHelper output)
{
    private static readonly Lazy<CombatData> Data = new(() => CombatData.LoadPack(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1"))!);

    private sealed record DummyFightRecord(string Run, int NodeIndex, int ActIndex, int Setting, IReadOnlyDictionary<string, double> ShownHp,
        IReadOnlyList<RunState> Decks, bool Won);

    private static IEnumerable<DummyFightRecord> Fights()
    {
        foreach (var path in TestPaths.AllRuns)
        {
            var json = File.ReadAllText(path);
            if (!json.Contains("EVENT.BATTLEWORN_DUMMY", StringComparison.Ordinal)) continue;
            var run = RunHistoryReader.Parse(json);
            using var doc = JsonDocument.Parse(json);
            var points = doc.RootElement.GetProperty("map_point_history").EnumerateArray().SelectMany(a => a.EnumerateArray()).ToList();
            for (var i = 0; i < run.Nodes.Count; i++)
            {
                var node = run.Nodes[i];
                if (!node.Rooms.Any(r => r.ModelId == DummyFight.EventId)) continue;
                // The event choice and the dummy HP it showed (the variables are on one player's entry).
                var choices = points[i].GetProperty("player_stats").EnumerateArray()
                    .SelectMany(s => s.TryGetProperty("event_choices", out var c) ? c.EnumerateArray() : Enumerable.Empty<JsonElement>())
                    .ToList();
                var key = choices[0].GetProperty("title").GetProperty("key").GetString()!;
                var setting = key.Contains("SETTING_1") ? 1 : key.Contains("SETTING_2") ? 2 : 3;
                var vars = choices.Where(c => c.TryGetProperty("variables", out _))
                    .Select(c => c.GetProperty("variables").EnumerateObject().ToDictionary(v => v.Name, v => v.Value.GetProperty("decimal_value").GetDouble()))
                    .First();
                var decks = run.Players.Select(p => DeckTimeline.Build(run, p.Id).StateBefore(i)).ToList();
                // The reward is the proof of a kill: 2 upgrades, a relic, a potion.
                var won = node.Stats.Any(s => setting switch
                {
                    1 => s.PotionChoices.Any(c => c.WasPicked),
                    2 => s.UpgradedCards.Count > 0,
                    _ => s.RelicChoices.Any(c => c.WasPicked),
                });
                yield return new DummyFightRecord(Path.GetFileName(path), i, node.ActIndex, setting, vars, decks, won);
            }
        }
    }

    [Fact]
    public void RecordedDummyHp_MatchesTheScalingRule()
    {
        var fights = Fights().ToList();
        Assert.NotEmpty(fights);
        foreach (var f in fights)
        {
            foreach (var (name, baseHp) in new[] { ("Setting1Hp", 75.0), ("Setting2Hp", 150.0), ("Setting3Hp", 300.0) })
            {
                Assert.Equal(f.ShownHp[name], DummyFight.ScaleHp(baseHp, f.Decks.Count, f.ActIndex));
            }
        }
    }

    private static IReadOnlyList<SimPlayer> Players(DummyFightRecord f)
        => f.Decks.Select(s => new SimPlayer { Character = s.Character, Deck = s.Deck, Relics = s.Relics }).ToList();

    /// <summary>
    /// The forecast the advisor shows, against what happened. A fight is one draw from the forecast, so the check is
    /// one-sided where it can be: a fight the players lost must not have looked likely (P &lt; 50%, the advisor's "처치
    /// 어려움" line), and a fight they won must have been possible in the simulation (P &gt; 0). The simulator plans
    /// worse than a good player (no multi-turn combo set-up), so a won fight with a low P is the expected kind of miss;
    /// the output lists every fight with its P for that reason.
    /// </summary>
    [Fact]
    public void RecordedOutcomes_AreWithinTheForecast()
    {
        var sim = new TurnSimulator(Data.Value);
        var fights = Fights().ToList();
        Assert.NotEmpty(fights);
        var wrong = new List<string>();
        foreach (var f in fights)
        {
            var forecast = DummyFight.Forecast(sim, Players(f));
            var hp = f.ShownHp[$"Setting{f.Setting}Hp"];
            var p = forecast.ProbAtLeast(hp);
            var cov = forecast.Coverage;
            output.WriteLine($"{f.Run} node {f.NodeIndex}: {string.Join(" + ", f.Decks.Select(d => $"{d.Character} {d.Deck.Count} cards"))}, setting {f.Setting} hp {hp}: "
                             + $"mean {forecast.Mean:0} (p10 {forecast.Quantile(0.1):0}, p90 {forecast.Quantile(0.9):0}) per turn {string.Join("/", forecast.MeanPerTurn.Select(t => t.ToString("0")))}, "
                             + $"P(kill) {p:0.00}, actual {(f.Won ? "killed" : "escaped")}; approx {cov.ApproxCards.Count}, unknown {cov.UnknownCards.Count}");
            if (!f.Won && p >= 0.5) wrong.Add($"{f.Run}: escaped, but the forecast said P {p:0.00}");
            if (f.Won && p <= 0) wrong.Add($"{f.Run}: killed, but no simulated fight reached {hp}");
        }
        Assert.Empty(wrong);
    }

    /// <summary>Trying cards out ahead (rollouts) must never play worse than the plain greedy choice it builds on.</summary>
    [Fact]
    public void Lookahead_DealsAtLeastAsMuchAsGreedy()
    {
        var sim = new TurnSimulator(Data.Value);
        foreach (var f in Fights())
        {
            foreach (var player in Players(f))
            {
                var greedy = sim.Forecast(player, new SimOptions { Iterations = 300 });
                var lookahead = sim.Forecast(player, new SimOptions { Iterations = 300, Rollouts = 2 });
                output.WriteLine($"{f.Run} {player.Character}: greedy {greedy.Mean:0}, look-ahead {lookahead.Mean:0}");
                Assert.True(lookahead.Mean >= greedy.Mean, $"{f.Run} {player.Character}: look-ahead {lookahead.Mean:0} < greedy {greedy.Mean:0}");
            }
        }
    }

    [Fact]
    public void Lookahead_IsRepeatable()
    {
        var f = Fights().First();
        var sim = new TurnSimulator(Data.Value);
        var a = DummyFight.Forecast(sim, Players(f));
        var b = DummyFight.Forecast(sim, Players(f));
        Assert.Equal(a.Totals, b.Totals);
    }
}
