using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OverlaySpike;

/// <summary>
/// Spike window. Checks three things the real overlay panel needs:
///  1. Topmost stays above the game in fullscreen (we re-assert HWND_TOPMOST every second).
///  2. WS_EX_NOACTIVATE: clicking the panel does not steal focus from the game.
///  3. WS_EX_TRANSPARENT toggle: click-through so mouse input reaches the game.
/// Results are appended to %LOCALAPPDATA%\Sts2Overlay\overlay-spike.log.
/// </summary>
public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    private const int HOTKEY_CLICKTHROUGH = 1, HOTKEY_QUIT = 2;
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, VK_F7 = 0x76, VK_Q = 0x51;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private IntPtr _hwnd;
    private bool _clickThrough;
    private readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "overlay-spike.log");

    public MainWindow()
    {
        InitializeComponent();
        Left = SystemParameters.PrimaryScreenWidth - Width - 24;
        Top = 120;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);

        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        RegisterHotKey(_hwnd, HOTKEY_CLICKTHROUGH, MOD_CONTROL, VK_F7);
        RegisterHotKey(_hwnd, HOTKEY_QUIT, MOD_CONTROL | MOD_SHIFT, VK_Q);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        UpdateModeText();
        Log("started");
    }

    private void Tick()
    {
        // Re-assert topmost: some fullscreen apps push themselves above other topmost windows.
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");

        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint pid);
        string fgName = "?";
        try { fgName = Process.GetProcessById((int)pid).ProcessName; } catch { }
        GetWindowRect(fg, out RECT r);
        string info = $"전면 창: {fgName} ({r.Right - r.Left}x{r.Bottom - r.Top} @ {r.Left},{r.Top})";
        if (GameText.Text != info)
        {
            GameText.Text = info;
            Log(info);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY)
        {
            if (wParam.ToInt32() == HOTKEY_CLICKTHROUGH) ToggleClickThrough();
            else if (wParam.ToInt32() == HOTKEY_QUIT) Close();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = _clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        UpdateModeText();
        Log($"clickThrough={_clickThrough}");
    }

    private void UpdateModeText() =>
        ModeText.Text = _clickThrough ? "클릭 통과: 켜짐 (마우스가 게임으로 감)" : "클릭 통과: 꺼짐 (패널 클릭 가능)";

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKey(_hwnd, HOTKEY_CLICKTHROUGH);
        UnregisterHotKey(_hwnd, HOTKEY_QUIT);
        Log("closed");
        base.OnClosed(e);
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
