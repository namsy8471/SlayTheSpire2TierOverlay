using Sts2Overlay.GameState.IO;
using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;
using Xunit.Abstractions;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// Read-only checks against the user's real game folder (%APPDATA%\SlayTheSpire2). They pass trivially on a
/// machine without the game (xUnit 2 has no runtime skip), and never write anything there.
/// </summary>
public class RealFilesTests(ITestOutputHelper output)
{
    private static GamePaths? RealPaths()
    {
        GamePaths p = GamePaths.Discover(TestPaths.RealRoot);
        return p.SavesDirectory != null ? p : null;
    }

    [Fact]
    public void Every_real_log_parses_and_contains_the_known_signals()
    {
        string logs = Path.Combine(TestPaths.RealRoot, "logs");
        if (!Directory.Exists(logs)) { output.WriteLine("no game logs on this machine"); return; }

        var counts = new Dictionary<string, int>();
        foreach (string file in Directory.GetFiles(logs, "godot*.log"))
        {
            var tailer = new LogTailer(file);
            for (IReadOnlyList<string> lines; (lines = tailer.Poll()).Count > 0;)
                foreach (string line in lines)
                    if (LogLineParser.Parse(line) is { } e)
                        counts[e.GetType().Name] = counts.GetValueOrDefault(e.GetType().Name) + 1;
        }
        foreach (var (k, v) in counts.OrderBy(kv => kv.Key)) output.WriteLine($"{k,-24} {v,6}");

        Assert.True(counts.GetValueOrDefault(nameof(RunStartedEvent)) > 0);
        Assert.True(counts.GetValueOrDefault(nameof(CombatWonEvent)) > 0);
        Assert.True(counts.GetValueOrDefault(nameof(CardRarityRolledEvent)) > 0);
        Assert.True(counts.GetValueOrDefault(nameof(CardObtainedEvent)) > 0);
    }

    [Fact]
    public void Real_coop_runs_guest_rebuild_never_shows_a_wrong_card_as_certain()
    {
        // Same check as CoopGuestDeckTests, on whatever co-op runs the real logs hold today (new runs included).
        string logs = Path.Combine(TestPaths.RealRoot, "logs");
        if (RealPaths()?.SavesDirectory is not { } saves || !Directory.Exists(logs)) { output.WriteLine("no game files on this machine"); return; }
        var results = Diagnostics.GuestDeckEvaluation.Run(logs, Path.Combine(saves, "history"));
        foreach (var r in results) output.WriteLine(r.ToString());
        Assert.All(results, r => Assert.True(r.SilentErrors == 0 && r.ReconciledExact, r.ToString()));
    }

