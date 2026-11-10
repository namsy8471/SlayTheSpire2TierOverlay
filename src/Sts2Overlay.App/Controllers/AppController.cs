using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Integration.Fakes;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.App.Views;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// The App's brain. Lives on the UI thread and connects:
/// <code>
///  IGameStateSource ──Changed──▶ OnSnapshot ──▶ deck/build tabs, capture cadence (3 Hz on likely reward/shop)
///  CapturePipeline  ──Recognized (worker)──▶ ApplyRecognition ──▶ RecommendationEngine ──▶ 추천 tab + badges
///  GameWindowTracker ──Changed──▶ UpdateVisibility (show only while the game is in front)
///  hotkeys / tray ──▶ ToggleHidden, ToggleClickThrough, RecognizeNow, SaveCapture, SetDemo, ReloadPack
/// </code>
/// Events from other threads are marshalled with <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/> because WPF
/// objects may only be touched by the thread that created them.
/// </summary>
public sealed partial class AppController : IDisposable
{
    /// <summary>Stand-in when a card offer is recognised before the save tracker has produced a run.</summary>
    private static readonly RunState UnknownRun = new() { Character = "CHARACTER.UNKNOWN" };

    private readonly Dispatcher _ui;
    private readonly AppSettings _settings;
    private readonly GameWindowTracker _tracker;
    private readonly CapturePipeline _pipeline;
    private readonly PanelWindow _panel;
    private readonly BadgeWindow _badges;
    private readonly PanelViewModel _vm;
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _messageTimer;

    private RecommendationEngine _engine;
    private CardInfoProvider _cards;
    private CardTextProvider _cardText = CardTextProvider.Empty;
    private RelicInfoProvider _relicInfo = RelicInfoProvider.Empty; // relic icons + effect text (data/local + relic-vars)

    /// <summary>Card portraits for the panel rows (thumbnail + hover); rebuilt when data/local is regenerated.</summary>
    private CardArtProvider _art = CardArtProvider.ForLocalData();
    private string _packDir;
    private bool _packLoaded;
    private int _engineVersion;

    private IGameStateSource _source = new NullGameStateSource();
    private GameStateSnapshot _snapshot = GameStateSnapshot.Empty;
    private RecognitionResult? _lastResult;
    private string? _lastOfferKey;
    private string? _lastEvalKey;
    private IReadOnlyList<BadgeSpec> _lastBadges = [];
    private SkipSpec? _lastSkip;
    private string? _lastRemovalText;

    /// <summary>The recognition the 추천 tab's card offer was built from (kept after the screen closes, for a re-render).</summary>
    private RecognitionResult? _offerSource;
    private bool _userHidden;
    private bool _clickThrough;

    public AppController(
        AppSettings settings,
        GameWindowTracker tracker,
        CapturePipeline pipeline,
        PanelWindow panel,
        BadgeWindow badges,
        PanelViewModel vm,
        string packDir,
        string? catalogPath)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _settings = settings;
        _tracker = tracker;
        _pipeline = pipeline;
        _panel = panel;
        _badges = badges;
        _vm = vm;
        _packDir = packDir;
        CatalogPath = catalogPath;
        (_engine, _cards, _packLoaded) = LoadPack(packDir, catalogPath);
        _engine.UseLearnedModel = !string.Equals(settings.CardModel, "engine", StringComparison.OrdinalIgnoreCase);
        AppLog.Info(_engine.LearnedModelActive ? "card rewards: learned model" : $"card rewards: engine rules (cardModel={settings.CardModel}, model in pack: {_engine.Pack.CardPolicy is not null})");
        vm.PackText = PackText();
        _cardText = CardTextProvider.ForLocalData(_engine.Pack, packDir);
        _relicInfo = RelicInfoProvider.ForLocalData(packDir);
        InitForecastReview(packDir, catalogPath); // forecast + run review (AppController.ForecastReview.cs)
        InitMapOverlay(); // marks on the game's map (AppController.MapOverlay.cs)
        InitEvents(packDir); // event option advice (AppController.Events.cs)
        InitHoverYield(); // step aside for the game's hover tips (AppController.HoverYield.cs)
        InitTiers(); // 티어표 tab (AppController.Tiers.cs)

        _tracker.Changed += (_, _) => OnTrackerChanged();
        // The panel moved to a monitor with another DPI: the DPI part of the scale changed, ours must follow.
        _panel.DpiChanged += (_, _) => ApplyUiScale();
        ApplyUiScale();
        _pipeline.Recognized += r => _ui.BeginInvoke(() => OnRecognized(r)); // ApplyRecognition, timed (AppController.Perf.cs)
        _pipeline.Status += s => _ui.BeginInvoke(() => ShowMessage(s));

