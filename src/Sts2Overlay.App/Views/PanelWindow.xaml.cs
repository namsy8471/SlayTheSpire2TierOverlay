using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.Services;
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
    private readonly OverlayCaptureVisibility _captureVisibility;
    private readonly DispatcherTimer _saveTimer;
    private IntPtr _hwnd;
    private bool _positioned;
    private bool _userPlaced;    // a saved position was restored, or the user dragged the panel
    private bool _followedGame;  // the default spot was taken on the game's monitor (done once)
    private PixelRect? _gameRect;

    public PanelWindow(PanelViewModel vm, AppSettings settings, OverlayCaptureVisibility captureVisibility)
    {
        _settings = settings;
        _captureVisibility = captureVisibility;
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
        _captureVisibility.Register(_hwnd);
        Closed += (_, _) => _captureVisibility.Unregister(_hwnd);
    }

    /// <summary>
    /// Restores the saved physical position, or docks to the right edge of the work area of the game's monitor (the
    /// primary monitor while the game is not known yet; <see cref="FollowGame"/> moves it once the game is found).
    /// </summary>
    private void PlaceInitially()
    {
        if (_positioned || _hwnd == IntPtr.Zero) return;
        if (_settings.PanelX is int x && _settings.PanelY is int y && Dpi.IsOnAnyMonitor(x + 20, y + 20))
        {
            OverlayWindowHelper.SetPhysicalPosition(_hwnd, x, y, KeepOnTop);
            _userPlaced = true;
        }
        else
        {
            PlaceDefault();
        }
        _positioned = true;
        Dispatcher.BeginInvoke(EnsureOnScreen, DispatcherPriority.Background);
    }

    /// <summary>
    /// The game window was found at <paramref name="gameRect"/> (physical px). On a first run (no saved position, not
    /// dragged yet) the panel takes its default spot on that monitor, once.
    /// </summary>
    public void FollowGame(PixelRect gameRect)
    {
        // A saved position sets _userPlaced when PlaceInitially restores it; before that, PlaceInitially decides anyway.
        // (Not settings.PanelX: the panel's own nudges are saved too, and they are not the user's choice.)
        if (_followedGame || _userPlaced || gameRect.IsEmpty) return;
        _followedGame = true;
        _gameRect = gameRect;
        if (!_positioned || _hwnd == IntPtr.Zero) return; // PlaceInitially uses it when the panel is first shown
        PlaceDefault();
        Dispatcher.BeginInvoke(EnsureOnScreen, DispatcherPriority.Background);
    }

    private void PlaceDefault()
    {
        var work = _gameRect is { } g ? Dpi.WorkAreaForPoint(g.X + g.Width / 2, g.Y + g.Height / 2) : Dpi.WorkAreaForPoint(0, 0);
        // Physical width now (the UI scale keeps it the same on a monitor with another DPI); margins in that monitor's DPI.
        var widthPx = (int)(ActualWidth * GetDpiForWindow(_hwnd) / 96.0);
        var (px, py) = PanelPlacement.DefaultPosition(work, widthPx, Dpi.ScaleForRect(work));
        OverlayWindowHelper.SetPhysicalPosition(_hwnd, px, py, KeepOnTop);
    }

    /// <summary>
    /// Keeps the whole panel inside the work area of the monitor it is on (physical pixels). The scroll area's height is
    /// capped first, so a big UI 배율 on a small screen scrolls instead of running off the bottom.
    /// </summary>
    private void EnsureOnScreen()
    {
        if (!_positioned || _hwnd == IntPtr.Zero) return;
        var r = OverlayWindowHelper.GetPhysicalBounds(_hwnd);
        if (r.IsEmpty) return;
        var work = Dpi.WorkAreaForPoint(r.X + 20, r.Y + 20);
        if (work.IsEmpty) return;
        FitScrollHeight(r, work);
        var x = Math.Max(work.X, Math.Min(r.X, work.Right - r.Width));
        var y = Math.Max(work.Y, Math.Min(r.Y, work.Bottom - r.Height));
        if (x != r.X || y != r.Y) OverlayWindowHelper.SetPhysicalPosition(_hwnd, x, y, KeepOnTop);
    }

    /// <summary>Caps the scroll area so the whole panel fits the work area's height (re-checked on every size change).</summary>
    private void FitScrollHeight(PixelRect window, PixelRect work)
    {
        if (!Scroll.IsVisible || DataContext is not PanelViewModel vm) return;
        // Scroll DIPs → physical px: our UI-scale transform times the monitor's DPI.
        var dipToPx = vm.UiScale * GetDpiForWindow(_hwnd) / 96.0;
        var max = PanelPlacement.ScrollMaxHeight(work.Height, window.Height, Scroll.ActualHeight, dipToPx);
        if (Math.Abs(max - Scroll.MaxHeight) > 1) Scroll.MaxHeight = max;
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
        if (_typing && !IsInSearchBox(e.OriginalSource)) EndTyping(returnFocus: true); // 티어표 search: a click outside the box
        base.OnPreviewMouseDown(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        _userPlaced = true; // the game being found later must not move it away again
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

    // ---- 티어표 search box: the one place the panel takes the keyboard ------------------------------------------
    //
    // The panel is WS_EX_NOACTIVATE, so keys always go to the game. Typing needs the panel to be the foreground window,
    // so ONLY while the search box is in use: a click on the box drops NOACTIVATE and activates the panel (allowed: the
    // click is the last input event, so Windows lets this process take the foreground), and Enter / Esc / a click
    // anywhere else puts NOACTIVATE back and hands the foreground to the window that had it before (the game). If the
    // user clicks the game or Alt+Tabs, Windows moves the foreground itself; Deactivated then just restores the style.
    // While the panel is in front the tracker counts it as "the game is in front" (OwnForegroundRule), so the overlay
    // stays up; capture pauses until the game is back in front.

    private bool _typing;
    private IntPtr _focusReturn;

    private void TierSearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_typing || _hwnd == IntPtr.Zero) return;
        var before = GetForegroundWindow();
        OverlayWindowHelper.SetExStyle(_hwnd, WS_EX_NOACTIVATE, false);
        _typing = true;
        Activate();
        if (GetForegroundWindow() != _hwnd)
        {
            // Windows refused the foreground (focus-steal protection): stay a no-activate panel, the click does nothing.
            _typing = false;
            OverlayWindowHelper.SetExStyle(_hwnd, WS_EX_NOACTIVATE, true);
            AppLog.Info("tier search: the panel could not take the keyboard");
            e.Handled = true;
            return;
        }
        _focusReturn = before == _hwnd ? IntPtr.Zero : before;
        TierSearchBox.Focus(); // the TextBox's own mouse handling then places the caret
    }

    private void TierSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // During an IME composition the IME owns Enter/Esc (e.Key is ImeProcessed then), so they finish the syllable first.
        if (e.Key == Key.Escape)
        {
            TierSearchBox.Clear();
            EndTyping(returnFocus: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            EndTyping(returnFocus: true);
            e.Handled = true;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        EndTyping(returnFocus: false); // another window took the foreground already (game clicked, Alt+Tab, panel hidden)
        base.OnDeactivated(e);
    }

    /// <summary>Puts the panel back to no-activate; <paramref name="returnFocus"/> also gives the foreground back to the game.</summary>
    private void EndTyping(bool returnFocus)
    {
        if (!_typing) return;
        _typing = false;
        Keyboard.ClearFocus();
        OverlayWindowHelper.SetExStyle(_hwnd, WS_EX_NOACTIVATE, true);
        if (returnFocus && _focusReturn != IntPtr.Zero && IsWindow(_focusReturn)) SetForegroundWindow(_focusReturn);
        _focusReturn = IntPtr.Zero;
    }

    private bool IsInSearchBox(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, TierSearchBox)) return true;
        }
        return false;
    }
}

