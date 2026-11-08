using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.GameState.Tracking;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Events;
using PixelRect = Sts2Overlay.App.Integration.PixelRect;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Event option advice: a "추천!" marker next to the recommended option button on the game screen, and the full
/// ranking in the panel's 추천 tab. Kept in its own partial file so the main controller only carries one-line hooks
/// (init, snapshot, pack reload, cadence, visibility, topmost).
/// <para>
/// Flow: the tracker says which event room I'm in (co-op log, or predicted from the single-player save) →
/// <see cref="EventButtonDetector"/> finds the option buttons on the periodic captures (worker thread) →
/// <see cref="EventScreenTracker"/> lines the buttons up with the page's option keys → <see cref="EventScorer"/>
/// ranks exactly those options → markers and panel.
/// </para>
/// </summary>
public sealed partial class AppController
{
    private EventScorer? _eventScorer;
    private EventTextProvider _eventText = EventTextProvider.Empty;
    private EventScreenTracker? _eventScreens;
    private EventMarkerWindow? _eventMarkers;
    private string? _eventKey;
    private volatile bool _eventWatch; // read on the capture worker: run the button detector only in event rooms

    /// <summary>The event evaluation on screen; null outside events.</summary>
    private EventEvaluation? _lastEvent;

    private void InitEvents(string packDir)
    {
        EventCatalog catalog;
        try
        {
            catalog = EventCatalog.LoadDirectory(packDir);
            AppLog.Info($"events: {catalog.Events.Count} events from {packDir}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            AppLog.Error("events: load failed", ex);
            catalog = EventCatalog.Empty;
        }
        _eventScorer = new EventScorer(_engine.Pack, catalog);
        _eventText = EventTextProvider.ForLocalData();
        _eventScreens = new EventScreenTracker(catalog);
        _eventKey = null;
        _pipeline.FrameProbe = OnEventFrame;
    }

    /// <summary>
    /// Every snapshot: when the tracker says an event room is open, score its options and show them.
    /// Card screens (reward/shop/choose-a-card recognition) own the tab and the screen while they are showing.
    /// </summary>
    private void UpdateEvent(GameStateSnapshot snap)
    {
        var active = (_source as GameStateAdapter)?.Tracked?.CurrentEvent;
        _eventWatch = active is not null;
        _eventScreens?.SetEvent(active?.EventId);
        if (active is null || snap.Run is not { } run || _eventScorer is null)
        {
            if (_lastEvent is not null)
            {
                _lastEvent = null;
                _eventKey = null;
                _eventMarkers?.Clear();
                if (_lastResult is null && _vm.Offer.HasContent && !_vm.Offer.IsStale) _vm.Offer = _vm.Offer with { IsStale = true };
            }
            return;
        }
        if (active.Source == EventSource.Log) _eventScreens?.SetLog(active.OptionKeys, active.ChosenKeys);
        if (_lastResult is not null)
        {
            _eventMarkers?.Clear(); // a card screen (event card choice) is up
            _eventKey = null;       // show the event again when it closes
            return;
        }

        // Buttons lined up on screen: exactly those options. Otherwise the page the log/data says.
        var match = _eventScreens?.Current;
        var page = match?.Page ?? CurrentPage(active);
        var keys = match?.Keys ?? (active.ChosenKeys.Count == 0 ? active.OptionKeys : null);
        var key = $"{active.EventId}|{page}|{string.Join(",", keys ?? [])}|{run.Hp}/{run.MaxHp}|{run.Gold}|{run.Deck.Count}#{_engineVersion}";
        if (key == _eventKey)
        {
            ShowEventMarkers(); // same options; the buttons may have moved
            return;
        }
        _eventKey = key;

        EventEvaluation? eval;
        try
        {
            eval = _eventScorer.Evaluate(run, active.EventId, keys, page);
        }
        catch (Exception ex)
        {
            AppLog.Error("event recommendation failed", ex);
            return;
        }
        if (eval is null)
        {
            AppLog.Info($"event {active.EventId}: not in events.json");
            return;
        }
        var first = _lastEvent?.Event.Id != eval.Event.Id;
        _lastEvent = eval;
        _vm.Offer = EventOffer(eval, active, match);
        if (first) _vm.SelectedTab = PanelTab.Offer;
        AppLog.Info($"event {eval.Event.Id} page {eval.Page} ({active.Source}{(match is null ? "" : $", {match.Keys.Count} buttons on screen")}): best {eval.Best?.Option.Key ?? "-"}");
        ShowEventMarkers();
    }

    /// <summary>The page showing now: the last chosen option's next page (co-op log), else the first page.</summary>
    private string? CurrentPage(ActiveEvent active)
    {
        if (active.ChosenKeys.Count == 0) return null;
        var hit = _eventScorer!.Catalog.FindOption(active.ChosenKeys[^1]);
        return hit?.Option.Next ?? hit?.Page.Page;
    }

    // ---- screen: buttons → markers ----------------------------------------------------------------------------

    /// <summary>Capture worker: find the option buttons while an event room is open, hand them to the UI thread.</summary>
    private void OnEventFrame(CapturedFrame frame)
    {
        if (!_eventWatch) return;
        EventScreen? screen;
        try
        {
            screen = EventButtonDetector.Detect(new ScreenFrame(frame.Bgra, frame.Width, frame.Height, frame.Stride));
        }
        catch (Exception ex)
        {
            AppLog.Error("event button detection failed", ex);
            return;
        }
        var origin = (frame.ScreenRect.X, frame.ScreenRect.Y);
        _ui.BeginInvoke(() => ApplyEventScreen(screen, origin));
    }

    private (int X, int Y) _eventOrigin;
    private int? _loggedButtons;

    private void ApplyEventScreen(EventScreen? screen, (int X, int Y) origin)
    {
        if (_eventScreens is null || !_eventWatch) return;
        var count = screen?.Buttons.Count ?? 0;
        if (count != _loggedButtons)
        {
            AppLog.Info($"event screen: {count} option buttons{(screen is null ? "" : $" (locked {screen.Buttons.Count(b => b.State == EventButtonState.Locked)}, hovered {screen.HoveredIndex})")}");
            _loggedButtons = count;
        }
        _eventOrigin = origin;
        if (_eventScreens.OnScreen(screen)) UpdateEvent(_snapshot);
    }

    /// <summary>Markers for the current evaluation, when its options are the buttons on screen.</summary>
    private void ShowEventMarkers()
    {
        if (_eventScreens?.Current is not { } match || _lastEvent is not { } eval || _lastResult is not null
            || eval.Options.Count != match.Buttons.Count)
        {
            _eventMarkers?.Clear();
            return;
        }
        var markers = EnsureEventMarkers();
        if (markers is null) return;
        var specs = new List<EventMarkerSpec>(eval.Options.Count);
        for (int i = 0; i < eval.Options.Count; i++)
        {
            var o = eval.Options[i];
            var b = match.Buttons[i];
            var warning = o.IsLethal ? "죽음 위험"
                : o.Option.Key.EndsWith(EventSlots.LockedSuffix, StringComparison.Ordinal) ? "잠김"
                : o.IsUnaffordable ? "골드 부족"
                : null;
            specs.Add(new EventMarkerSpec(new PixelRect(b.X + _eventOrigin.X, b.Y + _eventOrigin.Y, b.Width, b.Height),
                o.Rank, o.IsBest, Fmt(o.Total), warning));
        }
        markers.Show(specs);
    }

    /// <summary>The marker layer, created on first use next to the badge layer (same capture exclusion).</summary>
    private EventMarkerWindow? EnsureEventMarkers()
    {
        if (_eventMarkers is not null) return _eventMarkers;
        if (_badges.Hwnd == IntPtr.Zero) return null;
        _eventMarkers = new EventMarkerWindow(EventMarkerWindow.IsExcludedFromCapture(_badges.Hwnd)) { ScaleMultiplier = _badges.ScaleMultiplier };
        _eventMarkers.Show();
        UpdateEventMarkerVisibility();
        return _eventMarkers;
    }

    /// <summary>Follows the badge layer: shown over the game while it is, hidden otherwise (hook in UpdateVisibility).</summary>
    private void UpdateEventMarkerVisibility()
    {
        if (_eventMarkers is null) return;
        var info = _tracker.Current;
        if (_badges.IsVisible && info is not null)
        {
            if (!_eventMarkers.IsVisible) _eventMarkers.Show();
            _eventMarkers.ScaleMultiplier = _badges.ScaleMultiplier;
            _eventMarkers.SetLayerRect(info.CaptureRect);
            OverlayWindowHelperTopmost(_eventMarkers.Hwnd);
        }
        else if (_eventMarkers.IsVisible)
        {
            _eventMarkers.Hide();
        }
    }

    private static void OverlayWindowHelperTopmost(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) Interop.OverlayWindowHelper.AssertTopmost(hwnd);
    }

