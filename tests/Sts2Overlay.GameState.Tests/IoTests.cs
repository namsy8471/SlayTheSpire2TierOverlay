using Sts2Overlay.GameState.IO;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.GameState.Tests;

public sealed class IoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sts2-io-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Discover_picks_most_recently_written_profile_and_reads_steam_id()
    {
        string old = Directory.CreateDirectory(Path.Combine(_dir, "steam", "76561190000000002", "profile1", "saves")).FullName;
        string recent = Directory.CreateDirectory(Path.Combine(_dir, "steam", "76561190000000001", "profile1", "saves")).FullName;
        Directory.CreateDirectory(Path.Combine(_dir, "steam", "76561190000000001", "modded", "profile1", "saves"));
        File.WriteAllText(Path.Combine(old, "progress.save"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(old, "progress.save"), DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        File.WriteAllText(Path.Combine(recent, "current_run.save"), "{}");

        GamePaths paths = GamePaths.Discover(_dir);
        Assert.Equal(recent, paths.SavesDirectory);
        Assert.Equal(TestPaths.Me, paths.SteamId);
        Assert.Equal(Path.Combine(_dir, "logs", "godot.log"), paths.LogFile);

        GamePaths overridden = GamePaths.Discover(_dir, savesOverride: old, logOverride: "x.log");
        Assert.Equal((old, "x.log", TestPaths.Friend), (overridden.SavesDirectory, overridden.LogFile, overridden.SteamId));
    }

    [Fact]
    public void Discover_without_game_folder_returns_nulls() =>
        Assert.Null(GamePaths.Discover(Path.Combine(_dir, "nope")).SavesDirectory);

    [Fact]
    public void Shared_read_works_while_another_process_holds_the_file_open_for_writing()
    {
        string path = Path.Combine(_dir, "current_run.save");
        File.WriteAllText(path, "{\"a\":1}");
        // Simulate the game holding a write handle that allows readers (like the real game does).
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal("{\"a\":1}", SharedFile.ReadAllText(path));
        Assert.Null(SharedFile.ReadAllText(Path.Combine(_dir, "missing.save")));
    }

    [Theory]
    [InlineData("current_run.save", "current_run.save")]
    [InlineData("current_run.save.tmp", "current_run.save")]
    [InlineData("current_run_mp.save.tmp", "current_run_mp.save")]
    [InlineData("current_run.save.backup", null)]
    [InlineData("progress.save", null)]
    public void Watcher_maps_sibling_files_to_the_run_save(string name, string? target) =>
        Assert.Equal(target, SaveWatcher.TargetName(name));

    [Fact]
    public async Task Watcher_sees_tmp_then_rename_writes_and_deletion()
    {
        var changed = new TaskCompletionSource<SaveSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new SaveWatcher(_dir, () => TestPaths.Me, TimeSpan.FromMilliseconds(100));
        watcher.SaveChanged += (_, s) => changed.TrySetResult(s);
        watcher.SaveRemoved += (_, p) => removed.TrySetResult(p);
        watcher.Start();

        // Write the way the game does: .tmp, then rename over the target.
        string target = Path.Combine(_dir, "current_run.save");
        File.WriteAllText(target + ".tmp", RunSaveParserTests.SingleJson);
        File.Move(target + ".tmp", target, overwrite: true);

        SaveSnapshot snap = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("SOLO1", snap.Save.Seed);
        Assert.Equal(new FileInfo(target).Length, snap.FileBytes);

        File.Delete(target);
        Assert.Equal(target, await removed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Embedded_starting_loadouts()
    {
        StartingLoadouts all = StartingLoadouts.Embedded;
        Assert.Equal(5, all.All.Count);
        StartingLoadout ironclad = Assert.IsType<StartingLoadout>(all.For("IRONCLAD"));
        Assert.Equal(10, ironclad.Deck.Count);
        Assert.Equal(5, ironclad.Deck.Count(c => c == "CARD.STRIKE_IRONCLAD"));
        Assert.Equal(["RELIC.BURNING_BLOOD"], ironclad.Relics);
        Assert.Equal((80, 99), (ironclad.StartingHp, ironclad.StartingGold));
        Assert.Equal(12, all.For("CHARACTER.SILENT")!.Deck.Count);
    }
}
