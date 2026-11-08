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
public sealed record GameStateSnapshot(
    RunState? Run,
    MapGraph? Map,
    ScreenHint Hint,
    OfferContext RewardContext,
    DateTime UpdatedAt)
{
    public static GameStateSnapshot Empty { get; } = new(null, null, ScreenHint.None, OfferContext.CardReward, DateTime.MinValue);
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
