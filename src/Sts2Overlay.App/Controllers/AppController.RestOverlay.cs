using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Rest;
using RectF = Sts2Overlay.Vision.RectF;
using RefTransform = Sts2Overlay.Vision.RefTransform;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Rest site on the game screen: "추천!" on the 휴식 / 재련 button and, in the Smith grid, on the card to upgrade.
/// Drawn by the same mark layer as the map (<see cref="Views.MapOverlayWindow"/>); the two never show at once.
///
/// <code>
///  VisionRecognizer: RestSiteDetector (buttons, Smith grid) ─▶ RecognitionResult.RestSite
///  RestSiteAdvisor(run, map) ─▶ RestMarks.Choice / RestMarks.Grid ─▶ _restPlan ─▶ mark layer
/// </code>
/// </summary>
public sealed partial class AppController
{
    private MapMarkPlan _restPlan = MapMarkPlan.Empty;
    private RestSiteAdvice? _restAdvice;
    private string? _restKey;

    /// <summary>Every recognition (also "Other" screens): rebuilds the rest-site marks or clears them.</summary>
    private void UpdateRestMarks(RecognitionResult r)
    {
        var plan = MapMarkPlan.Empty;
        try
        {
            if (_settings.MapOverlay && Demo is null && _snapshot.Run is { } run && r.RestSite is { } rest)
            {
                if (rest.ChoiceScreen)
                {
                    plan = RestMarks.Choice(rest, RestAdvice(run), _cards.Name);
                }
                // The same deck grid is used for removals, transforms and pile views: only trust it at a rest site.
                else if (rest.CardGridOpen && _snapshot.Hint == ScreenHint.RestSite)
                {
                    // The card recogniser reads the scrolled grid in only some frames; the ranking panel shows in all of them.
                    List<(CardRef, RectF)> cards = [];
                    if (r.ScreenKind == ScreenKind.Grid && _tracker.Current is { CaptureRect.IsEmpty: false } win)
                    {
                        var t = RefTransform.For(win.CaptureRect.Width, win.CaptureRect.Height);
                        cards = r.Cards
                            .Select(c => (c.Card, t.ToRef(new RectF(c.ScreenBounds.X - win.CaptureRect.X, c.ScreenBounds.Y - win.CaptureRect.Y, c.ScreenBounds.Width, c.ScreenBounds.Height))))
                            .ToList();
                    }
                    plan = RestMarks.Grid(cards, RestAdvice(run), _cards.Name);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("rest-site advice failed", ex);
        }
        if (plan.IsEmpty && _restPlan.IsEmpty) return;
        if (!plan.IsEmpty && _restPlan.IsEmpty) AppLog.Info($"rest marks: {string.Join(", ", plan.Rects.Select(x => x.Ribbon))} ({_restAdvice?.Action})");
        _restPlan = plan;
        RedrawMap();
        UpdateCadence();
    }

    /// <summary>Cached per run state: HP, deck and position decide the advice.</summary>
    private RestSiteAdvice RestAdvice(RunState run)
    {
        var map = _snapshot.Map;
        var key = $"{run.Hp}/{run.MaxHp}|{run.Deck.Count}|{string.Join(",", run.Deck.Select(d => d.Card))}|{map?.ActId}|{map?.Current}#{_engineVersion}";
        if (key != _restKey || _restAdvice is null)
        {
            _restAdvice = new RestSiteAdvisor(_engine.Pack).Advise(run, map);
            _restKey = key;
            AppLog.Info($"rest advice: {_restAdvice.Action} ({_restAdvice.Confidence}) {_restAdvice.Reason}");
        }
        return _restAdvice;
    }

    /// <summary>Capture rate at a rest site: like a reward screen, so the grid marks follow scrolling and hovering.</summary>
    private double RestCadenceHz() =>
        _snapshot.Hint == ScreenHint.RestSite || !_restPlan.IsEmpty ? Math.Clamp(_settings.RecognizeHz, 0.5, 10) : 0;
}
