using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Velopack;
using Velopack.Sources;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Velopack auto-update for installed builds (Setup.exe → <c>%LOCALAPPDATA%\namsy8471.Sts2Overlay</c>).
/// <list type="bullet">
/// <item>Automatic checks only run when the exe was installed by Velopack (<see cref="UpdateManager.IsInstalled"/>; a
/// checkout, dist\ or a plain publish folder are not) and the <c>autoUpdate</c> setting is on.</item>
/// <item>~15 s after start: check the source (<see cref="UpdateSourceSpec"/>) and, if there is a newer version, download
/// it in the background. Then again at most every 6 h (<see cref="UpdateSchedule"/>).</item>
/// <item>A downloaded update is applied when the overlay exits normally (silently, no restart), or right away from the
/// tray ("업데이트 적용 (재시작)"). It never restarts the overlay on its own.</item>
/// <item>The tray "업데이트 확인" always answers with a <see cref="Message"/>: checking, up to date, downloading,
/// downloaded (restart to apply), already ready, too soon, failed (with the reason) or not installed. A finished
/// download is announced even when an automatic check found it. Automatic checks are otherwise silent (app.log only).</item>
/// </list>
/// All public members are called on the UI thread; events are raised there too (in tests: on whichever thread the
/// awaited client call resumed).
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly UpdateSchedule _schedule = new();
    private readonly IUpdateClient? _client;
    private readonly UpdateSourceSpec _source;
    private readonly Func<DateTime> _utcNow;
    private DispatcherTimer? _timer;
    private bool _restartRequested;

    public UpdateService(AppSettings settings)
        : this(settings, null, () => DateTime.UtcNow)
    {
    }

    /// <param name="client">Null = the real Velopack client for the <c>updateUrl</c> setting.</param>
    internal UpdateService(AppSettings settings, IUpdateClient? client, Func<DateTime> utcNow)
    {
        _settings = settings;
        _utcNow = utcNow;
        var spec = UpdateSourceSpec.TryParse(settings.UpdateUrl);
        if (spec is null)
        {
            AppLog.Warn($"update: updateUrl '{settings.UpdateUrl}' is not a URL or an absolute folder; using {UpdateSourceSpec.DefaultRepoUrl}");
        }
        _source = spec ?? UpdateSourceSpec.Default;
        if (client is null)
        {
            try
            {
                client = new VelopackUpdateClient(_source);
            }
            catch (Exception ex)
            {
                // Velopack could not even locate itself: behave like a non-installed run.
                AppLog.Warn($"update: unavailable ({ex.GetType().Name}: {ex.Message})");
            }
        }
        _client = client;
        IsInstalled = client?.IsInstalled == true;
        CurrentVersion = IsInstalled && client!.CurrentVersion is { } v ? v : AppVersion.FromAssembly();
    }

    /// <summary>True when Velopack installed this exe (updates are possible).</summary>
    public bool IsInstalled { get; }

    /// <summary>"0.1.0".</summary>
    public string CurrentVersion { get; }

    /// <summary>The downloaded version waiting to be applied ("0.2.0"), else null.</summary>
    public string? ReadyVersion { get; private set; }

    /// <summary>A check or download is running (the tray disables "업데이트 확인" meanwhile).</summary>
    public bool IsChecking { get; private set; }

    /// <summary><see cref="ReadyVersion"/> or <see cref="IsChecking"/> changed.</summary>
    public event Action? StateChanged;

    /// <summary>A short Korean line for the user (tray balloon + panel): every manual check result, and a finished download.</summary>
    public event Action<string>? Message;

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

    /// <summary>Tray "업데이트 확인". Always answers with at least one <see cref="Message"/>.</summary>
    public Task CheckNowAsync()
    {
        if (!IsInstalled || _client is null)
        {
            Message?.Invoke(Loc.T($"개발용 실행(v{CurrentVersion})이라 업데이트를 확인하지 않습니다. 설치판(Setup.exe)에서만 업데이트됩니다.", $"Dev run (v{CurrentVersion}): updates are not checked. Only the installed build (Setup.exe) updates."));
            return Task.CompletedTask;
        }
        if (ReadyVersion is not null)
        {
            Message?.Invoke(ReadyText(ReadyVersion, ReadySummary));
            return Task.CompletedTask;
        }
        if (IsChecking)
        {
            Message?.Invoke(Loc.T("이미 업데이트를 확인하는 중입니다…", "Already checking for updates…"));
            return Task.CompletedTask;
        }
        return CheckAsync(manual: true);
    }

    /// <summary>A one-line summary of the downloaded version's release notes (null when it has none).</summary>
    public string? ReadySummary { get; private set; }

    /// <param name="summary">From the release notes, which are written in Korean whatever the UI language.</param>
    internal static string ReadyText(string version, string? summary = null)
    {
        var text = Loc.T($"새 버전 v{version} 다운로드 완료 — 재시작하면 적용됩니다 (트레이: 업데이트 적용). 그냥 종료해도 적용됩니다.",
            $"v{version} downloaded — restart to apply (tray: Apply update). It is also applied when you exit.");
        return summary is null ? text : text + "\n" + Loc.T($"바뀐 점: {summary}", $"What's new: {summary}");
    }

    private async Task CheckAsync(bool manual)
    {
        if (_client is null || IsChecking || ReadyVersion is not null) return;
        var now = _utcNow();
        if (!_schedule.ShouldCheck(now, manual))
        {
            if (manual) Message?.Invoke(Loc.T($"방금 확인했습니다 (현재 v{CurrentVersion}). 1분 뒤에 다시 눌러 주세요.", $"Just checked (current v{CurrentVersion}). Try again in a minute."));
            return;
        }
        _schedule.MarkChecked(now);
        SetChecking(true);
        var downloading = false;
        string? target = null;
        try
        {
            if (manual) Message?.Invoke(Loc.T("업데이트 확인 중…", "Checking for updates…"));
            AppLog.Info($"update: checking {_source}");
            // Off the UI thread: the check/download do file and network I/O. The awaits resume on the UI thread.
            target = await Task.Run(_client.CheckAsync);
            if (target is null)
            {
                AppLog.Info($"update: up to date (v{CurrentVersion})");
                if (manual) Message?.Invoke(Loc.T($"최신 버전입니다 (v{CurrentVersion}).", $"Up to date (v{CurrentVersion})."));
                return;
            }
            AppLog.Info($"update: v{target} available; downloading");
            if (manual) Message?.Invoke(Loc.T($"새 버전 v{target} 다운로드 중…", $"Downloading v{target}…"));
            downloading = true;
            await Task.Run(_client.DownloadAsync);
            ReadySummary = ReleaseNotesSummary.FromMarkdown(_client.ReleaseNotes);
            ReadyVersion = target;
            AppLog.Info($"update: v{target} downloaded; applied on exit or from the tray");
            // Announced for automatic checks too: otherwise the only sign is a tray menu item nobody opens.
            Message?.Invoke(ReadyText(target, ReadySummary));
        }
        catch (Exception ex)
        {
            // Offline, GitHub rate limit, a broken feed...: the next periodic check tries again.
            AppLog.Warn($"update: {(downloading ? "download" : "check")} failed ({ex.GetType().Name}: {ex.Message})");
            if (manual)
            {
                Message?.Invoke(downloading
                    ? Loc.T($"v{target} 다운로드 실패: {DescribeError(ex)}", $"v{target} download failed: {DescribeError(ex)}")
                    : Loc.T($"업데이트 확인 실패: {DescribeError(ex)}", $"Update check failed: {DescribeError(ex)}"));
            }
        }
        finally
        {
            SetChecking(false);
        }
    }

    private void SetChecking(bool on)
    {
        IsChecking = on;
        StateChanged?.Invoke();
    }

    /// <summary>A short Korean reason for a failed check/download (the full exception is in app.log).</summary>
    internal static string DescribeError(Exception ex)
    {
        // Velopack may wrap the HTTP error.
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }:
                    return Loc.T("GitHub 요청 한도를 넘었습니다. 1시간쯤 뒤에 다시 시도해 주세요.", "GitHub rate limit reached. Try again in about an hour.");
                case HttpRequestException { StatusCode: HttpStatusCode.NotFound }:
                    return Loc.T("업데이트 주소에서 릴리스를 찾지 못했습니다 (설정의 updateUrl 확인).", "No release found at the update address (check updateUrl in settings).");
                case HttpRequestException { StatusCode: { } code }:
                    return Loc.T($"서버 응답 오류 ({(int)code}).", $"Server error ({(int)code}).");
                case HttpRequestException:
                    return Loc.T("인터넷에 연결할 수 없습니다.", "Cannot connect to the internet.");
                case TaskCanceledException or TimeoutException:
                    return Loc.T("응답 시간이 초과되었습니다. 인터넷 연결을 확인해 주세요.", "The request timed out. Check your internet connection.");
                case IOException or UnauthorizedAccessException:
                    return Loc.T($"파일을 쓰지 못했습니다 ({e.Message}).", $"Could not write the file ({e.Message}).");
            }
        }
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        if (message.Length > 120) message = message[..120] + "…";
        return Loc.T($"{message} (자세한 내용은 로그)", $"{message} (details in the log)");
    }

    /// <summary>
    /// Tray "업데이트 적용 (재시작)": the caller then shuts the app down normally (settings are saved in OnExit), and
    /// <see cref="ApplyOnExit"/> hands over to Velopack's updater with restart = true. This is what
    /// <see cref="UpdateManager.ApplyUpdatesAndRestart"/> does too, minus its immediate Environment.Exit, which would skip OnExit.
    /// </summary>
    public bool RequestApplyAndRestart()
    {
        if (ReadyVersion is null) return false;
        _restartRequested = true;
        return true;
    }

    /// <summary>
    /// Called from App.OnExit. With a downloaded update, Velopack's Update.exe waits for this process to end, then swaps
    /// in the new version: silently and without restarting, unless <see cref="RequestApplyAndRestart"/> was used.
    /// </summary>
    public void ApplyOnExit()
    {
        if (_client is null || ReadyVersion is null) return;
        try
        {
            AppLog.Info($"update: applying v{ReadyVersion} after exit (restart {_restartRequested})");
            _client.ApplyOnExit(_restartRequested);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"update: could not start the updater ({ex.GetType().Name}: {ex.Message})");
        }
    }

    public void Dispose() => _timer?.Stop();
}

