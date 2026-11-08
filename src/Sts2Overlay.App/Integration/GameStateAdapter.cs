using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState;
using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Tracking;
using TrackerHint = Sts2Overlay.GameState.Tracking.ScreenHint;

namespace Sts2Overlay.App.Integration;

/// <summary>
/// Adapts <see cref="GameStateTracker"/> (save watcher + godot.log tail) to the App's <see cref="IGameStateSource"/> seam.
/// The tracker has a richer screen enum; the App only needs "is a card offer probably on screen?" to drive capture.
/// </summary>
public sealed class GameStateAdapter : IGameStateSource, ICardOfferSink
{
    private readonly GameStateTracker _tracker;
    private volatile GameStateSnapshot _current = GameStateSnapshot.Empty;

    public GameStateAdapter(TrackerOptions? options = null)
    {
        // Without explicit paths the tracker watches nothing, so discover %APPDATA%\SlayTheSpire2 here.
        options ??= new TrackerOptions();
        if (options.Paths is null) options = options with { Paths = GamePaths.Discover() };
        AppLog.Info($"game state: saves={options.Paths?.SavesDirectory ?? "-"} log={options.Paths?.LogFile ?? "-"}");
        _tracker = new GameStateTracker(options);
        _tracker.Changed += (_, run) =>
        {
            _current = Map(run);
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _tracker.Diagnostic += (_, msg) => AppLog.Info("[state] " + msg);
    }

    public event EventHandler? Changed;

    public GameStateSnapshot Current => _current;

    /// <summary>The tracker's full snapshot (potions, pity offset, approximations) for future panels.</summary>
    public TrackedRun? Tracked => _tracker.Current;

    public void Start()
    {
        try
        {
            _tracker.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No game data folder yet (game never launched on this PC): stay empty instead of crashing the overlay.
            AppLog.Error("game state tracker failed to start", ex);
        }
    }

    public void Dispose() => _tracker.Dispose();

    /// <summary>The recognizer read a card-reward screen: the tracker uses it for the picked card's upgrade if replay fails.</summary>
    public void OnCardOfferSeen(IReadOnlyList<CardRef> offered) => _tracker.OnCardOfferSeen(offered);

    internal static GameStateSnapshot Map(TrackedRun? run)
    {
        if (run is null || run.Outcome != RunOutcome.InProgress) return GameStateSnapshot.Empty with { UpdatedAt = DateTime.Now };

        var hint = run.Screen switch
        {
            TrackerHint.RewardScreenLikely => ScreenHint.RewardLikely,
            TrackerHint.ShopLikely => ScreenHint.ShopLikely,
            TrackerHint.Combat => ScreenHint.Combat,
            TrackerHint.RestSite => ScreenHint.RestSite,
            TrackerHint.Map => ScreenHint.Map,
            _ => ScreenHint.None,
        };
        var rewardContext = run.LastCombatKind switch
        {
            CombatKind.Elite => OfferContext.EliteReward,
            CombatKind.Boss => OfferContext.BossReward,
            _ => OfferContext.CardReward,
        };
        return new GameStateSnapshot(run.State, run.Map, hint, rewardContext, run.UpdatedAt.ToLocalTime(), Doubts(run))
        {
            Seed = run.Seed,
            PartnerVotes = run.PartnerVotes.Select(v => v.Coord).ToList(),
            UnknownOdds = run.Save is { } save ? UnknownRoomOdds.From(save.UnknownMapPointOdds) : null,
        };
    }

    private static DeckDoubts? Doubts(TrackedRun run)
    {
        static string Reason(UncertainReason r) => r switch
        {
            UncertainReason.UnknownCardSelect => "로그로 해석할 수 없는 카드 선택(이벤트/유물)에 쓰였을 수 있음",
            UncertainReason.Inferred => "획득 로그 없이 전투에서 확인됨: 강화 여부 모름",
            UncertainReason.UpgradeRoll => "보상 카드 무료 강화 여부를 확인하지 못함",
            _ => "불확실",
        };
        var reasons = run.UncertainCards
            .GroupBy(u => u.CardId)
            .ToDictionary(g => g.Key, g => string.Join(" / ", g.Select(u => Reason(u.Reason)).Distinct()));
        var d = new DeckDoubts(reasons, run.UnidentifiedCards, run.UnknownUpgrades, run.UnknownRemovals);
        return d.Any ? d : null;
    }
}
