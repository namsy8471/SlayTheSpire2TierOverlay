using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Rest;
using RectF = Sts2Overlay.Vision.RectF;
using RefTransform = Sts2Overlay.Vision.RefTransform;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Rest site on the game screen: "추천!" on the 휴식 / 재련 button and, in the Smith grid, on the card to upgrade.
/// Drawn by the same mark layer as the map (<see cref="Views.MapOverlayWindow"/>); the two never show at once.
/// An event option that lets the player pick a card to upgrade (Sapphire Seed's Eat, ...) opens the very same grid
/// (the game's <c>CardSelectCmd.FromDeckForUpgrade</c>), so it gets the same marks (<see cref="EventUpgradePick"/>).
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
    private EventUpgradePick _eventUpgrade = new(Core.Events.EventCatalog.Empty);

    /// <summary>Every recognition (also "Other" screens): rebuilds the rest-site marks or clears them.</summary>
    private void UpdateRestMarks(RecognitionResult r)
    {
        var plan = MapMarkPlan.Empty;
        // An event option that upgrades a card of the player's choice was clicked and its grid is up (or not yet over).
        var eventPick = _eventUpgrade.Update(_eventWatch ? _eventScreens?.Clicked : null, r.RestSite is { CardGridOpen: true }, _loggedButtons > 0);
        try
        {
            if (_settings.MapOverlay && Demo is null && _snapshot.Run is { } run && r.RestSite is { } rest)
            {
                if (rest.ChoiceScreen)
                {
                    var enabled = rest.Buttons.Where(b => b.Enabled).ToArray();
                    var actions = enabled.All(b => Enum.TryParse<RestAction>(b.Option.ToString(), out _))
                        ? enabled.Select(b => Enum.Parse<RestAction>(b.Option.ToString())).ToArray() : [];
                    plan = RestMarks.Choice(rest, RestAdvice(run, actions), _cards.Name);
                }
                // The same deck grid is used for removals, transforms and pile views: only trust it at a rest site, or
                // right after an event option that opens the upgrade pick.
                else if (rest.CardGridOpen && (_snapshot.Hint == ScreenHint.RestSite || eventPick))
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
                    var advice = eventPick ? RestAdvice(run, []) : RestAdvice(run);
                    plan = RestMarks.Grid(cards, advice, _cards.Name);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("rest-site advice failed", ex);
        }
        if (plan.IsEmpty && _restPlan.IsEmpty) return;
        if (!plan.IsEmpty && _restPlan.IsEmpty)
            AppLog.Info($"rest marks: {string.Join(", ", plan.Rects.Select(x => x.Ribbon))} ({(eventPick ? $"event pick {_eventScreens?.Clicked}" : _restAdvice?.Action)})");
        _restPlan = plan;
        RedrawMap();
        UpdateCadence();
    }

    /// <summary>Cached per run state: HP, deck and position decide the advice.</summary>
    private RestSiteAdvice RestAdvice(RunState run, IReadOnlyList<RestAction>? available = null)
    {
        var map = _snapshot.Map;
        var key = $"{run.Character}/{run.Ascension}/{run.IsMultiplayer}/{run.Act}|{run.Hp}/{run.MaxHp}{(run.VitalsKnown ? "" : "?")}|{run.Gold}|{string.Join(",", run.Deck)}|{map?.ActId}|{map?.Current}#{_engineVersion}@{Loc.CurrentCode}";
        // Keep the same selected upgrade when the option buttons disappear and the Smith grid opens.
        if (available == null && _restAdvice != null && _restKey?.StartsWith(key + "|", StringComparison.Ordinal) == true)
            return _restAdvice;
        key += "|" + (available == null ? "" : string.Join(",", available));
        if (key != _restKey || _restAdvice is null)
        {
            var advisor = new RestSiteAdvisor(_engine.Pack);
            _restAdvice = available == null ? advisor.Advise(run, map) : advisor.Advise(run, map, available);
            _restKey = key;
            AppLog.Info($"rest advice: {_restAdvice.Action} ({_restAdvice.Confidence}) {_restAdvice.Reason}");
        }
        return _restAdvice;
    }

    /// <summary>Capture rate at a rest site: like a reward screen, so the grid marks follow scrolling and hovering.</summary>
    private double RestCadenceHz() =>
        _snapshot.Hint == ScreenHint.RestSite || !_restPlan.IsEmpty ? Math.Clamp(_settings.RecognizeHz, 0.5, 10) : 0;

    /// <summary>Marks expected soon (layer hook): at a rest site, or while an event's upgrade pick is open or about to open.</summary>
    private bool RestMarksLikely => _snapshot.Hint == ScreenHint.RestSite || !_restPlan.IsEmpty || (_eventWatch && _eventUpgrade.Expects(_eventScreens?.Clicked));
}