        _panel.KeepOnTop = settings.AlwaysOnTop;
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        _topmostTimer.Start();

        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            _vm.MessageText = null;
        };
    }

    /// <summary>Raised (UI thread) when hidden/demo/status changed, so the tray can update.</summary>
    public event Action? StateChanged;

    public DemoScreen? Demo { get; private set; }
    public bool UserHidden => _userHidden;
    public string? CatalogPath { get; private set; }

    public string StatusLine => $"{_vm.StatusText} · {_vm.PackText}";

    // ---- sources --------------------------------------------------------------------------------------

    /// <summary>Swaps the game-state source and recogniser (demo on/off). Disposes the previous source.</summary>
    public void Attach(IGameStateSource source, ICardRecognizer recognizer, DemoScreen? demo)
    {
        _source.Changed -= OnSourceChanged;
        _source.Dispose();

        _source = source;
        Demo = demo;
        _vm.IsDemo = demo is not null;
        _tracker.Simulate = demo is not null;
        _pipeline.Recognizer = recognizer;
        ResetOffer();
        UpdatePath(demo is not null ? DemoScenario.Path : null);

        _source.Changed += OnSourceChanged;
        _source.Start();
        OnSnapshot();
        AppLog.Info($"sources attached: {source.GetType().Name} + {recognizer.GetType().Name}{(demo is { } d ? $" (demo {d})" : "")}");
        StateChanged?.Invoke();
    }

    /// <summary>Tray "데모 모드" menu. Null switches back to the real (save/log + vision) sources.</summary>
    public void SetDemo(DemoScreen? screen)
    {
        if (screen is { } s && _source is FakeGameStateSource fake)
        {
            fake.SetScreen(s);
            Demo = s;
            _pipeline.RequestRecognize();
            StateChanged?.Invoke();
            return;
        }
        var (state, recognizer) = screen is { } s2 ? IntegrationFactory.CreateDemo(s2) : IntegrationFactory.CreateReal();
        Attach(state, recognizer, screen);
    }

    /// <summary>Path section input. Safe to call from any thread.</summary>
    public void UpdatePath(PathView? view)
    {
        if (!_ui.CheckAccess())
        {
            _ui.BeginInvoke(() => UpdatePath(view));
            return;
        }
        _vm.Path = view is null
            ? PathSectionVm.Empty
            : new PathSectionVm
            {
                NextStep = view.NextStep,
                Note = view.Note,
                Options = view.Options
                    .Select(o => new PathOptionVm(o.Label, o.Summary, o.ReasonChips, o.Score is { } sc ? Fmt(sc) : null, o.IsRecommended))
                    .ToArray(),
            };
    }

    private void OnSourceChanged(object? sender, EventArgs e) => _ui.BeginInvoke(OnSnapshot);

    private void OnSnapshot()
    {
        var snap = _source.Current;
        var prevHint = _snapshot.Hint;
        _snapshot = snap;
        if (snap.Hint != prevHint)
        {
            _hintChangedAt = Stopwatch.GetTimestamp(); // perf: how early the hint came (AppController.Perf.cs)
            _hintChangedUtc = DateTime.UtcNow; // a new chance to hunt for the screen (UpdateCadence)
        }
        _pipeline.CharacterHint = snap.Run?.Character;
        // Every stage on its own: one that throws must not skip the others, nor the badge cleanup and cadence below.
        RunStage("run views", () => RefreshRunViews(snap.Run));
        RunStage("forecast/review", () => UpdateForecastReview(snap)); // AppController.ForecastReview.cs
        RunStage("event", () => UpdateEvent(snap)); // AppController.Events.cs
        if (Demo is null) RunStage("path", () => RefreshPath(snap));
        RunStage("map target", () => UpdateMapTarget(snap)); // AppController.MapOverlay.cs
        RunStage("tiers", () => UpdateTiers(snap)); // AppController.Tiers.cs (owned marks; builds only while the tab shows)

        var likely = IsOfferHint(snap.Hint);
        RunStage("badges", () =>
        {
            if (likely && prevHint != snap.Hint)
            {
                _pipeline.RequestRecognize();
            }
            else if (!likely && IsOfferHint(prevHint))
            {
                // The tracker says the reward/shop screen is gone (card picked, map node entered, next combat...).
                ClearBadges(forgetScreen: true);
                _removal.EndVisit(); // the next merchant visit starts with its removal service for sale
            }
            else if (_lastResult is not null)
            {
                ApplyRecognition(_lastResult); // same screen, new run state (gold, deck): re-evaluate
            }
        });
        RunStage("cadence", UpdateCadence);
    }

    private readonly HashSet<string> _stageErrors = [];

    /// <summary>Runs one stage of <see cref="OnSnapshot"/>; an exception is logged once per stage and message (snapshots come with every log line).</summary>
    private void RunStage(string name, Action stage)
    {
        try
        {
            stage();
        }
        catch (Exception ex)
        {
            if (_stageErrors.Count < 64 && _stageErrors.Add($"{name}: {ex.GetType().Name}: {ex.Message}")) AppLog.Error($"snapshot stage '{name}' failed", ex);
        }
    }

    // ---- path tab ---------------------------------------------------------------------------------------------

    private string? _pathKey;

    /// <summary>
    /// Re-runs <see cref="PathAdvisor"/> when something it depends on changed (position, HP, gold, deck size, pack).
    /// It takes a few ms, but snapshots arrive on every log line, so the key avoids redundant work.
    /// </summary>
    private void RefreshPath(GameStateSnapshot snap)
    {
        if (snap.Run is not { } run || snap.Map is not { } map)
        {
            _pathKey = null;
            if (IsCoopGuestWithoutMap(snap)) _vm.Path = PathSectionVm.CoopGuestEmpty; // on the UI thread (OnSnapshot)
            else UpdatePath(null);
            SetMapRecommendation(null);
            return;
        }
        var key = $"{map.ActId}|{map.Current}|{run.Hp}/{run.MaxHp}|{run.Gold}|{run.Deck.Count}|{run.Deck.Count(c => c.Upgrade > 0)}#{_engineVersion}@{Loc.CurrentCode}";
        if (key == _pathKey) return;
        _pathKey = key;
        try
        {
            var rec = new PathAdvisor(_engine.Pack).Recommend(run, map);
            UpdatePath(ToPathView(rec, map));
            SetMapRecommendation(rec);
        }
        catch (Exception ex)
        {
            AppLog.Error("path advice failed", ex);
            UpdatePath(null);
        }
    }

    /// <summary>A co-op run rebuilt from the log alone (no save file): the map never comes, so the path tab says why.</summary>
    internal static bool IsCoopGuestWithoutMap(GameStateSnapshot snap)
        => snap is { LogOnly: true, Map: null, Run.IsMultiplayer: true };

    /// <summary>Label of the i-th path option: the best, then alternatives.</summary>
    private static string PathLabel(int i) => i switch
    {
        0 => Loc.T("추천", "Best"),
        1 => Loc.T("대안 1", "Alt 1"),
        _ => Loc.T("대안 2", "Alt 2"),
    };

    internal static PathView? ToPathView(PathRecommendation rec, MapGraph map)
    {
        if (rec.IsEmpty) return new PathView([], null, Loc.T("보스 앞이거나 갈 수 있는 길이 없습니다", "At the Boss, or no path left to take"));
        var options = rec.Top.Select((o, i) => new PathOptionView(
            PathLabel(i),
            Loc.T($"엘리트 {o.Elites} · 휴식 {o.Rests} · 상점 {o.CountOf(MapNodeType.Shop)} · ? {o.CountOf(MapNodeType.Unknown)} · 보스 도착 HP {o.ExpectedBossHp:P0}",
                  $"Elite {o.Elites} · Rest {o.Rests} · Shop {o.CountOf(MapNodeType.Shop)} · ? {o.CountOf(MapNodeType.Unknown)} · HP at Boss {o.ExpectedBossHp:P0}"),
            PathChips(o),
            o.Total,
            i == 0)).ToArray();
        string? next = rec.NextStep is { } n && map.Nodes.TryGetValue(n, out var node)
            ? Loc.T($"다음: {NodeName(node.Type)} {Direction(map.Current ?? map.Start, n)}", $"Next: {NodeName(node.Type)} {Direction(map.Current ?? map.Start, n)}")
            : null;
        var note = Loc.T(
            $"덱 강도 {rec.DeckStrength:0.00} 기준 · 경로 {rec.TotalPaths}개 중" + (rec.Exhaustive ? "" : " 일부") + " 비교 [추정 가중치]",
            $"Deck strength {rec.DeckStrength:0.00} · compared {(rec.Exhaustive ? "all" : "some")} of {rec.TotalPaths} paths [est. weights]");
        return new PathView(options, next, note);
    }

    /// <summary>
    /// The path's reason chips, at most 5. <see cref="PathAdvisor"/> already puts the worded risks first.
    /// <see cref="PathOption.Risks"/> holds raw keys ("elite_chain") and is never shown.
    /// </summary>
    internal static IReadOnlyList<string> PathChips(PathOption o) => o.Chips.Distinct().Take(5).ToArray();

    private static string NodeName(MapNodeType t) => t switch
    {
        MapNodeType.Monster => Loc.T("일반 전투", "Enemy"),
        MapNodeType.Elite => Loc.T("엘리트", "Elite"),
        MapNodeType.RestSite => Loc.T("휴식처", "Rest Site"),
        MapNodeType.Shop => Loc.T("상점", "Shop"),
        MapNodeType.Treasure => Loc.T("보물", "Treasure"),
        MapNodeType.Boss => Loc.T("보스", "Boss"),
        MapNodeType.Ancient => Loc.T("고대의 존재", "Ancient"),
        _ => Loc.T("? 이벤트", "? Event"),
    };

    /// <summary>Map columns grow to the right on screen, so the column delta reads as left/straight/right.</summary>
    private static string Direction(MapCoord from, MapCoord to) => (to.Col - from.Col) switch
    {
        < 0 => Loc.T("(왼쪽)", "(left)"),
        > 0 => Loc.T("(오른쪽)", "(right)"),
        _ => Loc.T("(위)", "(up)"),
    };

    private static bool IsOfferHint(ScreenHint h) => h is ScreenHint.RewardLikely or ScreenHint.ShopLikely;

    /// <summary>
    /// Periodic capture: fast (<see cref="AppSettings.RecognizeHz"/>) while a reward/shop screen is likely, and a slower
    /// background watch (<see cref="AppSettings.IdleRecognizeHz"/>) otherwise, so a reward screen is found even when the
    /// log hint comes late or never (the hint only speeds things up; nothing needs a key press). The pipeline only
    /// captures while the game is the foreground window and skips recognition of visually unchanged frames, so the idle
    /// watch costs about 5 ms of capture per tick at 4K.
    /// While nothing can be drawn (overlay hidden with Ctrl+F8, game not in front or minimised) capture stops after
    /// <see cref="CapturePauseClock.After"/>, which also ends the capture session (no 4K frame copies for hours of
    /// alt-tab); it resumes the moment the game is in front and the overlay shown (<see cref="UpdateVisibility"/>).
    /// From the moment the log hints a reward / shop screen until its badges show (at most a few seconds), capture runs
    /// at the dense hunt rate (<see cref="AppSettings.HuntRecognizeHz"/>, <see cref="OfferHunt"/>), so the screen is
    /// captured ~50 ms after it appears instead of up to one 3 Hz interval later.
    /// </summary>
    private void UpdateCadence()
    {
        var now = DateTime.UtcNow;
        var offer = IsOfferHint(_snapshot.Hint) || (_snapshot.Hint == ScreenHint.None && _badges.HasBadges) || EventScreenLikely;
        var hz = offer ? Math.Clamp(_settings.RecognizeHz, 0.5, 10) : Math.Clamp(_settings.IdleRecognizeHz, 0, 10);
        hz = Math.Max(hz, Math.Max(MapCadenceHz(), RestCadenceHz())); // faster while the map / a rest site is open, so the marks follow
        var wasHunting = _hunt.Hunting;
        var huntHz = HuntHz;
        var hunting = _hunt.Update(huntHz > 0, _snapshot.Hint, _hintChangedUtc, _badges.HasBadges, now, out var huntEnds);
        if (hunting) hz = Math.Max(hz, huntHz);
        if (wasHunting && !hunting) Perf.CountHuntEnd(_hunt.LastEnd);
        // The budget ends without any event of its own: wake up then to drop back to the normal rate.
        if (huntEnds is { } left) RunLater(ref _huntTimer, left, UpdateCadence);
        else _huntTimer?.Stop();
        var paused = _pauseClock.ShouldPause(!_userHidden && _tracker.Current is { CanCapture: true }, now, out var recheck);
        if (paused) hz = 0;
        if (paused != _capturePaused)
        {
            _capturePaused = paused;
            AppLog.Info(paused ? "capture paused (overlay hidden or game not in front)" : "capture resumed");
        }
        if (recheck is { } wait) RunLater(ref _pauseTimer, wait, UpdateCadence);
        Perf.SetHunting(hunting && !paused); // perf summary: "hunt N steps in X s"
        _pipeline.SetInterval(hz > 0 ? TimeSpan.FromSeconds(1 / hz) : null);
    }

    private readonly CapturePauseClock _pauseClock = new();
    private bool _capturePaused;
    private DispatcherTimer? _pauseTimer;

    /// <summary>Dense polling while a hinted reward / shop screen is not found yet (Integration/OfferHunt.cs).</summary>
    private readonly OfferHunt _hunt = new();
    private DispatcherTimer? _huntTimer;

    /// <summary>When <see cref="GameStateSnapshot.Hint"/> last changed (UTC; default = not yet): the hunt belongs to that change.</summary>
    private DateTime _hintChangedUtc;

    /// <summary>The hunt rate; 0 (or a negative setting) = no hunt. Capped at 30 Hz: a look costs 13.5 ms (p90 20 ms).</summary>
    private double HuntHz => Math.Clamp(_settings.HuntRecognizeHz, 0, 30);

    /// <summary>One-shot UI-thread callback after <paramref name="delay"/> (restarts the timer if it is pending).</summary>
    private static void RunLater(ref DispatcherTimer? timer, TimeSpan delay, Action action)
    {
        if (timer is null)
        {
            var t = new DispatcherTimer();
            t.Tick += (_, _) =>
            {
                t.Stop();
                action();
            };
            timer = t;
        }
        timer.Stop();
        timer.Interval = (delay > TimeSpan.Zero ? delay : TimeSpan.Zero) + TimeSpan.FromMilliseconds(50);
        timer.Start();
    }

    // ---- recognition → engine → UI ------------------------------------------------------------------

    private ScreenKind? _loggedKind;

    /// <summary>Which results may draw, when the tab switches, when a half-read row shows its verdict (Integration/OfferLifecycle.cs).</summary>
    private readonly OfferLifecycle _lifecycle = new();
    private DispatcherTimer? _verdictTimer;

    private void ApplyRecognition(RecognitionResult r)
    {
        // Computed before the log said the screen closed, or the closing screen's last frames: never drawn again.
        if (!_lifecycle.Accept(r, DateTime.UtcNow))
        {
            UpdateCadence();
            return;
        }
        _applyAccepted = true;
        // Periodic captures are only logged when they are slow; log every change of screen so a missed reward screen
        // can be told apart from "the watch was not running" in app.log.
        if (r.ScreenKind != _loggedKind)
        {
            AppLog.Info($"screen: {_loggedKind?.ToString() ?? "-"} -> {r.ScreenKind} ({r.Cards.Count} cards, hint {_snapshot.Hint})");
            _loggedKind = r.ScreenKind;
        }
        UpdateRestMarks(r); // rest-site buttons / Smith grid (AppController.RestOverlay.cs)
        _removal.Observe(r.ScreenKind, r.RemovalService is not null); // before a deck grid becomes "Other": Shop → Grid → Shop = removal used
        // In combat a choose-a-card row or a card grid is a temporary pick (Discovery, Attack Potion, the draw/discard
        // piles with generated cards), not a deck offer: no badges, no skip mark, no panel offer.
        if (r.ScreenKind is ScreenKind.Choice or ScreenKind.Grid && _snapshot.Hint == ScreenHint.Combat)
            r = RecognitionResult.Other;
        // A card grid is an offer only when it shows cards the deck does not hold; otherwise it is the deck itself
        // (remove / upgrade / transform screens, deck view), which needs no badges.
        if (r.ScreenKind == ScreenKind.Grid && !CardGridFilter.IsOffer(r.Cards.Select(c => c.Card).ToList(), _snapshot.Run?.Deck.Select(d => d.Card), _snapshot.Hint))
            r = RecognitionResult.Other;
        // A shop counts with relics alone (every card covered, or all bought).
        if (r.ScreenKind == ScreenKind.Other || (r.Cards.Count == 0 && (r.ScreenKind != ScreenKind.Shop || r.Relics.Count == 0)))
        {
            _lifecycle.ScreenClosed();
            ClearBadges();
            UpdateCadence();
            return;
        }
        _lastResult = r;
        // The tracker links the next "obtained CARD.X from card reward" to this screen (upgrade read from the title colour).
        if (r.ScreenKind == ScreenKind.Reward && _source is Sts2Overlay.GameState.Tracking.ICardOfferSink sink) sink.OnCardOfferSeen(r.Cards.Select(c => c.Card).ToList());

        // A reward / choose-a-card row with a card not read yet: tier badges now, the best pick and skip verdict a bit later.
        var verdict = _lifecycle.Verdict(r, DateTime.UtcNow);
        if (verdict == PartialVerdict.Hold && _lifecycle.VerdictDue(r) is { } due)
            RunLater(ref _verdictTimer, due - DateTime.UtcNow, () => { if (_lastResult is { } last) ApplyRecognition(last); });
        var offerKey = r.ScreenKind + ":" + string.Join(",", r.Cards.Select(c => $"{c.Card}${c.Price}{(c.Unaffordable == true ? "r" : "")}{(c.OnSale ? "s" : "")}"))
                       + (r.Relics.Count > 0 ? "|" + string.Join(",", r.Relics.Select(x => $"{x.RelicId}${x.Price}{(x.Unaffordable == true ? "r" : "")}")) : "");
        var evalKey = $"{offerKey}{(_removal.Available ? "+rm" : "")}@{_snapshot.UpdatedAt.Ticks}#{_engineVersion}@{Loc.CurrentCode}/{verdict}";
        if (evalKey != _lastEvalKey)
        {
            try
            {
                var (offer, badges, skip, removal) = r.ScreenKind == ScreenKind.Shop ? EvaluateShop(r) : EvaluateReward(r);
                if (verdict != PartialVerdict.Complete) (offer, badges, skip) = PartialRow(r, verdict, offer, badges, skip);
                _vm.Offer = offer;
                _offerSource = r;
                _eventOfferRebuild = null;
                _lastBadges = badges;
                _lastSkip = skip;
                _lastRemovalText = removal;
                _lastEvalKey = evalKey;
            }
            catch (Exception ex)
            {
                AppLog.Error("recommendation failed", ex);
                ShowMessage(Loc.T("추천 계산 실패: ", "Recommendation failed: ") + ex.Message);
                return;
            }
            if (offerKey != _lastOfferKey)
            {
                _lastOfferKey = offerKey;
                AppLog.Info($"offer {offerKey}");
            }
            // PLAN §8: switch to 추천 when a reward/shop screen opens; not for a price, colour or relic change of the
            // open screen, and not again for it after the log cleared its badges.
            if (_lifecycle.SelectTab(r)) _vm.SelectedTab = PanelTab.Offer;
        }

        // Card positions can change every frame (hovering enlarges and lifts a card) even when the offer is the same,
        // so the evaluation is cached but the badge positions always come from the latest recognition.
        var specs = new BadgeSpec[r.Cards.Count];
        for (var i = 0; i < specs.Length; i++)
        {
            specs[i] = i < _lastBadges.Count
                ? _lastBadges[i] with { CardBounds = r.Cards[i].ScreenBounds, PriceBounds = r.Cards[i].PriceBounds }
                : new BadgeSpec(r.Cards[i].ScreenBounds, "?", "", false, true, r.Cards[i].Card.IsUpgraded, r.Cards[i].PriceBounds);
        }
        // The removal marker needs both: the engine recommends removal AND the probe sees the service still for sale.
        var removalMark = r.RemovalService is { } coin && _lastRemovalText is { } text ? new RemovalSpec(coin, text) : null;
        NoteBadgesShown(r); // perf: frame-to-paint of a newly shown screen (AppController.Perf.cs)
        _badges.ShowBadges(specs, _lastSkip, removalMark, showBounds: Demo is not null || DebugOutlines);
        UpdateCadence();
    }

    /// <summary>
    /// A reward / choose-a-card row with unread slots (<see cref="OfferLifecycle.Verdict"/>). Holding: the best pick and
    /// skip verdict were computed from part of the offer, so the ribbon, the crown, the skip mark and the panel's pick
    /// wait and the footer says what is being read. After the wait: the verdict, with a note that a card was not read.
    /// </summary>
    internal static (OfferSectionVm, IReadOnlyList<BadgeSpec>, SkipSpec?) PartialRow(RecognitionResult r, PartialVerdict verdict,
        OfferSectionVm offer, IReadOnlyList<BadgeSpec> badges, SkipSpec? skip)
    {
        int read = r.Cards.Count, slots = r.Slots, missing = slots - read;
        if (verdict == PartialVerdict.ShowIncomplete)
        {
            var note = Loc.T($"카드 {missing}장을 인식하지 못했습니다 ({slots}장 중 {read}장으로 판단)",
                $"{missing} card{(missing == 1 ? "" : "s")} not read (judged from {read} of {slots})");
            return (offer with { Footer = offer.Footer is { } f ? f + "\n" + note : note }, badges, skip);
        }
        return (offer with
            {
                Rows = offer.Rows.Select(row => row with { IsBest = false }).ToArray(),
                FlipHint = null,
                Footer = Loc.T($"{slots}장 중 {read}장 인식 중…", $"Reading {read} of {slots} cards…"),
            },
            badges.Select(b => b with { IsBest = false, Ribbon = null }).ToArray(),
            null);
    }

    private (OfferSectionVm, IReadOnlyList<BadgeSpec>, SkipSpec?, string?) EvaluateReward(RecognitionResult r)
    {
        var run = _snapshot.Run ?? UnknownRun;
        var cards = r.Cards.Select(c => c.Card).ToArray();
        // Event grids and "pick 1 of N" rows are not combat rewards: score them as event offers.
        var context = r.ScreenKind == ScreenKind.Reward ? _snapshot.RewardContext : OfferContext.Event;
        var eval = _engine.EvaluateOffer(run, cards, context);
        NoteOffer(r, context, eval); // play-time decision log (AppController.DecisionLog.cs)

        var rows = new List<OfferRowVm>();
        foreach (var (e, i) in eval.Cards.Select((e, i) => (e, i)).OrderBy(x => x.e.Rank))
        {
            var sub = Loc.T($"{e.Rank}위 · 데이터 신뢰도 {e.Confidence:P0}", $"#{e.Rank} · data confidence {e.Confidence:P0}");
            if (r.Cards[i].Confidence < 0.8) sub += Loc.T($" · 인식 {r.Cards[i].Confidence:P0}", $" · recognized {r.Cards[i].Confidence:P0}");
            if (!e.IsRated) sub += Loc.T(" · 미평가 카드", " · unrated card");
            if (ScoringLabels.BuildRoleText(e.BuildRoles) is { } buildRole) sub = buildRole + "\n" + sub;
            sub = WithCoopOnlyTag(sub, e);
            rows.Add(new OfferRowVm
            {
                Name = _cards.Name(e.Card),
                TierText = e.TierText,
                ScoreText = Fmt(e.Total),
                BarWidth = Bar(e.Total),
                IsBest = e.IsBest,
                IsEstimate = e.IsEstimate,
                IsUpgraded = e.Card.IsUpgraded,
                SubText = sub,
                Tip = _engine.Pack.FindTier(run.Character, e.Card.Id)?.LocalTip,
                Art = ArtOf(e.Card.Id),
                Chips = e.Chips.Select(c => Chip(c.Label, c.Value)).ToArray(),
            });
        }
        rows.Add(new OfferRowVm
        {
            Name = Loc.T("스킵", "Skip"),
            IsSkip = true,
            ScoreText = Fmt(eval.Skip.Total),
            BarWidth = Bar(eval.Skip.Total),
            IsBest = eval.SkipRecommended,
            SubText = Loc.T("카드를 받지 않음", "Take no card"),
            Chips = eval.Skip.TopReasons(3).Select(c => Chip(c.Label, c.Value)).ToArray(),
        });

        var offer = new OfferSectionVm
        {
            Title = r.ScreenKind switch
            {
                ScreenKind.Choice => Loc.T("카드 선택", "Choose a Card"),
                ScreenKind.Grid => Loc.T("카드 선택 · 이벤트", "Choose a Card · Event"),
                _ => ContextTitle(context),
            },
            ConfidenceText = ConfidenceText(eval.Confidence),
            ConfidenceKind = eval.Confidence.ToString(),
            FlipHint = CoopNote(run, eval.FlipHint),
            Rows = rows,
            Footer = eval.NoData ? Loc.T("평가 데이터가 없는 카드입니다", "No rating data for these cards")
                : eval.Best is { } best ? Loc.T($"추천: {_cards.Name(best)}", $"Pick: {_cards.Name(best)}") : Loc.T("추천: 스킵", "Pick: Skip"),
        };

        var badges = eval.Cards
            .Select((e, i) => new BadgeSpec(r.Cards[i].ScreenBounds, e.TierText, Fmt(e.Total), e.IsBest, e.IsEstimate, e.Card.IsUpgraded,
                Chips: OfferMarkers.CardChips(e.Chips), Ribbon: e.IsBest ? OfferMarkers.RecommendRibbon : null))
            .ToArray();
        return (offer, badges, OfferMarkers.Skip(eval, Fmt), null);
    }

    /// <summary>Whether this merchant visit's card-removal service is still for sale (fed every recognition result).</summary>
    private readonly RemovalAvailability _removal = new();

    private (OfferSectionVm, IReadOnlyList<BadgeSpec>, SkipSpec?, string?) EvaluateShop(RecognitionResult r)
    {
        var run = _snapshot.Run ?? UnknownRun;
        // --- shop prices (m2/shop-price): unread prices are estimated by the scorer; label colours bound the gold ---
        var offer = new ShopOffer(r.Cards.Select(c => new ShopCardOffer(c.Card, c.Price ?? 0, c.Price is not null, c.Unaffordable is bool u ? !u : null, c.OnSale)).ToArray())
        {
            Relics = r.Relics.Select(x => new ShopRelicOffer(x.RelicId, x.Rarity, x.Price ?? 0, x.Price is not null, x.Unaffordable is bool u ? !u : null)).ToArray(),
            RemovalAvailable = _removal.Available, // gone only after evidence it was used, not while the coin is merely covered
        };
        var eval = _engine.EvaluateShop(run, offer);
        NoteShop(r, eval); // play-time decision log (AppController.DecisionLog.cs)
        var recommended = eval.RecommendedPurchase.ToHashSet(ReferenceEqualityComparer.Instance);

        var rows = eval.Items.Select(item =>
        {
            var e = item.CardEvaluation;
            var isCard = item.Kind == ShopItemKind.Card && item.Card is not null;
            var isRelic = item.Kind == ShopItemKind.Relic && item.RelicId is not null;
            var score = e?.Total ?? item.Value.Total;
            var perGold = item.ValuePerGold.ToString("0.00", CultureInfo.InvariantCulture);
            var sub = Loc.T($"구매 가치 {Fmt(item.Value.Total)} · 골드당 {perGold}", $"Buy value {Fmt(item.Value.Total)} · per Gold {perGold}");
            if (!item.Affordable) sub += Loc.T(" · 골드 부족", " · not enough Gold");
            if (e is not null && ScoringLabels.BuildRoleText(e.BuildRoles) is { } buildRole) sub = buildRole + "\n" + sub;
            sub = WithCoopOnlyTag(sub, e);
            if (item.RelicEvaluation is { FitArchetype: { } fit } re && re.FitMultiplier != 1.0)
            {
                var mult = re.FitMultiplier.ToString("0.00", CultureInfo.InvariantCulture);
                sub = Loc.T($"{fit.DisplayName} 빌드 적합 ×{mult}", $"{fit.DisplayName} build fit ×{mult}") + "\n" + sub;
            }
            return new OfferRowVm
            {
                Name = isCard ? _cards.Name(item.Card!.Value)
                    : isRelic ? RelicName(item.RelicId!) + Loc.T(" (유물)", " (Relic)")
                    : $"{RemovalName()} ({(item.RemovalTarget is { } t ? _cards.Name(t) : "?")})",
                TierText = e?.TierText ?? "—",
                ScoreText = Fmt(score),
                BarWidth = Bar(score),
                IsBest = recommended.Contains(item),
                IsEstimate = e?.IsEstimate ?? false,
                IsUpgraded = item.Card?.IsUpgraded ?? false,
                Unaffordable = !item.Affordable,
                PriceText = ShopPriceText(item),
                Art = isCard ? ArtOf(item.Card!.Value.Id) : null,
                Relics = isRelic ? [RelicTip(item.RelicId!)] : [],
                SubText = sub,
                Chips = (e?.Chips.Select(c => Chip(c.Label, c.Value)) ?? item.Value.TopReasons(3).Select(c => Chip(c.Label, c.Value))).ToArray(),
            };
        }).ToArray();

        var names = eval.RecommendedPurchase
            .Select(i => i.Kind == ShopItemKind.Card && i.Card is { } c ? _cards.Name(c)
                : i.Kind == ShopItemKind.Relic && i.RelicId is { } id ? RelicName(id)
                : RemovalName())
            .ToArray();
        var section = new OfferSectionVm
        {
            Title = !eval.GoldKnown ? Loc.T("상점 · 보유 골드 모름", "Shop · Gold unknown")
                : eval.GoldCorrectedByScreen
                ? Loc.T($"상점 · 보유 ~{eval.Gold}골드 (가격 색으로 보정)", $"Shop · ~{eval.Gold} Gold (from price colors)")
                : Loc.T($"상점 · 보유 {eval.Gold}골드", $"Shop · {eval.Gold} Gold"),
            ConfidenceText = "",
            FlipHint = CoopNote(run, null), // shop cards are valued like the reward screen: the same co-op caveat applies
            Rows = rows,
            Footer = !eval.GoldKnown ? ShopFooterGoldUnknown(names)
                : names.Length > 0
                ? Loc.T($"추천 구매: {string.Join(" + ", names)} · 남는 골드 {eval.GoldLeft}", $"Buy: {string.Join(" + ", names)} · {eval.GoldLeft} Gold left")
                : Loc.T("추천 구매 없음 (골드 아끼기)", "Buy nothing (save Gold)"),
        };

        // Badges in recognition order. Match each on-screen card to its evaluated item (a queue per card handles duplicates).
        var byCard = eval.Items.Where(i => i.Card is not null)
            .GroupBy(i => i.Card!.Value)
            .ToDictionary(g => g.Key, g => new Queue<ShopItemEvaluation>(g));
        var badges = r.Cards.Select(rc =>
        {
            var item = byCard.TryGetValue(rc.Card, out var q) && q.Count > 0 ? q.Dequeue() : null;
            var e = item?.CardEvaluation;
            var buy = item is not null && recommended.Contains(item);
            return new BadgeSpec(rc.ScreenBounds, e?.TierText ?? "?", e is null ? "" : Fmt(e.Total),
                buy, e?.IsEstimate ?? true, rc.Card.IsUpgraded, Ribbon: buy ? OfferMarkers.BuyRibbon : null);
        }).ToArray();
        return (section, badges, null, OfferMarkers.RemovalText(eval, _cards.Name));
    }

    /// <summary>Korean relic title from the local game text (data/local, never committed); the id when it is missing.</summary>
    private string RelicName(string relicId) => _eventText.RelicTitle(relicId) ?? relicId.Replace("RELIC.", "", StringComparison.Ordinal);

    /// <summary>Icon + effect text of a relic in the current language (title falls back to <see cref="RelicName"/>).</summary>
    private RelicTipVm RelicTip(string relicId) => _relicInfo.For(relicId, Loc.CurrentCode, RelicName(relicId));

    /// <summary>"75골드", "~150골드 (추정)" for an unread label, "37골드 세일" for the half-price card.</summary>
    private static string ShopPriceText(ShopItemEvaluation item)
        => (item.PriceEstimated ? Loc.T($"~{item.Price}골드 (추정)", $"~{item.Price} Gold (est.)") : Loc.T($"{item.Price}골드", $"{item.Price} Gold"))
           + (item.OnSale ? Loc.T(" 세일", " sale") : "");

    /// <summary>
    /// Shop footer when the gold is unknown (log-only run): the bundle assumes every item without a red price is affordable,
    /// so it says so and shows no "gold left".
    /// </summary>
    internal static string ShopFooterGoldUnknown(IReadOnlyList<string> names) => names.Count > 0
        ? Loc.T($"추천 구매 (골드 모름, 가격 색 기준): {string.Join(" + ", names)}", $"Buy (Gold unknown, by price colors): {string.Join(" + ", names)}")
        : Loc.T("추천 구매 없음 (골드 모름)", "Buy nothing (Gold unknown)");

    /// <summary>The merchant's card-removal service (game term: Card Removal).</summary>
    private static string RemovalName() => Loc.T("카드 제거", "Card Removal");
    // --- end shop prices ---

    /// <param name="forgetScreen">
    /// The log says the card screen closed (card picked, room left): the recogniser also forgets the frames it merged, so
    /// a held or late result can never draw these badges again, and a fresh recognition reads whatever is open now (a
    /// second card reward of the same reward list shows at once).
    /// </param>
    private void ClearBadges(bool forgetScreen = false)
    {
        if (forgetScreen) _lifecycle.Forget(_lastResult, _pipeline.ResetRecognition(), DateTime.UtcNow);
        _verdictTimer?.Stop();
        _badges.Clear();
        _paintedScreen = null; // the next screen with badges is timed again (AppController.Perf.cs)
        _lastResult = null;
        _lastEvalKey = null;
        _lastOfferKey = null;
        // In an event room the 추천 tab shows the event's options (AppController.Events.cs): keep it fresh.
        if (_vm.Offer.HasContent && !_vm.Offer.IsStale && _lastEvent is null) _vm.Offer = _vm.Offer with { IsStale = true };
    }

    private void ResetOffer()
    {
        ClearBadges();
        _vm.Offer = OfferSectionVm.Empty;
        _offerSource = null;
        _eventOfferRebuild = null;
    }

    // ---- deck / build tabs ------------------------------------------------------------------------------------

    private void RefreshRunViews(RunState? run)
    {
        if (run is null)
        {
            _vm.RunText = Loc.T("런 정보 없음", "No run info");
            _vm.Deck = DeckSectionVm.Empty;
            _vm.Build = BuildSectionVm.Empty;
            return;
        }

        _vm.RunText = RunHeader(run);
        try
        {
            var profile = _engine.Analyze(run);
            _vm.Deck = BuildDeck(run, profile);
            _vm.Build = BuildArchetypes(run, _engine.Archetypes(run, EstimateRemainingRewards(run)));
        }
        catch (Exception ex)
        {
            AppLog.Error("deck/build analysis failed", ex);
            ShowMessage(Loc.T("덱 분석 실패: ", "Deck analysis failed: ") + ex.Message);
        }
    }

    /// <summary>
    /// The panel's run line. HP and gold show as "?" with a "로그 추정" tag when they are only placeholders
    /// (<see cref="RunState.VitalsKnown"/>: log-only run, e.g. a co-op guest).
    /// </summary>
    internal static string RunHeader(RunState run)
    {
        var asc = run.Ascension > 0 ? Loc.T($" · 승천 {run.Ascension}", $" · Ascension {run.Ascension}") : "";
        if (!run.VitalsKnown)
        {
            return Loc.T(
                $"{CharacterName(run.Character)} · {run.Act}막 {run.Floor}층 · HP ? · 골드 ?{asc} · 로그 추정",
                $"{CharacterName(run.Character)} · Act {run.Act} Floor {run.Floor} · HP ? · Gold ?{asc} · from log");
        }
        return Loc.T(
            $"{CharacterName(run.Character)} · {run.Act}막 {run.Floor}층 · HP {run.Hp}/{run.MaxHp} · {run.Gold}골드{asc}",
            $"{CharacterName(run.Character)} · Act {run.Act} Floor {run.Floor} · HP {run.Hp}/{run.MaxHp} · {run.Gold} Gold{asc}");
    }

    private DeckSectionVm BuildDeck(RunState run, DeckProfile profile)
    {
        // Grouped by a kind (not by its label), so the order and grouping don't depend on the display language.
        DeckGroup GroupOf(string id) => _cards.Type(run.Character, id) switch
        {
            CardTypes.Attack => DeckGroup.Attack,
            CardTypes.Skill => DeckGroup.Skill,
            CardTypes.Power => DeckGroup.Power,
            CardTypes.Curse or CardTypes.Status => DeckGroup.CurseStatus,
            _ => DeckGroup.Other,
        };

        var groups = run.Deck
            .GroupBy(c => GroupOf(c.Id))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var cards = g.GroupBy(c => c.Id)
                    .Select(byId =>
                    {
                        var tier = TierOf(run, byId.Key);
                        var count = byId.Count();
                        var upgraded = byId.Count(c => c.Upgrade > 0);
                        var upText = upgraded == 0 ? null : count == 1 ? Loc.T("강화", "Upgraded") : Loc.T($"강화 {upgraded}", $"Upgraded {upgraded}");
                        string? doubt = _snapshot.Doubts?.ReasonText(byId.Key);
                        return (Tier: tier, Vm: new DeckCardVm(tier?.ToString() ?? "?", _cards.Name(byId.Key), $"×{count}", upText, doubt, ArtOf(byId.Key)));
                    })
                    .OrderBy(x => x.Tier is null ? 99 : (int)x.Tier)
                    .ThenBy(x => x.Vm.Name, StringComparer.CurrentCulture)
                    .Select(x => x.Vm)
                    .ToArray();
                return new DeckGroupVm($"{DeckGroupName(g.Key)} ({g.Count()})", cards);
            })
            .ToArray();

        var roles = Roles.All
            .Where(profile.RoleTarget.ContainsKey)
            .Select(role =>
            {
                var supply = profile.RoleSupply.GetValueOrDefault(role);
                var target = profile.RoleTarget[role];
                var ratio = target > 0 ? Math.Min(1, supply / target) : 1;
                var weak = profile.WeakRoles.Contains(role) || profile.RoleGap.GetValueOrDefault(role) > 0.5;
                return new RoleBarVm(ScoringLabels.Role(role), $"{Fmt1(supply)} / {Fmt1(target)}", ratio * 150, weak);
            })
            .ToArray();

        var upgradedTotal = run.Deck.Count(c => c.Upgrade > 0);
        var summary = Loc.T(
            $"덱 {run.Deck.Count}장 · 강화 {upgradedTotal}장 · 파워 {profile.PowerCount}장",
            $"Deck {run.Deck.Count} · Upgraded {upgradedTotal} · Powers {profile.PowerCount}");
        if (profile.Curses > 0) summary += Loc.T($" · 저주 {profile.Curses}장", $" · Curses {profile.Curses}");
        if (_snapshot.Doubts is { } d)
        {
            // Log-only rebuild (co-op guest): say what it can't know yet. Exact again at run end (.run file).
            var parts = new List<string>();
            if (d.Unidentified > 0) parts.Add(Loc.T($"미확인 카드 {d.Unidentified}장", $"{d.Unidentified} unidentified cards"));
            if (d.UnknownUpgrades > 0) parts.Add(Loc.T($"위치 모를 강화 {d.UnknownUpgrades}회", $"{d.UnknownUpgrades} unplaced upgrades"));
            if (d.UnknownRemovals > 0) parts.Add(Loc.T($"모를 제거 {d.UnknownRemovals}장", $"{d.UnknownRemovals} unknown removals"));
            if (d.CardReasons.Count > 0) parts.Add(Loc.T($"? 표시 {d.CardReasons.Count}종", $"{d.CardReasons.Count} marked ?"));
            summary += Loc.T(" · 추정: ", " · Estimated: ") + string.Join(" · ", parts);
        }
        if (_snapshot.LogOnly) summary += "\n" + LogDeckNote();
        return new DeckSectionVm { Summary = summary, Groups = groups, Roles = roles };
    }

    /// <summary>Standing 덱-tab note for a deck rebuilt from the game log (no save file: co-op guest).</summary>
    internal static string LogDeckNote() => Loc.T("로그로 추정한 덱입니다 (상점 구매 등은 빠질 수 있음)", "Deck rebuilt from the log (shop purchases etc. may be missing)");

    /// <summary>Deck tab groups, in display order.</summary>
    private enum DeckGroup
    {
        Attack,
        Skill,
        Power,
        CurseStatus,
        Other,
    }

    private static string DeckGroupName(DeckGroup g) => g switch
    {
        DeckGroup.Attack => Loc.T("공격", "Attack"),
        DeckGroup.Skill => Loc.T("스킬", "Skill"),
        DeckGroup.Power => Loc.T("파워", "Power"),
        DeckGroup.CurseStatus => Loc.T("저주·상태", "Curse·Status"),
        _ => Loc.T("기타", "Other"),
    };

    private Tier? TierOf(RunState run, string id)
    {
        var entry = _engine.Pack.FindTier(run.Character, id);
        if (entry is null) return null;
        return entry.ActTier is not null && entry.ActTier.TryGetValue(run.Act, out var t) ? t : entry.Tier;
    }

    /// <summary>
    /// Archetype id → card tiles shown, for archetypes the user opened or closed. The build section is rebuilt on
    /// every snapshot, so the choice lives here; archetypes not in it follow the default (only the primary is open).
    /// </summary>
    private readonly Dictionary<string, bool> _buildCardsExpanded = new(StringComparer.Ordinal);

    private void ToggleBuildCards(string archetypeId)
    {
        var items = _vm.Build.Archetypes.Select(a =>
        {
            if (a.Id != archetypeId) return a;
            _buildCardsExpanded[archetypeId] = !a.IsExpanded;
            return a with { IsExpanded = !a.IsExpanded };
        }).ToArray();
        _vm.Build = _vm.Build with { Archetypes = items };
    }

    /// <summary>Most relics a build lists: its key relics first, then the ranker favourites by lift.</summary>
    private const int MaxBuildRelics = 6;

    /// <summary>Lift above which a measured relic counts as one top players favour in a build (relic-fit.json fitRatio).</summary>
    private const double FavouredRelicLift = 1.5;

    /// <summary>
    /// Relics that suit <paramref name="a"/>: the pack's key relics (the build's own relics), then relics top players take clearly
    /// more often in this build (relic-fit.json, use = true, fitRatio ≥ 1.5), strongest first.
    /// </summary>
    private IReadOnlyList<BuildRelicVm> BuildRelics(RunState run, Archetype a)
    {
        var owned = run.Relics.ToHashSet(StringComparer.Ordinal);
        var list = new List<BuildRelicVm>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in a.KeyRelics)
        {
            if (list.Count >= MaxBuildRelics || !seen.Add(k.Id)) continue;
            list.Add(new BuildRelicVm(RelicTip(k.Id), owned.Contains(k.Id), Loc.T("빌드 핵심 유물", "Key relic of this build")));
        }
        if (_engine.Pack.RelicFit?.Archetypes.TryGetValue(a.Id, out var fits) == true)
        {
            foreach (var (id, f) in fits.Where(p => p.Value.Use == true && FitLift(p.Value) >= FavouredRelicLift).OrderByDescending(p => FitLift(p.Value)))
            {
                if (list.Count >= MaxBuildRelics) break;
                if (!seen.Add(id)) continue;
                var lift = FitLift(f).ToString("0.0", CultureInfo.InvariantCulture);
                list.Add(new BuildRelicVm(RelicTip(id), owned.Contains(id),
                    Loc.T($"상위 플레이어가 이 빌드에서 ×{lift} 더 자주 고름", $"Top players take it ×{lift} more often in this build")));
            }
        }
        return list;
    }

    private static double FitLift(RelicFitStat f) => f.FitRatio ?? (f.PickRateOut > 0 ? f.PickRateIn / f.PickRateOut : 0);

    private BuildSectionVm BuildArchetypes(RunState run, ArchetypeReport report)
    {
        if (report.Statuses.Count == 0) return BuildSectionVm.Empty;

        static string RoleName(string role) => BuildCardsPresenter.RoleName(role);

        BuildCardVm Card(string id, string role, bool owned)
        {
            var rarity = _cards.Rarity(run.Character, id);
            return new BuildCardVm(_cards.Name(id), role, rarity is null ? null : CardInfoProvider.RarityName(rarity),
                TierOf(run, id)?.ToString() ?? "?", owned, ArtOf(id), _cardText.For(id, Loc.CurrentCode));
        }

        // Every build of the character, highest commit first. No cap: Ironclad has 8 builds and Defect 6, and a top-5 cut
        // hid whole builds (자해 sorts 5th by id at zero commit, so one card for any build after it pushed it out).
        // The panel scrolls and only the primary build's card tiles start open, so the extra rows stay short.
        var items = report.Statuses.Select(st =>
        {
            var a = st.Archetype;
            var groups = BuildCardsPresenter.Groups(st, _engine.Pack.Canonical, Card);
            var isPrimary = ReferenceEquals(st, report.Primary);
            // m2/forecast-calib: the old "핵심 카드가 나올 확률" line is replaced by the calibrated forecast line,
            // filled in by ForecastReviewPresenter.WithForecast (AppController.ForecastReview.cs).
            string? prob = null;
            var missing = st.MissingCards.Count == 0
                ? null
                : Loc.T("필요: ", "Needs: ") + string.Join(", ", st.MissingCards.Take(5).Select(m =>
                    Loc.T($"{_cards.Name(m.CardId)}({RoleName(m.Role)}, {CardInfoProvider.RarityName(m.Rarity)})",
                          $"{_cards.Name(m.CardId)} ({RoleName(m.Role)}, {CardInfoProvider.RarityName(m.Rarity)})")));
            // 코어 먼저: before a core the scorer locks this build's synergy; after it, essentials (payoff + enabler) before support.
            var cores = string.Join(", ", st.MissingCore.Take(3).Select(m => _cards.Name(m.CardId)));
            var stage = st.CoreTarget == 0 || st.Commit <= 0 ? null
                : st.OwnedCore == 0 ? Loc.T($"코어 전: 시너지 점수 잠김 · 먼저 노릴 코어 {cores}", $"No Core yet: synergy locked · go for {cores}")
                : (st.EssentialTarget > 0
                      ? Loc.T($"필수 시너지 {Math.Min(st.OwnedEssential, st.EssentialTarget)}/{st.EssentialTarget}", $"Key synergy {Math.Min(st.OwnedEssential, st.EssentialTarget)}/{st.EssentialTarget}")
                      : "")
                  + (st.CoreSecured ? "" : Loc.T($" · 코어 {st.OwnedCore}/{st.CoreTarget}, 다음 코어 {cores}", $" · Core {st.OwnedCore}/{st.CoreTarget}, next Core {cores}"));
            if (!string.IsNullOrWhiteSpace(stage)) missing = stage.TrimStart(' ', '·') + (missing is null ? "" : "\n" + missing);
            var needs = st.UnmetNeeds.Count == 0
                ? null
                : Loc.T("조건 미충족: ", "Unmet: ") + string.Join(", ", st.UnmetNeeds.Select(n =>
                    $"{(n.Role is { } r ? ScoringLabels.Role(r) : n.Tag is { } tag ? ScoringLabels.Tag(tag) : "?")} {Fmt1(n.Min)}+"));
            return new ArchetypeVm
            {
                Id = a.Id,
                Name = a.DisplayName,
                Color = ParseColor(a.Color),
                IsPrimary = isPrimary,
                CardGroups = groups,
                Relics = BuildRelics(run, a),
                CardsSummaryText = BuildCardsPresenter.Summary(groups),
                IsExpanded = _buildCardsExpanded.TryGetValue(a.Id, out var open) ? open : isPrimary,
                ToggleCardsCommand = new RelayCommand(_ => ToggleBuildCards(a.Id)),
                CommitText = Loc.T(
                    $"몰입 {st.Commit:P0} · 완성도 {st.CompletionPercent:0}%{(st.IsCommitted ? " · 방향 확정" : "")}",
                    $"Commit {st.Commit:P0} · Completion {st.CompletionPercent:0}%{(st.IsCommitted ? " · locked in" : "")}"),
                CompletionBarWidth = Math.Clamp(st.CompletionPercent, 0, 100) / 100 * 356,
                StageText = new string('●', st.Stage) + new string('○', Math.Max(0, 5 - st.Stage)),
                ProbabilityText = prob,
                MissingText = missing,
                NeedsText = needs,
            };
        }).ToArray();

        var summary = report.Primary is not { } primary
            ? Loc.T("아직 정해진 빌드 방향이 없습니다", "No build direction yet")
            : report.IsHybrid && report.Statuses.Count > 1
                ? Loc.T($"현재 방향: {primary.Archetype.DisplayName} + {report.Statuses[1].Archetype.DisplayName} (하이브리드)",
                        $"Direction: {primary.Archetype.DisplayName} + {report.Statuses[1].Archetype.DisplayName} (hybrid)")
                : Loc.T($"현재 방향: {primary.Archetype.DisplayName}", $"Direction: {primary.Archetype.DisplayName}");
        return new BuildSectionVm { Summary = summary, Archetypes = items };
    }

    /// <summary>
    /// [추정] Card rewards left in the current act: ~15 floors per act, about half of them fights with a card reward.
    /// The lead can replace this with a count from the map / PathAdvisor.
    /// </summary>
    private static int EstimateRemainingRewards(RunState run)
    {
        const int floorsPerAct = 15;
        var floorsLeft = Math.Max(0, floorsPerAct * run.Act - run.Floor);
        return Math.Max(1, (int)Math.Round(floorsLeft * 0.5));
    }

    // ---- visibility / windows ----------------------------------------------------------------------------------

    private void OnTrackerChanged()
    {
        var info = _tracker.Current;
        _vm.StatusText = info switch
        {
            null => Loc.T("게임 창을 찾는 중…", "Looking for the game window…"),
            { IsSimulated: true } => Loc.T($"데모 (가상 화면 {info.CaptureRect.Width}x{info.CaptureRect.Height})", $"Demo (virtual screen {info.CaptureRect.Width}x{info.CaptureRect.Height})"),
            { IsMinimized: true } => Loc.T("게임 최소화됨", "Game minimized"),
            _ => Loc.T(
                $"게임 {info.CaptureRect.Width}x{info.CaptureRect.Height} · x{Dpi.ScaleForRect(info.CaptureRect):0.##}{(info.IsForeground ? "" : " (뒤에 있음)")}",
                $"Game {info.CaptureRect.Width}x{info.CaptureRect.Height} · x{Dpi.ScaleForRect(info.CaptureRect):0.##}{(info.IsForeground ? "" : " (in background)")}"),
        };
        UpdateShellForGame(info); // notices, panel on the game's monitor (AppController.Shell.cs)
        ApplyUiScale();
        UpdateVisibility();
    }

    // ---- UI 배율 ------------------------------------------------------------------------------------------------

    /// <summary>The scale in use (physical factor vs. the 1080p look) and whether it is the automatic one.</summary>
    public (double Effective, bool IsAuto) CurrentUiScale
    {
        get
        {
            var h = GameHeight();
            return (UiScale.Effective(_settings.UiScale, h), _settings.UiScale is null or <= 0);
        }
    }

    /// <summary>Tray "UI 배율": null = auto. Persisted in settings.json.</summary>
    public void SetUiScale(double? scale)
    {
        _settings.UiScale = scale;
        SettingsStore.Save(AppPaths.SettingsFile, _settings);
        ApplyUiScale();
        ShowMessage(Loc.T("UI 배율: ", "UI scale: ") + _vm.UiScaleText);
        StateChanged?.Invoke();
    }

    private int GameHeight()
    {
        var info = _tracker.Current;
        return info is { CaptureRect.IsEmpty: false } ? info.CaptureRect.Height : Dpi.PrimaryMonitorRect().Height;
    }

    /// <summary>
    /// Panel: LayoutTransform = UI 배율 / panel monitor DPI (WPF applies the DPI itself, so it is not counted twice).
    /// Badges: card-relative size × (UI 배율 / auto), i.e. unchanged at auto (the card width already follows the game size).
    /// </summary>
    private void ApplyUiScale()
    {
        var h = GameHeight();
        var auto = UiScale.Auto(h);
        var (effective, isAuto) = CurrentUiScale;
        var dpi = _panel.Hwnd != IntPtr.Zero ? Math.Max(1u, NativeMethods.GetDpiForWindow(_panel.Hwnd)) / 96.0 : 1.0;
        _vm.UiScale = UiScale.LayoutFactor(effective, dpi);
        _vm.UiScaleText = $"UI {effective * 100:0}%{(isAuto ? Loc.T(" (자동)", " (auto)") : "")}";
        _badges.ScaleMultiplier = effective / auto;
        SetMapScale(effective / auto);
    }

    /// <summary>
    /// Overlays are visible only while the game (or one of our own windows) is the foreground window and the user
    /// hasn't hidden them with Ctrl+F8. Otherwise a topmost overlay would float over the desktop and other apps.
    /// </summary>
    public void UpdateVisibility()
    {
        var info = _tracker.Current;
        var show = !_userHidden && info is { CanShowOverlay: true };
        // "항상 맨 앞" off: the panel is a normal window, so it may stay open while the game runs in the background.
        var showPanel = show || (!_userHidden && !_settings.AlwaysOnTop && info is { IsMinimized: false });
        if (show)
        {
            if (!_badges.IsVisible) _badges.Show();
            _badges.SetLayerRect(info!.CaptureRect);
        }
        else if (_badges.IsVisible)
        {
            _badges.Hide();
        }
        UpdateMapLayerVisibility(show, info);
        UpdateEventMarkerVisibility(); // AppController.Events.cs
        UpdateHoverWatch(show); // AppController.HoverYield.cs
        if (showPanel)
        {
            if (!_panel.IsVisible) _panel.Show();
            ReassertTopmost();
        }
        else if (_panel.IsVisible)
        {
            _panel.Hide();
        }
        UpdateCadence(); // capture pauses while nothing can be drawn, and resumes at once
        StateChanged?.Invoke();
    }

    private void ReassertTopmost()
    {
        if (!_panel.IsVisible && !_badges.IsVisible) return;
        if (TrayMenuOpen) return; // would lift our windows above the open tray menu (AppController.Shell.cs)
        // Badges first, then the panel, so the panel ends up above the badge layer.
        OverlayWindowHelper.AssertTopmost(_badges.Hwnd);
        ReassertMapTopmost();
        ReassertEventMarkersTopmost(); // AppController.Events.cs
        if (_settings.AlwaysOnTop) OverlayWindowHelper.AssertTopmost(_panel.Hwnd);
    }

    // ---- "항상 맨 앞에 표시" (tray / Ctrl+F6), persisted in settings.json ----

    public bool AlwaysOnTop => _settings.AlwaysOnTop;

    public void ToggleAlwaysOnTop()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        SettingsStore.Save(AppPaths.SettingsFile, _settings);
        _panel.KeepOnTop = _settings.AlwaysOnTop;
        AppLog.Info($"always on top = {_settings.AlwaysOnTop}");
        ShowMessage(_settings.AlwaysOnTop
            ? Loc.T("항상 맨 앞에 표시 켜짐", "Always on top: on")
            : Loc.T("항상 맨 앞에 표시 꺼짐: 패널이 일반 창처럼 다른 창 뒤로 갈 수 있습니다", "Always on top: off. The panel can go behind other windows like a normal window"));
        UpdateVisibility();
    }

    /// <summary>"방송·캡처에 오버레이 표시" is on (the user's choice; it can still be hidden for a GDI frame).</summary>
    public bool ShowOverlayInCapture => _pipeline.CaptureVisibility.ShowInCapture;

    /// <summary>True while recognition reads the desktop (GDI fallback), which hides the overlay from capture anyway.</summary>
    public bool CaptureForcedHidden => _pipeline.CaptureVisibility.RecognitionSeesDesktop;

    /// <summary>Tray: shows or hides the overlay in OBS / Discord / screenshots (persisted). Recognition is unaffected.</summary>
    public void ToggleShowInCapture()
    {
        _settings.ShowOverlayInCapture = !_pipeline.CaptureVisibility.ShowInCapture;
        _pipeline.CaptureVisibility.ShowInCapture = _settings.ShowOverlayInCapture;
        SettingsStore.Save(AppPaths.SettingsFile, _settings);
        AppLog.Info($"show overlay in capture = {_settings.ShowOverlayInCapture}");
        ShowMessage(_settings.ShowOverlayInCapture
            ? Loc.T("방송·캡처에 오버레이 표시 켜짐: OBS 디스플레이 캡처, 디스코드 화면 공유, 스크린샷에 보입니다",
                    "Overlay in streams/captures: on. Visible in OBS display capture, Discord screen share and screenshots")
            : Loc.T("방송·캡처에 오버레이 표시 꺼짐: 내 화면에만 보이고 녹화·방송·스크린샷에는 안 나옵니다",
                    "Overlay in streams/captures: off. Only on your screen, not in recordings, streams or screenshots"));
    }

    // ---- commands (hotkeys / tray) ------------------------------------------------------------------------

    public void ToggleHidden()
    {
        _userHidden = !_userHidden;
        AppLog.Info($"user hidden = {_userHidden}");
        UpdateVisibility();
    }

    public void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        _panel.SetClickThrough(_clickThrough);
        _vm.ClickThrough = _clickThrough;
        ShowMessage(_clickThrough
            ? Loc.T("클릭 통과 켜짐: 마우스가 게임으로 갑니다", "Click-through on: the mouse goes to the game")
            : Loc.T("클릭 통과 꺼짐: 패널을 클릭할 수 있습니다", "Click-through off: the panel can be clicked"));
        StateChanged?.Invoke(); // tray check mark
    }

    // ---- debug outline mode (tray / Ctrl+F11): draw the recognised card and price boxes on the badge layer ----

    /// <summary>Outlines of what the recogniser found (cards blue, price labels orange). Session only, not saved.</summary>
    public bool DebugOutlines { get; private set; }

    public void ToggleDebugOutlines()
    {
        DebugOutlines = !DebugOutlines;
        AppLog.Info($"debug outlines = {DebugOutlines}");
        ShowMessage(DebugOutlines
            ? Loc.T("인식 영역 표시 켜짐: 카드(파랑)·가격(주황) 상자", "Recognition outlines on: card (blue) and price (orange) boxes")
            : Loc.T("인식 영역 표시 꺼짐", "Recognition outlines off"));
        _pipeline.RequestRecognize(); // redraw with the next result
        StateChanged?.Invoke();
    }

    public void RecognizeNow()
    {
        ShowMessage(Loc.T("지금 인식하는 중…", "Recognizing now…"));
        _pipeline.RequestRecognize();
    }

    /// <summary>
    /// Ctrl+F9 (developer debug): saves the game screen as a PNG in the captures folder. Only with
    /// <c>"debugCaptures": true</c> in settings.json, so screenshots are never left there by accident; players never
    /// need it (recognition works in memory). <paramref name="force"/> is for <c>--selftest</c>.
    /// </summary>
    public void SaveCapture(bool force = false)
    {
        if (!force && !_settings.DebugCaptures)
        {
            AppLog.Info("capture save ignored: debugCaptures is off");
            return;
        }
        _pipeline.RequestSave();
    }

    public void ReloadPack()
    {
        (_engine, _cards, _packLoaded) = LoadPack(_packDir, CatalogPath);
        _engine.UseLearnedModel = !string.Equals(_settings.CardModel, "engine", StringComparison.OrdinalIgnoreCase); // keep cardModel after a reload
        _vm.PackText = PackText();
        _cardText = CardTextProvider.ForLocalData(_engine.Pack, _packDir);
        _relicInfo = RelicInfoProvider.ForLocalData(_packDir); // new icons / localization after a resource prepare
        _engineVersion++;
        RefreshRunViews(_snapshot.Run);
        ReloadForecastReview(); // AppController.ForecastReview.cs
        ReloadEvents(); // AppController.Events.cs
        if (_lastResult is not null) ApplyRecognition(_lastResult);
        ShowMessage(Loc.T("데이터 팩을 다시 읽었습니다", "Data pack reloaded"));
        PackReloaded?.Invoke();
    }

    // ---- local resources (Services.LocalResourceService) -------------------------------------------------

    /// <summary>The loaded data pack and its folder, for the game-version guard.</summary>
    public DataPack Pack => _engine.Pack;

    public string PackDir => _packDir;

    /// <summary>Raised (UI thread) after <see cref="ReloadPack"/>.</summary>
    public event Action? PackReloaded;

    /// <summary>
    /// data/local was (re)generated: use the new catalog for names, and a fresh recogniser that reads the new art and
    /// catalog (its art cache is warmed on a worker thread). Demo mode keeps its fake recogniser.
    /// </summary>
    public void ApplyPreparedResources(string? catalogPath)
    {
        CatalogPath = catalogPath ?? CatalogPath;
        _art = CardArtProvider.ForLocalData(); // new art + catalog for the panel thumbnails
        if (Demo is null)
        {
            var recognizer = IntegrationFactory.CreateRealRecognizer();
            _pipeline.Recognizer = recognizer;
            _ = Task.Run(recognizer.Preload);
        }
        ReloadPack();
        ShowMessage(Loc.T("게임 리소스를 준비했습니다 (카드 그림·이름)", "Game resources ready (card art and names)"));
    }
    // ---- end local resources ----

    private CardArtVm? ArtOf(string cardId) => _art.For(_engine.Pack.Canonical(cardId));

    public void ShowMessage(string text)
    {
        _vm.MessageText = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>"팩 sample", or "팩 sample 로드 실패" when the last load failed. Rebuilt on a language change.</summary>
    private string PackText()
    {
        var name = Path.GetFileName(_packDir.TrimEnd('\\', '/'));
        return _packLoaded ? Loc.T($"팩 {name}", $"Pack {name}") : Loc.T($"팩 {name} 로드 실패", $"Pack {name} failed to load");
    }

    /// <summary>Loads the data pack with <see cref="DataPackLoader.LoadDirectory"/>; an error leaves an empty pack (every card unrated).</summary>
    private static (RecommendationEngine, CardInfoProvider, bool Loaded) LoadPack(string dir, string? catalogPath)
    {
        DataPack pack;
        var loaded = true;
        try
        {
            pack = DataPackLoader.LoadDirectory(dir);
            // The catalog comes from data/catalog next to the packs (DataPackLoader.SiblingCatalogPath). Without it curses
            // and statuses look like unrated cards (e.g. a curse offered as the best upgrade), so say so loudly.
            var catalogText = pack.Catalog is { } cat ? $"{cat.Cards.Count} catalog cards" : "NO CATALOG: curse/status detection off";
            AppLog.Info($"pack loaded: {dir} ({pack.Tiers.Sum(t => t.Value.Cards.Count)} rated cards, {pack.Archetypes.Count} archetypes, {catalogText})");
            if (pack.Catalog is null) AppLog.Warn($"pack has no card catalog: expected {DataPackLoader.SiblingCatalogPath(dir)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Error($"pack load failed: {dir}", ex);
            pack = new DataPack();
            loaded = false;
        }
        return (new RecommendationEngine(pack), CardInfoProvider.Load(catalogPath, pack), loaded);
    }

    private static string ContextTitle(OfferContext c) => c switch
    {
        OfferContext.EliteReward => Loc.T("카드 보상 · 엘리트", "Card Reward · Elite"),
        OfferContext.BossReward => Loc.T("카드 보상 · 보스", "Card Reward · Boss"),
        OfferContext.Event => Loc.T("카드 선택 · 이벤트", "Choose a Card · Event"),
        OfferContext.Shop => Loc.T("상점", "Shop"),
        _ => Loc.T("카드 보상", "Card Reward"),
    };

    /// <summary>The panel's confidence label for a recommendation (cards and events).</summary>
    private static string ConfidenceText(RecommendationConfidence c) => c switch
    {
        RecommendationConfidence.Sure => Loc.T("확신", "Confident"),
        RecommendationConfidence.Close => Loc.T("박빙", "Close call"),
        RecommendationConfidence.Estimate => Loc.T("추정", "Estimate"),
        _ => Loc.T("보통", "Moderate"),
    };

    /// <summary>The card panel's top line: the flip hint, plus the co-op caveat when a multiplayer run is ranked by the learned model.</summary>
    private string? CoopNote(RunState run, string? flipHint) => WithCoopNote(flipHint, run.IsMultiplayer && _engine.LearnedModelActive);

    /// <summary>
    /// The learned model was trained on solo Ascension 10 top players' picks; in co-op it is used as is, so the panel says so
    /// (one standing line; the confidence numbers are left alone).
    /// </summary>
    internal static string? WithCoopNote(string? flipHint, bool coop)
    {
        if (!coop) return flipHint;
        var note = Loc.T("솔로 승천 10 랭커 기록 기준입니다 (협동 보정 없음)", "Based on solo Ascension 10 top-player picks (no co-op adjustment)");
        return flipHint is null ? note : note + Environment.NewLine + flipHint;
    }

    /// <summary>
    /// A card row's sub line, with a first line "협동 전용 · 솔로 기록 없음" for a co-op-only card the learned model has no solo
    /// record of (<see cref="CardEvaluation.CoopOnlyNoSoloData"/>): its score is the engine part alone, and the row says so.
    /// </summary>
    internal static string WithCoopOnlyTag(string sub, CardEvaluation? e)
        => e is { CoopOnlyNoSoloData: true } ? Loc.T("협동 전용 · 솔로 기록 없음", "Co-op only · no solo data") + "\n" + sub : sub;

    private static string CharacterName(string id) => ForecastReviewPresenter.CharacterName(id);

    private static ChipVm Chip(string label, double value)
        => new($"{label} {value.ToString("+0;-0;0", CultureInfo.InvariantCulture)}", value >= 0);

    private static string Fmt(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    private static string Fmt1(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Score bar width (DIPs) on a 0..100 scale.</summary>
    private static double Bar(double score) => Math.Clamp(score, 0, 100) / 100 * 70;

    private static Brush ParseColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }
        catch (FormatException)
        {
        }
        return Brushes.SteelBlue;
    }

    public void Dispose()
    {
        _topmostTimer.Stop();
        _messageTimer.Stop();
        _hoverTimer?.Stop();
        _pauseTimer?.Stop();
        _verdictTimer?.Stop();
        _source.Changed -= OnSourceChanged;
        _source.Dispose();
        DisposeForecastReview(); // AppController.ForecastReview.cs
        DisposeMapOverlay();
    }
}
