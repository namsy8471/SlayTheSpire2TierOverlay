using System.Diagnostics;
using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services.Perf;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Background worker that captures the game and runs the recogniser, so the UI thread never blocks on a 33 MB copy
/// or on image matching.
///
/// One dedicated thread does capture → recognise → (optional) save, strictly in sequence. That is what makes it
/// safe to reuse the frame buffer: the next capture can't start while the recogniser still reads the previous one.
/// The thread sleeps on an <see cref="AutoResetEvent"/>: with a timeout while periodic capture is on (3 Hz on likely
/// reward/shop screens, 20 Hz while one is expected but not found yet), forever when it's off, and it wakes early for
/// hotkey requests and rate changes. Before sleeping forever it suspends the capturer, so a WGC session doesn't keep
/// copying 4K frames while nobody asks for them.
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private readonly IFrameCapturer _capturer;
    private readonly Func<GameWindowInfo?> _target;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private int _recognizeRequested;
    private int _saveRequested;
    private int _resetRequested;
    private int _epoch;
    private long _intervalTicks; // 0 = periodic capture off
    private volatile ICardRecognizer _recognizer = new NullCardRecognizer();
    private volatile string? _characterHint;
    private byte[]? _lastSignature;
    private bool _lastHadResult;
    private long _lastRun;
    private long _lastRunEnd;
    private long _lastLook; // start of the last step that captured a frame (FrameTiming.PreviousLook)

    /// <param name="target">Current game window (read on the worker thread).</param>
    /// <param name="captureBackend">The <c>captureBackend</c> setting: "auto" (null), "wgc" or "gdi".</param>
    /// <param name="captureVisibility">Hides our windows before a frame that reads the desktop; null = a new one that shows them.</param>
    /// <param name="perf">Where stage times go; null = a recorder that is never written to the log.</param>
    public CapturePipeline(Func<GameWindowInfo?> target, string? captureBackend = null, OverlayCaptureVisibility? captureVisibility = null,
        PerfRecorder? perf = null)
    {
        _target = target;
        CaptureVisibility = captureVisibility ?? new OverlayCaptureVisibility(showInCapture: true);
        Perf = perf ?? new PerfRecorder(sampleResources: false);
        _capturer = FrameCapturerFactory.Create(captureBackend, CaptureVisibility.BeforeCapture, Perf.GameFrames);
        _thread = new Thread(Run) { IsBackground = true, Name = "CapturePipeline", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Stage times and counters of the capture worker; the UI side records into it too (AppController).</summary>
    public PerfRecorder Perf { get; }

    /// <summary>Whether our overlay windows show up in screen capture (OBS, screenshots); every overlay window registers here.</summary>
    public OverlayCaptureVisibility CaptureVisibility { get; }

    /// <summary>Raised on the WORKER thread with each non-null recognition result.</summary>
    public event Action<RecognitionResult>? Recognized;

    /// <summary>
    /// Raised on the WORKER thread after every periodic/manual capture while <see cref="MapWatcher"/> has a target,
    /// whether or not the map was found (so the overlay can take its marks down when the map closes).
    /// </summary>
    public event Action<MapSighting>? MapSighted;

    /// <summary>Looks for the act map in each captured frame; null = don't.</summary>
    public MapScreenWatcher? MapWatcher { get; set; }

    /// <summary>Raised on the WORKER thread with a short Korean status line (capture saved / failed).</summary>
    public event Action<string>? Status;

    /// <summary>
    /// Optional extra reader of every periodic frame, called on the WORKER thread while the frame is valid (the
    /// buffer is reused afterwards, so copy what you keep). Used by the event option markers.
    /// </summary>
    public Action<CapturedFrame>? FrameProbe { get; set; }

    public ICardRecognizer Recognizer
    {
        get => _recognizer;
        set => _recognizer = value;
    }

    public string? CharacterHint
    {
        get => _characterHint;
        set => _characterHint = value;
    }

    /// <summary>Periodic capture interval, or null to stop periodic capture.</summary>
    public void SetInterval(TimeSpan? interval)
    {
        var ticks = interval is { } i && i > TimeSpan.Zero ? i.Ticks : 0;
        if (Interlocked.Exchange(ref _intervalTicks, ticks) != ticks) _wake.Set();
    }

    /// <summary>Capture and recognise as soon as possible, even if the screen looks unchanged.</summary>
    public void RequestRecognize()
    {
        Interlocked.Exchange(ref _recognizeRequested, 1);
        _wake.Set();
    }

    /// <summary>
    /// The log says the card screen closed: the recogniser forgets the frames it merged (on the worker, before its next
    /// recognition) and a fresh recognition is requested, so a still-open or newly opened screen is read again at once.
    /// Returns the new epoch: results stamped with a lower <see cref="RecognitionResult.Epoch"/> were computed before
    /// the reset (they may still be on their way to the UI thread) and must be dropped.
    /// </summary>
    public int ResetRecognition()
    {
        // Flag first, then the epoch: the worker consumes the flag, then reads the epoch, so a result that saw the new
        // epoch always comes from a reset tracker (the other orders only drop a fresh result, never keep a stale one).
        Interlocked.Exchange(ref _resetRequested, 1);
        var epoch = Interlocked.Increment(ref _epoch);
        RequestRecognize();
        return epoch;
    }

    /// <summary>
    /// Capture and save a PNG to the captures folder (debug, for bug reports and the vision test set). The only disk
    /// write of the pipeline: periodic and manual recognition work on the in-memory frame. Callers check the
    /// "debugCaptures" setting (<see cref="Controllers.AppController.SaveCapture"/>).
    /// </summary>
    public void RequestSave()
    {
        Interlocked.Exchange(ref _saveRequested, 1);
        _wake.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            var interval = Interlocked.Read(ref _intervalTicks);
            if (interval == 0) SuspendCapturer();
            _wake.WaitOne(interval > 0
                ? NextWait(interval, Stopwatch.GetElapsedTime(_lastRun).Ticks, Stopwatch.GetElapsedTime(_lastRunEnd).Ticks)
                : Timeout.InfiniteTimeSpan);
            if (_stop) break;

            var manual = Interlocked.Exchange(ref _recognizeRequested, 0) == 1;
            var save = Interlocked.Exchange(ref _saveRequested, 0) == 1;
            interval = Interlocked.Read(ref _intervalTicks);
            var periodic = interval > 0 && Stopwatch.GetElapsedTime(_lastRun).Ticks >= interval * 9 / 10;
            if (!manual && !save && !periodic) continue;

            try
            {
                Step(manual, save, periodic);
            }
            catch (Exception ex)
            {
                AppLog.Error("capture pipeline step failed", ex);
                Status?.Invoke(Loc.T("캡처/인식 실패: ", "Capture/recognition failed: ") + ex.Message);
            }
            finally
            {
                _lastRunEnd = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// How long the worker sleeps before the next periodic look: the rest of the interval since the last step STARTED,
    /// so the rate is the one asked for (sleeping a whole interval after each step made 3 Hz a ~347 ms rhythm, and 20 Hz
    /// would be ~16 Hz), but at least half an interval after it ENDED, so slow steps on a loaded PC never run back to
    /// back. Zero when the next look is already due (a rate change woke the worker long after the last step).
    /// All values in <see cref="TimeSpan"/> ticks.
    /// </summary>
    internal static TimeSpan NextWait(long intervalTicks, long sinceStepStartTicks, long sinceStepEndTicks)
        => TimeSpan.FromTicks(Math.Max(0, Math.Max(intervalTicks - sinceStepStartTicks, intervalTicks / 2 - sinceStepEndTicks)));

    private void SuspendCapturer()
    {
        try
        {
            _capturer.Suspend();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"capture suspend failed: {ex.Message}");
        }
    }

    private void Step(bool manual, bool save, bool periodic)
    {
        var stepStart = Stopwatch.GetTimestamp();
        _lastRun = stepStart;
        var info = _target();
        if (info is null || !info.CanCapture)
        {
            if (manual || save) Status?.Invoke(Loc.T("게임 창이 앞에 없어 캡처하지 않았습니다", "Not captured: the game window is not in front"));
            return;
        }

        var t0 = Stopwatch.GetTimestamp();
        var frame = _capturer.Capture(info);
        var captureDone = Stopwatch.GetTimestamp();
        var captureMs = Stopwatch.GetElapsedTime(t0, captureDone).TotalMilliseconds;
        var timings = _capturer.LastTimings;
        var previousLook = _lastLook;
        _lastLook = stepStart;

        if (save) SavePng(frame);
        if (!manual && !periodic) return;
        // Perf (always on): a save-only step is left out, its PNG write is not part of the watch's cost.
        var perf = Perf;
        perf.CountStep(_capturer.LastFrameReused);
        perf.Record(PerfStage.Capture, captureMs);
        perf.Record(PerfStage.CaptureAcquire, timings.AcquireMs);
        perf.Record(PerfStage.CaptureCopy, timings.CopyMs);
        try
        {
            if (FrameProbe is { } probe)
            {
                var tp = Stopwatch.GetTimestamp();
                probe(frame); // before the "unchanged screen" skip: a hover change is small but matters to the probe
                perf.RecordSince(PerfStage.EventProbe, tp);
            }

            // The map is checked on every frame, before the "unchanged screen" shortcut below: a scrolled map can look
            // almost the same to the coarse signature while every node moved.
            if (MapWatcher is { } watcher)
            {
                var tm = Stopwatch.GetTimestamp();
                var sighting = watcher.Analyze(frame);
                perf.RecordSince(PerfStage.MapAnalyze, tm);
                if (sighting is not null) MapSighted?.Invoke(sighting);
            }

            // Skip recognition while the screen is visually unchanged and we already have an answer for it. An unsettled
            // answer (the last frame did NOT show the screen and the recogniser holds it for a frame or two; a shop slot
            // looked empty once) is not one: it only settles by looking at more frames, so a closed screen's marks never
            // stay over a still picture.
            var sig = Signature(frame);
            var same = _lastSignature is not null && MeanDiff(sig, _lastSignature) < 2.0;
            _lastSignature = sig;
            if (CanSkip(manual, same, _lastHadResult))
            {
                perf.CountUnchanged();
                return;
            }

            var recognizer = _recognizer;
            if (Interlocked.Exchange(ref _resetRequested, 0) == 1) recognizer.Reset();
            var epoch = Volatile.Read(ref _epoch);
            var t1 = Stopwatch.GetTimestamp();
            var result = recognizer.Recognize(frame, _characterHint);
            var recognizeDone = Stopwatch.GetTimestamp();
            var recognizeMs = Stopwatch.GetElapsedTime(t1, recognizeDone).TotalMilliseconds;
            perf.CountRecognised();
            perf.Record(PerfStage.Recognize, recognizeMs);
            _lastHadResult = IsSteady(result);
            if (result is not null) result = result with { Epoch = epoch, Timing = new FrameTiming(previousLook, t0, captureDone, t1, recognizeDone) };

            if (manual || captureMs + recognizeMs > 250)
            {
                AppLog.Info($"capture[{_capturer.Name}] {frame.Width}x{frame.Height} {captureMs:0}ms (acquire {timings.AcquireMs:0.0}, copy {timings.CopyMs:0.0}), recognise {recognizeMs:0}ms → {result?.ScreenKind.ToString() ?? "null"} ({result?.Cards.Count ?? 0} cards){(manual ? " [manual]" : "")}");
            }
            if (result is not null) Recognized?.Invoke(result);
        }
        finally
        {
            // Includes the synchronous Recognized / MapSighted handlers (they only queue work for the UI thread).
            perf.RecordSince(PerfStage.StepTotal, stepStart);
        }
    }

    /// <summary>Recognition may be skipped: a periodic frame, the picture unchanged, and the last answer a steady one.</summary>
    internal static bool CanSkip(bool manual, bool samePicture, bool lastSteady) => !manual && samePicture && lastSteady;

    /// <summary>An answer that stays right while the picture does not change: not null and settled (see <see cref="RecognitionResult.Unsettled"/>).</summary>
    internal static bool IsSteady(RecognitionResult? result) => result is { Unsettled: false };

    private void SavePng(CapturedFrame frame)
    {
        var file = Path.Combine(AppPaths.CapturesDir, $"manual_{frame.At:yyyyMMdd_HHmmss_fff}.png");
        FramePng.Save(frame, file);
        AppLog.Info($"capture saved {file} ({frame.Width}x{frame.Height} @ {frame.ScreenRect}, {_capturer.Name})");
        Status?.Invoke(Loc.T($"캡처 저장: {Path.GetFileName(file)} ({frame.Width}x{frame.Height})", $"Capture saved: {Path.GetFileName(file)} ({frame.Width}x{frame.Height})"));
    }

    /// <summary>32x18 grid of luminance samples: a cheap "did the screen change?" fingerprint.</summary>
    private static byte[] Signature(CapturedFrame f)
    {
        const int gw = 32, gh = 18;
        var sig = new byte[gw * gh];
        for (var gy = 0; gy < gh; gy++)
        {
            var y = (int)((gy + 0.5) * f.Height / gh);
            for (var gx = 0; gx < gw; gx++)
            {
                var x = (int)((gx + 0.5) * f.Width / gw);
                var i = y * f.Stride + x * 4;
                sig[gy * gw + gx] = (byte)((f.Bgra[i] + f.Bgra[i + 1] * 6 + f.Bgra[i + 2] * 3) / 10);
            }
        }
        return sig;
    }

    private static double MeanDiff(byte[] a, byte[] b)
    {
        var sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _capturer.Dispose();
        _wake.Dispose();
    }
}
