using System.IO;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState;
using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Save;
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
        _tracker.FormatProblemDetected += (_, _) =>
        {
            AppLog.Warn("game state: a save or log line could not be handled (details above); panel notice shown");
            StandingNotices.Set(StandingNotices.GameFormat, FormatProblemText);
            FormatProblemDetected?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    /// <summary>
    /// A save or log line could not be read at all, probably because a game update changed the format
    /// (<see cref="GameStateTracker.FormatProblem"/>). Stays true for the session; the panel banner says so.
    /// </summary>
    public bool FormatProblem => _tracker.FormatProblem;

    /// <summary>Raised once (from a background thread) when <see cref="FormatProblem"/> becomes true.</summary>
    public event EventHandler? FormatProblemDetected;

    /// <summary>The standing panel line for <see cref="FormatProblem"/>, in the current language.</summary>
    internal static string FormatProblemText() => Loc.T(
        "게임 세이브/로그 형식이 달라 읽지 못했습니다. 오버레이 업데이트가 필요할 수 있습니다",
        "The game's save/log format has changed and could not be read. The overlay may need an update");

    public GameStateSnapshot Current => _current;

    /// <summary>The tracker's full snapshot (potions, pity offset, approximations) for future panels.</summary>
    public TrackedRun? Tracked => _tracker.Current;

    /// <summary>
    /// Full path of the single-player <c>current_run.save</c> in the saves folder the tracker watches (the file may not
    /// exist yet), or null while no saves folder is known. Co-op runs use another file; callers check the run first.
    /// </summary>
    public string? SinglePlayerSavePath => _tracker.SavesDirectory is { } dir ? Path.Combine(dir, RunSaveParser.RunSaveFileNames[0]) : null;

    /// <summary>Full path of the <c>godot.log</c> the tracker tails, or null.</summary>
    public string? LogFile => _tracker.LogFile;

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
            TrackerHint.MainMenu => ScreenHint.MainMenu,
            _ => ScreenHint.None,
        };
        var rewardContext = run.LastCombatKind switch
        {
            CombatKind.Elite => OfferContext.EliteReward,
            CombatKind.Boss => OfferContext.BossReward,
            _ => OfferContext.CardReward,
        };
        // Log-only rebuild: HP and gold are starting values, not facts (TrackedRun.HpGoldReliable). Carried on the RunState so
        // every advisor that reads HP/gold sees it.
        var state = run.HpGoldReliable || !run.State.VitalsKnown ? run.State : run.State with { VitalsKnown = false };
        return new GameStateSnapshot(state, run.Map, hint, rewardContext, run.UpdatedAt.ToLocalTime(), Doubts(run))
        {
            LogOnly = !run.FromSave,
            Seed = run.Seed,
            RngScheme = run.RngScheme,
            PartnerVotes = run.PartnerVotes.Select(v => v.Coord).ToList(),
            MyVote = run.MyVote,
            UnknownOdds = run.Save is { } save ? UnknownRoomOdds.From(save.UnknownMapPointOdds) : null,
        };
    }

    private static DeckDoubts? Doubts(TrackedRun run)
    {
        // Kept as reason kinds: DeckDoubts.ReasonText words them in the language current when the deck tab is drawn.
        var reasons = run.UncertainCards
            .GroupBy(u => u.CardId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UncertainReason>)g.Select(u => u.Reason).Distinct().ToArray());
        var d = new DeckDoubts(reasons, run.UnidentifiedCards, run.UnknownUpgrades, run.UnknownRemovals);
        return d.Any ? d : null;
    }
}
