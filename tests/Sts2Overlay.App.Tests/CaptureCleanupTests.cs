using System.IO;
using Sts2Overlay.App.Services;

namespace Sts2Overlay.App.Tests;

public sealed class CaptureCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sts2OverlayCaptureCleanup_" + Guid.NewGuid().ToString("N"));

    private string Dir => Path.Combine(_root, "captures");

    private void WriteFile(string relative, int bytes)
    {
        var path = Path.Combine(Dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void Clean_DeletesEveryFileAndTheFolder()
    {
        WriteFile("reward_20261108_201012_394.png", 100);
        WriteFile("shop_20261109_021637_948.png", 50);
        WriteFile(Path.Combine("bench", "gdi.png"), 10);

        var r = CaptureCleanup.Clean(Dir);

        Assert.Equal(3, r.Deleted);
        Assert.Equal(160, r.Bytes);
        Assert.Equal(0, r.Failed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Dir)); // files and sub-folders gone
        Assert.True(Directory.Exists(Dir)); // the folder itself stays (a save may be creating it right now)
    }

    [Fact]
    public void Clean_MissingFolder_IsNoOp()
    {
        Assert.Equal(default, CaptureCleanup.Clean(Dir));
    }

    [Fact]
    public void Clean_SkipsLockedFile_AndKeepsTheFolder()
    {
        WriteFile("a.png", 10);
        WriteFile("locked.png", 20);
        using (new FileStream(Path.Combine(Dir, "locked.png"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = CaptureCleanup.Clean(Dir);
            Assert.Equal(1, r.Deleted);
            Assert.Equal(1, r.Failed);
        }
        Assert.True(File.Exists(Path.Combine(Dir, "locked.png")));
        Assert.False(File.Exists(Path.Combine(Dir, "a.png")));
    }

    [Fact]
    public async Task RunAtStartup_KeepsFilesWhileDebugCapturesIsOn()
    {
        WriteFile("manual_20261109_120000_000.png", 10);
        // A leftover from an earlier run (files written after the start are kept on purpose).
        File.SetLastWriteTimeUtc(Path.Combine(Dir, "manual_20261109_120000_000.png"), DateTime.UtcNow.AddHours(-1));

        await CaptureCleanup.RunAtStartup(keepCaptures: true, Dir);
        Assert.True(File.Exists(Path.Combine(Dir, "manual_20261109_120000_000.png")));

        await CaptureCleanup.RunAtStartup(keepCaptures: false, Dir);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Dir));
    }

    [Fact]
    public void Clean_KeepsFilesWrittenAfterTheCutoff()
    {
        WriteFile("old.png", 10);
        WriteFile("new.png", 20);
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        File.SetLastWriteTimeUtc(Path.Combine(Dir, "old.png"), cutoff.AddHours(-1));

        var r = CaptureCleanup.Clean(Dir, cutoff);

        Assert.Equal(1, r.Deleted);
        Assert.True(File.Exists(Path.Combine(Dir, "new.png")));
        Assert.False(File.Exists(Path.Combine(Dir, "old.png")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
