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
using Sts2Overlay.Core.Localization;
using Velopack;

namespace Sts2Overlay.App;

/// <summary>
/// Composition root: reads settings and arguments, creates services and windows, wires hotkeys and tray.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private bool _settingsLoaded; // OnExit saves only settings that were read in this run
    private GameWindowTracker? _tracker;
    private CapturePipeline? _pipeline;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private AppController? _controller;
    private PanelWindow? _panel;
    private PanelViewModel? _vm;
    private LocalResourceService? _resources;
    private UpdateService? _updates;
    private GameLanguageWatcher? _gameLanguage;
    private DispatcherTimer? _languageTimer;

    /// <summary>
    /// Entry point (StartupObject in the csproj; App.xaml is a Page, so WPF does not generate its own Main).
    /// Velopack first: when Setup.exe / Update.exe start the exe with a --veloapp-* hook argument, Run() handles it and
    /// exits before any window or mutex exists. Otherwise it returns at once and WPF starts as before
    /// (OnStartup still gets the command line through StartupEventArgs.Args).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        return app.Run(); // the code passed to Shutdown(..), e.g. --prepare / --verify-capture results
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Init(AppPaths.LogFile);
        AppLog.Info($"---- start {string.Join(' ', e.Args)} (exe {AppContext.BaseDirectory})");
        AppLog.Info($"data: {AppPaths.Layout.Kind}, content {AppPaths.ContentRoot}, local {AppPaths.LocalDataDir}");
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

        // Language first, so every text below (including --prepare's console lines) is in the right one.
        _settings = SettingsStore.Load(AppPaths.SettingsFile);
        _settingsLoaded = true;
        _gameLanguage = new GameLanguageWatcher(Sts2Overlay.GameState.GamePaths.DefaultRoot);
        ApplyLanguage();

        // ---- local resources: headless --prepare (no windows, no single-instance check) ----
        if (e.Args.Any(a => a.Equals("--prepare", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(PrepareCommand.Run(AppOptions.Parse(e.Args), _settings));
            return;
        }

        // One instance only: a second copy could not register the hotkeys anyway.
        _singleInstance = new Mutex(true, @"Local\Sts2Overlay.App", out var first);
        if (!first)
        {
            AppLog.Warn("another instance is running; exiting");
            MessageBox.Show(Loc.T("STS2 티어 오버레이가 이미 실행 중입니다 (알림 영역 아이콘 확인).",
                "STS2 Tier Overlay is already running (see the notification area icon)."), UiText.Instance.AppName);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("unhandled UI exception", args.Exception);
            args.Handled = true; // keep the overlay alive; the error is in the log
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("unhandled exception", args.ExceptionObject as Exception);

        // Leftover debug screenshots (old OverlaySpike bursts, earlier Ctrl+F9 saves) are deleted unless the user keeps
        // them on purpose ("debugCaptures"). Runs in the background; recognition never reads these files.
        _ = CaptureCleanup.RunAtStartup(_settings.DebugCaptures);

        // --show-in-capture forces it on for this run (old debug flag); recognition stays safe either way.
        var captureVisibility = new OverlayCaptureVisibility(_settings.ShowOverlayInCapture || options.ShowInCapture);
        var packDir = AppPaths.ResolvePackDir(options.PackDir ?? _settings.PackDir);
        var catalog = AppPaths.FindLocalCatalog(options.CatalogPath ?? _settings.CatalogPath);

        _vm = new PanelViewModel
        {
            IsCollapsed = _settings.PanelCollapsed,
            SelectedTab = Enum.TryParse<PanelTab>(_settings.SelectedTab, out var tab) ? tab : PanelTab.Offer,
            PanelBackground = PanelBrush(_settings.PanelOpacity),
        };

        _tracker = new GameWindowTracker();
        _pipeline = new CapturePipeline(() => _tracker.Current, _settings.CaptureBackend, captureVisibility);

        var badges = new BadgeWindow(captureVisibility);
        _panel = new PanelWindow(_vm, _settings, captureVisibility);
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

        _updates = new UpdateService(_settings);
        // Both places: the panel line is hidden whenever the game is not in front (exactly when the tray is used),
        // so the tray balloon is what the user actually sees after "업데이트 확인".
        _updates.Message += text =>
        {
            _controller.ShowMessage(text);
            _tray?.ShowBalloon(Loc.T("STS2 티어 오버레이 업데이트", "STS2 Tier Overlay update"), text);
        };
        _updates.StateChanged += () =>
        {
            RefreshUpdateText();
            UpdateTray();
        };

        SetUpTray();
        SetUpHotkeys(); // after the tray: a key another program holds is also reported with a balloon
        _controller.StateChanged += UpdateTray;
        Loc.Changed += OnLanguageChanged;
        StartLanguageWatch();
        _tracker.Refresh();
        _controller.UpdateVisibility();

        AppLog.Info($"ready: pack {packDir}, catalog {catalog ?? "(none)"}, {_vm.UiScaleText} (layout x{_vm.UiScale:0.###}), overlay in screen capture {!captureVisibility.IsExcluded}");
        if (options.SelfTest)
        {
            RunSelfTest();
            return;
        }
        _tray?.ShowBalloon(UiText.Instance.AppName, options.Demo is not null
            ? Loc.T("데모 모드로 실행 중입니다.", "Running in demo mode.")
            : Loc.T("실행 중입니다. 게임이 앞에 있을 때 표시됩니다.", "Running. Shown while the game is in front."));
        _updates.Start();
    }

    /// <summary>
    /// --selftest: after 2 s save one capture (even with "debugCaptures" off; the next normal start removes it) and
    /// recognise once, then exit after 6 s (results are in app.log).
    /// </summary>
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
                _controller!.SaveCapture(force: true);
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
        _hotkeys.AddCtrl(HotkeyManager.VK_F5, "F5", () => _controller!.ToggleMapOverlay());
        _hotkeys.AddCtrl(HotkeyManager.VK_F6, "F6", () => _controller!.ToggleAlwaysOnTop());
        _hotkeys.AddCtrl(HotkeyManager.VK_F7, "F7", () => _controller!.ToggleClickThrough());
        _hotkeys.AddCtrl(HotkeyManager.VK_F8, "F8", () => _controller!.ToggleHidden());
        // Debug-only screenshot key: registered only with "debugCaptures" on, so Ctrl+F9 stays free for other programs.
        if (_settings.DebugCaptures) _hotkeys.AddCtrl(HotkeyManager.VK_F9, "F9", () => _controller!.SaveCapture());
        _hotkeys.AddCtrl(HotkeyManager.VK_F10, "F10", () => _controller!.RecognizeNow());
        _hotkeys.AddCtrl(HotkeyManager.VK_F11, "F11", () => _controller!.ToggleDebugOutlines());
        // Registered only while the game (or our tray menu opened from it) is in front, so Ctrl+F5 still refreshes the
        // browser and so on; every activation retries the keys another program held.
        _tracker!.Changed += (_, _) => UpdateHotkeys();
        UpdateHotkeys();
    }

    private bool _hotkeyBalloonShown;

    private void UpdateHotkeys()
    {
        if (_hotkeys is null || _controller is null) return;
        if (!_hotkeys.SetActive(HotkeyPolicy.ShouldBeActive(_tracker?.Current))) return;
        var failed = _hotkeys.Failed;
        var spike = false;
        if (failed.Count > 0)
        {
            var procs = Process.GetProcessesByName("OverlaySpike");
            spike = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
        }
        _controller.SetHotkeyFailures(failed, spike);
        // The panel line stays until the keys register; the balloon once per run (the panel may be hidden right now).
        // Deferred, so the "실행 중입니다" balloon at the end of OnStartup does not replace it.
        if (failed.Count > 0 && !_hotkeyBalloonShown && _tray is not null && AppController.HotkeyNotice(failed, spike) is { } text)
        {
            _hotkeyBalloonShown = true;
            Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(UiText.Instance.AppName, text), DispatcherPriority.Background);
        }
    }

    private void SetUpTray()
    {
        _tray = new TrayIcon(_updates!.CurrentVersion, devBuild: !_updates.IsInstalled);
        _tray.ExitRequested += () => Shutdown();
        _tray.CheckUpdateRequested += () => _ = _updates.CheckNowAsync();
        _tray.ApplyUpdateRequested += () =>
        {
            // Normal shutdown first (settings saved in OnExit), then Velopack swaps the files and restarts us.
            if (_updates.RequestApplyAndRestart()) Shutdown();
        };
        _tray.ToggleVisibilityRequested += () => _controller!.ToggleHidden();
        _tray.RecognizeRequested += () => _controller!.RecognizeNow();
        _tray.UiScaleRequested += s => _controller!.SetUiScale(s);
        _tray.DemoRequested += s => _controller!.SetDemo(s);
        _tray.DebugOutlinesRequested += () => _controller!.ToggleDebugOutlines();
        _tray.AlwaysOnTopRequested += () => _controller!.ToggleAlwaysOnTop();
        _tray.ClickThroughRequested += () => _controller!.ToggleClickThrough();
        // While the menu is open the 1 s topmost re-assert must not lift the panel above it.
        _tray.MenuOpenChanged += open => _controller!.TrayMenuOpen = open;
        _tray.MapOverlayRequested += () => _controller!.ToggleMapOverlay();
        _tray.ShowInCaptureRequested += () =>
        {
            _controller!.ToggleShowInCapture();
            UpdateTray();
        };
        // Raised on the capture thread when a GDI frame hides the overlay (or window capture shows it again).
        _pipeline!.CaptureVisibility.ExcludedChanged += _ => Dispatcher.BeginInvoke(UpdateTray);
        _tray.ReloadPackRequested += () => _controller!.ReloadPack();
        _tray.LanguageRequested += setting =>
        {
            _settings.Language = GameLanguage.NormalizeSetting(setting);
            SettingsStore.Save(AppPaths.SettingsFile, _settings);
            _tray.SetLanguageSetting(_settings.Language);
            ApplyLanguage();
        };
        _tray.SetLanguageSetting(_settings.Language);
        UpdateTray();
    }

    // ---- language: the "언어 / Language" setting + the game's own language (Services.GameLanguage) ----

    /// <summary>
    /// Sets <see cref="Loc"/> from the setting: a fixed kor/eng, or on "auto" the game's settings.save. Unknown (auto, and
    /// the game's file is missing or unreadable) keeps the current language. A change raises <see cref="Loc.Changed"/>,
    /// which re-renders everything (<see cref="OnLanguageChanged"/>).
    /// </summary>
    private void ApplyLanguage()
    {
        string? gameCode = null;
        if (GameLanguage.NormalizeSetting(_settings.Language) == GameLanguage.Auto && _gameLanguage is not null)
        {
            _gameLanguage.Poll();
            gameCode = _gameLanguage.Code;
        }
        if (GameLanguage.Resolve(_settings.Language, gameCode) is not { } lang || lang == Loc.Current) return;
        AppLog.Info($"language: {Loc.Code(lang)} (setting {_settings.Language}, game {gameCode ?? "unknown"})");
        Loc.SetLanguage(lang);
    }

    /// <summary>On "auto", re-reads the game's language every few seconds (the player can change it mid-session).</summary>
    private void StartLanguageWatch()
    {
        // The game's "Aspect Ratio" option comes from the same settings.save read: polled whatever the language setting.
        _gameLanguage!.AspectRatioChanged += () => _controller?.SetGameAspectRatio(_gameLanguage.AspectRatio);
        _controller?.SetGameAspectRatio(_gameLanguage.AspectRatio);
        _languageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _languageTimer.Tick += (_, _) =>
        {
            _gameLanguage.Poll(); // cheap when the file is unchanged; ApplyLanguage's own poll then does nothing
            if (GameLanguage.NormalizeSetting(_settings.Language) == GameLanguage.Auto) ApplyLanguage();
        };
        _languageTimer.Start();
    }

    /// <summary>Re-renders every text in the new language: panel and badges (controller), banners, tray menu.</summary>
    private void OnLanguageChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnLanguageChanged);
            return;
        }
        // The XAML's fixed texts follow by themselves (ViewModels.UiText listens to Loc.Changed).
        _controller?.RefreshLanguage();
        _resources?.RefreshLanguage();
        RefreshUpdateText();
        _tray?.RelabelMenu();
        UpdateTray();
    }

    private void RefreshUpdateText()
    {
        if (_vm is null || _updates is null) return;
        _vm.UpdateText = _updates.ReadyVersion is { } v
            ? Loc.T($"새 버전 v{v} 준비됨 — 종료하면 적용 (트레이: 업데이트 적용)", $"New version v{v} ready — applied on exit (tray: Apply update)")
            : null;
    }

    private void UpdateTray()
    {
        if (_tray is null || _controller is null) return;
        var (scale, isAuto) = _controller.CurrentUiScale;
        _tray.SetState(_controller.UserHidden, _controller.Demo, scale, isAuto, UiText.Instance.AppName + " — " + _controller.StatusLine);
        _tray.SetDebugOutlines(_controller.DebugOutlines);
        _tray.SetAlwaysOnTop(_controller.AlwaysOnTop);
        _tray.SetClickThrough(_controller.ClickThrough);
        _tray.SetMapOverlay(_controller.MapOverlayOn);
        _tray.SetShowInCapture(_controller.ShowOverlayInCapture, _controller.CaptureForcedHidden);
        _tray.SetUpdateReady(_updates?.ReadyVersion);
        _tray.SetUpdateChecking(_updates?.IsChecking == true);
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
            Loc.Changed -= OnLanguageChanged;
            _languageTimer?.Stop();
            // Only settings read in this run: --verify-capture / --bench-capture exit before loading them, and saving
            // the fresh defaults would overwrite the user's file.
            if (_settingsLoaded)
            {
                _panel?.SavePosition(); // also saves the other settings
                SettingsStore.Save(AppPaths.SettingsFile, _settings);
            }
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _resources?.Dispose();
            _controller?.Dispose();
            _pipeline?.Dispose();
            _tracker?.Dispose();
            _updates?.Dispose();
            _updates?.ApplyOnExit(); // only does something when an update was downloaded
            AppLog.Info("---- exit");
        }
        finally
        {
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
