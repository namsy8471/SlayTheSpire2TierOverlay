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

    /// <summary>Like <see cref="SetPhysicalBounds"/>, but the window keeps its place in the z-order.</summary>
    public static void SetPhysicalBoundsInPlace(IntPtr hwnd, PixelRect r)
    {
        SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
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
/// <para>The native calls and the log are injectable for tests. <paramref name="setBoundsInPlace"/> (default:
/// <paramref name="setBounds"/>) is the move used by <see cref="SetTrimmed"/>, which must not lift the layer above the
/// panel the way a placement does (the controller re-orders the windows after a placement).</para>
/// </summary>
internal sealed class LayerPlacement(string name, Func<IntPtr, PixelRect> getBounds, Action<IntPtr, PixelRect> setBounds, Action<string> log,
    Action<IntPtr, PixelRect>? setBoundsInPlace = null)
{
    private string? _lastMismatch;

    public LayerPlacement(string name)
        : this(name, OverlayWindowHelper.GetPhysicalBounds, OverlayWindowHelper.SetPhysicalBounds, AppLog.Info, OverlayWindowHelper.SetPhysicalBoundsInPlace)
    {
    }

    /// <summary>Where the layer should be (empty until the first placement).</summary>
    public PixelRect Intended { get; private set; }

    /// <summary>
    /// The layer is cut down to one pixel at the game's top-left corner (<see cref="SetTrimmed"/>): an empty layer that
    /// gives its full-screen surface back. It still follows the game (and so its monitor) while trimmed.
    /// </summary>
    public bool Trimmed { get; private set; }

    /// <summary>Where the window is put: <see cref="Intended"/>, or its 1x1 corner while trimmed.</summary>
    public PixelRect Target => Trimmed ? new PixelRect(Intended.X, Intended.Y, 1, 1) : Intended;

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

    /// <summary>
    /// Trims the layer to its 1x1 corner, or brings it back to the full rect. A WPF layered window keeps a surface of
    /// its own size for as long as it exists (hidden or not; tools/BadgeRenderBench --layer-mem), about 65 MB of RAM at
    /// 4K, and only a smaller size gives it back. Returns true when the state changed.
    /// </summary>
    public bool SetTrimmed(IntPtr hwnd, bool trimmed)
    {
        if (trimmed == Trimmed) return false;
        Trimmed = trimmed;
        Apply(hwnd, trimmed ? "trim" : "untrim", setBoundsInPlace ?? setBounds);
        return true;
    }

    /// <summary>After a DPI change: WPF may have resized the window to Windows' suggested rect; put it back.</summary>
    public void Reapply(IntPtr hwnd)
    {
        if (Intended.IsEmpty) return;
        var target = Target;
        var before = getBounds(hwnd);
        if (before != target) log($"{name} layer: window was {before} after a DPI change, intended {target}; placing it again");
        Apply(hwnd, "after a DPI change");
    }

    private void Apply(IntPtr hwnd, string when, Action<IntPtr, PixelRect>? move = null)
    {
        if (hwnd == IntPtr.Zero || Intended.IsEmpty) return;
        var target = Target;
        move ??= setBounds;
        // Twice at most: the first move may cross into a monitor with another DPI, and WPF's resize then wins.
        for (var i = 0; i < 2 && getBounds(hwnd) != target; i++) move(hwnd, target);
        var actual = getBounds(hwnd);
        if (actual == target)
        {
            _lastMismatch = null;
            return;
        }
        var key = $"{target}|{actual}";
        if (key == _lastMismatch) return;
        _lastMismatch = key;
        log($"{name} layer: intended {target}, actual {actual} ({when})");
    }
}

/// <summary>
/// When an empty layer is trimmed (<see cref="LayerPlacement.SetTrimmed"/>): after it has had nothing to draw for
/// <see cref="DelayMs"/>, so the map's short gaps (marks cleared while the map scrolls) never cost a resize, and never
/// while the controller holds it ready for marks it expects (<see cref="SetHold"/>). Marks bring it back at once,
/// before they are drawn. Pure: the window drives it from its content changes and a timer.
/// </summary>
internal sealed class EmptyLayerTrim(long delayMs)
{
    private long? _emptySince;
    private bool _hold;

    public long DelayMs => delayMs;

    /// <summary>
    /// The controller expects marks soon (the map is about to open, an event room is open): keep the layer full size,
    /// so the marks come up as fast as on a layer that was never trimmed. True when a trimmed layer must come back now.
    /// Once the hold ends, an empty layer waits a whole delay again before it is trimmed.
    /// </summary>
    public bool SetHold(bool hold, bool trimmed, long nowMs)
    {
        if (hold == _hold) return false;
        _hold = hold;
        if (!hold && _emptySince is not null) _emptySince = nowMs;
        return hold && trimmed;
    }

    /// <summary>
    /// The layer's content changed, or it was placed (a layer placed before its first marks counts as empty from then).
    /// True when it is trimmed and must be full size again before drawing.
    /// </summary>
    public bool OnContent(bool hasMarks, bool trimmed, long nowMs)
    {
        if (hasMarks)
        {
            _emptySince = null;
            return trimmed;
        }
        _emptySince ??= nowMs;
        return false;
    }

    /// <summary>True when the layer is still empty, not trimmed yet, and has been empty long enough.</summary>
    public bool ShouldTrim(bool trimmed, long nowMs)
        => !trimmed && !_hold && _emptySince is { } since && nowMs - since >= delayMs;

    /// <summary>Whether a timer should run to call <see cref="ShouldTrim"/> later.</summary>
    public bool Waiting(bool trimmed) => !trimmed && _emptySince is not null;
}
