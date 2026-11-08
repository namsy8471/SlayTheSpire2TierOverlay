using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;

namespace Sts2Overlay.App;

/// <summary>
/// Composition root: reads settings and arguments, creates services and windows, wires hotkeys and tray.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private GameWindowTracker? _tracker;
    private CapturePipeline? _pipeline;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private AppController? _controller;
    private PanelWindow? _panel;
    private PanelViewModel? _vm;
    private LocalResourceService? _resources;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Init(AppPaths.LogFile);
        AppLog.Info($"---- start {string.Join(' ', e.Args)} (exe {AppContext.BaseDirectory})");
        var options = AppOptions.Parse(e.Args);

        if (options.VerifyCapture)
        {
            var exitCode = 1;
            try
            {
                exitCode = CaptureVerify.Run(options) == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                AppLog.Error("capture verification failed", ex);
                Console.Error.WriteLine(ex);
            }
            Shutdown(exitCode);
            return;
        }

        if (options.BenchCapture)
        {
            // Measurement mode: no windows, no single-instance check (it may run next to the overlay).
            try
            {
                CaptureBench.Run(options);
            }
            catch (Exception ex)
            {
                AppLog.Error("capture benchmark failed", ex);
                Console.Error.WriteLine(ex);
            }
            Shutdown();
            return;
        }

        // ---- local resources: headless --prepare (no windows, no single-instance check) ----
        if (e.Args.Any(a => a.Equals("--prepare", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(PrepareCommand.Run(AppOptions.Parse(e.Args), SettingsStore.Load(AppPaths.SettingsFile)));
            return;
        }

        // One instance only: a second copy could not register the hotkeys anyway.
        _singleInstance = new Mutex(true, @"Local\Sts2Overlay.App", out var first);
        if (!first)
        {
            AppLog.Warn("another instance is running; exiting");
            MessageBox.Show("STS2 티어 오버레이가 이미 실행 중입니다 (알림 영역 아이콘 확인).", "STS2 티어 오버레이");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("unhandled UI exception", args.Exception);
            args.Handled = true; // keep the overlay alive; the error is in the log
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("unhandled exception", args.ExceptionObject as Exception);

        _settings = SettingsStore.Load(AppPaths.SettingsFile);
        var excludeFromCapture = _settings.HideOverlayFromCapture && !options.ShowInCapture;
        var packDir = AppPaths.ResolvePackDir(options.PackDir ?? _settings.PackDir);
        var catalog = AppPaths.FindLocalCatalog(options.CatalogPath ?? _settings.CatalogPath);

        _vm = new PanelViewModel
        {
            IsCollapsed = _settings.PanelCollapsed,
            SelectedTab = Enum.TryParse<PanelTab>(_settings.SelectedTab, out var tab) ? tab : PanelTab.Offer,
            PanelBackground = PanelBrush(_settings.PanelOpacity),
        };

        _tracker = new GameWindowTracker();
        _pipeline = new CapturePipeline(() => _tracker.Current, _settings.CaptureBackend);

        var badges = new BadgeWindow(excludeFromCapture);
        _panel = new PanelWindow(_vm, _settings, excludeFromCapture);
        // Create the HWNDs (SourceInitialized → window styles) without showing anything yet;
        // the controller shows them when the game is in front.
        new WindowInteropHelper(badges).EnsureHandle();
        new WindowInteropHelper(_panel).EnsureHandle();

        _controller = new AppController(_settings, _tracker, _pipeline, _panel, badges, _vm, packDir, catalog);
        var (state, recognizer) = options.Demo is { } demo ? IntegrationFactory.CreateDemo(demo) : IntegrationFactory.CreateReal();
        _controller.Attach(state, recognizer, options.Demo);

        // ---- local resources: first-run preparation + game-version guard (Services.LocalResourceService) ----
        _resources = new LocalResourceService(_vm, options.GameDir ?? _settings.GameDir, new Sts2Overlay.Resources.LocalLayout(AppPaths.LocalDataDir));
        _resources.Prepared += catalogPath => _controller.ApplyPreparedResources(catalogPath);
        _controller.PackReloaded += () => _resources.EvaluateGuard(_controller.Pack, _controller.PackDir);
        _resources.EvaluateGuard(_controller.Pack, _controller.PackDir);
        _resources.Start();

        SetUpHotkeys();
        SetUpTray();
        _controller.StateChanged += UpdateTray;
        _tracker.Refresh();
        _controller.UpdateVisibility();

        AppLog.Info($"ready: pack {packDir}, catalog {catalog ?? "(none)"}, {_vm.UiScaleText} (layout x{_vm.UiScale:0.###}), excludeFromCapture {excludeFromCapture}");
        if (options.SelfTest)
        {
            RunSelfTest();
            return;
        }
        _tray?.ShowBalloon("STS2 티어 오버레이",
            options.Demo is not null ? "데모 모드로 실행 중입니다." : "실행 중입니다. 게임이 앞에 있을 때 표시됩니다.");
    }

    /// <summary>--selftest: after 2 s save one capture and recognise once, then exit after 6 s (results are in app.log).</summary>
    private void RunSelfTest()
    {
        var step = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            step++;
            if (step == 1)
            {
                AppLog.Info($"selftest: window {_tracker?.Current?.ToString() ?? "(none)"}");
                _controller!.SaveCapture();
                _controller.RecognizeNow();
            }
            else if (step == 3)
            {
                timer.Stop();
                AppLog.Info("selftest: done");
                Shutdown();
            }
        };
        timer.Start();
    }

    private void SetUpHotkeys()
    {
        _hotkeys = new HotkeyManager();
        var failed = new List<string>();
        void Reg(uint vk, string name, Action a)
        {
            if (!_hotkeys!.RegisterCtrl(vk, name, a)) failed.Add("Ctrl+" + name);
        }
        Reg(HotkeyManager.VK_F7, "F7", () => _controller!.ToggleClickThrough());
        Reg(HotkeyManager.VK_F8, "F8", () => _controller!.ToggleHidden());
        Reg(HotkeyManager.VK_F9, "F9", () => _controller!.SaveCapture());
        Reg(HotkeyManager.VK_F10, "F10", () => _controller!.RecognizeNow());
        Reg(HotkeyManager.VK_F11, "F11", () => _controller!.ToggleDebugOutlines());
        if (failed.Count > 0) _controller!.ShowMessage($"단축키 등록 실패 (다른 프로그램이 사용 중): {string.Join(", ", failed)}");
    }

    private void SetUpTray()
    {
        _tray = new TrayIcon();
        _tray.ExitRequested += () => Shutdown();
        _tray.ToggleVisibilityRequested += () => _controller!.ToggleHidden();
        _tray.RecognizeRequested += () => _controller!.RecognizeNow();
        _tray.SaveCaptureRequested += () => _controller!.SaveCapture();
        _tray.UiScaleRequested += s => _controller!.SetUiScale(s);
        _tray.DemoRequested += s => _controller!.SetDemo(s);
        _tray.DebugOutlinesRequested += () => _controller!.ToggleDebugOutlines();
        _tray.ReloadPackRequested += () => _controller!.ReloadPack();
        _tray.OpenCapturesRequested += () =>
        {
            Directory.CreateDirectory(AppPaths.CapturesDir);
            OpenShell(AppPaths.CapturesDir);
        };
        _tray.OpenLogRequested += () =>
        {
            if (AppLog.FilePath is { } p && File.Exists(p)) OpenShell(p);
        };
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray is null || _controller is null) return;
        var (scale, isAuto) = _controller.CurrentUiScale;
        _tray.SetState(_controller.UserHidden, _controller.Demo, scale, isAuto, "STS2 티어 오버레이 — " + _controller.StatusLine);
        _tray.SetDebugOutlines(_controller.DebugOutlines);
    }

    private static void OpenShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn($"could not open {path}: {ex.Message}");
        }
    }

    private static SolidColorBrush PanelBrush(double opacity)
    {
        var a = (byte)Math.Round(Math.Clamp(opacity, 0.3, 1.0) * 255);
        var brush = new SolidColorBrush(Color.FromArgb(a, 18, 19, 25));
        brush.Freeze();
        return brush;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_vm is not null)
            {
                _settings.PanelCollapsed = _vm.IsCollapsed;
                _settings.SelectedTab = _vm.SelectedTab.ToString();
            }
            _panel?.SavePosition(); // also saves the other settings
            SettingsStore.Save(AppPaths.SettingsFile, _settings);
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _resources?.Dispose();
            _controller?.Dispose();
            _pipeline?.Dispose();
            _tracker?.Dispose();
            AppLog.Info("---- exit");
        }
        finally
        {
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
