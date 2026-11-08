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

/// <summary>Why a rebuilt deck card is uncertain.</summary>
public enum UncertainReason
{
    /// <summary>A card-select screen the log can't interpret (event / relic) may have removed, transformed or upgraded it.</summary>
    UnknownCardSelect,
    /// <summary>
    /// The card was never logged as obtained but the log proves it is in the deck (played from the deck in a fight, or
    /// picked from the deck by a card-select). Identity is certain; its upgrade level is not.
    /// </summary>
    Inferred,
    /// <summary>A card reward from act 2 on: it may have rolled a free upgrade (CardFactory.RollForUpgrade), which isn't logged.</summary>
    UpgradeRoll,
}

/// <summary>Another co-op player's current map vote. <paramref name="Generation"/> = act number the vote is for.</summary>
public sealed record PartnerVote(ulong PlayerId, int Generation, MapCoord Coord);

/// <summary>How the tracker knows which event is open.</summary>
public enum EventSource
{
    /// <summary>Co-op log: "[EventSynchronizer] Event X began for player me with options: …" (event and options exact).</summary>
    Log,
    /// <summary>Single player: the act's pre-rolled event list in the save, at the index the game pulls next.</summary>
    SavePrediction,
}

/// <summary>The event room the player is in.</summary>
/// <param name="EventId"><c>EVENT.X</c>.</param>
/// <param name="OptionKeys">Exact option text keys offered on the first page, in button order; null when only the event is known.</param>
/// <param name="ChosenKeys">Options the player already chose in this event (co-op log), oldest first; they tell which page is showing.</param>
public sealed record ActiveEvent(string EventId, IReadOnlyList<string>? OptionKeys, IReadOnlyList<string> ChosenKeys, EventSource Source)
{
    /// <summary>False when the save prediction rested on an approximate check (see <see cref="Alternatives"/>).</summary>
    public bool Certain { get; init; } = true;

    /// <summary>Other events the prediction could be (save prediction only).</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>
    /// <see cref="OptionKeys"/> were replayed from the run seed (Neow: <see cref="NeowOptions"/>; act-start ancients:
    /// <see cref="AncientOptions"/>) rather than read from the log.
    /// </summary>
    public bool OptionsReplayed { get; init; }

    /// <summary>With <see cref="OptionsReplayed"/>: every option this run could have drawn (the pool the others came from).</summary>
    public IReadOnlyList<string>? PossibleKeys { get; init; }
}

/// <summary>A deck card the log-only rebuild is not sure about (see <see cref="TrackedRun.UncertainCards"/>).</summary>
public sealed record UncertainCard(int DeckIndex, string CardId, UncertainReason Reason);

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

    /// <summary>The event room the player is in (null outside events, or when it can't be told).</summary>
    public ActiveEvent? CurrentEvent { get; init; }

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

    /// <summary>
    /// Deck cards the log could not pin down (co-op guest rebuild): e.g. a card an event may have removed or
    /// transformed, or an upgrade whose target is unknown. Indices point into <c>State.Deck</c>. Empty when the deck
    /// came from a save or a run-end <c>.run</c> reconciliation.
    /// </summary>
    public IReadOnlyList<UncertainCard> UncertainCards { get; init; } = [];

    /// <summary>
    /// Cards the log proves joined the deck but can't name yet (random transform results, random event cards). Usually
    /// identified later when they are played in a fight; until then the deck is this many cards short.
    /// </summary>
    public int UnidentifiedCards { get; init; }

    /// <summary>Upgrades the log proves happened but can't place (Whetstone, Doors of Light ... pick cards with the game RNG).</summary>
    public int UnknownUpgrades { get; init; }

    /// <summary>Random removals the log can't place (Slippery Bridge).</summary>
    public int UnknownRemovals { get; init; }

    /// <summary>True when the deck was replaced by the run-history file the game wrote at run end (exact).</summary>
    public bool DeckFromRunHistory { get; init; }

    /// <summary>True when <see cref="UncertainCards"/> lists <paramref name="deckIndex"/>.</summary>
    public bool IsUncertain(int deckIndex) => UncertainCards.Any(u => u.DeckIndex == deckIndex);

    /// <summary>Number of log events (obtain/remove/upgrade/relic/potion/gold) applied on top of the base state.</summary>
    public int AppliedLogPatches { get; init; }

    /// <summary>Our net id (1 in single player, Steam ID in co-op).</summary>
    public ulong LocalPlayerId { get; init; }

    /// <summary>Players in the run (1 in single player). Monster HP scales with it.</summary>
    public int PlayerCount { get; init; } = 1;

    /// <summary>
    /// The other co-op players. From the save (host): character, deck and relics. From the log (guest): character only,
    /// with an empty deck. Empty in single player.
    /// </summary>
    public IReadOnlyList<PartnerPlayer> Partners { get; init; } = [];

    /// <summary>
    /// Co-op: where each other player currently votes to go on this act's map (<c>Player vote changed for X</c>),
    /// since the party last moved. Empty in single player and right after entering a room.
    /// </summary>
    public IReadOnlyList<PartnerVote> PartnerVotes { get; init; } = [];

    public string? Seed { get; init; }
    public RunOutcome Outcome { get; init; }

    /// <summary>Extras from the save when available (odds, slots ...). Null for log-only runs.</summary>
    public ParsedSave? Save { get; init; }

    public DateTime UpdatedAt { get; init; }
}
