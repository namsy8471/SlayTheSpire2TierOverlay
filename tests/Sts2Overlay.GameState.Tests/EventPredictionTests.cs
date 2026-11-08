using System.Text.Json;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;
using Xunit.Abstractions;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// <see cref="EventPrediction"/> against what really happened: every event room in a save's <c>map_point_history</c>
/// is replayed through the same pull loop the live prediction uses, from the act's <c>event_ids</c>, and must give the
/// event the game actually pulled. The final index must equal the save's <c>events_visited</c>.
/// </summary>
public class EventPredictionTests(ITestOutputHelper output)
{
    private static readonly RunState Empty = new() { Character = "CHARACTER.IRONCLAD", Hp = 80, MaxHp = 80, Gold = 99 };

    private static EventPrediction.Facts Facts(int act, int gold = 99, int hp = 80, int maxHp = 80, int floor = 1, RunState? state = null, IReadOnlyList<string>? potions = null)
        => new(act, gold, hp, maxHp, floor, state ?? Empty, potions ?? []);

    [Fact]
    public void Pull_skips_seen_and_disallowed_events_and_stops_on_the_first_allowed_one()
    {
        // Act 1 (index 0): Fake Merchant and Ranwid need act 2+, so the pull walks past them.
        string[] ids = ["EVENT.ABYSSAL_BATHS", "EVENT.FAKE_MERCHANT", "EVENT.RANWID_THE_ELDER", "EVENT.SELF_HELP_BOOK", "EVENT.ROOM_FULL_OF_CHEESE"];
        var g = EventPrediction.Pull(ids, 1, new HashSet<string>(), Facts(0), out int next);
        Assert.Equal("EVENT.SELF_HELP_BOOK", g.EventId);
        Assert.True(g.Certain);
        Assert.Equal(4, next);

        g = EventPrediction.Pull(ids, 4, new HashSet<string> { "EVENT.SELF_HELP_BOOK" }, Facts(0), out next);
        Assert.Equal("EVENT.ROOM_FULL_OF_CHEESE", g.EventId);
        Assert.Equal(5, next);

        // Wraps around past the end: Abyssal Baths at index 0.
        g = EventPrediction.Pull(ids, 5, new HashSet<string> { "EVENT.SELF_HELP_BOOK", "EVENT.ROOM_FULL_OF_CHEESE" }, Facts(0), out next);
        Assert.Equal("EVENT.ABYSSAL_BATHS", g.EventId);
        Assert.Equal(6, next);
    }