/// <summary>What <see cref="UpdateService"/> needs from Velopack, so its states can be tested without a real install.</summary>
internal interface IUpdateClient
{
    bool IsInstalled { get; }

    /// <summary>The installed version ("0.1.0"), null when not installed.</summary>
    string? CurrentVersion { get; }

    /// <summary>The newer version's number, or null when up to date. Remembers it for <see cref="DownloadAsync"/>.</summary>
    Task<string?> CheckAsync();

    /// <summary>Downloads the version the last <see cref="CheckAsync"/> found.</summary>
    Task DownloadAsync();

    /// <summary>The Markdown release notes of the version the last <see cref="CheckAsync"/> found, if the package has any.</summary>
    string? ReleaseNotes { get; }

    /// <summary>Hands the downloaded version to Velopack's updater, which waits for this process to exit.</summary>
    void ApplyOnExit(bool restart);
}

internal sealed class VelopackUpdateClient : IUpdateClient
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _found;

    public VelopackUpdateClient(UpdateSourceSpec source)
    {
        _manager = new UpdateManager(CreateSource(source));
    }

    private static IUpdateSource CreateSource(UpdateSourceSpec spec) => spec.Kind switch
    {
        UpdateSourceKind.GitHub => new GithubSource(spec.Location, null, false),
        UpdateSourceKind.Web => new SimpleWebSource(spec.Location),
        _ => new SimpleFileSource(new DirectoryInfo(spec.Location)),
    };

    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    public async Task<string?> CheckAsync()
    {
        _found = await _manager.CheckForUpdatesAsync();
        return _found?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync()
        => _found is null ? Task.CompletedTask : _manager.DownloadUpdatesAsync(_found);

    public string? ReleaseNotes => _found?.TargetFullRelease.NotesMarkdown;

    public void ApplyOnExit(bool restart)
    {
        if (_found is null) return;
        _manager.WaitExitThenApplyUpdates(_found.TargetFullRelease, silent: true, restart: restart);
    }
}
