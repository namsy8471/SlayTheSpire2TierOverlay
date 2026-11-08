using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Interop;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Pure rule for <see cref="OverlayCaptureVisibility"/>: are our windows hidden from screen capture right now?
/// </summary>
internal static class CaptureExclusionPolicy
{
    /// <param name="showInCapture">The user wants the overlay in OBS / Discord / screenshots ("방송·캡처에 표시").</param>
    /// <param name="recognitionSeesDesktop">
    /// The recogniser's next frame is cut out of the composed desktop (GDI BitBlt, or WGC of a whole monitor), so any
    /// visible overlay window would be IN that frame. WGC of the game window alone never contains other windows.
    /// </param>
    public static bool ShouldExclude(bool showInCapture, bool recognitionSeesDesktop) => !showInCapture || recognitionSeesDesktop;
}

/// <summary>
/// Owns the "hide from screen capture" flag (<c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>) of every
/// overlay window: badges, panel, map marks, event markers.
///
/// Why this exists: the flag hides a window from EVERY capture on the PC, not just ours. We used to set it always, so
/// the badges never polluted the frames we recognise, but that also hid the overlay from OBS, Discord and
/// screenshots. Our normal capture path (Windows.Graphics.Capture of the game window) only ever receives the game's
/// own pixels, so the flag isn't needed there. Only the fallbacks read the composed desktop: GDI BitBlt and WGC of a
/// monitor (demo mode). Before such a frame, <see cref="BeforeCapture"/> sets the flag on every window and waits for
/// DWM to compose one frame without them; when window capture is back, the windows become visible again.
///
/// Thread-safety: <see cref="Register"/>/<see cref="ShowInCapture"/> are called on the UI thread,
/// <see cref="BeforeCapture"/> on the capture worker. <c>SetWindowDisplayAffinity</c> works from any thread of the
/// owning process and returns at once even while the owning UI thread is blocked and not pumping messages (checked:
/// &lt;1 ms), so the worker sets the flag itself and holding <c>_applyLock</c> around it can't deadlock with the UI.
/// </summary>
public sealed class OverlayCaptureVisibility
{
    private readonly object _lock = new();

    // Held while affinity calls run, and the state to apply is read inside it: whichever thread applies last applies
    // the newest state, so a slow UI-thread "show" can never land after the worker's "hide" for a GDI frame.
    private readonly object _applyLock = new();
    private readonly HashSet<IntPtr> _windows = [];
    private bool _showInCapture;
    private bool _recognitionSeesDesktop;
    private bool _excluded;

    /// <param name="showInCapture">Initial user choice (settings <c>showOverlayInCapture</c>).</param>
    public OverlayCaptureVisibility(bool showInCapture)
    {
        _showInCapture = showInCapture;
        _excluded = CaptureExclusionPolicy.ShouldExclude(showInCapture, false);
    }

    /// <summary>Raised (on the thread that made the change) when the windows switch between hidden and visible.</summary>
    public event Action<bool>? ExcludedChanged;

    /// <summary>The user's choice: show the overlay in screen capture whenever recognition allows it.</summary>
    public bool ShowInCapture
    {
        get
        {
            lock (_lock) return _showInCapture;
        }
        set
        {
            lock (_lock)
            {
                if (_showInCapture == value) return;
                _showInCapture = value;
            }
            Reapply();
        }
    }

    /// <summary>True while our windows are hidden from capture.</summary>
    public bool IsExcluded
    {
        get
        {
            lock (_lock) return _excluded;
        }
    }

    /// <summary>
    /// True while recognition has to read the desktop (GDI / monitor capture), which forces the windows hidden even
    /// when the user wants them in capture. Shown in the tray so the user knows why the overlay vanished from OBS.
    /// </summary>
    public bool RecognitionSeesDesktop
    {
        get
        {
            lock (_lock) return _recognitionSeesDesktop;
        }
    }

    /// <summary>Starts managing <paramref name="hwnd"/> and applies the current state to it. Call from SourceInitialized.</summary>
    public void Register(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (_applyLock)
        {
            bool excluded;
            lock (_lock)
            {
                _windows.Add(hwnd);
                excluded = _excluded;
            }
            // Windows start visible to capture (affinity WDA_NONE), so only the hidden state needs a call.
            if (excluded) OverlayWindowHelper.SetExcludeFromCapture(hwnd, true);
        }
    }

    public void Unregister(IntPtr hwnd)
    {
        lock (_lock) _windows.Remove(hwnd);
    }

    /// <summary>
    /// Called by the capturer right before it grabs a frame. When <paramref name="recognitionSeesDesktop"/> and our
    /// windows are visible, hides them and returns only after DWM has composed a frame without them, so the frame
    /// about to be read can't contain a badge.
    /// </summary>
    public void BeforeCapture(bool recognitionSeesDesktop)
    {
        lock (_lock)
        {
            if (_recognitionSeesDesktop == recognitionSeesDesktop) return;
            _recognitionSeesDesktop = recognitionSeesDesktop;
        }
        AppLog.Info($"capture visibility: recognition {(recognitionSeesDesktop ? "reads the desktop" : "reads the game window only")}");
        Reapply();
        if (recognitionSeesDesktop)
        {
            // Always wait here, even when the windows were already hidden: the UI thread may have hidden them a moment
            // ago. DwmFlush blocks until DWM presents its next frame; the affinity change takes effect at a
            // composition, so after two of them the desktop the GDI fallback reads no longer has our windows. They
            // can't come back meanwhile: the policy keeps them hidden while recognition reads the desktop.
            var hr1 = DwmFlush();
            var hr2 = DwmFlush();
            if (hr1 != 0 || hr2 != 0) AppLog.Warn($"capture visibility: DwmFlush failed (0x{hr1:X8}, 0x{hr2:X8}); the next desktop frame may still show the overlay");
        }
    }

    private void Reapply()
    {
        IntPtr[] windows;
        bool excluded;
        lock (_applyLock)
        {
            lock (_lock)
            {
                var want = CaptureExclusionPolicy.ShouldExclude(_showInCapture, _recognitionSeesDesktop);
                if (want == _excluded) return;
                _excluded = excluded = want;
                windows = [.. _windows];
            }
            foreach (var hwnd in windows) OverlayWindowHelper.SetExcludeFromCapture(hwnd, excluded);
        }
        AppLog.Info($"capture visibility: overlay {(excluded ? "hidden from" : "shown in")} screen capture ({windows.Length} windows)");
        ExcludedChanged?.Invoke(excluded);
    }
}
