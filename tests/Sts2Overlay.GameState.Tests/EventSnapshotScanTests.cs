using System.Text.Json;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;
using Xunit.Abstractions;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// Opt-in measurement over save snapshots taken while playing (a script copies <c>current_run.save</c> on every write).
/// Every room-entry save of an event room goes through the live <see cref="EventPrediction.Predict"/>; the truth is the
/// event the run's latest snapshot recorded in <c>map_point_history</c> at that room. Must be 100%.
/// <para>
/// Run with <c>SNAP_DIRS=dir1;dir2</c> (searched recursively for <c>*.save*</c>). With <c>EXPORT_DIR</c> set it also
/// writes the room-entry saves + <c>truth.txt</c> and the latest save per run (<c>latest/</c>), the input of the
/// <c>Fixtures/event_saves</c> files (see the README there). Without <c>SNAP_DIRS</c> it does nothing.
/// </para>
/// </summary>
public class EventSnapshotScanTests(ITestOutputHelper output)
{
    [Fact]
    public void Snapshot_room_entry_saves_predict_the_pulled_event()
    {
        var dirs = (Environment.GetEnvironmentVariable("SNAP_DIRS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (dirs.Length == 0) return;
        var export = Environment.GetEnvironmentVariable("EXPORT_DIR");

        var loaded = new List<(string Path, string Seed, JsonDocument Doc, ParsedSave Save)>();
        foreach (var f in dirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.save*", SearchOption.AllDirectories)))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(f)); } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { continue; }
            if (!doc.RootElement.TryGetProperty("events_seen", out _) || !doc.RootElement.TryGetProperty("rng", out var rng)) continue;
            var save = RunSaveParser.Parse(doc.RootElement.GetRawText(), null);
            if (save.PlayerNetIds.Count != 1) continue; // co-op: the log names the event
            loaded.Add((f, rng.GetProperty("seed").ToString(), doc, save));
        }

        // The latest snapshot of a run (longest history) holds the truth for all its rooms.
        var latest = loaded.GroupBy(x => x.Seed).ToDictionary(g => g.Key,
            g => g.OrderByDescending(x => x.Save.Events?.TotalHistory ?? 0).First().Doc.RootElement);

        int rooms = 0, exact = 0;
        var done = new HashSet<string>();
        foreach (var (path, seed, doc, save) in loaded.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            if (save.Events is not { IsRoomEntrySave: true } ev || EventPrediction.Predict(save) is not { } guess) continue;
            var history = latest[seed].GetProperty("map_point_history");
            if (history.GetArrayLength() <= ev.ActIndex || history[ev.ActIndex].GetArrayLength() <= ev.HistoryInAct) continue; // not reached yet
            var room = history[ev.ActIndex][ev.HistoryInAct].GetProperty("rooms").EnumerateArray().FirstOrDefault();
            if (room.ValueKind != JsonValueKind.Object || room.GetProperty("room_type").GetString() != "event") continue;
            if (!done.Add($"{seed}/{ev.ActIndex}/{ev.HistoryInAct}")) continue;

            var actual = room.GetProperty("model_id").GetString()!;
            rooms++;
            if (actual == guess.EventId) exact++;
            output.WriteLine($"{(actual == guess.EventId ? "ok  " : "MISS")} {Path.GetFileName(path)} {seed} act {ev.ActIndex} room {ev.HistoryInAct}: guess {guess.EventId}{(guess.Certain ? "" : "?")} actual {actual}");

            if (export is null) continue;
            Directory.CreateDirectory(export);
            var name = $"{seed}_act{ev.ActIndex}_room{ev.HistoryInAct:00}.save";
            using (var fs = File.Create(Path.Combine(export, name)))
            using (var w = new Utf8JsonWriter(fs)) doc.RootElement.WriteTo(w);
            File.AppendAllText(Path.Combine(export, "truth.txt"), $"{name} {actual}{Environment.NewLine}");
        }

        if (export is not null)
            foreach (var (seed, root) in latest)
            {
                Directory.CreateDirectory(Path.Combine(export, "latest"));
                using var fs = File.Create(Path.Combine(export, "latest", $"{seed}.save"));
                using var w = new Utf8JsonWriter(fs);
                root.WriteTo(w);
            }

        output.WriteLine($"{loaded.Count} single-player saves, {latest.Count} runs, {rooms} event rooms, {exact} predicted exactly");
        Assert.Equal(rooms, exact);
    }
}
