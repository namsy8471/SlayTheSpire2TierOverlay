using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// Event option advice (추천 tab while an event room is open). Kept in its own partial file so the main controller only
/// carries one-line hooks: init, snapshot and pack reload.
/// </summary>
public sealed partial class AppController
{
    private EventScorer? _eventScorer;
    private EventTextProvider _eventText = EventTextProvider.Empty;
    private string? _eventKey;

    /// <summary>The event evaluation on screen, for the on-screen option marker; null outside events.</summary>
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
        _eventKey = null;
    }

    /// <summary>
    /// Every snapshot: when the tracker says an event room is open, score its options and show them in the 추천 tab.
    /// Card screens (reward/shop recognition) own the tab while they are showing.
    /// </summary>
    private void UpdateEvent(GameStateSnapshot snap)
    {
        var active = (_source as GameStateAdapter)?.Tracked?.CurrentEvent;
        if (active is null || snap.Run is not { } run || _eventScorer is null)
        {
            if (_lastEvent is not null)
            {
                _lastEvent = null;
                _eventKey = null;
                OnEventChanged();
                if (_lastResult is null && _vm.Offer.HasContent && !_vm.Offer.IsStale) _vm.Offer = _vm.Offer with { IsStale = true };
            }
            return;
        }
        if (_lastResult is not null) return; // a card screen (event card choice) is being recognized

        var page = CurrentPage(active);
        var keys = active.ChosenKeys.Count == 0 ? active.OptionKeys : null;
        var key = $"{active.EventId}|{page}|{string.Join(",", keys ?? [])}|{run.Hp}/{run.MaxHp}|{run.Gold}|{run.Deck.Count}#{_engineVersion}";
        if (key == _eventKey) return;
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
        _vm.Offer = EventOffer(eval, active);
        if (first)
        {
            _vm.SelectedTab = PanelTab.Offer;
            AppLog.Info($"event {eval.Event.Id} page {eval.Page} ({active.Source}): best {eval.Best?.Option.Key ?? "-"}");
        }
        OnEventChanged();
    }

    /// <summary>The page showing now: the last chosen option's next page (co-op log), else the first page.</summary>
    private string? CurrentPage(ActiveEvent active)
    {
        if (active.ChosenKeys.Count == 0) return null;
        var hit = _eventScorer!.Catalog.FindOption(active.ChosenKeys[^1]);
        return hit?.Option.Next ?? hit?.Page.Page;
    }

    private OfferSectionVm EventOffer(EventEvaluation eval, ActiveEvent active)
    {
        var rows = new List<OfferRowVm>();
        foreach (var o in eval.Options.OrderBy(o => o.Rank))
        {
            var sub = new List<string> { $"{o.Rank}위" };
            if (o.IsLethal) sub.Add("죽음 위험");
            if (o.IsUnaffordable) sub.Add("골드 부족");
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

        var source = active.Source == EventSource.Log ? "로그" : "세이브 예측";
        var options = eval.OptionsExact ? "" : " · 선택지는 데이터 기준";
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

    /// <summary>The evaluation changed (or ended): refresh the on-screen marker.</summary>
    private void OnEventChanged()
    {
    }
}
