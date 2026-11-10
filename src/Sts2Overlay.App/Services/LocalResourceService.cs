using System.IO;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Resources;
using Sts2Overlay.Vision;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Services;

/// <summary>
/// First-run resource preparation and the game-version guard, so the user never has to run the extraction tools by hand.
/// <code>
///  Start() ──(worker)──▶ locate game → release_info.json + fingerprint → manifest check
///                          ├─ up to date ─▶ nothing to do (≈30 ms)
///                          └─ missing/stale ─▶ ResourcePreparer.Prepare (progress → panel banner) ─▶ Prepared (UI thread)
///  EvaluateGuard(pack) (UI thread, on start and after every pack reload) ─▶ "게임 버전 …이 데이터 팩 …보다 새롭습니다" lines
/// </code>
/// Both parts write to <see cref="PanelViewModel.ResourceBanner"/>: the preparation line first, then the guard lines.
/// </summary>
public sealed class LocalResourceService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly PanelViewModel _vm;
    private readonly string? _configuredGameDir;
    private readonly ResourcePreparer _preparer;
    private readonly CancellationTokenSource _cts = new();

    private InstalledGame? _game;
    /// <summary>Builds the preparation line in the current language (re-run on a language change).</summary>
    private Func<string>? _prepareLine;
    private IReadOnlyList<string> _guardLines = [];
    private (DataPack Pack, string PackDir)? _lastPack;

    public LocalResourceService(PanelViewModel vm, string? configuredGameDir, LocalLayout layout)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _vm = vm;
        _configuredGameDir = configuredGameDir;
        _preparer = new ResourcePreparer(layout);
        StandingNotices.Changed += OnNoticesChanged; // unreadable save/log format, broken settings.json
        UpdateBanner(); // a notice set before we existed (settings.json is read first)
    }

    private void OnNoticesChanged() => Post(UpdateBanner);

    /// <summary>Raised on the UI thread after new resources were written; the argument is the new catalog path.</summary>
    public event Action<string?>? Prepared;

    /// <summary>The detected game (null until the worker has run, or when no install was found).</summary>
    public InstalledGame? Game => _game;

    /// <summary>Runs detection (and preparation if needed) on a worker thread. Never throws; errors go to the banner/log.</summary>
    public Task Start()
    {
        // Progress<T> remembers the current SynchronizationContext (the UI dispatcher), so Report() from the worker
        // runs the callback on the UI thread, in order.
        var progress = new Progress<PrepareProgress>(p => SetPrepareLine(() => $"{p.Text} · {p.Fraction * 100:0}%"));
        return Task.Run(() => Run(progress, _cts.Token));
    }

    private void Run(IProgress<PrepareProgress> progress, CancellationToken ct)
    {
        try
        {
            var install = GameInstall.Locate(_configuredGameDir);
            if (install is null)
            {
                AppLog.Warn($"resources: Slay the Spire 2 install not found (settings gameDir = {_configuredGameDir ?? "(auto)"})");
                if (_preparer.Layout.ManifestCatalog() is null)
                    Post(() => SetPrepareLine(() => Loc.T("게임 설치 폴더를 찾지 못해 카드 그림·이름을 준비하지 못했습니다 (settings.json의 gameDir 지정)", "Game folder not found, so card art and names were not prepared (set gameDir in settings.json)")));
                return;
            }

            var game = InstalledGame.Detect(install);
            AppLog.Info($"resources: game {game.VersionLabel} at {install.Directory}, fingerprint {game.Fingerprint}");
            if (game.AssemblyHashMatches == false)
                AppLog.Warn("resources: release_info.json main_assembly_hash does not match sts2.dll (modded or partially updated game?)");
            Post(() =>
            {
                _game = game;
                if (_lastPack is { } lp) EvaluateGuard(lp.Pack, lp.PackDir);
            });

            var check = _preparer.Check(game);
            AppLog.Info($"resources: {check.Need} ({check.Reason})");
            if (check.Need == PrepareNeed.UpToDate) return;

            var manifest = _preparer.Prepare(game, progress, ct);
            AppLog.Info($"resources: prepared {manifest.CatalogCards} cards, {manifest.CardArtFiles} portraits, {manifest.LocalizationFiles} text files in {manifest.DurationSeconds:0.0} s");
            var catalog = _preparer.Layout.ManifestCatalog();
            Post(() =>
            {
                SetPrepareLine(null);
                Prepared?.Invoke(catalog);
            });
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("resources: preparation cancelled");
        }
        catch (PrepareBusyException)
        {
            AppLog.Warn("resources: another process is preparing; using what is there");
            Post(() => SetPrepareLine(() => Loc.T("다른 프로세스가 리소스를 준비 중입니다. 끝나면 오버레이를 다시 시작해 주세요", "Another process is preparing resources. Restart the overlay when it is done")));
        }
        catch (Exception ex)
        {
            // Keep the overlay alive: the old resources (if any) stay in place, see ResourcePreparer. Most likely a game
            // update changed sts2.dll / the .pck: the player can only get a newer overlay, so say that (details in app.log).
            // (No extra update check from here: installed builds check by themselves ~15 s after start, UpdateService.)
            AppLog.Error("resources: preparation failed", ex);
            Post(() => SetPrepareLine(PrepareFailedText));
        }
    }

    /// <summary>
    /// Compares the installed game with the data pack (UI thread). Called at start and after every pack reload; does
    /// nothing visible until the game version is known.
    /// </summary>
    public void EvaluateGuard(DataPack pack, string packDir)
    {
        _lastPack = (pack, packDir);
        var game = _game;
        if (game is null) return;

        var packVersion = PackVersion(pack, packDir);
        var catalogPath = _preparer.Layout.ManifestCatalog();
        var catalog = VersionGuard.LoadCatalog(catalogPath);
        var result = VersionGuard.Evaluate(
            game.Version?.Text,
            packVersion,
            catalog,
            VisionCatalog.OfferablePools,
            id => pack.FindTier("", id) is not null,
            pack.Tiers.Values.SelectMany(t => t.Cards.Keys).Select(pack.Canonical));

        AppLog.Info($"version guard: game {game.VersionLabel} vs pack {packVersion}: {result.UnratedCards.Count} offerable cards without a tier"
                    + $"{(result.UnratedCards.Count > 0 ? " (" + string.Join(", ", result.UnratedCards.Take(20)) + ")" : "")}, {result.RemovedCards.Count} tier entries not in the game");
        // Card names in the overlay language (the same lookup as the panel's), not ids like BALL_LIGHTNING.
        if (_names is not { } names || names.Pack != pack || names.CatalogPath != catalogPath)
            _names = names = (pack, catalogPath, CardInfoProvider.Load(catalogPath, pack));
        _guardLines = result.PanelLines(pack.Weights.UnknownTier.ToString(), cardName: names.Provider.Name);
        UpdateBanner();
    }

    private (DataPack Pack, string? CatalogPath, CardInfoProvider Provider)? _names;

    /// <summary>The pack's <c>dataVersion</c> (tier files), else its folder name (<c>data/packs/v0.107.1</c>).</summary>
    internal static string PackVersion(DataPack pack, string packDir)
        => pack.Tiers.Values.Select(t => t.DataVersion).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
           ?? Path.GetFileName(packDir.TrimEnd('\\', '/'));

    /// <summary>Rebuilds the banner in the current language (UI thread): the guard lines and the preparation line.</summary>
    public void RefreshLanguage()
    {
        if (_lastPack is { } lp) EvaluateGuard(lp.Pack, lp.PackDir);
        UpdateBanner();
    }

    private void SetPrepareLine(Func<string>? line)
    {
        _prepareLine = line;
        UpdateBanner();
    }

    /// <summary>The banner line after a failed preparation: what the player can do, without the exception text.</summary>
    internal static string PrepareFailedText() => Loc.T(
        "게임 파일을 읽지 못했습니다. 게임이 업데이트되었다면 오버레이 새 버전이 필요합니다. 트레이 메뉴의 업데이트 확인을 눌러 주세요.",
        "Could not read the game files. If the game was updated, a new overlay version is needed. Click Check for updates in the tray menu.");

    private void UpdateBanner()
    {
        var lines = (_prepareLine is null ? Enumerable.Empty<string>() : [_prepareLine()]).Concat(_guardLines)
            .Concat(StandingNotices.Lines()).ToList();
        _vm.ResourceBanner = lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private void Post(Action action) => _ui.BeginInvoke(action);

    public void Dispose()
    {
        // Not disposed: the worker may still read the token while it winds down.
        _cts.Cancel();
        StandingNotices.Changed -= OnNoticesChanged;
    }
}
