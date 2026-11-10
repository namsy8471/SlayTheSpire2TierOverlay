using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.GameState;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Build-completion forecast (빌드 tab) and end-of-run review (리뷰 tab). Kept in its own partial file so the main
/// controller only carries four one-line hooks: init, snapshot, pack reload and dispose.
/// </summary>
public sealed partial class AppController
{
    private ArchetypeForecastService? _forecast;
    private RunReviewService? _reviews;
    private RunReview? _lastReview;
    private ForecastResult? _lastForecast;
    private bool _hadRun;

    private void InitForecastReview(string packDir, string? catalogPath)
    {
        _forecast = new ArchetypeForecastService(_ui, OnForecast);
        _forecast.SetPack(_engine.Pack, packDir, catalogPath);

        var paths = GamePaths.Discover();
        var history = paths.SavesDirectory is { } saves ? System.IO.Path.Combine(saves, "history") : null;
        InitDecisionLog(); // play-time advice the review judges against (AppController.DecisionLog.cs)
        _reviews = new RunReviewService(history, paths.SteamId is { } id ? (long)id : null, _engine.Pack, _ui, OnReview)
        {
            Decisions = _decisionStore,
            UseLearnedModel = _engine.UseLearnedModel,
        };
        _reviews.Start();
    }

    /// <summary>Every snapshot: re-forecast when the run changed (the service ignores unchanged keys); spot a run ending.</summary>
    private void UpdateForecastReview(GameStateSnapshot snap)
    {
        TickDecisionLog(snap); // finish a card screen's record when the floor / run moved on (AppController.DecisionLog.cs)
        var offset = (_source as GameStateAdapter)?.Tracked?.RarityPityOffset;
        // RefreshRunViews just rebuilt the archetype list: put the last forecast line back until the new one arrives.
        _vm.Build = ForecastReviewPresenter.WithForecast(_vm.Build, snap.Run is null ? null : _lastForecast);
        _forecast?.Request(snap.Run, snap.Map, offset);

        var hasRun = snap.Run is not null;
        if (_hadRun && !hasRun) _reviews?.Poke(); // run ended (or main menu): its .run file is about to appear
        _hadRun = hasRun;
    }

    private void ReloadForecastReview()
    {
        _forecast?.SetPack(_engine.Pack, _packDir, CatalogPath);
        _forecast?.Request(_snapshot.Run, _snapshot.Map, (_source as GameStateAdapter)?.Tracked?.RarityPityOffset);
        _reviews?.SetPack(_engine.Pack);
        _reviews?.Refresh();
    }

    private void DisposeForecastReview()
    {
        _forecast?.Dispose();
        DisposeDecisionLog();
        _reviews?.Dispose();
    }

    private void OnForecast(ForecastResult? result)
    {
        _lastForecast = result;
        _vm.Forecast = ForecastReviewPresenter.Forecast(result);
        _vm.Build = ForecastReviewPresenter.WithForecast(_vm.Build, result);
        if (result is { FromCache: false })
        {
            AppLog.Info($"forecast: {result.Simulations} sims in {result.ElapsedMs:0.0} ms, map {result.UsedMap}, offset {result.RarityOffsetUsed:0.###}");
        }
    }

    private void OnReview(RunReview review, bool isNew)
    {
        // A refresh after a pack reload must not bring back a banner the user already dismissed.
        var showBanner = isNew || (_vm.Review.ShowBanner && _lastReview?.Run.StartTime == review.Run.StartTime);
        _lastReview = review;
        _vm.Review = ForecastReviewPresenter.Review(review, _cards, showBanner);
        if (isNew) ShowMessage(_vm.Review.BannerText);
    }
}
