using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.App.Integration;

/// <summary>What the save/log tracker thinks is on screen. Drives the capture cadence (PLAN §5.0 "화면 인식 흐름").</summary>
public enum ScreenHint
{
    None,
    Combat,
    /// <summary>Combat just ended ("has won against encounter"): the card reward screen is probably open or about to be.</summary>
    RewardLikely,
    /// <summary>Entered a merchant room ("Preloading 'Merchant Room'").</summary>
    ShopLikely,
    RestSite,
    Map,
}

/// <summary>One immutable view of the run. A new instance is published on every change.</summary>
/// <param name="Run">The local player's run, or null when no run is in progress / not read yet.</param>
/// <param name="Map">The current act's map, or null.</param>
/// <param name="Hint">Best guess of the current screen.</param>
/// <param name="RewardContext">Kind of the last combat (CardReward / EliteReward / BossReward), used for reward offers.</param>
/// <param name="UpdatedAt">When this snapshot was produced (local time).</param>
/// <param name="Doubts">What a log-only deck rebuild (co-op guest) can't pin down; null when the deck is exact.</param>
public sealed record GameStateSnapshot(
    RunState? Run,
    MapGraph? Map,
    ScreenHint Hint,
    OfferContext RewardContext,
    DateTime UpdatedAt,
    DeckDoubts? Doubts = null)
{
    public static GameStateSnapshot Empty { get; } = new(null, null, ScreenHint.None, OfferContext.CardReward, DateTime.MinValue);

    /// <summary>
    /// The run was rebuilt from the game log alone, with no save file (co-op guest, or no save matched the run): the deck is an
    /// estimate, there is no map, and HP/gold are unknown (<see cref="RunState.VitalsKnown"/>).
    /// </summary>
    public bool LogOnly { get; init; }

    /// <summary>Run seed string (save <c>rng.seed</c>): the map overlay needs it to place the nodes exactly.</summary>
    public string? Seed { get; init; }

    /// <summary>Co-op: the nodes the other players currently vote for on this act's map (empty in single player).</summary>
    public IReadOnlyList<MapCoord> PartnerVotes { get; init; } = [];

    /// <summary>What the next "?" room turns into, from the save's run odds. Null when unknown (log-only run).</summary>
    public UnknownRoomOdds? UnknownOdds { get; init; }
}

/// <summary>
/// Save/log tracker seam. Implemented by an adapter over <c>Sts2Overlay.GameState</c> (lead wires it in
/// <see cref="IntegrationFactory"/>); <see cref="Fakes.FakeGameStateSource"/> drives demo mode.
/// </summary>
public interface IGameStateSource : IDisposable
{
    /// <summary>Raised after <see cref="Current"/> changed. May be raised on ANY thread; the App marshals to the UI thread.</summary>
    event EventHandler? Changed;

    /// <summary>Latest snapshot. Never null; thread-safe to read.</summary>
    GameStateSnapshot Current { get; }

    /// <summary>Begins watching (file watchers, log tail). Called once by the App after subscribing to <see cref="Changed"/>.</summary>
    void Start();
}

/// <summary>
/// Doubts about the deck shown in the 덱 tab: per card id, why its copy (or upgrade) is uncertain; plus cards / upgrades /
/// removals the log proves happened but can't place. The reasons are kept as kinds and worded when shown
/// (<see cref="ReasonText"/>), so a snapshot taken before a language change still reads in the new language.
/// </summary>
public sealed record DeckDoubts(IReadOnlyDictionary<string, IReadOnlyList<UncertainReason>> CardReasons, int Unidentified, int UnknownUpgrades, int UnknownRemovals)
{
    public bool Any => CardReasons.Count > 0 || Unidentified > 0 || UnknownUpgrades > 0 || UnknownRemovals > 0;

    /// <summary>Why this card is marked "?", in the current language; null when it isn't in doubt.</summary>
    public string? ReasonText(string cardId)
        => CardReasons.TryGetValue(cardId, out var reasons) && reasons.Count > 0
            ? string.Join(" / ", reasons.Distinct().Select(Reason))
            : null;

    /// <summary>One reason's wording.</summary>
    public static string Reason(UncertainReason r) => r switch
    {
        UncertainReason.UnknownCardSelect => Loc.T("로그로 해석할 수 없는 카드 선택(이벤트/유물)에 쓰였을 수 있음", "May have gone to a card choice the log can't read (event/relic)"),
        UncertainReason.Inferred => Loc.T("획득 로그 없이 전투에서 확인됨: 강화 여부 모름", "Seen in combat with no pickup log: Upgrade unknown"),
        UncertainReason.UpgradeRoll => Loc.T("보상 카드 무료 강화 여부를 확인하지 못함", "Couldn't confirm a free Upgrade on the reward card"),
        _ => Loc.T("불확실", "Uncertain"),
    };
}
