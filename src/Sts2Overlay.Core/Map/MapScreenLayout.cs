namespace Sts2Overlay.Core.Map;

/// <summary>A point in the game's 1920x1080 design space (y down). Screen px = offset + scale × design.</summary>
public readonly record struct DesignPoint(float X, float Y);

/// <summary>
/// Where the game draws each node of the act map, in design pixels, for a map scrolled to 0
/// (computed by <c>Sts2Overlay.GameState.Tracking.MapScreenGeometry</c> from the save).
///
/// The map screen moves the whole map vertically (<c>TheMap.Position.y</c>, "scroll"): a node is drawn at
/// <c>(X, Y + scroll)</c>. Nothing in the files says how far the player scrolled, so the overlay measures that
/// one number from the screen (<c>Sts2Overlay.Vision.Map.MapScrollEstimator</c>).
/// </summary>
public sealed record MapScreenLayout
{
    /// <summary>Node centres at scroll 0 (design px), every node of <see cref="MapGraph.Nodes"/> included.</summary>
    public required IReadOnlyDictionary<MapCoord, DesignPoint> Centers { get; init; }

    /// <summary>Vertical distance between rows (design px): 2325 / (height − 1), × 0.9 with a second boss.</summary>
    public float RowSpacing { get; init; }

    /// <summary>Scroll the game sets when the map opens mid-act: −600 + current row × <see cref="RowSpacing"/>, clamped.</summary>
    public float DefaultScroll { get; init; }

    /// <summary>The game's scroll limits (it springs back inside them when a drag ends).</summary>
    public const float MinScroll = -600f, MaxScroll = 1800f;

    /// <summary>Act index (0-based) the jitter was computed for; a different act needs a new layout.</summary>
    public int ActIndex { get; init; }

    /// <summary>Run seed string the jitter came from (<c>rng.seed</c>).</summary>
    public string Seed { get; init; } = "";
}
