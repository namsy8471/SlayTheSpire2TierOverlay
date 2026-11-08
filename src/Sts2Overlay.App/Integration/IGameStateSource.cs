using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;

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
/// removals the log proves happened but can't place.
/// </summary>
public sealed record DeckDoubts(IReadOnlyDictionary<string, string> CardReasons, int Unidentified, int UnknownUpgrades, int UnknownRemovals)
{
    public bool Any => CardReasons.Count > 0 || Unidentified > 0 || UnknownUpgrades > 0 || UnknownRemovals > 0;
}
