using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;

namespace Sts2Overlay.App.Services;

/// <summary>Where one capture spent its time.</summary>
/// <param name="AcquireMs">Getting the pixels into memory the CPU can read: BitBlt (GDI) or GPU copy + Map wait (WGC).</param>
/// <param name="CopyMs">CPU copy from that memory into the frame's managed buffer.</param>
public readonly record struct CaptureTimings(double AcquireMs, double CopyMs)
{
    public double TotalMs => AcquireMs + CopyMs;
}

/// <summary>
/// One way of grabbing the game window into a <see cref="CapturedFrame"/>. Implementations reuse their buffer (see
/// <see cref="CapturedFrame"/>) and are used from a single thread (the capture worker).
/// </summary>
internal interface IFrameCapturer : IDisposable
{
    /// <summary>Short backend name for logs: "gdi" or "wgc".</summary>
    string Name { get; }

    CaptureTimings LastTimings { get; }

    /// <summary>Captures <see cref="GameWindowInfo.CaptureRect"/> of the target. Throws on failure.</summary>
    CapturedFrame Capture(GameWindowInfo target);

    /// <summary>
    /// True when the frame is cut out of the composed desktop, so windows above the game (our overlay!) would be in it
    /// unless hidden from capture: GDI BitBlt always, WGC only when capturing a monitor (no game window, demo mode).
    /// WGC of a window gets that window's own surface, never what is drawn over it.
    /// </summary>
    bool ReadsDesktop(GameWindowInfo target);

    /// <summary>Periodic capture stopped: release what is expensive to keep running (WGC session). Idempotent.</summary>
    void Suspend();
}

public enum CaptureBackend
{
    Gdi,
    Wgc,
}

/// <summary>Picks the capture backend from the <c>captureBackend</c> setting and what the OS supports.</summary>
internal static class CaptureBackendSelector
{
    /// <param name="setting">"auto" (default, also null/empty), "wgc" or "gdi"; case-insensitive.</param>
    /// <param name="wgcSupported">Windows.Graphics.Capture is usable on this system.</param>
    public static (CaptureBackend Backend, string Reason) Choose(string? setting, bool wgcSupported)
    {
        var s = setting?.Trim().ToLowerInvariant();
        var prefix = "";
        switch (s)
        {
            case "gdi":
                return (CaptureBackend.Gdi, "settings captureBackend=gdi");
            case null or "" or "auto" or "wgc":
                break;
            default:
                prefix = $"unknown captureBackend '{setting}', treated as auto; ";
                break;
        }
        return wgcSupported
            ? (CaptureBackend.Wgc, prefix + "Windows.Graphics.Capture available")
            : (CaptureBackend.Gdi, prefix + "Windows.Graphics.Capture not supported on this system");
    }
}

/// <summary>
/// Uses the primary backend (WGC) and falls back to GDI: for one frame when WGC throws, and for good after
/// <see cref="MaxConsecutiveFailures"/> failures in a row (e.g. a driver that never delivers frames).
/// </summary>
internal sealed class FallbackFrameCapturer : IFrameCapturer
{
    public const int MaxConsecutiveFailures = 3;

    private readonly IFrameCapturer _fallback;
    private IFrameCapturer? _primary;
    private IFrameCapturer? _lastUsed;
    private int _failures;

    public FallbackFrameCapturer(IFrameCapturer? primary, IFrameCapturer fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    /// <summary>Name of the backend that produced the last frame (or the one that will be tried first).</summary>
    public string Name => (_lastUsed ?? _primary ?? _fallback).Name;

    public CaptureTimings LastTimings => _lastUsed?.LastTimings ?? default;

    /// <summary>True once the primary backend has been given up on (or there never was one).</summary>
    public bool UsingFallbackOnly => _primary is null;

    /// <summary>
    /// Called right before each capture with <see cref="IFrameCapturer.ReadsDesktop"/> of the backend about to run
    /// (see <see cref="OverlayCaptureVisibility.BeforeCapture"/>): hides the overlay before a desktop read.
    /// </summary>
    public Action<bool>? BeforeCapture { get; set; }

    public bool ReadsDesktop(GameWindowInfo target) => (_primary ?? _fallback).ReadsDesktop(target);

    public CapturedFrame Capture(GameWindowInfo target)
    {
        if (_primary is { } primary)
        {
            try
            {
                BeforeCapture?.Invoke(primary.ReadsDesktop(target));
                var frame = primary.Capture(target);
                _failures = 0;
                MarkUsed(primary);
                return frame;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _failures++;
                AppLog.Warn($"capture: {primary.Name} failed ({_failures}/{MaxConsecutiveFailures}), using {_fallback.Name} for this frame: {ex.GetType().Name}: {ex.Message}");
                if (_failures >= MaxConsecutiveFailures)
                {
                    AppLog.Warn($"capture: {primary.Name} disabled after {_failures} consecutive failures; {_fallback.Name} from now on");
                    primary.Dispose();
                    _primary = null;
                }
            }
        }
        BeforeCapture?.Invoke(_fallback.ReadsDesktop(target));
        var fallbackFrame = _fallback.Capture(target);
        MarkUsed(_fallback);
        return fallbackFrame;
    }

    private void MarkUsed(IFrameCapturer capturer)
    {
        if (ReferenceEquals(capturer, _lastUsed)) return;
        _lastUsed = capturer;
        AppLog.Info($"capture backend active: {capturer.Name}");
    }

    public void Suspend()
    {
        _primary?.Suspend();
        _fallback.Suspend();
    }

    public void Dispose()
    {
        _primary?.Dispose();
        _fallback.Dispose();
    }
}

internal static class FrameCapturerFactory
{
    /// <summary>The capturer the app uses: WGC with GDI fallback, or GDI only (setting or unsupported OS).</summary>
    /// <param name="beforeCapture">See <see cref="FallbackFrameCapturer.BeforeCapture"/>.</param>
    public static IFrameCapturer Create(string? setting, Action<bool>? beforeCapture = null)
    {
        var (backend, reason) = CaptureBackendSelector.Choose(setting, WgcCapturer.IsSupported());
        AppLog.Info($"capture backend: {backend.ToString().ToLowerInvariant()} ({reason})");
        var capturer = backend == CaptureBackend.Wgc
            ? new FallbackFrameCapturer(new WgcCapturer(), new ScreenCapturer())
            : new FallbackFrameCapturer(null, new ScreenCapturer());
        capturer.BeforeCapture = beforeCapture;
        return capturer;
    }
}
