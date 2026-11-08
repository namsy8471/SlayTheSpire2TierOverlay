using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.ViewModels;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Views;

/// <summary>
/// The side panel. Topmost and never activated:
/// - WS_EX_NOACTIVATE: clicking a tab or dragging the panel leaves keyboard focus in the game. (Activating another
///   window would make the game lose focus, and some games minimise or drop out of fullscreen when that happens.)
/// - WS_EX_TOOLWINDOW: no taskbar button / Alt+Tab entry.
/// - WS_EX_TRANSPARENT (Ctrl+F7): click-through, for when the panel sits over something you need to click.
/// The position is stored in PHYSICAL pixels and restored with SetWindowPos, which stays correct across monitors
/// with different DPI (WPF's Left/Top are DIPs of whichever monitor the window is on).
/// </summary>
public partial class PanelWindow : Window
{
    private readonly AppSettings _settings;
    private readonly bool _excludeFromCapture;
    private readonly DispatcherTimer _saveTimer;
    private IntPtr _hwnd;
    private bool _positioned;

    public PanelWindow(PanelViewModel vm, AppSettings settings, bool excludeFromCapture)
    {
        _settings = settings;
        _excludeFromCapture = excludeFromCapture;
        InitializeComponent();
        DataContext = vm;

        // Debounce: LocationChanged fires continuously while dragging; save once it settles.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SavePosition();
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PlaceInitially();
        // A bigger UI 배율 or expanding the panel can push it past the screen edge: nudge it back inside.
        // Deferred: for SizeToContent windows WPF raises SizeChanged during layout, BEFORE the HWND itself is resized,
        // so GetWindowRect would still return the old size. Background priority runs after layout and rendering.
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(EnsureOnScreen, DispatcherPriority.Background);
        LocationChanged += (_, _) =>
        {
            if (!_positioned) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
    }

    public IntPtr Hwnd => _hwnd;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = OverlayWindowHelper.Handle(this);
        OverlayWindowHelper.SetExStyle(_hwnd, WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, true);
        if (_excludeFromCapture) OverlayWindowHelper.SetExcludeFromCapture(_hwnd, true);
    }

    /// <summary>Restores the saved physical position, or docks to the right edge of the primary monitor's work area.</summary>
    private void PlaceInitially()
    {
        if (_positioned || _hwnd == IntPtr.Zero) return;
        if (_settings.PanelX is int x && _settings.PanelY is int y && Dpi.IsOnAnyMonitor(x + 20, y + 20))
        {
            OverlayWindowHelper.SetPhysicalPosition(_hwnd, x, y, KeepOnTop);
        }
        else
        {
            var work = Dpi.WorkAreaForPoint(0, 0);
            var scale = GetDpiForWindow(_hwnd) / 96.0;
            var widthPx = (int)(ActualWidth * scale);
            OverlayWindowHelper.SetPhysicalPosition(_hwnd, work.Right - widthPx - (int)(24 * scale), work.Y + (int)(140 * scale), KeepOnTop);
        }
        _positioned = true;
        Dispatcher.BeginInvoke(EnsureOnScreen, DispatcherPriority.Background);
    }

    /// <summary>Keeps the whole panel inside the work area of the monitor it is on (physical pixels).</summary>
    private void EnsureOnScreen()
    {
        if (!_positioned || _hwnd == IntPtr.Zero) return;
        var r = OverlayWindowHelper.GetPhysicalBounds(_hwnd);
        if (r.IsEmpty) return;
        var work = Dpi.WorkAreaForPoint(r.X + 20, r.Y + 20);
        if (work.IsEmpty) return;
        var x = Math.Max(work.X, Math.Min(r.X, work.Right - r.Width));
        var y = Math.Max(work.Y, Math.Min(r.Y, work.Bottom - r.Height));
        if (x != r.X || y != r.Y) OverlayWindowHelper.SetPhysicalPosition(_hwnd, x, y, KeepOnTop);
    }

    /// <summary>Ctrl+F7: let mouse input fall through to the game.</summary>
    public void SetClickThrough(bool on)
    {
        if (_hwnd != IntPtr.Zero) OverlayWindowHelper.SetExStyle(_hwnd, WS_EX_TRANSPARENT, on);
    }

    /// <summary>
    /// "항상 맨 앞" (tray / Ctrl+F6). Off: the panel is a normal window that other windows (and the game) can cover;
    /// clicking it raises it once. The badge layer stays on top either way (it only appears over the game's cards).
    /// </summary>
    public bool KeepOnTop
    {
        get => _keepOnTop;
        set
        {
            _keepOnTop = value;
            Topmost = value;
            if (!value) OverlayWindowHelper.ClearTopmost(_hwnd);
        }
    }

    private bool _keepOnTop = true;

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (!_keepOnTop) OverlayWindowHelper.BringToTop(_hwnd);
        base.OnPreviewMouseDown(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        // DragMove runs the system move loop (like dragging a title bar); it does not activate a NOACTIVATE window.
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Mouse button already released.
        }
    }

    public void SavePosition()
    {
        if (_hwnd == IntPtr.Zero || !_positioned) return;
        var r = OverlayWindowHelper.GetPhysicalBounds(_hwnd);
        if (r.IsEmpty) return;
        _settings.PanelX = r.X;
        _settings.PanelY = r.Y;
        SettingsStore.Save(AppPaths.SettingsFile, _settings);
    }
}
