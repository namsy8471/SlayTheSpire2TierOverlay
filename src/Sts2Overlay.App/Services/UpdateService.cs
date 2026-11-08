using System.IO;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Velopack;
using Velopack.Sources;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Velopack auto-update for installed builds (Setup.exe → <c>%LOCALAPPDATA%\namsy8471.Sts2Overlay</c>).
/// <list type="bullet">
/// <item>Does nothing unless the exe was installed by Velopack (<see cref="UpdateManager.IsInstalled"/>; a checkout,
/// dist\ or a plain publish folder are not) and the <c>autoUpdate</c> setting is on.</item>
/// <item>~15 s after start: check the source (<see cref="UpdateSourceSpec"/>) and, if there is a newer version, download
/// it in the background. Then again at most every 6 h (<see cref="UpdateSchedule"/>).</item>
/// <item>A downloaded update is applied when the overlay exits normally (silently, no restart), or right away from the
/// tray ("업데이트 적용 (재시작)"). It never restarts the overlay on its own.</item>
/// <item>Errors and being offline only go to app.log.</item>
/// </list>
/// All public members are called on the UI thread; events are raised there too.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly UpdateSchedule _schedule = new();
    private readonly UpdateManager? _manager;
    private readonly UpdateSourceSpec _source;
    private DispatcherTimer? _timer;
    private UpdateInfo? _ready;
    private bool _busy;
    private bool _restartRequested;

    public UpdateService(AppSettings settings)
    {
        _settings = settings;
        var spec = UpdateSourceSpec.TryParse(settings.UpdateUrl);
        if (spec is null)
        {
            AppLog.Warn($"update: updateUrl '{settings.UpdateUrl}' is not a URL or an absolute folder; using {UpdateSourceSpec.DefaultRepoUrl}");
        }
        _source = spec ?? UpdateSourceSpec.Default;
        try
        {
            _manager = new UpdateManager(CreateSource(_source));
            IsInstalled = _manager.IsInstalled;
        }
        catch (Exception ex)
        {
            // Velopack could not even locate itself: behave like a non-installed run.
            AppLog.Warn($"update: unavailable ({ex.GetType().Name}: {ex.Message})");
        }
        CurrentVersion = IsInstalled && _manager!.CurrentVersion is { } v ? v.ToString() : AppVersion.FromAssembly();
    }

    /// <summary>True when Velopack installed this exe (updates are possible).</summary>
    public bool IsInstalled { get; }

    /// <summary>"0.1.0".</summary>
    public string CurrentVersion { get; }

    /// <summary>The downloaded version waiting to be applied ("0.2.0"), else null.</summary>
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>A downloaded update became ready (<see cref="ReadyVersion"/>).</summary>
    public event Action? StateChanged;

    /// <summary>A short line for the panel (manual check results).</summary>
    public event Action<string>? Message;

    private static IUpdateSource CreateSource(UpdateSourceSpec spec) => spec.Kind switch
    {
        UpdateSourceKind.GitHub => new GithubSource(spec.Location, null, false),
        UpdateSourceKind.Web => new SimpleWebSource(spec.Location),
        _ => new SimpleFileSource(new DirectoryInfo(spec.Location)),
    };

    /// <summary>Schedules the first check (~15 s) and the periodic ones. No-op unless installed and <c>autoUpdate</c> is on.</summary>
    public void Start()
    {
        if (!IsInstalled)
        {
            AppLog.Info($"update: not an installed build (v{CurrentVersion}); automatic updates off");
            return;
        }
        if (!_settings.AutoUpdate)
        {
            AppLog.Info($"update: autoUpdate is off in settings (v{CurrentVersion})");
            return;
        }
        AppLog.Info($"update: v{CurrentVersion} installed; first check in {UpdateSchedule.FirstCheckDelay.TotalSeconds:0} s from {_source}");
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UpdateSchedule.FirstCheckDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = UpdateSchedule.PollInterval;
            _ = CheckAsync(manual: false);
        };
        _timer.Start();
    }

    /// <summary>Tray "업데이트 확인".</summary>
    public void CheckNow()
    {
        if (!IsInstalled)
        {
            Message?.Invoke("설치된 버전이 아니라서 업데이트를 확인할 수 없습니다 (개발용 실행).");
            return;
        }
        if (_ready is not null)
        {
            Message?.Invoke($"새 버전 v{ReadyVersion} 준비됨 — 종료하면 적용됩니다.");
            return;
        }
        _ = CheckAsync(manual: true);
    }

    private async Task CheckAsync(bool manual)
    {
        if (_manager is null || _busy || _ready is not null) return;
        var now = DateTime.UtcNow;
        if (!_schedule.ShouldCheck(now, manual))
        {
            if (manual) Message?.Invoke("방금 확인했습니다. 잠시 후 다시 시도해 주세요.");
            return;
        }
        _schedule.MarkChecked(now);
        _busy = true;
        try
        {
            if (manual) Message?.Invoke("업데이트 확인 중…");
            AppLog.Info($"update: checking {_source}");
            // Off the UI thread: the check/download do file and network I/O. The awaits resume on the UI thread.
            var info = await Task.Run(() => _manager.CheckForUpdatesAsync());
            if (info is null)
            {
                AppLog.Info($"update: up to date (v{CurrentVersion})");
                if (manual) Message?.Invoke($"최신 버전입니다 (v{CurrentVersion}).");
                return;
            }
            var target = info.TargetFullRelease.Version.ToString();
            AppLog.Info($"update: v{target} available; downloading");
            if (manual) Message?.Invoke($"새 버전 v{target} 내려받는 중…");
            await Task.Run(() => _manager.DownloadUpdatesAsync(info));
            _ready = info;
            AppLog.Info($"update: v{target} downloaded; applied on exit or from the tray");
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Offline, GitHub rate limit, a broken feed...: log only (the next periodic check tries again).
            AppLog.Warn($"update: check/download failed ({ex.GetType().Name}: {ex.Message})");
            if (manual) Message?.Invoke("업데이트 확인 실패 (인터넷 연결 확인, 자세한 내용은 로그).");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Tray "업데이트 적용 (재시작)": the caller then shuts the app down normally (settings are saved in OnExit), and
    /// <see cref="ApplyOnExit"/> hands over to Velopack's updater with restart = true. This is what
    /// <see cref="UpdateManager.ApplyUpdatesAndRestart"/> does too, minus its immediate Environment.Exit, which would skip OnExit.
    /// </summary>
    public bool RequestApplyAndRestart()
    {
        if (_ready is null) return false;
        _restartRequested = true;
        return true;
    }

    /// <summary>
    /// Called from App.OnExit. With a downloaded update, Velopack's Update.exe waits for this process to end, then swaps
    /// in the new version: silently and without restarting, unless <see cref="RequestApplyAndRestart"/> was used.
    /// </summary>
    public void ApplyOnExit()
    {
        if (_manager is null || _ready is null) return;
        try
        {
            AppLog.Info($"update: applying v{ReadyVersion} after exit (restart {_restartRequested})");
            _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: _restartRequested);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"update: could not start the updater ({ex.GetType().Name}: {ex.Message})");
        }
    }

    public void Dispose() => _timer?.Stop();
}