    [Fact]
    public void Gold_and_hp_conditions_decide_exactly()
    {
        string[] ids = ["EVENT.TEA_MASTER", "EVENT.COLOSSAL_FLOWER", "EVENT.DENSE_VEGETATION"];
        Assert.Equal("EVENT.TEA_MASTER", EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1, gold: 150), out _).EventId);
        Assert.Equal("EVENT.COLOSSAL_FLOWER", EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1, gold: 149), out _).EventId);
        Assert.Equal("EVENT.DENSE_VEGETATION", EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1, gold: 149, hp: 18), out _).EventId);
    }

    [Fact]
    public void An_approximate_check_names_the_fallback()
    {
        // Relic Trader needs five tradable relics, which the save can't tell exactly: it is skipped but listed.
        string[] ids = ["EVENT.RELIC_TRADER", "EVENT.DENSE_VEGETATION"];
        var g = EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1), out _);
        Assert.Equal("EVENT.DENSE_VEGETATION", g.EventId);
        Assert.False(g.Certain);
        Assert.Equal(["EVENT.RELIC_TRADER"], g.Alternatives);
    }

    [Fact]
    public void Unknown_potions_make_potion_checks_approximate()
    {
        string[] ids = ["EVENT.THE_FUTURE_OF_POTIONS", "EVENT.DENSE_VEGETATION"];
        var known = EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1, potions: ["POTION.A"]), out _);
        Assert.Equal("EVENT.DENSE_VEGETATION", known.EventId);
        Assert.True(known.Certain);

        var unknown = EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1) with { Potions = null }, out _);
        Assert.Equal("EVENT.THE_FUTURE_OF_POTIONS", unknown.EventId);
        Assert.False(unknown.Certain);
    }

    /// <summary>Saves on this machine with the event lists (frozen test capture + the live run); missing ones are skipped.</summary>
    private static IEnumerable<string> RealSaves()
    {
        var frozen = Path.Combine(TestPaths.RepoRoot, "data", "local", "vision-robust", "frozen", "saves", "current_run.save");
        if (File.Exists(frozen)) yield return frozen;
        var saves = GamePaths.Discover(TestPaths.RealRoot).SavesDirectory;
        if (saves is null) yield break;
        foreach (var name in new[] { "current_run.save", "current_run.save.backup" })
        {
            var p = Path.Combine(saves, name);
            if (File.Exists(p)) yield return p;
        }
    }

    [Fact]
    public void Replaying_real_saves_gives_every_pulled_event()
    {
        int pulls = 0, exact = 0, files = 0;
        foreach (var path in RealSaves())
        {
            var json = File.ReadAllText(path);
            var parsed = RunSaveParser.Parse(json, null);
            if (parsed.PlayerNetIds.Count > 1) continue; // co-op saves: the log names the event
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("events_seen", out _)) continue; // older schema
            files++;

            var r = Replay(root, parsed.State);
            foreach (var line in r.Lines) output.WriteLine($"{Path.GetFileName(path)}: {line}");
            pulls += r.Pulls;
            exact += r.Exact;
            Assert.True(r.Consistent, $"{path}: a pulled event contradicts the pull loop");
            Assert.True(r.IndexesMatch, $"{path}: replayed events_visited differs from the save");
        }
        output.WriteLine($"{files} saves, {pulls} event pulls, {exact} predicted exactly");
        Assert.Equal(pulls, exact);
    }

    private sealed record ReplayResult(int Pulls, int Exact, bool Consistent, bool IndexesMatch, List<string> Lines);

    /// <summary>
    /// Walks <c>map_point_history</c>: gold/HP come from the entry before each event room (what the room-entry save
    /// held), potions are unknown, deck and relics are the save's current ones.
    /// </summary>
    private static ReplayResult Replay(JsonElement root, RunState state)
    {
        var lines = new List<string>();
        var history = root.GetProperty("map_point_history");
        var acts = root.GetProperty("acts");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int pulls = 0, exact = 0, floor = 0;
        bool consistent = true, indexes = true;
        int gold = 99, hp = 80, maxHp = 80;

        for (int a = 0; a < history.GetArrayLength() && a < acts.GetArrayLength(); a++)
        {
            var rooms = acts[a].GetProperty("rooms");
            var ids = rooms.GetProperty("event_ids").EnumerateArray().Select(e => e.GetString()!).ToList();
            int idx = 0;
            foreach (var entry in history[a].EnumerateArray())
            {
                var type = entry.GetProperty("map_point_type").GetString();
                var room = entry.GetProperty("rooms").EnumerateArray().FirstOrDefault();
                if (type == "ancient") idx++;
                else if (type == "unknown" && room.ValueKind == JsonValueKind.Object && room.GetProperty("room_type").GetString() == "event")
                {
                    var actual = room.GetProperty("model_id").GetString()!;
                    var facts = new EventPrediction.Facts(a, gold, hp, maxHp, floor, state, null);
                    var guess = EventPrediction.Pull(ids, idx, seen, facts, out _);
                    pulls++;
                    if (guess.EventId == actual) exact++;
                    else if (!guess.Alternatives.Contains(actual)) consistent = false;
                    // Where the game really took it from: the next position holding that event.
                    int pos = Enumerable.Range(idx, ids.Count).FirstOrDefault(i => ids[i % ids.Count] == actual, -1);
                    lines.Add($"act {a} floor {floor}: {actual} at {pos} (guess {guess.EventId}{(guess.Certain ? "" : "?")}, gold {gold} hp {hp}/{maxHp})");
                    if (pos < 0) consistent = false; else idx = pos + 1;
                    seen.Add(actual);
                }
                if (entry.TryGetProperty("player_stats", out var ps) && ps.GetArrayLength() > 0)
                {
                    gold = ps[0].GetProperty("current_gold").GetInt32();
                    hp = ps[0].GetProperty("current_hp").GetInt32();
                    maxHp = ps[0].GetProperty("max_hp").GetInt32();
                }
                floor++;
            }
            int stored = rooms.GetProperty("events_visited").GetInt32();
            if (history[a].GetArrayLength() > 0 && stored != idx)
            {
                indexes = false;
                lines.Add($"act {a}: replay ends at {idx}, save says {stored}");
            }
        }
        return new ReplayResult(pulls, exact, consistent, indexes, lines);
    }
}
