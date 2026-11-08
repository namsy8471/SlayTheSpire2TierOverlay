using System.Text.Json.Serialization;

namespace Sts2Overlay.Contracts;

/// <summary>
/// Base of every message on the mod ↔ app WebSocket. Serialized as one JSON object whose
/// <c>type</c> property (see <see cref="MessageTypes"/>) selects the concrete record.
/// Use <see cref="BridgeJson"/> to (de)serialize.
/// </summary>
/// <remarks>
/// DTO properties use <c>set</c> rather than <c>init</c> on purpose: the System.Text.Json source
/// generator (net9) assigns <c>default</c> to every init-only property missing from the JSON, which
/// would wipe initializers such as <c>Protocol = 1</c> or <c>Deck = []</c>. Treat messages as immutable
/// once built; use <c>with</c> to derive variants.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), MessageTypes.Hello)]
[JsonDerivedType(typeof(RunSnapshotMessage), MessageTypes.RunSnapshot)]
[JsonDerivedType(typeof(ScreenOpenedMessage), MessageTypes.ScreenOpened)]
[JsonDerivedType(typeof(ScreenClosedMessage), MessageTypes.ScreenClosed)]
[JsonDerivedType(typeof(RunEventMessage), MessageTypes.RunEvent)]
[JsonDerivedType(typeof(BadgesSetMessage), MessageTypes.BadgesSet)]
[JsonDerivedType(typeof(BadgesClearMessage), MessageTypes.BadgesClear)]
public abstract record BridgeMessage
{
    /// <summary>Protocol version of the sender (<see cref="ProtocolVersion.Current"/>).</summary>
    public int Protocol { get; set; } = ProtocolVersion.Current;

    /// <summary>Per-sender, per-connection monotonically increasing sequence number.</summary>
    public long Seq { get; set; }
}

// ───────────────────────────── mod → app ─────────────────────────────

/// <summary>First message the mod sends after a connection is accepted.</summary>
public sealed record HelloMessage : BridgeMessage
{
    /// <summary>Game version from <c>release_info.json</c>, e.g. <c>0.98.3</c>.</summary>
    public string GameVersion { get; set; } = "";

    /// <summary>Game build commit from <c>release_info.json</c>, if known.</summary>
    public string? GameCommit { get; set; }

    /// <summary>Bridge mod version.</summary>
    public string ModVersion { get; set; } = "";

    /// <summary>True in a co-op run (or lobby).</summary>
    public bool IsMultiplayer { get; set; }

    /// <summary>The local player's id in a co-op run; null in single player.</summary>
    public string? LocalPlayerId { get; set; }
}

/// <summary>Full state of the local player's run. Sent on connect, on run start and whenever it changes.</summary>
public sealed record RunSnapshotMessage : BridgeMessage
{
    /// <summary>Character model id, e.g. <c>CHARACTER.DEFECT</c>.</summary>
    public string Character { get; set; } = "";

    public int Ascension { get; set; }

    /// <summary>Act number, 1-based.</summary>
    public int Act { get; set; } = 1;

    /// <summary>Global floor number, 1-based, counted across acts.</summary>
    public int Floor { get; set; } = 1;

    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Gold { get; set; }

    public IReadOnlyList<SnapshotCard> Deck { get; set; } = [];

    /// <summary>Relic model ids, e.g. <c>RELIC.CRACKED_CORE</c>.</summary>
    public IReadOnlyList<string> Relics { get; set; } = [];

    /// <summary>Potion model ids currently held (empty slots omitted).</summary>
    public IReadOnlyList<string> Potions { get; set; } = [];

    /// <summary>Card removals already bought at merchants this run, if the mod can read it.</summary>
    public int? CardRemovalsBought { get; set; }

    /// <summary>The current act's map, if available.</summary>
    public MapSnapshot? Map { get; set; }
}

/// <summary>A screen with selectable cards (or the map) was opened.</summary>
public sealed record ScreenOpenedMessage : BridgeMessage
{
    /// <summary>One of <see cref="ScreenKinds"/>.</summary>
    public string Screen { get; set; } = ScreenKinds.Other;

    /// <summary>For card rewards: one of <see cref="RewardContexts"/>; null when unknown or not applicable.</summary>
    public string? Context { get; set; }

    /// <summary>Selectable cards, in on-screen order (left to right, top to bottom).</summary>
    public IReadOnlyList<ScreenItem> Items { get; set; } = [];

    /// <summary>Non-card choices on the screen, such as skip.</summary>
    public IReadOnlyList<ScreenAlternative> Alternatives { get; set; } = [];

    /// <summary>Merchant only: price of the card removal service; null if sold out or not a merchant.</summary>
    public int? RemovalPrice { get; set; }

    /// <summary>Size of the coordinate space used by every <see cref="ScreenRect"/> in this message.</summary>
    public ScreenSize? Viewport { get; set; }
}

/// <summary>The screen announced by the last <see cref="ScreenOpenedMessage"/> was closed.</summary>
public sealed record ScreenClosedMessage : BridgeMessage
{
    /// <summary>One of <see cref="ScreenKinds"/>, if known.</summary>
    public string? Screen { get; set; }
}

/// <summary>Something happened in the run. Fields other than <see cref="Event"/> are set depending on the event.</summary>
public sealed record RunEventMessage : BridgeMessage
{
    /// <summary>One of <see cref="RunEventKinds"/>.</summary>
    public string Event { get; set; } = "";

    /// <summary>card_obtained / card_removed / card_upgraded: the card (after the change).</summary>
    public RunEventCard? Card { get; set; }

    /// <summary>room_entered: one of <see cref="MapNodeTypes"/>.</summary>
    public string? Room { get; set; }

    /// <summary>room_entered: map coordinate of the room.</summary>
    public MapCoord? Coord { get; set; }

    /// <summary>Act at the time of the event (1-based), if known.</summary>
    public int? Act { get; set; }

    /// <summary>Global floor at the time of the event (1-based), if known.</summary>
    public int? Floor { get; set; }

    /// <summary>run_ended: true on victory, false on defeat or abandon.</summary>
    public bool? Victory { get; set; }
}

// ───────────────────────────── app → mod ─────────────────────────────

/// <summary>Badges to draw on the cards of the currently open screen. Replaces any previous badges.</summary>
public sealed record BadgesSetMessage : BridgeMessage
{
    public IReadOnlyList<CardBadge> Badges { get; set; } = [];

    /// <summary>When true the mod marks the skip button as recommended.</summary>
    public bool? SkipRecommended { get; set; }

    /// <summary><see cref="BridgeMessage.Seq"/> of the <c>screen.opened</c> these badges answer, so stale badges can be dropped.</summary>
    public long? ScreenSeq { get; set; }
}

/// <summary>Remove every badge the app asked the mod to draw.</summary>
public sealed record BadgesClearMessage : BridgeMessage;
