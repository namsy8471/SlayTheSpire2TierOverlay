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
    private const int HOTKEY_CLICKTHROUGH = 1, HOTKEY_QUIT = 2, HOTKEY_CAPTURE = 3;
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, VK_F7 = 0x76, VK_F9 = 0x78, VK_Q = 0x51;

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
        RegisterHotKey(_hwnd, HOTKEY_CAPTURE, MOD_CONTROL, VK_F9);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();

        // Auto-capture: watch godot.log; after a combat win or entering a shop, take a burst of
        // screenshots so the reward / shop screen gets captured without the user pressing anything.
        var logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        logTimer.Tick += (_, _) => PollLog();
        logTimer.Start();

        UpdateModeText();
        Log("started");
    }

    // ---- auto-capture -------------------------------------------------------------------------

    private static readonly string GameLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2", "logs", "godot.log");

    private long _logPos = -1;          // -1 = start at the current end (ignore old lines)
    private string _logCarry = "";      // partial last line between polls
    private int _burstLeft;             // shots remaining in the current burst
    private string _burstTag = "";
    private DateTime _nextShot;
    private byte[]? _lastThumb;         // to skip near-identical frames

    private void PollLog()
    {
        try
        {
            // FileShare.ReadWrite: the game keeps the file open for writing.
            using var fs = new FileStream(GameLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (_logPos < 0 || fs.Length < _logPos) _logPos = fs.Length; // first run, or game restarted (log rotated)
            if (fs.Length > _logPos)
            {
                fs.Seek(_logPos, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                string chunk = _logCarry + reader.ReadToEnd();
                _logPos = fs.Length;
                int lastNl = chunk.LastIndexOf('\n');
                _logCarry = lastNl >= 0 ? chunk[(lastNl + 1)..] : chunk;
                if (lastNl >= 0)
                {
                    foreach (string line in chunk[..lastNl].Split('\n')) OnLogLine(line);
                }
            }
        }
        catch (IOException) { /* game not running / log missing */ }

        if (_burstLeft > 0 && DateTime.Now >= _nextShot)
        {
            _burstLeft--;
            _nextShot = DateTime.Now.AddSeconds(2);
            CaptureGame(hidePanel: false, tag: _burstTag, skipIfSame: true);
        }
    }

    private void OnLogLine(string line)
    {
        if (line.Contains("has won against encounter")) StartBurst("reward", line);
        else if (line.Contains("Preloading 'Merchant Room'")) StartBurst("shop", line);
    }

    private void StartBurst(string tag, string line)
    {
        // 25 shots x 2 s = 50 s window; near-identical frames are skipped, so disk use stays small.
        _burstTag = tag;
        _burstLeft = 25;
        _nextShot = DateTime.Now.AddSeconds(1.5);
        _lastThumb = null;
        Log($"burst {tag}: {line.Trim()}");
        CaptureText.Text = $"자동 캡처 시작 ({tag})";
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
            else if (wParam.ToInt32() == HOTKEY_CAPTURE) CaptureGame(hidePanel: true, tag: "manual", skipIfSame: false);
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

    /// <summary>
    /// Saves a PNG of the game window. Simplest possible capture (GDI copy of the composed desktop):
    /// works if the game is a borderless/"fullscreen" window composed by DWM, which is how Godot 4.5
    /// implements fullscreen. If the PNG comes out black, we need Windows.Graphics.Capture instead.
    /// We hide our own panel for the shot so it doesn't cover the cards.
    /// </summary>
    private void CaptureGame(bool hidePanel, string tag, bool skipIfSame)
    {
        try
        {
            Process? game = Process.GetProcessesByName("SlayTheSpire2").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (game is null)
            {
                CaptureText.Text = "게임 창을 찾지 못함";
                return;
            }
            // Only capture while the game is in front (otherwise we'd save the desktop / other apps).
            if (GetForegroundWindow() != game.MainWindowHandle && !hidePanel) return;

            GetWindowRect(game.MainWindowHandle, out RECT r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;

            if (hidePanel)
            {
                Visibility = Visibility.Hidden;
                Dispatcher.Invoke(() => { }, DispatcherPriority.Render); // let the hide render first
                Thread.Sleep(80);
            }

            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
            }
            if (hidePanel) Visibility = Visibility.Visible;

            // Tiny 16x16 grayscale thumbnail: if it barely changed since the last shot, skip saving.
            byte[] thumb = Thumbnail(bmp);
            if (skipIfSame && _lastThumb is not null && MeanDiff(thumb, _lastThumb) < 3) return;
            _lastThumb = thumb;

            // Save at most 1920 px wide: card art is drawn at ~250x190 game pixels, so half of 4K
            // keeps full art detail while cutting file size ~4x.
            int outW = Math.Min(w, 1920), outH = (int)Math.Round(h * (outW / (double)w));
            using var scaled = new System.Drawing.Bitmap(bmp, outW, outH);

            string dir = Path.Combine(Path.GetDirectoryName(_logPath)!, "captures");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{tag}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            scaled.Save(file, System.Drawing.Imaging.ImageFormat.Png);

            CaptureText.Text = $"캡처 저장: {Path.GetFileName(file)} ({w}x{h} → {outW}x{outH})";
            Log($"capture {file} {w}x{h}");
        }
        catch (Exception ex)
        {
            Visibility = Visibility.Visible;
            CaptureText.Text = "캡처 실패: " + ex.Message;
            Log("capture failed: " + ex);
        }
    }

    private static byte[] Thumbnail(System.Drawing.Bitmap src)
    {
        using var t = new System.Drawing.Bitmap(src, 16, 16);
        var bytes = new byte[256];
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            var c = t.GetPixel(x, y);
            bytes[y * 16 + x] = (byte)((c.R * 3 + c.G * 6 + c.B) / 10);
        }
        return bytes;
    }

    private static double MeanDiff(byte[] a, byte[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    private void UpdateModeText() =>
        ModeText.Text = _clickThrough ? "클릭 통과: 켜짐 (마우스가 게임으로 감)" : "클릭 통과: 꺼짐 (패널 클릭 가능)";

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKey(_hwnd, HOTKEY_CLICKTHROUGH);
        UnregisterHotKey(_hwnd, HOTKEY_QUIT);
        UnregisterHotKey(_hwnd, HOTKEY_CAPTURE);
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