/// <summary>Where the panel goes and how tall its scroll area may be (pure, unit-tested; physical pixels).</summary>
public static class PanelPlacement
{
    /// <summary>The design height of the scroll area (PanelWindow.xaml), in the panel's own DIPs.</summary>
    public const double DesignScrollMaxHeight = 620;

    /// <summary>Never smaller than this, so a tiny screen still shows a few rows.</summary>
    public const double MinScrollHeight = 120;

    /// <summary>Top-left of a panel <paramref name="widthPx"/> wide docked to the right of <paramref name="work"/>.</summary>
    /// <param name="dpiScale">The monitor's scale (1.5 at 150 %): 24 DIPs from the right edge, 140 from the top.</param>
    public static (int X, int Y) DefaultPosition(PixelRect work, int widthPx, double dpiScale)
        => (work.Right - widthPx - (int)(24 * dpiScale), work.Y + (int)(140 * dpiScale));

    /// <summary>
    /// The scroll area's MaxHeight (its own DIPs) so the whole window fits <paramref name="workHeightPx"/>.
    /// Everything but the scroll area (header, tabs, notices, footer) keeps its height, so the scroll area gets the rest.
    /// </summary>
    /// <param name="windowHeightPx">The panel window's height now.</param>
    /// <param name="scrollHeightDip">The scroll area's height now, in its own DIPs.</param>
    /// <param name="dipToPx">Its DIPs → physical px (UI-scale transform × monitor DPI scale).</param>
    public static double ScrollMaxHeight(int workHeightPx, int windowHeightPx, double scrollHeightDip, double dipToPx)
    {
        if (dipToPx <= 0 || workHeightPx <= 0) return DesignScrollMaxHeight;
        var chromePx = Math.Max(0, windowHeightPx - scrollHeightDip * dipToPx);
        var fit = (workHeightPx - chromePx) / dipToPx;
        return Math.Clamp(Math.Floor(fit), MinScrollHeight, DesignScrollMaxHeight);
    }
}