    /// <summary>Topmost hook (ReassertTopmost): markers sit with the badges, under the panel.</summary>
    private void ReassertEventMarkersTopmost()
    {
        if (_eventMarkers is { IsVisible: true } m) OverlayWindowHelperTopmost(m.Hwnd);
    }

    /// <summary>Cadence hook: an event room is watched at the fast rate so a hover and a click are both seen.</summary>
    private bool EventScreenLikely => _eventWatch;

    // ---- panel ---------------------------------------------------------------------------------------------------

    private OfferSectionVm EventOffer(EventEvaluation eval, ActiveEvent active, EventButtonMatch? match)
    {
        var rows = new List<OfferRowVm>();
        foreach (var o in eval.Options.OrderBy(o => o.Rank))
        {
            var sub = new List<string> { $"{o.Rank}위" };
            if (o.IsLethal) sub.Add("죽음 위험");
            if (o.IsUnaffordable) sub.Add(o.Option.Key.EndsWith(EventSlots.LockedSuffix, StringComparison.Ordinal) ? "잠김" : "골드 부족");
            if (o.IsRandom) sub.Add("운 요소");
            if (o.HasUnmodelled) sub.Add("일부 효과 미반영");
            rows.Add(new OfferRowVm
            {
                Name = _eventText.OptionTitle(o.Option.Key),
                ScoreText = Fmt(o.Total),
                BarWidth = Bar(Math.Max(0, o.Total + 30)), // option scores sit around 0 (leave = 0): shift for the bar
                IsBest = o.IsBest,
                IsEstimate = o.HasUnmodelled,
                Unaffordable = o.IsUnaffordable || o.IsLethal,
                SubText = string.Join(" · ", sub),
                Tip = o.EffectTexts.Count > 0 ? string.Join(", ", o.EffectTexts.Select(DisplayEffect)) : null,
                Chips = o.Score.Components
                    .Where(c => Math.Abs(c.Value) >= 0.5)
                    .OrderByDescending(c => Math.Abs(c.Value))
                    .Take(3)
                    .Select(c => Chip(ComponentLabel(c), c.Value))
                    .ToArray(),
            });
        }

        var source = active.Source == EventSource.Log ? "로그" : active.Certain ? "세이브 예측" : "세이브 예측 (추정)";
        var options = match is not null ? " · 화면 버튼 일치" : eval.OptionsExact ? "" : " · 선택지는 데이터 기준";
        return new OfferSectionVm
        {
            Title = $"이벤트 · {_eventText.EventTitle(eval.Event.Id)}",
            ConfidenceText = eval.Confidence switch
            {
                RecommendationConfidence.Sure => "확신",
                RecommendationConfidence.Close => "박빙",
                RecommendationConfidence.Estimate => "추정",
                _ => "보통",
            },
            ConfidenceKind = eval.Confidence.ToString(),
            Rows = rows,
            Footer = (eval.Best is { } best ? $"추천: {_eventText.OptionTitle(best.Option.Key)}" : "추천 없음")
                     + $" · 인식: {source}{options}",
        };
    }

    /// <summary>Chip text: the component label, with a card/relic name when the component names one.</summary>
    private string ComponentLabel(ScoreComponent c)
    {
        if (c.Detail is { } d)
        {
            if (d.StartsWith("CARD.", StringComparison.Ordinal)) return $"{c.Label} {_cards.Name(d)}";
            if (d.StartsWith("RELIC.", StringComparison.Ordinal) && _eventText.RelicTitle(d) is { } r) return $"{c.Label} {r}";
        }
        return c.Label;
    }

    private static string DisplayEffect(string text) => text.Length > 60 ? text[..57] + "…" : text;

    private void ReloadEvents()
    {
        InitEvents(_packDir);
        if (_snapshot.Run is not null) UpdateEvent(_snapshot);
    }
}
