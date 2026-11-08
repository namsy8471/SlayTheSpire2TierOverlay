using Sts2Overlay.App.Integration;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Interop;

/// <summary>
/// DPI helpers.
///
/// WPF measures everything in device-independent pixels (DIPs, 1/96 inch). On a monitor at 150 % scaling
/// (144 DPI) one DIP is 1.5 physical pixels. Win32 (GetWindowRect, BitBlt, the recogniser's card bounds) speaks
/// physical pixels. So: <c>dip = physical / (dpi / 96)</c>. Each monitor can have its own DPI, so always use the DPI of
/// the monitor the window is on.
/// </summary>
internal static class Dpi
{
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>Scale factor (1.0 = 96 DPI = 100 %) of the monitor that contains most of <paramref name="r"/>.</summary>
    public static double ScaleForRect(PixelRect r)
    {
        var mon = MonitorFromPoint(new POINT { X = r.X + r.Width / 2, Y = r.Y + r.Height / 2 }, MONITOR_DEFAULTTONEAREST);
        return ScaleForMonitor(mon);
    }

    public static double ScaleForMonitor(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
        {
            return dx / 96.0;
        }
        return 1.0;
    }

    /// <summary>Full bounds of the monitor nearest to a window, in physical pixels.</summary>
    public static PixelRect MonitorRectForWindow(IntPtr hwnd)
        => MonitorRect(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    public static PixelRect PrimaryMonitorRect()
        => MonitorRect(MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY));

    public static PixelRect WorkAreaForPoint(int x, int y)
    {
        var mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(mon, ref mi) ? PixelRect.FromLtrb(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom) : default;
    }

    /// <summary>True when the point lies on some monitor (used to reject a saved panel position on a disconnected screen).</summary>
    public static bool IsOnAnyMonitor(int x, int y)
        => MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONULL) != IntPtr.Zero;

    private static PixelRect MonitorRect(IntPtr mon)
    {
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        return mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi)
            ? PixelRect.FromLtrb(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom)
            : default;
    }
}
