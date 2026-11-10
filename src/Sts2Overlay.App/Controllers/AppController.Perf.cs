using System.Diagnostics;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services.Perf;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// The UI-thread side of the perf log (Services/Perf/PerfRecorder.cs): how long a recognition waits for the UI thread
/// and takes to apply, and, once per newly shown reward / shop screen, how long from the frame that showed it to its
/// badges on screen.
///
/// <code>
///  worker: capture ─ probe+map ─ recognise ─▶ Recognized ──(BeginInvoke)──▶ OnRecognized ─ ApplyRecognition
///                                                                              │ badges for a new screen?
///                                                                              ▼
///                                       BeginInvoke(Loaded) ─▶ OnOfferPainted: one "perf: Reward shown …" line
/// </code>
/// A Loaded-priority callback runs after the Render-priority layout and render work queued by the badge change, so
/// "painted" means WPF finished drawing it; DWM composition adds up to one more display frame, which is not measured.
/// </summary>
public sealed partial class AppController
{
    private PerfRecorder Perf => _pipeline.Perf;

    /// <summary>When <see cref="GameStateSnapshot.Hint"/> last changed (Stopwatch timestamp; 0 = not yet).</summary>
    private long _hintChangedAt;

    /// <summary>Non-zero while <see cref="OnRecognized"/> applies a fresh pipeline result (its UI start).</summary>
    private long _applyStart;
    private bool _applyAccepted;

    /// <summary>
    /// The screen the last badge latency was measured for. Cleared with the badges, so the next screen to show badges
    /// is timed: a tracker screen (<see cref="RecognitionResult.ScreenId"/> changes when the recogniser starts a new one)
    /// is timed once however many frames re-show it.
    /// </summary>
    private (ScreenKind Kind, int ScreenId)? _paintedScreen;
    private PendingPaint? _pendingPaint;

    private readonly record struct PendingPaint(ScreenKind Kind, FrameTiming Frame);

    /// <summary>A result from the capture worker, on the UI thread: apply it and time it.</summary>
    private void OnRecognized(RecognitionResult r)
    {
        var start = Stopwatch.GetTimestamp();
        _applyStart = start;
        _applyAccepted = false;
        _pendingPaint = null;
        try
        {
            ApplyRecognition(r);
        }
        finally
        {
            _applyStart = 0;
        }
        var done = Stopwatch.GetTimestamp();
        if (!_applyAccepted) return; // a dropped stale result says nothing about the UI's cost
        if (r.Timing is { } t) Perf.RecordBetween(PerfStage.UiQueue, t.RecognizeDone, start);
        Perf.RecordBetween(PerfStage.UiApply, start, done);
        if (_pendingPaint is not { } paint) return;
        _pendingPaint = null;
        var hint = _snapshot.Hint;
        var hintAt = _hintChangedAt;
        _ui.BeginInvoke(() => OnOfferPainted(paint, start, done, hint, hintAt), DispatcherPriority.Loaded);
    }

    /// <summary>Called by <see cref="ApplyRecognition"/> right before it shows badges for <paramref name="r"/>.</summary>
    private void NoteBadgesShown(RecognitionResult r)
    {
        var key = (r.ScreenKind, r.ScreenId);
        if (_paintedScreen == key) return;
        _paintedScreen = key;
        // Only a fresh pipeline result has a frame to measure from; a re-apply (new run state, pack reload, held
        // verdict) shows an older frame again and would report its age, not the overlay's speed.
        if (_applyStart != 0 && r.Timing is { } t) _pendingPaint = new PendingPaint(r.ScreenKind, t);
    }

    private void OnOfferPainted(PendingPaint paint, long uiStart, long uiDone, ScreenHint hint, long hintAt)
    {
        var latency = OfferLatency.From(paint.Frame, uiStart, uiDone, Stopwatch.GetTimestamp(), hint, hintAt, Stopwatch.Frequency);
        Perf.Record(PerfStage.FrameToPaint, latency.TotalMs);
        AppLog.Info(latency.ToLogLine(paint.Kind, hint));
    }
}
