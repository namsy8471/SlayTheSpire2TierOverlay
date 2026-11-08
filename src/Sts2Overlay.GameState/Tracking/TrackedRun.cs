using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Save;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>Best guess of what the game is showing, from log events (the log never says "screen X opened").</summary>
public enum ScreenHint
{
    Unknown,
    MainMenu,
    Map,
    Combat,
    /// <summary>Combat won → the reward screen opens. Lasts until our card pick, the next map vote, or the next room.</summary>
    RewardScreenLikely,
    /// <summary>Inside a merchant room (shop inventory is rolled right before the room loads).</summary>
    ShopLikely,
    RestSite,
    Event,
    Treasure,
    RunEnded,
}

public enum RunOutcome { InProgress, Won, Lost, Abandoned, Ended }

/// <summary>One card slot's rarity roll from <c>Card rarity: Rolled ...</c>.</summary>
/// <param name="OffsetBefore">Pity offset the roll used (printed in the line).</param>
public sealed record RarityRoll(double Roll, double Need, double OffsetBefore, bool IsRare, RaritySource Source);

/// <summary>
/// The tracker's current snapshot: Core <see cref="RunState"/> + map + log-derived hints. Immutable; a new
/// instance is published on every change.
/// </summary>
public sealed record TrackedRun
{
    public required RunState State { get; init; }
    public MapGraph? Map { get; init; }

    public ScreenHint Screen { get; init; }

    /// <summary>Kind of the most recent real combat (encounter suffix, falling back to the map node type).</summary>
    public CombatKind? LastCombatKind { get; init; }
    public string? LastEncounterId { get; init; }

    /// <summary>What the cards currently on screen are offered as, if we think a card offer is showing.</summary>
    public OfferContext? OfferContext => Screen switch
    {
        ScreenHint.RewardScreenLikely => LastCombatKind switch
        {
            CombatKind.Elite => Core.Model.OfferContext.EliteReward,
            CombatKind.Boss => Core.Model.OfferContext.BossReward,
            _ => Core.Model.OfferContext.CardReward,
        },
        ScreenHint.ShopLikely => Core.Model.OfferContext.Shop,
        _ => null,
    };

    /// <summary>
    /// Current rare-card pity offset for MY next reward (-0.05 … 0.4). From the save, updated by later
    /// <c>Card rarity:</c> log lines. Rare chance of a regular reward card = 3% + this (A7: 1.49% + this).
    /// </summary>
    public double? RarityPityOffset { get; init; }

    /// <summary>My rolls for the latest card reward (one per card slot, left → right). Empty before any reward.</summary>
    public IReadOnlyList<RarityRoll> LastRewardRolls { get; init; } = [];

    public IReadOnlyList<PotionSlot> Potions { get; init; } = [];

    /// <summary>True when the base state came from a save file (single player or co-op host).</summary>
    public bool FromSave { get; init; }

    /// <summary>
    /// False for a log-only rebuild (co-op guest): HP is the character's starting HP and gold is starting gold +
    /// logged gold rewards (shop spending is not logged). Don't show them as facts.
    /// </summary>
    public bool HpGoldReliable { get; init; } = true;

    /// <summary>
    /// True when the deck may be off: log-only rebuild (event/Neow effects and shop purchases are not logged),
    /// or log patches applied on top of a save (upgrade level of reward cards is unknown until the next save).
    /// </summary>
    public bool DeckApproximate { get; init; }

    /// <summary>Number of log events (obtain/remove/upgrade/relic/potion/gold) applied on top of the base state.</summary>
    public int AppliedLogPatches { get; init; }

    /// <summary>Our net id (1 in single player, Steam ID in co-op).</summary>
    public ulong LocalPlayerId { get; init; }

    public string? Seed { get; init; }
    public RunOutcome Outcome { get; init; }

    /// <summary>Extras from the save when available (odds, slots ...). Null for log-only runs.</summary>
    public ParsedSave? Save { get; init; }

    public DateTime UpdatedAt { get; init; }
}
