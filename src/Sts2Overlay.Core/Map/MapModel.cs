namespace Sts2Overlay.Core.Map;

/// <summary>Kind of a map node. Mirrors the <c>type</c> strings in <c>current_run.save</c> (<c>saved_map.points[].type</c>).</summary>
public enum MapNodeType
{
    Unknown,   // "unknown" = "?" event room (can roll into a fight, shop or treasure)
    Monster,   // "monster"
    Elite,     // "elite"
    RestSite,  // "rest_site"
    Shop,      // "shop"
    Treasure,  // "treasure"
    Ancient,   // "ancient" (act start)
    Boss,      // "boss"
}

/// <summary>Grid coordinate on an act map. Row 0 is the act start, the boss sits on the last row.</summary>
public readonly record struct MapCoord(int Col, int Row)
{
    public override string ToString() => $"({Col},{Row})";
}

/// <summary>One node of the act map with its outgoing edges (<c>children</c> in the save).</summary>
public sealed record MapNode(MapCoord Coord, MapNodeType Type, IReadOnlyList<MapCoord> Children);

/// <summary>
/// The current act's map as stored in the save (<c>acts[current_act_index].saved_map</c>), plus where the player is.
/// Only the current act is known; later acts are generated on arrival.
/// </summary>
public sealed record MapGraph
{
    /// <summary>Act id, e.g. <c>ACT.UNDERDOCKS</c>.</summary>
    public string ActId { get; init; } = "";

    /// <summary>Act number, 1-based.</summary>
    public int Act { get; init; } = 1;

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>All nodes including start and boss, keyed by coordinate.</summary>
    public required IReadOnlyDictionary<MapCoord, MapNode> Nodes { get; init; }

    public required MapCoord Start { get; init; }
    public required MapCoord Boss { get; init; }

    /// <summary>Last visited coordinate in this act (<c>visited_map_coords</c> tail), or null before the first step.</summary>
    public MapCoord? Current { get; init; }

    public static MapNodeType ParseType(string? saveType) => saveType switch
    {
        "monster" => MapNodeType.Monster,
        "elite" => MapNodeType.Elite,
        "rest_site" => MapNodeType.RestSite,
        "shop" => MapNodeType.Shop,
        "treasure" => MapNodeType.Treasure,
        "ancient" => MapNodeType.Ancient,
        "boss" => MapNodeType.Boss,
        _ => MapNodeType.Unknown,
    };
}
