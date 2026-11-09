using System.IO;
using Sts2Overlay.App.Infrastructure;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Empties the debug captures folder (<see cref="AppPaths.CapturesDir"/>) at start when the <c>debugCaptures</c> setting
/// is off, so PNGs never pile up there. Recognition never needs these files: frames are recognised in memory and the
/// buffer is reused for the next capture. The only writers are the debug save (Ctrl+F9 / tray, which needs
/// <c>debugCaptures</c>), <c>--selftest</c>, the <c>--bench-capture</c> / <c>--verify-capture</c> tools, and the old
/// OverlaySpike tool, which left thousands of reward_/shop_ screenshots there.
/// </summary>
public static class CaptureCleanup
{
    /// <summary>What a cleanup removed (and how many files it could not, e.g. open in an image viewer).</summary>
    public readonly record struct Result(int Deleted, long Bytes, int Failed);

    /// <summary>
    /// Deletes every file under <paramref name="dir"/> last written before <paramref name="olderThanUtc"/> (null = all),
    /// then the empty sub-folders. <paramref name="dir"/> itself is kept, so a save that is creating it right now
    /// (FramePng: CreateDirectory, then File.Create) can't lose its folder in between. Files that can't be deleted are
    /// skipped and counted. A missing folder is a no-op.
    /// </summary>
    public static Result Clean(string dir, DateTime? olderThanUtc = null)
    {
        if (!Directory.Exists(dir)) return default;

        int deleted = 0, failed = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                // A capture this run saved (--selftest, Ctrl+F9) while the cleanup was still going: not a leftover.
                if (olderThanUtc is { } cutoff && info.LastWriteTimeUtc >= cutoff) continue;
                var length = info.Length;
                File.Delete(file);
                deleted++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        // Deepest folders first, so a parent is empty by the time we get to it.
        var dirs = Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();
        foreach (var d in dirs)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use (e.g. open in Explorer): leave it, it's empty anyway or holds a locked file.
            }
        }
        return new Result(deleted, bytes, failed);
    }

    /// <summary>
    /// Start-up hook: off the UI thread (thousands of 4K PNGs take a few seconds), logs what was removed. Does nothing
    /// while <paramref name="keepCaptures"/> (the <c>debugCaptures</c> setting) is on.
    /// </summary>
    public static Task RunAtStartup(bool keepCaptures, string? dir = null)
    {
        dir ??= AppPaths.CapturesDir;
        if (keepCaptures)
        {
            AppLog.Info($"captures: debugCaptures is on, keeping {dir}");
            return Task.CompletedTask;
        }
        if (!Directory.Exists(dir)) return Task.CompletedTask;

        var startedUtc = DateTime.UtcNow;
        return Task.Run(() =>
        {
            try
            {
                var r = Clean(dir, startedUtc);
                if (r.Deleted > 0 || r.Failed > 0)
                {
                    AppLog.Info($"captures: removed {r.Deleted} file(s), {r.Bytes / (1024.0 * 1024.0):0.#} MB from {dir}"
                        + (r.Failed > 0 ? $"; {r.Failed} could not be deleted (in use)" : ""));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("captures: cleanup failed", ex);
            }
        });
    }
}
