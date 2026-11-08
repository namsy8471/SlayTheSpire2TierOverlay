using System.Diagnostics;
using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Background worker that captures the game and runs the recogniser, so the UI thread never blocks on a 33 MB copy
/// or on image matching.
///
/// One dedicated thread does capture → recognise → (optional) save, strictly in sequence. That is what makes it
/// safe to reuse the frame buffer: the next capture can't start while the recogniser still reads the previous one.
/// The thread sleeps on an <see cref="AutoResetEvent"/>: with a timeout while periodic capture is on (3 Hz on likely
/// reward/shop screens), forever when it's off, and it wakes early for hotkey requests. Before sleeping forever it
/// suspends the capturer, so a WGC session doesn't keep copying 4K frames while nobody asks for them.
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
    private long _intervalTicks; // 0 = periodic capture off
    private volatile ICardRecognizer _recognizer = new NullCardRecognizer();
    private volatile string? _characterHint;
    private byte[]? _lastSignature;
    private bool _lastHadResult;
    private long _lastRun;

    /// <param name="target">Current game window (read on the worker thread).</param>
    /// <param name="captureBackend">The <c>captureBackend</c> setting: "auto" (null), "wgc" or "gdi".</param>
    /// <param name="captureVisibility">Hides our windows before a frame that reads the desktop; null = a new one that shows them.</param>
    public CapturePipeline(Func<GameWindowInfo?> target, string? captureBackend = null, OverlayCaptureVisibility? captureVisibility = null)
    {
        _target = target;
        CaptureVisibility = captureVisibility ?? new OverlayCaptureVisibility(showInCapture: true);
        _capturer = FrameCapturerFactory.Create(captureBackend, CaptureVisibility.BeforeCapture);
        _thread = new Thread(Run) { IsBackground = true, Name = "CapturePipeline", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

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

    /// <summary>Capture and save a PNG to the captures folder (debug, for building the vision test set).</summary>
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
            _wake.WaitOne(interval > 0 ? TimeSpan.FromTicks(interval) : Timeout.InfiniteTimeSpan);
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
                Status?.Invoke("캡처/인식 실패: " + ex.Message);
            }
        }
    }

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
        _lastRun = Stopwatch.GetTimestamp();
        var info = _target();
        if (info is null || !info.CanCapture)
        {
            if (manual || save) Status?.Invoke("게임 창이 앞에 없어 캡처하지 않았습니다");
            return;
        }

        var t0 = Stopwatch.GetTimestamp();
        var frame = _capturer.Capture(info);
        var captureMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var timings = _capturer.LastTimings;

        if (save) SavePng(frame);
        if (!manual && !periodic) return;
        FrameProbe?.Invoke(frame); // before the "unchanged screen" skip: a hover change is small but matters to the probe

        // The map is checked on every frame, before the "unchanged screen" shortcut below: a scrolled map can look
        // almost the same to the coarse signature while every node moved.
        if (MapWatcher?.Analyze(frame) is { } sighting) MapSighted?.Invoke(sighting);

        // Skip recognition while the screen is visually unchanged and we already have an answer for it.
        var sig = Signature(frame);
        var same = _lastSignature is not null && MeanDiff(sig, _lastSignature) < 2.0;
        _lastSignature = sig;
        if (!manual && same && _lastHadResult) return;

        var t1 = Stopwatch.GetTimestamp();
        var result = _recognizer.Recognize(frame, _characterHint);
        var recognizeMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
        _lastHadResult = result is not null;

        if (manual || captureMs + recognizeMs > 250)
        {
            AppLog.Info($"capture[{_capturer.Name}] {frame.Width}x{frame.Height} {captureMs:0}ms (acquire {timings.AcquireMs:0.0}, copy {timings.CopyMs:0.0}), recognise {recognizeMs:0}ms → {result?.ScreenKind.ToString() ?? "null"} ({result?.Cards.Count ?? 0} cards){(manual ? " [manual]" : "")}");
        }
        if (result is not null) Recognized?.Invoke(result);
    }

    private void SavePng(CapturedFrame frame)
    {
        var file = Path.Combine(AppPaths.CapturesDir, $"manual_{frame.At:yyyyMMdd_HHmmss_fff}.png");
        FramePng.Save(frame, file);
        AppLog.Info($"capture saved {file} ({frame.Width}x{frame.Height} @ {frame.ScreenRect}, {_capturer.Name})");
        Status?.Invoke($"캡처 저장: {Path.GetFileName(file)} ({frame.Width}x{frame.Height})");
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
