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
