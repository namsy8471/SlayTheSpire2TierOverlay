using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.GameState.Save;
using Path = System.IO.Path;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// "플레이 기록 보관" (settings.json <c>playRecord</c>, off by default): every few seconds, on a thread-pool timer,
/// <see cref="PlayRecorder"/> copies the run saves and godot.log the overlay already follows into a local folder.
/// Read at start; nothing runs while the setting is off.
/// </summary>
public sealed partial class AppController
{
    private static readonly TimeSpan PlayRecordInterval = TimeSpan.FromSeconds(3);
    private PlayRecorder? _playRecorder;
    private Timer? _playRecordTimer;
    private string? _playRecordPausedFor;
    private int _playRecordBusy;

    private void InitPlayRecord()
    {
        if (!_settings.PlayRecord) return;
        string dir = string.IsNullOrWhiteSpace(_settings.PlayRecordDir)
            ? Path.Combine(AppPaths.DataRoot, "play-records")
            : Environment.ExpandEnvironmentVariables(_settings.PlayRecordDir);
        _playRecorder = new PlayRecorder(dir, Math.Max(0, (long)_settings.PlayRecordMaxMb) * 1024 * 1024);
        AppLog.Info($"play record: on, folder {dir}, cap {(_settings.PlayRecordMaxMb > 0 ? $"{_settings.PlayRecordMaxMb} MB" : "none")}");
        _playRecordTimer = new Timer(_ => PlayRecordTick(), null, PlayRecordInterval, PlayRecordInterval);
    }

    /// <summary>Timer thread. Skipped when the previous tick is still copying (a big log on a slow disk).</summary>
    private void PlayRecordTick()
    {
        if (_playRecorder is not { } recorder || Interlocked.Exchange(ref _playRecordBusy, 1) == 1) return;
        try
        {
            if (_source is not GameStateAdapter adapter) return;
            string? solo = adapter.SinglePlayerSavePath;
            string? coop = solo is null ? null : Path.Combine(Path.GetDirectoryName(solo)!, RunSaveParser.RunSaveFileNames[1]);
            var step = recorder.Step(solo, coop, adapter.LogFile);
            if (step.PausedFor != _playRecordPausedFor)
            {
                AppLog.Info(step.PausedFor is { } other
                    ? $"play record: paused, another program is writing {other} in the same folder (datawatch.ps1?)"
                    : "play record: resumed");
                _playRecordPausedFor = step.PausedFor;
            }
            if (step.Saves > 0 || step.Pruned > 0)
                AppLog.Info($"play record: {step.Saves} save(s){(step.Pruned > 0 ? $", {step.Pruned} old file(s) removed for the size cap" : "")}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"play record: step failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            Volatile.Write(ref _playRecordBusy, 0);
        }
    }

    private void DisposePlayRecord()
    {
        _playRecordTimer?.Dispose();
        _playRecordTimer = null;
    }
}
