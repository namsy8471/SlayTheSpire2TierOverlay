using System.IO;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Resources;
using Sts2Overlay.Vision;

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
    private string? _prepareLine;
    private IReadOnlyList<string> _guardLines = [];
    private (DataPack Pack, string PackDir)? _lastPack;

    public LocalResourceService(PanelViewModel vm, string? configuredGameDir, LocalLayout layout)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _vm = vm;
        _configuredGameDir = configuredGameDir;
        _preparer = new ResourcePreparer(layout);
    }

    /// <summary>Raised on the UI thread after new resources were written; the argument is the new catalog path.</summary>
    public event Action<string?>? Prepared;

    /// <summary>The detected game (null until the worker has run, or when no install was found).</summary>
    public InstalledGame? Game => _game;

    /// <summary>Runs detection (and preparation if needed) on a worker thread. Never throws; errors go to the banner/log.</summary>
    public Task Start()
    {
        // Progress<T> remembers the current SynchronizationContext (the UI dispatcher), so Report() from the worker
        // runs the callback on the UI thread, in order.
        var progress = new Progress<PrepareProgress>(p => SetPrepareLine($"{p.Text} · {p.Fraction * 100:0}%"));
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
                    Post(() => SetPrepareLine("게임 설치 폴더를 찾지 못해 카드 그림·이름을 준비하지 못했습니다 (settings.json의 gameDir 지정)"));
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
            Post(() => SetPrepareLine("다른 프로세스가 리소스를 준비 중입니다. 끝나면 오버레이를 다시 시작해 주세요"));
        }
        catch (Exception ex)
        {
            // Keep the overlay alive: the old resources (if any) stay in place, see ResourcePreparer.
            AppLog.Error("resources: preparation failed", ex);
            Post(() => SetPrepareLine($"리소스 준비 실패: {ex.Message} (app.log 참고, --prepare로 다시 시도)"));
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
        var catalog = VersionGuard.LoadCatalog(_preparer.Layout.ManifestCatalog());
        var result = VersionGuard.Evaluate(
            game.Version?.Text,
            packVersion,
            catalog,
            VisionCatalog.OfferablePools,
            id => pack.FindTier("", id) is not null,
            pack.Tiers.Values.SelectMany(t => t.Cards.Keys).Select(pack.Canonical));

        AppLog.Info($"version guard: game {game.VersionLabel} vs pack {packVersion}: {result.UnratedCards.Count} offerable cards without a tier"
                    + $"{(result.UnratedCards.Count > 0 ? " (" + string.Join(", ", result.UnratedCards.Take(20)) + ")" : "")}, {result.RemovedCards.Count} tier entries not in the game");
        _guardLines = result.PanelLines(pack.Weights.UnknownTier.ToString());
        UpdateBanner();
    }

    /// <summary>The pack's <c>dataVersion</c> (tier files), else its folder name (<c>data/packs/v0.107.1</c>).</summary>
    internal static string PackVersion(DataPack pack, string packDir)
        => pack.Tiers.Values.Select(t => t.DataVersion).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
           ?? Path.GetFileName(packDir.TrimEnd('\\', '/'));

    private void SetPrepareLine(string? line)
    {
        _prepareLine = line;
        UpdateBanner();
    }

    private void UpdateBanner()
    {
        var lines = (_prepareLine is null ? Enumerable.Empty<string>() : [_prepareLine]).Concat(_guardLines).ToList();
        _vm.ResourceBanner = lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private void Post(Action action) => _ui.BeginInvoke(action);

    public void Dispose()
    {
        // Not disposed: the worker may still read the token while it winds down.
        _cts.Cancel();
    }
}
