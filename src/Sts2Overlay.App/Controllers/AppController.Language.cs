using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Integration.Fakes;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Display-language switch. Every text the controller hands to the panel, the badges and the marks is built in the language
/// current at that moment, and Core bakes its reason labels (score chips, build roles, event effects, path chips, rest
/// reasons) when it scores. So a language change can't just re-render cached objects: it runs every evaluation again from
/// the current state.
/// </summary>
public sealed partial class AppController
{
    /// <summary>
    /// Rebuilds everything shown (run line, 덱 / 빌드 / 경로 / 추천 / 리뷰 tabs, badges, event and map/rest marks) in the
    /// current language. Call it after <c>Loc.SetLanguage</c>; safe from any thread (marshalled to the UI thread).
    /// </summary>
    public void RefreshLanguage()
    {
        if (!_ui.CheckAccess())
        {
            _ui.BeginInvoke(RefreshLanguage);
            return;
        }
        try
        {
            // The state keys already include the language, but clear them so nothing can be skipped as "unchanged".
            _pathKey = null;
            _lastEvalKey = null;
            _eventKey = null;
            _restKey = null;
            _restAdvice = null;
            _eventText = EventTextProvider.ForLocalData(); // event / option / relic titles in the new language

            _vm.PackText = PackText();
            OnTrackerChanged(); // status line + UI scale text

            RefreshRunViews(_snapshot.Run); // run line, 덱 and 빌드 tabs (re-analyses the deck)
            _vm.Build = ForecastReviewPresenter.WithForecast(_vm.Build, _snapshot.Run is null ? null : _lastForecast);
            _vm.Forecast = ForecastReviewPresenter.Forecast(_lastForecast);
            if (_lastReview is { } review) _vm.Review = ForecastReviewPresenter.Review(review, _cards, _vm.Review.ShowBanner);
            _reviews?.Refresh(); // re-scores the last run's review against the pack

            if (Demo is not null) UpdatePath(DemoScenario.Path);
            else RefreshPath(_snapshot); // re-runs PathAdvisor and redraws the map marks

            RefreshOffer();
            UpdateEvent(_snapshot); // the open event room: re-scored (key cleared above), markers redrawn
            RedrawMap();
            _pipeline.RequestRecognize(); // next capture rebuilds the rest-site marks with the re-advised text
        }
        catch (Exception ex)
        {
            AppLog.Error("language refresh failed", ex);
        }
        StateChanged?.Invoke();
    }

    /// <summary>The 추천 tab and the badges: the card screen on screen, else the last offer (kept stale-marked).</summary>
    private void RefreshOffer()
    {
        if (_lastResult is not null)
        {
            ApplyRecognition(_lastResult); // re-scores (eval key cleared) and redraws badges, skip pill, removal mark
            return;
        }
        if (!_vm.Offer.HasContent) return;
        var stale = _vm.Offer.IsStale;
        OfferSectionVm? offer = null;
        if (_offerSource is { } r)
        {
            offer = (r.ScreenKind == ScreenKind.Shop ? EvaluateShop(r) : EvaluateReward(r)).Item1;
        }
        else if (_eventOfferRebuild is { } rebuild && _lastEvent is null)
        {
            offer = rebuild(); // an event room that has closed; an open one is redone by UpdateEvent
        }
        if (offer is not null) _vm.Offer = offer with { IsStale = stale };
    }
}
