using System.Diagnostics;
using Sts2Overlay.App.Integration;
using Sts2Overlay.Core.Map;
using Sts2Overlay.GameState.Tracking;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Map;

namespace Sts2Overlay.App.Services;

/// <summary>
/// One act map placed on the game's 1920x1080 design screen: where every node is drawn when the map is not scrolled.
/// Built once per act from the save (map + seed); see <see cref="MapScreenGeometry"/>.
/// </summary>
/// <param name="Key">Changes when another map is shown (other seed or act), so stale sightings can be dropped.</param>
/// <param name="Map">The act map the layout was built from.</param>
/// <param name="Layout">Node centres at scroll 0.</param>
/// <param name="Marks">The normal grid nodes the fit looks for (start and boss are big scenes and left out).</param>
public sealed record MapTarget(string Key, MapGraph Map, MapScreenLayout Layout, IReadOnlyList<MapMark> Marks)
{
    /// <summary>Null when the map or seed is unknown (the node jitter can't be reproduced without the seed).</summary>
    public static MapTarget? For(MapGraph? map, string? seed)
    {
        if (map is null || MapScreenGeometry.Compute(map, seed) is not { } layout) return null;
        var marks = layout.Centers
            .Where(kv => kv.Key != map.Start && kv.Key != map.Boss && kv.Key != map.SecondBoss)
            .Select(kv => new MapMark(kv.Value.X, kv.Value.Y))
            .ToList();
        return new MapTarget(KeyFor(map, seed), map, layout, marks);
    }

    /// <summary>Same key = same node positions (the layout depends on the seed and the act's nodes, not on where we are).</summary>
    public static string KeyFor(MapGraph map, string? seed) => $"{seed}|{map.Act}|{map.ActId}|{map.Nodes.Count}|{map.Width}x{map.Height}";
}

/// <summary>Result of looking for the map in one captured frame.</summary>
/// <param name="Target">The map that was looked for.</param>
/// <param name="IsMap">The map screen is open and every check of the fit passed.</param>
/// <param name="Scroll">Vertical scroll in design px: a node is on screen at <c>Layout.Centers[c].Y + Scroll</c>.</param>
/// <param name="At">When the frame was captured.</param>
/// <param name="Ms">Time the fit took.</param>
public sealed record MapSighting(MapTarget Target, bool IsMap, float Scroll, DateTime At, double Ms);

/// <summary>
/// Finds the act map in captured frames (worker thread of <see cref="CapturePipeline"/>). The UI thread only swaps
/// <see cref="Target"/>; the estimator keeps its buffers between frames and is used by the worker alone.
/// </summary>
public sealed class MapScreenWatcher
{
    private readonly MapScrollEstimator _estimator = new();
    private volatile MapTarget? _target;

    /// <summary>The map to look for, or null to skip the work (no run, no seed, or the overlay is off).</summary>
    public MapTarget? Target
    {
        get => _target;
        set => _target = value;
    }

    /// <summary>Fits <see cref="Target"/> to <paramref name="frame"/>. About 2.5 ms at 4K. Null when there is no target.</summary>
    public MapSighting? Analyze(CapturedFrame frame)
    {
        if (_target is not { } target) return null;
        var t0 = Stopwatch.GetTimestamp();
        var fit = _estimator.Estimate(new ScreenFrame(frame.Bgra, frame.Width, frame.Height, frame.Stride), target.Marks);
        return new MapSighting(target, fit.IsMap, fit.Scroll, frame.At, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }
}
