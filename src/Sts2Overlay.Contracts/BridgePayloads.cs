using System.Text.Json.Serialization;

namespace Sts2Overlay.Contracts;

/// <summary>One card instance in the deck.</summary>
public sealed record SnapshotCard
{
    /// <summary>Card model id, e.g. <c>CARD.BALL_LIGHTNING</c>.</summary>
    public string Id { get; set; } = "";

    /// <summary>Upgrade level (0 = not upgraded).</summary>
    public int Upgrade { get; set; }

    /// <summary>Stable id of this card instance for the lifetime of the run.</summary>
    public string InstanceId { get; set; } = "";

    public CardEnchantment? Enchantment { get; set; }

    /// <summary>Global floor the card was added on, if known.</summary>
    public int? FloorAdded { get; set; }
}

/// <summary>An enchantment on a card.</summary>
public sealed record CardEnchantment
{
    /// <summary>Enchantment model id.</summary>
    public string Id { get; set; } = "";

    public int Amount { get; set; }
}

/// <summary>The current act's map.</summary>
public sealed record MapSnapshot
{
    public IReadOnlyList<MapNode> Nodes { get; set; } = [];

    /// <summary>Directed edges, serialized as <c>[[from, to], ...]</c>.</summary>
    public IReadOnlyList<MapEdge> Edges { get; set; } = [];

    /// <summary>The node the player is on; null before the first node of the act.</summary>
    public MapCoord? Current { get; set; }
}

/// <summary>A map node.</summary>
public sealed record MapNode
{
    public MapCoord Coord { get; set; }

    /// <summary>One of <see cref="MapNodeTypes"/>.</summary>
    public string Type { get; set; } = MapNodeTypes.Unknown;
}

/// <summary>A map coordinate: column and row (row 0 is the bottom of the act).</summary>
public readonly record struct MapCoord(int Col, int Row);

/// <summary>A directed map edge. Serialized as the two-element array <c>[from, to]</c>.</summary>
[JsonConverter(typeof(MapEdgeJsonConverter))]
public readonly record struct MapEdge(MapCoord From, MapCoord To);

/// <summary>A selectable card on a screen.</summary>
public sealed record ScreenItem
{
    /// <summary>Id the app uses in <see cref="CardBadge.InstanceId"/> to target this card.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>Card model id.</summary>
    public string CardId { get; set; } = "";

    public int Upgrade { get; set; }

    /// <summary>Merchant only: gold price.</summary>
    public int? Price { get; set; }

    /// <summary>On-screen rectangle in <see cref="ScreenOpenedMessage.Viewport"/> coordinates.</summary>
    public ScreenRect? Rect { get; set; }
}

/// <summary>A non-card choice on a screen (e.g. skip).</summary>
public sealed record ScreenAlternative
{
    /// <summary>One of <see cref="AlternativeKinds"/>.</summary>
    public string Kind { get; set; } = "";

    public ScreenRect? Rect { get; set; }
}

/// <summary>A rectangle in viewport pixels, origin top-left.</summary>
public readonly record struct ScreenRect(double X, double Y, double W, double H);

/// <summary>Width and height of a coordinate space.</summary>
public readonly record struct ScreenSize(double W, double H);

/// <summary>The card a <see cref="RunEventMessage"/> is about.</summary>
public sealed record RunEventCard
{
    public string Id { get; set; } = "";
    public int Upgrade { get; set; }
    public string? InstanceId { get; set; }
}

/// <summary>A badge the app asks the mod to draw on one card.</summary>
public sealed record CardBadge
{
    /// <summary>Matches <see cref="ScreenItem.InstanceId"/>.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>Short tier label, e.g. <c>S</c>.</summary>
    public string Label { get; set; } = "";

    /// <summary>Badge color as <c>#RRGGBB</c>.</summary>
    public string Color { get; set; } = TierColors.C;

    /// <summary>Total score (0..100 scale, informational).</summary>
    public double Score { get; set; }

    /// <summary>True for the top recommendation (drawn with a crown).</summary>
    public bool IsBest { get; set; }

    /// <summary>Short Korean reason chips, most important first.</summary>
    public IReadOnlyList<string> Chips { get; set; } = [];
}
