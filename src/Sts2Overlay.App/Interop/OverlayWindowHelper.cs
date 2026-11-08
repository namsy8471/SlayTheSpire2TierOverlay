using System.Windows;
using System.Windows.Interop;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Interop;

/// <summary>
/// Window-style plumbing shared by the panel and the badge layer.
///
/// Why extended styles instead of WPF properties? WPF has no property for "never take focus" or "let clicks fall
/// through". Those are Win32 extended styles (WS_EX_*) on the window's HWND, so we flip them with
/// GetWindowLong/SetWindowLong once WPF has created the HWND (Window.SourceInitialized).
/// </summary>
internal static class OverlayWindowHelper
{
    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    /// <summary>Adds (<paramref name="on"/>=true) or removes extended style bits.</summary>
    public static void SetExStyle(IntPtr hwnd, int bits, bool on)
    {
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        var next = on ? ex | bits : ex & ~bits;
        if (next != ex) SetWindowLong(hwnd, GWL_EXSTYLE, next);
    }

    /// <summary>
    /// Puts the window back at the top of the TOPMOST band without moving or activating it.
    /// Some fullscreen apps (and the shell) re-order themselves above other topmost windows, so we re-assert every second.
    /// </summary>
    public static void AssertTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Moves/resizes in PHYSICAL pixels. We bypass WPF's Left/Top/Width/Height (which are DIPs relative to the
    /// window's current monitor DPI) because the game rect we track is in physical pixels.
    /// </summary>
    public static void SetPhysicalBounds(IntPtr hwnd, PixelRect r)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public static void SetPhysicalPosition(IntPtr hwnd, int x, int y)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public static PixelRect GetPhysicalBounds(IntPtr hwnd)
        => GetWindowRect(hwnd, out var r) ? PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom) : default;

    /// <summary>
    /// Keeps our windows out of screen captures so the badges never pollute the frames we recognise
    /// (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+). Falls back silently on older systems.
    /// </summary>
    public static void SetExcludeFromCapture(IntPtr hwnd, bool exclude)
    {
        if (!SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE))
        {
            AppLog.Warn($"SetWindowDisplayAffinity({exclude}) failed for 0x{hwnd:X}");
        }
    }
}