    [Fact]
    public void Neow_replay_matches_every_history_run_of_this_game_version()
    {
        // Run-history files list the options Neow generated (ancient_choice, in button order). Check the seed replay against
        // the shared fixtures and every real run made on the version the replay was ported from.
        var dirs = new List<string> { TestPaths.RunsDir };
        bool? unlocked = null;
        if (RealPaths()?.SavesDirectory is { } saves)
        {
            dirs.Add(Path.Combine(saves, "history"));
            unlocked = ProgressSave.AllCharactersUnlockedIn(saves);
        }
        int ok = 0, skipped = 0;
        var misses = new List<string>();
        foreach (string file in dirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.run")))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.GetProperty("build_id").GetString() != "v0.107.1" || root.GetProperty("modifiers").GetArrayLength() > 0) { skipped++; continue; }
            var first = root.GetProperty("map_point_history")[0][0];
            if (first.GetProperty("rooms")[0].GetProperty("model_id").GetString() != NeowOptions.EventId) { skipped++; continue; }
            var players = root.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("id").GetRawText()).ToList();
            string seed = root.GetProperty("seed").GetString()!;
            foreach (var stats in first.GetProperty("player_stats").EnumerateArray())
            {
                if (!stats.TryGetProperty("ancient_choice", out var choices)) continue;
                int slot = players.IndexOf(stats.GetProperty("player_id").GetRawText());
                var truth = choices.EnumerateArray().Select(c => c.GetProperty("TextKey").GetString()!).ToArray();
                var replay = NeowOptions.Relics(seed, slot, players.Count, unlocked ?? true);
                if (truth.SequenceEqual(replay)) ok++;
                else misses.Add($"{Path.GetFileName(file)} slot {slot}: game [{string.Join(", ", truth)}] replay [{string.Join(", ", replay)}]");
            }
        }
        output.WriteLine($"neow replay: {ok} ok, {misses.Count} wrong, {skipped} runs of other versions/modes skipped");
        foreach (var m in misses) output.WriteLine(m);
        Assert.Empty(misses);
    }

    [Fact]
    public void Ancient_replay_matches_every_history_run_of_this_game_version()
    {
        // Every act-start ancient (Pael, Orobas, Tanx ...) in the real history: deck and relics rebuilt from the run file,
        // options replayed from the seed, compared with ancient_choice (button order).
        if (RealPaths()?.SavesDirectory is not { } saves) { output.WriteLine("no saves folder"); return; }
        var files = Directory.GetFiles(Path.Combine(saves, "history"), "*.run");
        var visits = Diagnostics.AncientReplayEvaluation.Run(files);
        foreach (var g in visits.GroupBy(v => v.EventId).OrderBy(g => g.Key))
            output.WriteLine($"{g.Key,-16} {g.Count(v => v.Exact)}/{g.Count()}");
        foreach (var v in visits.Where(v => !v.Exact)) output.WriteLine(v.ToString());
        Assert.All(visits, v => Assert.True(v.Exact, v.ToString()));
    }

    [Fact]
    public void Live_run_save_parses()
    {
        if (RealPaths() is not { SavesDirectory: { } saves } paths) { output.WriteLine("no saves folder"); return; }
        foreach (string name in RunSaveParser.RunSaveFileNames)
        {
            string path = Path.Combine(saves, name);
            if (SharedFile.ReadAllBytes(path) is not { } bytes) continue;
            ParsedSave s = RunSaveParser.Parse(SharedFile.Decode(bytes), paths.SteamId);
            output.WriteLine($"{name}: {s.State.Character} A{s.State.Ascension} act {s.State.Act} floor {s.State.Floor} " +
                             $"deck {s.State.Deck.Count} relics {s.State.Relics.Count} removals {s.State.CardRemovalsBought} pity {s.CardRarityOffset} " +
                             $"map nodes {s.Map?.Nodes.Count} current {s.Map?.Current}");
            Assert.Equal(16, s.SchemaVersion);
            Assert.True(s.MyPlayerMatched);
            Assert.NotEmpty(s.State.Deck);
            Assert.True(s.State.MaxHp > 0);
            Assert.NotNull(s.Map);
            Assert.Contains(s.Map!.Current!.Value, s.Map.Nodes.Keys);
        }
    }

    [Fact]
    public void Tracker_on_real_save_and_log_agrees_with_the_save()
    {
        if (RealPaths() is not { SavesDirectory: { } saves } paths) { output.WriteLine("no saves folder"); return; }
        string savePath = Path.Combine(saves, "current_run.save");
        if (SharedFile.ReadAllBytes(savePath) is not { } bytes || paths.LogFile is not { } log || !File.Exists(log)) return;

        var t = new GameStateTracker(new TrackerOptions());
        ParsedSave save = RunSaveParser.Parse(SharedFile.Decode(bytes), paths.SteamId);
        t.OnSave(new SaveSnapshot(savePath, save, File.GetLastWriteTimeUtc(savePath), bytes.Length));
        var tailer = new LogTailer(log);
        for (IReadOnlyList<string> lines; (lines = tailer.Poll()).Count > 0;) t.ProcessLines(lines);

        TrackedRun? run = t.Current;
        Assert.NotNull(run);
        output.WriteLine($"screen {run!.Screen} fromSave {run.FromSave} patches {run.AppliedLogPatches} pity {run.RarityPityOffset} floor {run.State.Floor} deck {run.State.Deck.Count}");
        // The live log may belong to a different run than the save (e.g. the game is closed), so only
        // compare when the tracker decided the save is the current run.
        if (run.FromSave)
        {
            Assert.Equal(save.Seed, run.Seed);
            Assert.Equal(save.State.Deck.Count + run.AppliedLogPatches, run.State.Deck.Count, tolerance: run.AppliedLogPatches);
        }
    }
}
