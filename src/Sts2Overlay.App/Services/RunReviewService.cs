using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Replay;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Watches the game's run history folder (<c>saves\history\*.run</c>). When a new file appears — the game writes one when a
/// run ends — it replays that run through the engine (<see cref="RunReviewBuilder"/>) and delivers the review on the UI thread.
/// At start the newest existing run is reviewed too (as "not new"), so the last review is always available.
/// </summary>
public sealed class RunReviewService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

    private readonly string? _historyDir;
    private readonly long? _steamId;
    private readonly Dispatcher _ui;
    private readonly Action<RunReview, bool> _onReview;
    private readonly Lock _gate = new();
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private volatile DataPack _pack;
    private string? _lastReviewed;
    private bool _disposed;

    /// <param name="historyDir"><c>...\steam\&lt;id&gt;\profile1\saves\history</c>, or null when the game was never run here.</param>
    /// <param name="steamId">Our Steam id, to find "me" in co-op runs.</param>
    /// <param name="pack">Data pack to replay with (replace with <see cref="SetPack"/> after a reload).</param>
    /// <param name="ui">UI dispatcher.</param>
    /// <param name="onReview">Called on the UI thread with the review and whether the run just ended (true) or is the newest old one (false).</param>
    public RunReviewService(string? historyDir, long? steamId, DataPack pack, Dispatcher ui, Action<RunReview, bool> onReview)
    {
        _historyDir = historyDir;
        _steamId = steamId;
        _pack = pack;
        _ui = ui;
        _onReview = onReview;
        _debounce = new Timer(_ => Scan(isNew: true), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetPack(DataPack pack) => _pack = pack;

    /// <summary>
    /// The play-time decision log: decisions it recorded are judged against the advice shown then, the rest are
    /// recomputed. Null = recompute everything (as before the log existed).
    /// </summary>
    public DecisionLogStore? Decisions { get; init; }

    /// <summary>The user's <c>cardModel</c> setting for the recomputed decisions (false = <c>"engine"</c>).</summary>
    public bool UseLearnedModel { get; init; } = true;

    /// <summary>Reviews the newest existing run (in the background) and starts watching for new ones.</summary>
    public void Start()
    {
        if (_historyDir is null || !Directory.Exists(_historyDir))
        {
            AppLog.Info($"run review: no history folder ({_historyDir ?? "-"})");
            return;
        }
        Task.Run(() => Scan(isNew: false));
        try
        {
            _watcher = new FileSystemWatcher(_historyDir, "*.run") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Created += (_, _) => Poke();
            _watcher.Changed += (_, _) => Poke();
            _watcher.Renamed += (_, _) => Poke();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            AppLog.Error("run review: watcher failed", ex);
        }
    }

    /// <summary>The run just ended according to the tracker: look for its file soon (covers missed watcher events).</summary>
    public void Poke()
    {
        lock (_gate)
        {
            if (!_disposed) _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Re-reviews the last run with the current pack (after a pack reload). Not shown as new.</summary>
    public void Refresh()
    {
        lock (_gate) _lastReviewed = null;
        Task.Run(() => Scan(isNew: false));
    }

    private void Scan(bool isNew)
    {
        try
        {
            if (_historyDir is null || !Directory.Exists(_historyDir)) return;
            // File names are the run's start time in unix seconds, so the newest run sorts last.
            var newest = Directory.GetFiles(_historyDir, "*.run").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).LastOrDefault();
            if (newest is null) return;
            lock (_gate)
            {
                if (string.Equals(newest, _lastReviewed, StringComparison.OrdinalIgnoreCase)) return;
            }

            var run = ReadWithRetry(newest);
            if (run is null) return;
            var log = Decisions?.Read(DecisionLog.RunKey(run, LocalPlayer.Resolve(run, _steamId))) ?? [];
            var review = RunReviewBuilder.Build(run, _pack, _steamId, log, UseLearnedModel);
            lock (_gate) _lastReviewed = newest;
            AppLog.Info($"run review: {Path.GetFileName(newest)} ({review.Decisions} decisions, {review.Agreements} agreed; "
                        + $"{review.StoredJudged} by play-time advice, {review.Recomputed} recomputed, log {(review.HasDecisionLog ? "yes" : "no")}){(isNew ? " [new]" : "")}");
            _ui.BeginInvoke(() => _onReview(review, isNew));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or InvalidDataException)
        {
            AppLog.Error("run review failed", ex);
        }
    }

    /// <summary>The game may still be writing the file when the watcher fires: retry a few times on a parse/IO error.</summary>
    private static RunHistory? ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return RunHistoryReader.ReadFile(path);
            }
            catch (Exception ex) when (attempt < 4 && ex is IOException or JsonException or InvalidDataException)
            {
                Thread.Sleep(700 * attempt);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _debounce.Dispose();
        }
        _watcher?.Dispose();
    }
}
