using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;
using Sts2Overlay.Core.Combat;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Localization;
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
/// <see cref="EventButtonDetector"/> (ancients: <see cref="AncientButtonDetector"/>) finds the option buttons on the
/// periodic captures (worker thread) →
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
    private volatile bool _eventAncient; // the room is an ancient's (its own button layout)

    /// <summary>The event evaluation on screen; null outside events.</summary>
    private EventEvaluation? _lastEvent;

    /// <summary>3-turn damage simulator for the training dummy (null when the pack has no combat data).</summary>
    private TurnSimulator? _combatSim;

    /// <summary>The last finished dummy forecast and the deck key it was made for.</summary>
    private DummyForecast? _dummy;

    /// <summary>Deck key of the forecast running on a worker thread; null when none is running.</summary>
    private string? _dummyPending;

    private sealed record DummyForecast(string Key, DamageForecast Forecast, IReadOnlyList<string> Notes);

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
        try
        {
            _combatSim = CombatData.LoadPack(packDir) is { } combat ? new TurnSimulator(combat) : null;
            AppLog.Info(_combatSim is null ? "combat: no combat data in the pack" : $"combat: {_combatSim.Data.CardCount} cards");
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            AppLog.Error("combat: load failed", ex);
            _combatSim = null;
        }
        _dummy = null;
        _dummyPending = null;
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
        _eventAncient = active is not null && _eventScorer?.Catalog.Find(active.EventId)?.IsAncient == true;
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
        // The log names the offered options; so does a seed replay (Neow, act-start ancients), in button order.
        if (active.Source == EventSource.Log || active.OptionsReplayed) _eventScreens?.SetLog(active.OptionKeys, active.ChosenKeys);
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
        var tracked = ((GameStateAdapter)_source!).Tracked!;
        var context = EventContextFor(active.EventId, tracked, run);
        var key = $"{active.EventId}|{page}|{string.Join(",", keys ?? [])}|{run.Hp}/{run.MaxHp}|{run.Gold}|{run.Deck.Count}"
                  + $"|{context.PlayerCount}|{(context.Forecast is null ? "-" : _dummy?.Key)}#{_engineVersion}@{Loc.CurrentCode}";
        if (key == _eventKey)
        {
            ShowEventMarkers(); // same options; the buttons may have moved
            return;
        }
        _eventKey = key;

        EventEvaluation? eval;
        try
        {
            eval = _eventScorer.Evaluate(run, active.EventId, keys, page, active.PossibleKeys, context);
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
        _offerSource = null;
        // Core bakes its reason labels in the current language when it scores, so a re-render scores again.
        var scorer = _eventScorer;
        _eventOfferRebuild = () => scorer.Evaluate(run, active.EventId, keys, page, active.PossibleKeys) is { } again
            ? EventOffer(again, active, match)
            : null;
        if (first) _vm.SelectedTab = PanelTab.Offer;
        AppLog.Info($"event {eval.Event.Id} page {eval.Page} ({active.Source}{(match is null ? "" : $", {match.Keys.Count} buttons on screen")}): best {eval.Best?.Option.Key ?? "-"}");
        if (eval.Forecast is { } fc)
        {
            // Kept in the log so the predictions can be checked against how the fights went.
            AppLog.Info($"dummy forecast: players {context.PlayerCount}, mean {fc.Mean:0.0}, per turn {string.Join("/", fc.MeanPerTurn.Select(t => t.ToString("0.0")))}, "
                        + string.Join(", ", eval.Options.Where(o => o.DummyHp is not null).Select(o => $"{o.Option.Key} hp {o.DummyHp} P {o.WinChance:0.00}")));
        }
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
            var f = new ScreenFrame(frame.Bgra, frame.Width, frame.Height, frame.Stride);
            screen = _eventAncient ? AncientButtonDetector.Detect(f) : EventButtonDetector.Detect(f);
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
            var warning = o.IsLethal ? LethalText()
                : o.Option.Key.EndsWith(EventSlots.LockedSuffix, StringComparison.Ordinal) ? LockedText()
                : o.IsUnaffordable ? NoGoldText()
                : o.WinChance < HardKill ? HardKillText()
                : null;
            var score = o.WinChance is { } w ? Loc.T($"{Fmt(o.Total)} · 처치 {w * 100:0}%", $"{Fmt(o.Total)} · kill {w * 100:0}%") : Fmt(o.Total);
            specs.Add(new EventMarkerSpec(new PixelRect(b.X + _eventOrigin.X, b.Y + _eventOrigin.Y, b.Width, b.Height),
                o.Rank, o.IsBest, score, warning));
        }
        markers.Show(specs);
    }

    /// <summary>The marker layer, created on first use next to the badge layer (same capture visibility).</summary>
    private EventMarkerWindow? EnsureEventMarkers()
    {
        if (_eventMarkers is not null) return _eventMarkers;
        if (_badges.Hwnd == IntPtr.Zero) return null;
        _eventMarkers = new EventMarkerWindow(_pipeline.CaptureVisibility) { ScaleMultiplier = _badges.ScaleMultiplier };
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
        var rows = eval.Options.OrderBy(o => o.Rank).Select(o => EventRow(o)).ToList();
        // An ancient's options that weren't drawn this time: their own ranking, below, for reference.
        var extra = eval.NotOffered.Select(o => EventRow(o, rankOnly: true)).ToList();

        var source = active.Source == EventSource.Log ? Loc.T("로그", "log")
            : active.Certain ? Loc.T("세이브 예측", "save prediction")
            : Loc.T("세이브 예측 (추정)", "save prediction (est.)");
        var options = match is not null ? Loc.T(" · 화면 버튼 일치", " · matches the buttons on screen")
            : active.OptionsReplayed ? Loc.T(" · 선택지는 시드로 계산", " · options from the seed")
            : eval.OptionsExact ? ""
            : Loc.T(" · 선택지는 데이터 기준", " · options from data");
        return new OfferSectionVm
        {
            Title = Loc.T($"이벤트 · {_eventText.EventTitle(eval.Event.Id)}", $"Event · {_eventText.EventTitle(eval.Event.Id)}"),
            ConfidenceText = ConfidenceText(eval.Confidence),
            ConfidenceKind = eval.Confidence.ToString(),
            FlipHint = eval.Forecast is { } f ? ForecastText(f, eval.ForecastNotes)
                : eval.Event.Id == DummyFight.EventId && _dummyPending is not null ? Loc.T("예상 피해 계산 중…", "Estimating damage…")
                : null,
            Rows = rows,
            Footer = (eval.Best is { } best
                         ? Loc.T($"추천: {_eventText.OptionTitle(best.Option.Key)}", $"Pick: {_eventText.OptionTitle(best.Option.Key)}")
                         : Loc.T("추천 없음", "No pick"))
                     + Loc.T($" · 인식: {source}{options}", $" · source: {source}{options}"),
            ExtraTitle = extra.Count > 0
                ? Loc.T($"이번에 안 나온 선택지 {extra.Count}개 · 참고 순위", $"{extra.Count} options not offered this time · ranked for reference")
                : null,
            ExtraRows = extra,
        };
    }

    /// <summary>One option row. <paramref name="rankOnly"/>: the sub line is just "N위" (one-line rows of the lower block).</summary>
    private OfferRowVm EventRow(EventOptionEvaluation o, bool rankOnly = false)
    {
        var sub = new List<string> { Loc.T($"{o.Rank}위", $"#{o.Rank}") };
        if (!rankOnly)
        {
            if (o.IsLethal) sub.Add(LethalText());
            if (o.IsUnaffordable) sub.Add(o.Option.Key.EndsWith(EventSlots.LockedSuffix, StringComparison.Ordinal) ? LockedText() : NoGoldText());
            if (o.WinChance is { } w)
            {
                sub.Add(w < HardKill
                    ? Loc.T($"처치 어려움 ({w * 100:0}%)", $"Hard to kill ({w * 100:0}%)")
                    : Loc.T($"처치 확률 {w * 100:0}%", $"Kill chance {w * 100:0}%"));
            }
            else if (o.IsRandom) sub.Add(Loc.T("운 요소", "Random"));
            if (o.HasUnmodelled) sub.Add(Loc.T("일부 효과 미반영", "Some effects not modeled"));
        }
        return new OfferRowVm
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
        };
    }

    /// <summary>Chip text: the component label, with a card/relic name when the component names one.</summary>
    private string ComponentLabel(ScoreComponent c)
    {
        if (c.Detail is { } d)
        {
            if (d.StartsWith("CARD.", StringComparison.Ordinal)) return $"{c.Label} {_cards.Name(d)}";
            if (d.StartsWith("RELIC.", StringComparison.Ordinal) && _eventText.RelicTitle(d) is { } r) return $"{c.Label} {r}";
            // Relic build fit: "빌드 적합 유물 (힘 빌드)" — the detail starts with the build's name.
            if (c.Key == EventComponentKeys.RelicFit && d.IndexOf(':') is > 0 and var colon) return $"{c.Label} ({d[..colon]})";
        }
        return c.Label;
    }

    private static string DisplayEffect(string text) => text.Length > 60 ? text[..57] + "…" : text;

    private static string LethalText() => Loc.T("죽음 위험", "Lethal risk");

    private static string LockedText() => Loc.T("잠김", "Locked");

    private static string NoGoldText() => Loc.T("골드 부족", "Not enough Gold");

    private static string HardKillText() => Loc.T("처치 어려움", "Hard to kill");

    /// <summary>Re-scores and re-formats the last event offer shown (also after the room closed and it went stale); null when none.</summary>
    private Func<OfferSectionVm?>? _eventOfferRebuild;

    // ---- training dummy: 3-turn damage forecast ----------------------------------------------------------------------

    /// <summary>Below this kill chance an option is flagged "처치 어려움".</summary>
    private const double HardKill = 0.5;

    /// <summary>
    /// "예상 피해 (자동 플레이, 포션 제외) · 1턴 18 · 2턴 20 · 3턴 19 · 합계 평균 57", then the notes on the next line. The
    /// simulator plays well but not like a good player planning a combo, so the numbers lean low.
    /// </summary>
    private static string ForecastText(DamageForecast f, IReadOnlyList<string> notes)
    {
        var turns = string.Join(" · ", f.MeanPerTurn.Select((t, i) => Loc.T($"{i + 1}턴 {t:0}", $"turn {i + 1} {t:0}")));
        var line = Loc.T($"예상 피해 (자동 플레이, 포션 제외) · {turns} · 합계 평균 {f.Mean:0}",
            $"Expected damage (auto-play, no potions) · {turns} · total avg {f.Mean:0}");
        return notes.Count == 0 ? line : line + Environment.NewLine + string.Join(" · ", notes);
    }

    /// <summary>
    /// Player count for every event; for the training dummy also the damage forecast. The forecast runs on a worker thread
    /// (a few thousand simulated fights) and is cached by deck: until it is done the dummy's options are scored without it.
    /// </summary>
    private EventContext EventContextFor(string eventId, TrackedRun tracked, Core.Model.RunState run)
    {
        var players = Math.Max(1, tracked.PlayerCount);
        if (eventId != DummyFight.EventId || _combatSim is not { } sim) return new EventContext { PlayerCount = players };
        var key = DummyKey(tracked, run);
        if (_dummy is { } d && d.Key == key) return new EventContext { PlayerCount = players, Forecast = d.Forecast, ForecastNotes = d.Notes };
        if (_dummyPending != key)
        {
            _dummyPending = key;
            Func<string, string> name = _cards.Name;
            Task.Run(() => BuildDummyForecast(sim, tracked, run, key, name)).ContinueWith(t => _ui.BeginInvoke(() =>
            {
                if (_dummyPending != key) return; // the deck changed (or the pack reloaded) meanwhile
                _dummyPending = null;
                if (t.Exception is { } ex)
                {
                    AppLog.Error("dummy forecast failed", ex.GetBaseException());
                    return;
                }
                _dummy = t.Result;
                _eventKey = null;
                if (_snapshot.Run is not null) UpdateEvent(_snapshot);
            }), TaskScheduler.Default);
        }
        return new EventContext { PlayerCount = players };
    }

    private static string DummyKey(TrackedRun tracked, Core.Model.RunState run)
    {
        static string Deck(IEnumerable<Core.Model.DeckCard> cards)
            => string.Join(",", cards.Select(c => $"{c.Id}+{c.Upgrade}{(c.EnchantmentId is null ? "" : $"/{c.EnchantmentId}{c.EnchantmentAmount}")}"));
        var partners = string.Join(";", tracked.Partners.Select(p => $"{p.Character}:{Deck(p.Deck)}:{string.Join(",", p.Relics)}"));
        // The language too: the forecast's notes (and the card names in them) are written once, in the current language.
        return $"{tracked.PlayerCount}|{run.Character}|{Deck(run.Deck)}|{string.Join(",", run.Relics)}|{partners}@{Loc.CurrentCode}";
    }

    /// <summary>
    /// Everyone's damage together: mine, each partner's from their own deck when the save has it, else mine again (another
    /// shuffle) with a note. Worker thread: touches no controller state.
    /// </summary>
    private static DummyForecast BuildDummyForecast(TurnSimulator sim, TrackedRun tracked, Core.Model.RunState run, string key, Func<string, string> cardName)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var mine = new SimPlayer { Character = run.Character, Deck = run.Deck, Relics = run.Relics };
        var everyone = new List<SimPlayer> { mine };
        var players = Math.Max(1, tracked.PlayerCount);
        var assumed = 0;
        for (var i = 1; i < players; i++)
        {
            var partner = i - 1 < tracked.Partners.Count ? tracked.Partners[i - 1] : null;
            if (partner is { Deck.Count: > 0 })
            {
                everyone.Add(new SimPlayer { Character = partner.Character, Deck = partner.Deck, Relics = partner.Relics });
            }
            else
            {
                everyone.Add(mine); // simulated with that player's own seed, so not the same hands as mine
                assumed++;
            }
        }
        var forecast = DummyFight.Forecast(sim, everyone);

        var notes = new List<string>();
        if (assumed > 0)
        {
            notes.Add(Loc.T($"동료 {assumed}명은 덱을 몰라 내 덱 피해로 가정",
                assumed == 1 ? "1 teammate's deck unknown: assumed my deck's damage" : $"{assumed} teammates' decks unknown: assumed my deck's damage"));
        }
        if (tracked.DeckApproximate) notes.Add(Loc.T("덱 일부가 추정", "Part of the deck is estimated"));
        var cov = forecast.Coverage;
        if (cov.UnknownCards.Count > 0)
        {
            var names = string.Join(", ", cov.UnknownCards.Select(cardName));
            notes.Add(Loc.T($"효과 미반영: {names}", $"Effects not modeled: {names}"));
        }
        if (cov.ApproxCards.Count > 0)
        {
            notes.Add(Loc.T($"근사 계산 {cov.ApproxCards.Count}종", $"{cov.ApproxCards.Count} card(s) approximated"));
        }
        AppLog.Info($"dummy forecast: {everyone.Count} player(s), {forecast.Samples} samples in {watch.ElapsedMilliseconds} ms");
        return new DummyForecast(key, forecast, notes);
    }

    private void ReloadEvents()
    {
        InitEvents(_packDir);
        if (_snapshot.Run is not null) UpdateEvent(_snapshot);
    }
}
