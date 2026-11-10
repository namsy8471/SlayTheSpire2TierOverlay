using System.Text;
using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// A game update can change the save/log format. Whatever the parser or the tracker cannot handle must be skipped and
/// reported, never thrown on a timer thread (that ends the overlay, and a restart replays the same log into the same throw).
/// </summary>
public sealed class InputRobustnessTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The id overflows ulong: LogLineParser throws OverflowException on it.</summary>
    private const string BadLine = "[INFO] Profile-scoped data path initialized: user://steam/99999999999999999999999/profile1";

    private readonly string _dir = Directory.CreateTempSubdirectory("sts2-robust-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static bool WaitFor(Func<bool> condition) => SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10));

    [Fact]
    public void A_log_line_that_breaks_the_parser_is_skipped_and_reported_at_most_once_a_minute()
    {
        DateTime now = T0;
        var t = new GameStateTracker(new TrackerOptions { Clock = () => now });
        var notes = new List<string>();
        t.Diagnostic += (_, m) => notes.Add(m);
        int raised = 0;
        t.FormatProblemDetected += (_, _) => raised++;
        string[] lines = TestPaths.FixtureLines("single_ironclad_run.log");
        int start = Array.FindIndex(lines, l => l.Contains("Embarking on a singleplayer IRONCLAD", StringComparison.Ordinal));

        t.ProcessLines([BadLine, .. lines[..(start + 1)]]); // must not throw

        Assert.Equal("CHARACTER.IRONCLAD", t.Current!.State.Character); // the rest of the read still counts
        Assert.True(t.FormatProblem);
        Assert.Equal(1, raised);
        Assert.Single(notes, n => n.Contains("line skipped", StringComparison.Ordinal) && n.Contains("OverflowException", StringComparison.Ordinal));

        t.ProcessLine(BadLine); // same problem within the minute: not logged again
        now = T0.AddSeconds(59);
        t.ProcessLine(BadLine);
        Assert.Single(notes, n => n.Contains("line skipped", StringComparison.Ordinal));

        now = T0.AddMinutes(2);
        t.ProcessLine(BadLine);
        Assert.Equal(2, notes.Count(n => n.Contains("line skipped", StringComparison.Ordinal)));
        Assert.Equal(1, raised); // the panel notice is raised once
    }

    [Fact]
    public void Report_throttle_says_each_message_once_per_window()
    {
        var throttle = new ReportThrottle(TimeSpan.FromMinutes(1));
        Assert.True(throttle.ShouldReport("a", T0));
        Assert.False(throttle.ShouldReport("a", T0.AddSeconds(30)));
        Assert.True(throttle.ShouldReport("b", T0.AddSeconds(30)));
        Assert.True(throttle.ShouldReport("a", T0.AddMinutes(1)));
    }

    [Fact]
    public void Tailer_skips_a_read_whose_handler_throws_and_goes_on_tailing()
    {
        string log = Path.Combine(_dir, "godot.log");
        File.WriteAllText(log, "one\n");
        using var tailer = new LogTailer(log);
        var errors = new List<Exception>();
        var got = new List<string>();
        tailer.Error += (_, ex) => { lock (errors) errors.Add(ex); };
        tailer.LinesRead += (_, lines) =>
        {
            if (lines.Contains("one")) throw new InvalidOperationException("handler bug");
            lock (got) got.AddRange(lines);
        };
        tailer.Start(TimeSpan.FromMilliseconds(20));

        Assert.True(WaitFor(() => { lock (errors) return errors.Count == 1; }));
        using (var fs = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(Encoding.UTF8.GetBytes("two\n"));
        Assert.True(WaitFor(() => { lock (got) return got.Count == 1; }));

        Assert.Equal(["two"], got);
        lock (errors) Assert.IsType<InvalidOperationException>(Assert.Single(errors)); // "one" was not handed over again
    }

    [Fact]
    public void Save_watcher_reports_a_save_its_handler_cannot_handle_instead_of_throwing()
    {
        File.WriteAllText(Path.Combine(_dir, "current_run.save"), RunSaveParserTests.SingleJson);
        using var watcher = new SaveWatcher(_dir, () => null);
        var errors = new List<Exception>();
        watcher.SaveChanged += (_, _) => throw new InvalidOperationException("tracker bug");
        watcher.SaveError += (_, e) => errors.Add(e.Error);

        watcher.Start(); // reads the existing save synchronously

        Assert.IsType<InvalidOperationException>(Assert.Single(errors)); // reported once: no retries for a non-IO error
    }

    // ---- saves folder discovery -----------------------------------------------------------------------------------

    private string SavesDir(string profile) => GamePaths.SavesDirectoryFor(_dir, TestPaths.Me, profile);

    private void WriteSave(string profile, int gold)
    {
        string dir = Directory.CreateDirectory(SavesDir(profile)).FullName;
        File.WriteAllText(Path.Combine(dir, "current_run.save"), RunSaveParserTests.SingleJson.Replace("\"gold\":99", $"\"gold\":{gold}", StringComparison.Ordinal));
    }

    private GameStateTracker StartTracker()
    {
        var t = new GameStateTracker(new TrackerOptions
        {
            Paths = GamePaths.Discover(_dir),
            Clock = () => T0,
            LogPollInterval = TimeSpan.FromMilliseconds(30),
            SaveDebounce = TimeSpan.FromMilliseconds(30),
            SavesRetryInterval = TimeSpan.FromMilliseconds(50),
        });
        t.Start();
        return t;
    }

    [Fact]
    public void Saves_folder_created_after_start_is_picked_up()
    {
        using GameStateTracker t = StartTracker(); // the game has never run: no steam\ folder yet
        Assert.Null(t.Current);

        WriteSave("profile1", gold: 77);

        Assert.True(WaitFor(() => t.Current?.State.Gold == 77));
    }

    [Fact]
    public void Follows_the_profile_the_log_names()
    {
        WriteSave("profile2", gold: 222);
        Directory.SetLastWriteTimeUtc(SavesDir("profile2"), DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(Path.Combine(SavesDir("profile2"), "current_run.save"), DateTime.UtcNow.AddDays(-3));
        WriteSave("profile1", gold: 111); // newest, so discovery starts with profile1
        Assert.Equal(SavesDir("profile1"), GamePaths.Discover(_dir).SavesDirectory);

        Directory.CreateDirectory(Path.Combine(_dir, "logs"));
        File.WriteAllText(Path.Combine(_dir, "logs", GamePaths.LogFileName),
            $"[INFO] Profile-scoped data path initialized: user://steam/{TestPaths.Me}/profile2\n");
        using GameStateTracker t = StartTracker();

        Assert.True(WaitFor(() => t.Current?.State.Gold == 222)); // profile1's run is dropped, profile2's is shown
    }

    [Fact]
    public void Profile_line_names_the_profile_folder()
    {
        var e = Assert.IsType<LocalPlayerIdEvent>(LogLineParser.Parse($"[INFO] Profile-scoped data path initialized: user://steam/{TestPaths.Me}/profile2"));
        Assert.Equal((TestPaths.Me, true, "profile2"), (e.NetId, e.FromProfilePath, e.Profile));
        var modded = Assert.IsType<LocalPlayerIdEvent>(LogLineParser.Parse($"[INFO] Profile-scoped data path initialized: user://steam/{TestPaths.Me}/modded/profile1"));
        Assert.Null(modded.Profile); // not a folder we watch
        Assert.Null(Assert.IsType<LocalPlayerIdEvent>(LogLineParser.Parse("[INFO] [SteamHost] Initializing Steam host. Our player id: 76561190000000001")).Profile);
    }

    [Fact]
    public void Discovered_paths_remember_the_root_unless_the_saves_folder_was_given()
    {
        Assert.Equal(_dir, GamePaths.Discover(_dir).Root);
        Assert.Null(GamePaths.Discover(_dir, savesOverride: SavesDir("profile1")).Root);
        Assert.Equal(Path.Combine(_dir, "steam", "76561190000000001", "profile3", "saves"), GamePaths.SavesDirectoryFor(_dir, TestPaths.Me, "profile3"));
    }
}
