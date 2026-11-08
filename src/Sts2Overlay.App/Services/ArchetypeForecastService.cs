using System.IO;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Runs <see cref="ArchetypeForecaster"/> off the UI thread. Snapshots arrive on every log line, so a request whose key
/// (deck, relics, position, offset) is unchanged is ignored, and a newer request cancels the one still running.
/// The result is delivered on the UI thread.
/// </summary>
public sealed class ArchetypeForecastService : IDisposable
{
    /// <summary>~5–15 ms on the real pack (see the report in the PR); enough for ±1–2 %p noise.</summary>
    public static readonly ForecastOptions Options = new() { Simulations = 1500 };

    private readonly Dispatcher _ui;
    private readonly Action<ForecastResult?> _onResult;
    private ArchetypeForecaster? _forecaster;
    private CancellationTokenSource? _running;
    private string? _lastKey;

    public ArchetypeForecastService(Dispatcher ui, Action<ForecastResult?> onResult)
    {
        _ui = ui;
        _onResult = onResult;
    }

    /// <summary>Builds the forecaster for a (re)loaded pack. Call on the UI thread.</summary>
    public void SetPack(DataPack pack, string packDir, string? localCatalog)
    {
        var (pool, source) = LoadPool(pack, packDir, localCatalog);
        _forecaster = new ArchetypeForecaster(pack, pool);
        _lastKey = null;
        AppLog.Info($"forecast pool: {pool.All.Count} cards from {source}");
    }

    /// <summary>Call on the UI thread with the latest run. Null clears the result.</summary>
    public void Request(RunState? run, MapGraph? map, double? rarityOffset)
    {
        if (run is null || _forecaster is not { } forecaster)
        {
            Cancel();
            _lastKey = null;
            _onResult(null);
            return;
        }

        var request = new ForecastRequest { State = run, Map = map, RarityOffset = rarityOffset };
        var key = ArchetypeForecaster.KeyOf(request, Options);
        if (key == _lastKey) return;
        _lastKey = key;

        Cancel();
        var cts = _running = new CancellationTokenSource();
        var token = cts.Token;
        Task.Run(() => forecaster.Forecast(request, Options, token), token).ContinueWith(t =>
        {
            if (token.IsCancellationRequested) return;
            if (t.IsFaulted)
            {
                AppLog.Error("forecast failed", t.Exception?.GetBaseException());
                return;
            }
            if (t.IsCompletedSuccessfully)
            {
                _ui.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested) _onResult(t.Result);
                });
            }
        }, TaskScheduler.Default);
    }

    private void Cancel()
    {
        // Cancel only: the running task may still read its token, so the source is left to the GC (it holds no timer).
        _running?.Cancel();
        _running = null;
    }

    /// <summary>
    /// Card pool for rewards: the committed id-only catalog next to the packs (<c>data/catalog/cards.*.json</c>), else the
    /// local catalog, else the pack's own data.
    /// </summary>
    private static (ForecastCardPool Pool, string Source) LoadPool(DataPack pack, string packDir, string? localCatalog)
    {
        var candidates = new List<string>();
        var catalogDir = Path.GetFullPath(Path.Combine(packDir, "..", "..", "catalog"));
        if (Directory.Exists(catalogDir)) candidates.AddRange(Directory.GetFiles(catalogDir, "cards.*.json").OrderDescending(StringComparer.Ordinal));
        if (localCatalog is not null) candidates.Add(localCatalog);

        foreach (var path in candidates)
        {
            try
            {
                var pool = ForecastCardPool.FromCatalogJson(File.ReadAllText(path));
                if (pool.All.Count > 0) return (pool, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                AppLog.Warn($"forecast pool: could not read {path} ({ex.Message})");
            }
        }
        return (ForecastCardPool.FromPack(pack), "data pack");
    }

    public void Dispose() => Cancel();
}
