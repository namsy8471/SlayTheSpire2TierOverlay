using System.Diagnostics;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Services;

/// <summary>Where the game window is and whether it is in front. Immutable; physical pixels.</summary>
/// <param name="Hwnd">Game top-level window (zero when simulated).</param>
/// <param name="WindowRect">GetWindowRect: Godot fullscreen makes this 2 px larger than the monitor (PLAN §5.4).</param>
/// <param name="CaptureRect">Client area clipped to its monitor: what we capture and what the badge layer covers.</param>
/// <param name="IsForeground">The game owns the foreground window (keyboard focus).</param>
/// <param name="IsOwnForeground">One of OUR windows (tray menu...) is the foreground window.</param>
/// <param name="IsSimulated">Demo mode: no game, the primary monitor stands in for it.</param>
public sealed record GameWindowInfo(
    IntPtr Hwnd,
    int ProcessId,
    PixelRect WindowRect,
    PixelRect CaptureRect,
    bool IsMinimized,
    bool IsForeground,
    bool IsOwnForeground,
    bool IsSimulated)
{
    /// <summary>Show the overlays: game visible and either it or one of our windows is in front.</summary>
    public bool CanShowOverlay => !IsMinimized && !CaptureRect.IsEmpty && (IsForeground || IsOwnForeground || IsSimulated);

    /// <summary>Capture only while the game itself is in front; otherwise we would grab whatever covers it.</summary>
    public bool CanCapture => !IsMinimized && !CaptureRect.IsEmpty && (IsForeground || IsSimulated);
}

/// <summary>
/// Finds the Slay the Spire 2 window and follows its position and foreground state.
///
/// Two mechanisms:
/// - WinEvent hooks (EVENT_SYSTEM_FOREGROUND, minimise start/end) react instantly to Alt+Tab. "Out of context" hooks
///   are delivered as messages to the thread that installed them, so the callback runs on the WPF UI thread.
/// - A 250 ms timer catches moves/resizes and the game starting/exiting (cheap: a few Win32 calls; the process
///   search runs at most every 2 s while the game is not found).
/// </summary>
public sealed class GameWindowTracker : IDisposable
{
    public const string ProcessName = "SlayTheSpire2";

    private readonly DispatcherTimer _timer;
    private readonly WinEventDelegate _winEventProc; // must stay referenced: native code holds a pointer to it
    private readonly List<IntPtr> _hooks = [];
    private readonly int _ownPid = Environment.ProcessId;
    private IntPtr _hwnd;
    private int _pid;
    private DateTime _nextSearch;
    private volatile GameWindowInfo? _current;
    private bool _simulate;

    public GameWindowTracker()
    {
        _winEventProc = OnWinEvent;
        _hooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT));
        _hooks.Add(SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT));

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Null while the game is not running. Safe to read from any thread (immutable record, volatile field).</summary>
    public GameWindowInfo? Current => _current;

    /// <summary>Demo mode: pretend the game covers the primary monitor and is always in front.</summary>
    public bool Simulate
    {
        get => _simulate;
        set
        {
            _simulate = value;
            Refresh();
        }
    }

    public void Refresh()
    {
        var next = _simulate ? Simulated() : Probe();
        if (next != _current)
        {
            var wasFound = _current is not null && !_current.IsSimulated;
            _current = next;
            if (next is { IsSimulated: false } && !wasFound)
            {
                AppLog.Info($"game window found: pid {next.ProcessId}, class '{ClassName(next.Hwnd)}', window {next.WindowRect}, capture {next.CaptureRect}, dpi x{Dpi.ScaleForRect(next.CaptureRect):0.##}");
            }
            else if (next is null && wasFound)
            {
                AppLog.Info("game window lost");
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) => Refresh();

    private GameWindowInfo Simulated()
    {
        var mon = Dpi.PrimaryMonitorRect();
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out var fgPid);
        return new GameWindowInfo(IntPtr.Zero, 0, mon, mon, false, true, fgPid == _ownPid, true);
    }

    private GameWindowInfo? Probe()
    {
        if (!IsGameWindowStillValid())
        {
            _hwnd = IntPtr.Zero;
            if (DateTime.UtcNow < _nextSearch) return null;
            _nextSearch = DateTime.UtcNow.AddSeconds(2);
            if (!FindGameWindow()) return null;
        }

        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out var fgPid);
        var minimized = IsIconic(_hwnd) || !IsWindowVisible(_hwnd);
        GetWindowRect(_hwnd, out var wr);
        var windowRect = PixelRect.FromLtrb(wr.Left, wr.Top, wr.Right, wr.Bottom);

        // Client area in screen coordinates. For Godot's borderless fullscreen this equals the window rect.
        GetClientRect(_hwnd, out var cr);
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(_hwnd, ref origin);
        var client = new PixelRect(origin.X, origin.Y, cr.Right - cr.Left, cr.Bottom - cr.Top);

        // Clip to the monitor: the extra 2 px of Godot's fullscreen window are off-screen and can't be drawn on or captured.
        var capture = client.Intersect(Dpi.MonitorRectForWindow(_hwnd));
        return new GameWindowInfo(_hwnd, _pid, windowRect, capture, minimized, fgPid == (uint)_pid, fgPid == _ownPid, false);
    }

    private bool IsGameWindowStillValid()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return false;
        GetWindowThreadProcessId(_hwnd, out var pid);
        return pid == (uint)_pid;
    }

    private bool FindGameWindow()
    {
        var procs = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (var p in procs)
            {
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    _hwnd = h;
                    _pid = p.Id;
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        var n = GetClassName(hwnd, buf, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var h in _hooks)
        {
            if (h != IntPtr.Zero) UnhookWinEvent(h);
        }
        _hooks.Clear();
    }
}
