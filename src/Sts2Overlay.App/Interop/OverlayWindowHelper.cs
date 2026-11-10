using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
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

    /// <summary>"항상 맨 앞" off: drop the window out of the TOPMOST band (it then stacks like a normal window).</summary>
    public static void ClearTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Raises a non-topmost window above normal windows once (on click), without activating it.</summary>
    public static void BringToTop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Moves/resizes in PHYSICAL pixels. We bypass WPF's Left/Top/Width/Height (which are DIPs relative to the
    /// window's current monitor DPI) because the game rect we track is in physical pixels.
    /// </summary>
    public static void SetPhysicalBounds(IntPtr hwnd, PixelRect r)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <param name="topmost">false = keep the current z-order (the panel with "항상 맨 앞" off).</param>
    public static void SetPhysicalPosition(IntPtr hwnd, int x, int y, bool topmost = true)
    {
        var flags = SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | (topmost ? 0 : SWP_NOZORDER);
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, flags);
    }

    /// <summary>
    /// Fades a layer's content out (hover tip showing under it) or back in. Only the drawing changes: the window stays
    /// shown, click-through and topmost, so nothing else (visibility, z-order, capture exclusion) has to know.
    /// Out is quick, so the game's tip is clear almost at once; in is slower, so the badges don't pop.
    /// </summary>
    public static void FadeContent(Window w, bool hidden)
    {
        UIElement target = w.Content as UIElement ?? w;
        var to = hidden ? 0.0 : 1.0;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(hidden ? 50 : 160));
        target.BeginAnimation(UIElement.OpacityProperty, anim);
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

/// <summary>
/// Keeps a full-game layer window (badges, map marks, event markers) exactly on the game's client rect.
///
/// <para>The layers are created at (0,0) on the primary monitor and moved over the game with SetWindowPos. When the game
/// is on a monitor with another DPI scale, the move sends WM_DPICHANGED and WPF resizes the window to the rect Windows
/// suggests (the old size scaled by the DPI ratio), so the layer could cover only part of the game. So every placement
/// is checked with GetWindowRect and done again when the window is not where it should be, and a DPI change puts it
/// back (<see cref="Reapply"/>). A rect that still differs is logged once at INFO, so it shows in a user's app.log.</para>
/// <para>The two native calls and the log are injectable for tests.</para>
/// </summary>
internal sealed class LayerPlacement(string name, Func<IntPtr, PixelRect> getBounds, Action<IntPtr, PixelRect> setBounds, Action<string> log)
{
    private string? _lastMismatch;

    public LayerPlacement(string name)
        : this(name, OverlayWindowHelper.GetPhysicalBounds, OverlayWindowHelper.SetPhysicalBounds, AppLog.Info)
    {
    }

    /// <summary>Where the layer should be (empty until the first placement).</summary>
    public PixelRect Intended { get; private set; }

    /// <summary>
    /// Puts the window on <paramref name="rect"/> unless it already is there. Returns true when the intended rect
    /// changed (the layer's content must be laid out again).
    /// </summary>
    public bool Place(IntPtr hwnd, PixelRect rect)
    {
        var changed = rect != Intended;
        Intended = rect;
        Apply(hwnd, "placement");
        return changed;
    }

    /// <summary>After a DPI change: WPF may have resized the window to Windows' suggested rect; put it back.</summary>
    public void Reapply(IntPtr hwnd)
    {
        if (Intended.IsEmpty) return;
        var before = getBounds(hwnd);
        if (before != Intended) log($"{name} layer: window was {before} after a DPI change, intended {Intended}; placing it again");
        Apply(hwnd, "after a DPI change");
    }

    private void Apply(IntPtr hwnd, string when)
    {
        if (hwnd == IntPtr.Zero || Intended.IsEmpty) return;
        // Twice at most: the first move may cross into a monitor with another DPI, and WPF's resize then wins.
        for (var i = 0; i < 2 && getBounds(hwnd) != Intended; i++) setBounds(hwnd, Intended);
        var actual = getBounds(hwnd);
        if (actual == Intended)
        {
            _lastMismatch = null;
            return;
        }
        var key = $"{Intended}|{actual}";
        if (key == _lastMismatch) return;
        _lastMismatch = key;
        log($"{name} layer: intended {Intended}, actual {actual} ({when})");
    }
}
