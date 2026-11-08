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
    private string _packDir;
    private int _engineVersion;

    private IGameStateSource _source = new NullGameStateSource();
    private GameStateSnapshot _snapshot = GameStateSnapshot.Empty;
    private RecognitionResult? _lastResult;
    private string? _lastOfferKey;
    private string? _lastEvalKey;
    private IReadOnlyList<BadgeSpec> _lastBadges = [];
    private string? _lastSkip;
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
        (_engine, _cards) = LoadPack(packDir, catalogPath, vm);
        InitForecastReview(packDir, catalogPath); // forecast + run review (AppController.ForecastReview.cs)

        _tracker.Changed += (_, _) => OnTrackerChanged();
        // The panel moved to a monitor with another DPI: the DPI part of the scale changed, ours must follow.
        _panel.DpiChanged += (_, _) => ApplyUiScale();
        ApplyUiScale();
        _pipeline.Recognized += r => _ui.BeginInvoke(() => ApplyRecognition(r));
        _pipeline.Status += s => _ui.BeginInvoke(() => ShowMessage(s));

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
        _pipeline.CharacterHint = snap.Run?.Character;
        RefreshRunViews(snap.Run);
        UpdateForecastReview(snap); // AppController.ForecastReview.cs
        if (Demo is null) RefreshPath(snap);

        var likely = IsOfferHint(snap.Hint);
        if (likely && prevHint != snap.Hint)
        {
            _pipeline.RequestRecognize();
        }
        else if (!likely && IsOfferHint(prevHint))
        {
            // The tracker says the reward/shop screen is gone (map node entered, next combat...).
            ClearBadges();
        }
        else if (_lastResult is not null)
        {
            ApplyRecognition(_lastResult); // same screen, new run state (gold, deck): re-evaluate
        }
        UpdateCadence();
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
            UpdatePath(null);
            return;
        }
        var key = $"{map.ActId}|{map.Current}|{run.Hp}/{run.MaxHp}|{run.Gold}|{run.Deck.Count}|{run.Deck.Count(c => c.Upgrade > 0)}#{_engineVersion}";
        if (key == _pathKey) return;
        _pathKey = key;
        try
        {
            var rec = new PathAdvisor(_engine.Pack).Recommend(run, map);
            UpdatePath(ToPathView(rec, map));
        }
        catch (Exception ex)
        {
            AppLog.Error("path advice failed", ex);
            UpdatePath(null);
        }
    }

    private static readonly string[] PathLabels = ["추천", "대안 1", "대안 2"];

    internal static PathView? ToPathView(PathRecommendation rec, MapGraph map)
    {
        if (rec.IsEmpty) return new PathView([], null, "보스 앞이거나 갈 수 있는 길이 없습니다");
        var options = rec.Top.Select((o, i) => new PathOptionView(
            PathLabels[Math.Min(i, PathLabels.Length - 1)],
            $"엘리트 {o.Elites} · 휴식 {o.Rests} · 상점 {o.CountOf(MapNodeType.Shop)} · ? {o.CountOf(MapNodeType.Unknown)} · 보스 도착 HP {o.ExpectedBossHp:P0}",
            o.Risks.Concat(o.Chips).Distinct().Take(5).ToArray(),
            o.Total,
            i == 0)).ToArray();
        string? next = rec.NextStep is { } n && map.Nodes.TryGetValue(n, out var node)
            ? $"다음: {NodeName(node.Type)} {Direction(map.Current ?? map.Start, n)}"
            : null;
        var note = $"덱 강도 {rec.DeckStrength:0.00} 기준 · 경로 {rec.TotalPaths}개 중" + (rec.Exhaustive ? "" : " 일부") + " 비교 [추정 가중치]";
        return new PathView(options, next, note);
    }

    private static string NodeName(MapNodeType t) => t switch
    {
        MapNodeType.Monster => "일반 전투",
        MapNodeType.Elite => "엘리트",
        MapNodeType.RestSite => "휴식처",
        MapNodeType.Shop => "상점",
        MapNodeType.Treasure => "보물",
        MapNodeType.Boss => "보스",
        MapNodeType.Ancient => "고대의 존재",
        _ => "? 이벤트",
    };

    /// <summary>Map columns grow to the right on screen, so the column delta reads as left/straight/right.</summary>
    private static string Direction(MapCoord from, MapCoord to) => (to.Col - from.Col) switch
    {
        < 0 => "(왼쪽)",
        > 0 => "(오른쪽)",
        _ => "(위)",
    };

    private static bool IsOfferHint(ScreenHint h) => h is ScreenHint.RewardLikely or ScreenHint.ShopLikely;

    /// <summary>
    /// Periodic capture runs while a reward/shop screen is likely. With no hint at all (tracker not wired / unsure)
    /// it also runs while badges are shown, so they disappear once the recogniser reports another screen.
    /// </summary>
    private void UpdateCadence()
    {
        var on = IsOfferHint(_snapshot.Hint) || (_snapshot.Hint == ScreenHint.None && _badges.HasBadges);
        var hz = Math.Clamp(_settings.RecognizeHz, 0.5, 10);
        _pipeline.SetInterval(on ? TimeSpan.FromSeconds(1 / hz) : null);
    }

    // ---- recognition → engine → UI ------------------------------------------------------------------

    private void ApplyRecognition(RecognitionResult r)
    {
        if (r.ScreenKind == ScreenKind.Other || r.Cards.Count == 0)
        {
            ClearBadges();
            UpdateCadence();
            return;
        }
        _lastResult = r;

        var offerKey = r.ScreenKind + ":" + string.Join(",", r.Cards.Select(c => $"{c.Card}${c.Price}"));
        var evalKey = $"{offerKey}@{_snapshot.UpdatedAt.Ticks}#{_engineVersion}";
        if (evalKey != _lastEvalKey)
        {
            try
            {
                var (offer, badges, skip) = r.ScreenKind == ScreenKind.Reward ? EvaluateReward(r) : EvaluateShop(r);
                _vm.Offer = offer;
                _lastBadges = badges;
                _lastSkip = skip;
                _lastEvalKey = evalKey;
            }
            catch (Exception ex)
            {
                AppLog.Error("recommendation failed", ex);
                ShowMessage("추천 계산 실패: " + ex.Message);
                return;
            }
            if (offerKey != _lastOfferKey)
            {
                _lastOfferKey = offerKey;
                _vm.SelectedTab = PanelTab.Offer; // PLAN §8: switch to 추천 when a reward/shop screen opens
                AppLog.Info($"offer {offerKey}");
            }
        }

        // Card positions can change every frame (hovering enlarges and lifts a card) even when the offer is the same,
        // so the evaluation is cached but the badge positions always come from the latest recognition.
        var specs = new BadgeSpec[r.Cards.Count];
        for (var i = 0; i < specs.Length; i++)
        {
            specs[i] = i < _lastBadges.Count
                ? _lastBadges[i] with { CardBounds = r.Cards[i].ScreenBounds }
                : new BadgeSpec(r.Cards[i].ScreenBounds, "?", "", false, true, r.Cards[i].Card.IsUpgraded);
        }
        _badges.ShowBadges(specs, _lastSkip, showBounds: Demo is not null);
        UpdateCadence();
    }

    private (OfferSectionVm, IReadOnlyList<BadgeSpec>, string?) EvaluateReward(RecognitionResult r)
    {
        var run = _snapshot.Run ?? UnknownRun;
        var cards = r.Cards.Select(c => c.Card).ToArray();
        var context = _snapshot.RewardContext;
        var eval = _engine.EvaluateOffer(run, cards, context);

        var rows = new List<OfferRowVm>();
        foreach (var (e, i) in eval.Cards.Select((e, i) => (e, i)).OrderBy(x => x.e.Rank))
        {
            var sub = $"{e.Rank}위 · 데이터 신뢰도 {e.Confidence:P0}";
            if (r.Cards[i].Confidence < 0.8) sub += $" · 인식 {r.Cards[i].Confidence:P0}";
            if (!e.IsRated) sub += " · 미평가 카드";
            rows.Add(new OfferRowVm
            {
                Name = _cards.Name(e.Card),
                TierText = e.DisplayTier.ToString(),
                ScoreText = Fmt(e.Total),
                BarWidth = Bar(e.Total),
                IsBest = e.IsBest,
                IsEstimate = e.IsEstimate,
                IsUpgraded = e.Card.IsUpgraded,
                SubText = sub,
                Tip = _engine.Pack.FindTier(run.Character, e.Card.Id)?.Tip,
                Chips = e.Chips.Select(c => Chip(c.Label, c.Value)).ToArray(),
            });
        }
        rows.Add(new OfferRowVm
        {
            Name = "스킵",
            IsSkip = true,
            ScoreText = Fmt(eval.Skip.Total),
            BarWidth = Bar(eval.Skip.Total),
            IsBest = eval.SkipRecommended,
            SubText = "카드를 받지 않음",
            Chips = eval.Skip.TopReasons(3).Select(c => Chip(c.Label, c.Value)).ToArray(),
        });

        var offer = new OfferSectionVm
        {
            Title = ContextTitle(context),
            ConfidenceText = eval.Confidence switch
            {
                RecommendationConfidence.Sure => "확신",
                RecommendationConfidence.Close => "박빙",
                RecommendationConfidence.Estimate => "추정",
                _ => "보통",
            },
            ConfidenceKind = eval.Confidence.ToString(),
            FlipHint = eval.FlipHint,
            Rows = rows,
            Footer = eval.Best is { } best ? $"추천: {_cards.Name(best)}" : "추천: 스킵",
        };

        var badges = eval.Cards
            .Select((e, i) => new BadgeSpec(r.Cards[i].ScreenBounds, e.DisplayTier.ToString(), Fmt(e.Total), e.IsBest, e.IsEstimate, e.Card.IsUpgraded))
            .ToArray();
        return (offer, badges, eval.SkipRecommended ? Fmt(eval.Skip.Total) : null);
    }

    private (OfferSectionVm, IReadOnlyList<BadgeSpec>, string?) EvaluateShop(RecognitionResult r)
    {
        var run = _snapshot.Run ?? UnknownRun;
        var offer = new ShopOffer(r.Cards.Select(c => new ShopCardOffer(c.Card, c.Price ?? 0)).ToArray());
        var eval = _engine.EvaluateShop(run, offer);
        var recommended = eval.RecommendedPurchase.ToHashSet(ReferenceEqualityComparer.Instance);

        var rows = eval.Items.Select(item =>
        {
            var e = item.CardEvaluation;
            var isCard = item.Kind == ShopItemKind.Card && item.Card is not null;
            var score = e?.Total ?? item.Value.Total;
            var sub = $"구매 가치 {Fmt(item.Value.Total)} · 골드당 {item.ValuePerGold.ToString("0.00", CultureInfo.InvariantCulture)}";
            if (!item.Affordable) sub += " · 골드 부족";
            return new OfferRowVm
            {
                Name = isCard ? _cards.Name(item.Card!.Value) : $"카드 제거 ({(item.RemovalTarget is { } t ? _cards.Name(t) : "?")})",
                TierText = e?.DisplayTier.ToString() ?? "—",
                ScoreText = Fmt(score),
                BarWidth = Bar(score),
                IsBest = recommended.Contains(item),
                IsEstimate = e?.IsEstimate ?? false,
                IsUpgraded = item.Card?.IsUpgraded ?? false,
                Unaffordable = !item.Affordable,
                PriceText = $"{item.Price}골드",
                SubText = sub,
                Chips = (e?.Chips.Select(c => Chip(c.Label, c.Value)) ?? item.Value.TopReasons(3).Select(c => Chip(c.Label, c.Value))).ToArray(),
            };
        }).ToArray();

        var names = eval.RecommendedPurchase
            .Select(i => i.Kind == ShopItemKind.Card && i.Card is { } c ? _cards.Name(c) : "카드 제거")
            .ToArray();
        var section = new OfferSectionVm
        {
            Title = $"상점 · 보유 {run.Gold}골드",
            ConfidenceText = "",
            Rows = rows,
            Footer = names.Length > 0 ? $"추천 구매: {string.Join(" + ", names)} · 남는 골드 {eval.GoldLeft}" : "추천 구매 없음 (골드 아끼기)",
        };

        // Badges in recognition order. Match each on-screen card to its evaluated item (a queue per card handles duplicates).
        var byCard = eval.Items.Where(i => i.Card is not null)
            .GroupBy(i => i.Card!.Value)
            .ToDictionary(g => g.Key, g => new Queue<ShopItemEvaluation>(g));
        var badges = r.Cards.Select(rc =>
        {
            var item = byCard.TryGetValue(rc.Card, out var q) && q.Count > 0 ? q.Dequeue() : null;
            var e = item?.CardEvaluation;
            return new BadgeSpec(rc.ScreenBounds, e?.DisplayTier.ToString() ?? "?", e is null ? "" : Fmt(e.Total),
                item is not null && recommended.Contains(item), e?.IsEstimate ?? true, rc.Card.IsUpgraded);
        }).ToArray();
        return (section, badges, null);
    }

    private void ClearBadges()
    {
        _badges.Clear();
        _lastResult = null;
        _lastEvalKey = null;
        _lastOfferKey = null;
        if (_vm.Offer.HasContent && !_vm.Offer.IsStale) _vm.Offer = _vm.Offer with { IsStale = true };
    }

    private void ResetOffer()
    {
        ClearBadges();
        _vm.Offer = OfferSectionVm.Empty;
    }

    // ---- deck / build tabs ------------------------------------------------------------------------------------

    private void RefreshRunViews(RunState? run)
    {
        if (run is null)
        {
            _vm.RunText = "런 정보 없음";
            _vm.Deck = DeckSectionVm.Empty;
            _vm.Build = BuildSectionVm.Empty;
            return;
        }

        var asc = run.Ascension > 0 ? $" · 승천 {run.Ascension}" : "";
        _vm.RunText = $"{CharacterName(run.Character)} · {run.Act}막 {run.Floor}층 · HP {run.Hp}/{run.MaxHp} · {run.Gold}골드{asc}";
        try
        {
            var profile = _engine.Analyze(run);
            _vm.Deck = BuildDeck(run, profile);
            _vm.Build = BuildArchetypes(_engine.Archetypes(run, EstimateRemainingRewards(run)));
        }
        catch (Exception ex)
        {
            AppLog.Error("deck/build analysis failed", ex);
            ShowMessage("덱 분석 실패: " + ex.Message);
        }
    }

    private DeckSectionVm BuildDeck(RunState run, DeckProfile profile)
    {
        string GroupOf(string id) => _cards.Type(run.Character, id) switch
        {
            CardTypes.Attack => "공격",
            CardTypes.Skill => "스킬",
            CardTypes.Power => "파워",
            CardTypes.Curse or CardTypes.Status => "저주·상태",
            _ => "기타",
        };
        string[] order = ["공격", "스킬", "파워", "저주·상태", "기타"];

        var groups = run.Deck
            .GroupBy(c => GroupOf(c.Id))
            .OrderBy(g => Array.IndexOf(order, g.Key))
            .Select(g =>
            {
                var cards = g.GroupBy(c => c.Id)
                    .Select(byId =>
                    {
                        var tier = TierOf(run, byId.Key);
                        var count = byId.Count();
                        var upgraded = byId.Count(c => c.Upgrade > 0);
                        var upText = upgraded == 0 ? null : count == 1 ? "강화" : $"강화 {upgraded}";
                        return (Tier: tier, Vm: new DeckCardVm(tier?.ToString() ?? "?", _cards.Name(byId.Key), $"×{count}", upText));
                    })
                    .OrderBy(x => x.Tier is null ? 99 : (int)x.Tier)
                    .ThenBy(x => x.Vm.Name, StringComparer.CurrentCulture)
                    .Select(x => x.Vm)
                    .ToArray();
                return new DeckGroupVm($"{g.Key} ({g.Count()})", cards);
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
        var summary = $"덱 {run.Deck.Count}장 · 강화 {upgradedTotal}장 · 파워 {profile.PowerCount}장";
        if (profile.Curses > 0) summary += $" · 저주 {profile.Curses}장";
        return new DeckSectionVm { Summary = summary, Groups = groups, Roles = roles };
    }

    private Tier? TierOf(RunState run, string id)
    {
        var entry = _engine.Pack.FindTier(run.Character, id);
        if (entry is null) return null;
        return entry.ActTier is not null && entry.ActTier.TryGetValue(run.Act, out var t) ? t : entry.Tier;
    }

    private BuildSectionVm BuildArchetypes(ArchetypeReport report)
    {
        if (report.Statuses.Count == 0) return BuildSectionVm.Empty;

        string RoleName(string role) => role switch
        {
            ArchetypeRoles.Core => "핵심",
            ArchetypeRoles.Payoff => "완성",
            ArchetypeRoles.Enabler => "조력",
            ArchetypeRoles.Support => "보조",
            _ => role,
        };

        var items = report.Statuses.Take(5).Select(st =>
        {
            var a = st.Archetype;
            var prob = report.CoreOfferProbability.TryGetValue(a.Id, out var p)
                ? $"이번 막 남은 보상 {report.RemainingRewards}번 안에 핵심 카드가 나올 확률 {p:P0} [추정]"
                : null;
            var missing = st.MissingCards.Count == 0
                ? null
                : "필요: " + string.Join(", ", st.MissingCards.Take(5).Select(m =>
                    $"{_cards.Name(m.CardId)}({RoleName(m.Role)}, {CardInfoProvider.RarityKorean(m.Rarity)})"));
            var needs = st.UnmetNeeds.Count == 0
                ? null
                : "조건 미충족: " + string.Join(", ", st.UnmetNeeds.Select(n =>
                    $"{(n.Role is { } r ? ScoringLabels.Role(r) : n.Tag ?? "?")} {Fmt1(n.Min)}+"));
            return new ArchetypeVm
            {
                Name = a.DisplayName,
                Color = ParseColor(a.Color),
                IsPrimary = ReferenceEquals(st, report.Primary),
                CommitText = $"몰입 {st.Commit:P0} · 완성도 {st.CompletionPercent:0}%{(st.IsCommitted ? " · 방향 확정" : "")}",
                CompletionBarWidth = Math.Clamp(st.CompletionPercent, 0, 100) / 100 * 356,
                StageText = new string('●', st.Stage) + new string('○', Math.Max(0, 5 - st.Stage)),
                ProbabilityText = prob,
                MissingText = missing,
                NeedsText = needs,
            };
        }).ToArray();

        var summary = report.Primary is not { } primary
            ? "아직 정해진 빌드 방향이 없습니다"
            : report.IsHybrid && report.Statuses.Count > 1
                ? $"현재 방향: {primary.Archetype.DisplayName} + {report.Statuses[1].Archetype.DisplayName} (하이브리드)"
                : $"현재 방향: {primary.Archetype.DisplayName}";
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
            null => "게임 창을 찾는 중…",
            { IsSimulated: true } => $"데모 (가상 화면 {info.CaptureRect.Width}x{info.CaptureRect.Height})",
            { IsMinimized: true } => "게임 최소화됨",
            _ => $"게임 {info.CaptureRect.Width}x{info.CaptureRect.Height} · x{Dpi.ScaleForRect(info.CaptureRect):0.##}{(info.IsForeground ? "" : " (뒤에 있음)")}",
        };
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
        ShowMessage("UI 배율: " + _vm.UiScaleText);
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
        _vm.UiScaleText = $"UI {effective * 100:0}%{(isAuto ? " (자동)" : "")}";
        _badges.ScaleMultiplier = effective / auto;
    }

    /// <summary>
    /// Overlays are visible only while the game (or one of our own windows) is the foreground window and the user
    /// hasn't hidden them with Ctrl+F8. Otherwise a topmost overlay would float over the desktop and other apps.
    /// </summary>
    public void UpdateVisibility()
    {
        var info = _tracker.Current;
        var show = !_userHidden && info is { CanShowOverlay: true };
        if (show)
        {
            if (!_badges.IsVisible) _badges.Show();
            _badges.SetLayerRect(info!.CaptureRect);
            if (!_panel.IsVisible) _panel.Show();
            ReassertTopmost();
        }
        else
        {
            if (_panel.IsVisible) _panel.Hide();
            if (_badges.IsVisible) _badges.Hide();
        }
        StateChanged?.Invoke();
    }

    private void ReassertTopmost()
    {
        if (!_panel.IsVisible && !_badges.IsVisible) return;
        // Badges first, then the panel, so the panel ends up above the badge layer.
        OverlayWindowHelper.AssertTopmost(_badges.Hwnd);
        OverlayWindowHelper.AssertTopmost(_panel.Hwnd);
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
        ShowMessage(_clickThrough ? "클릭 통과 켜짐: 마우스가 게임으로 갑니다" : "클릭 통과 꺼짐: 패널을 클릭할 수 있습니다");
    }

    public void RecognizeNow()
    {
        ShowMessage("지금 인식하는 중…");
        _pipeline.RequestRecognize();
    }

    public void SaveCapture() => _pipeline.RequestSave();

    public void ReloadPack()
    {
        (_engine, _cards) = LoadPack(_packDir, CatalogPath, _vm);
        _engineVersion++;
        RefreshRunViews(_snapshot.Run);
        ReloadForecastReview(); // AppController.ForecastReview.cs
        if (_lastResult is not null) ApplyRecognition(_lastResult);
        ShowMessage("데이터 팩을 다시 읽었습니다");
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
        if (Demo is null)
        {
            var recognizer = IntegrationFactory.CreateRealRecognizer();
            _pipeline.Recognizer = recognizer;
            _ = Task.Run(recognizer.Preload);
        }
        ReloadPack();
        ShowMessage("게임 리소스를 준비했습니다 (카드 그림·이름)");
    }
    // ---- end local resources ----

    public void ShowMessage(string text)
    {
        _vm.MessageText = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>Loads the data pack with <see cref="DataPackLoader.LoadDirectory"/>; an error leaves an empty pack (every card unrated).</summary>
    private static (RecommendationEngine, CardInfoProvider) LoadPack(string dir, string? catalogPath, PanelViewModel vm)
    {
        DataPack pack;
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        try
        {
            pack = DataPackLoader.LoadDirectory(dir);
            vm.PackText = $"팩 {name}";
            AppLog.Info($"pack loaded: {dir} ({pack.Tiers.Sum(t => t.Value.Cards.Count)} rated cards, {pack.Archetypes.Count} archetypes)");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Error($"pack load failed: {dir}", ex);
            pack = new DataPack();
            vm.PackText = $"팩 {name} 로드 실패";
        }
        return (new RecommendationEngine(pack), CardInfoProvider.Load(catalogPath, pack));
    }

    private static string ContextTitle(OfferContext c) => c switch
    {
        OfferContext.EliteReward => "카드 보상 · 엘리트",
        OfferContext.BossReward => "카드 보상 · 보스",
        OfferContext.Event => "카드 선택 · 이벤트",
        OfferContext.Shop => "상점",
        _ => "카드 보상",
    };

    private static string CharacterName(string id) => id switch
    {
        "CHARACTER.IRONCLAD" => "아이언클래드",
        "CHARACTER.SILENT" => "사일런트",
        "CHARACTER.DEFECT" => "디펙트",
        "CHARACTER.REGENT" => "리젠트",
        "CHARACTER.NECROBINDER" => "네크로바인더",
        _ => id.Replace("CHARACTER.", "", StringComparison.Ordinal),
    };

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
        _source.Changed -= OnSourceChanged;
        _source.Dispose();
        DisposeForecastReview(); // AppController.ForecastReview.cs
    }
}
