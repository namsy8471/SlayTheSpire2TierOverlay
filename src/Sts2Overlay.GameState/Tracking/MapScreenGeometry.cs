using Sts2Overlay.Core.Map;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// Reproduces the act map node centres computed by <c>NMapScreen.SetMap</c>. In v0.113 the game
/// anchors the node centres directly and reserves space for each occupied row before spreading the rows.
///
/// <para>In v0.107.1 and v0.111.0, every normal node's box is placed on this grid with a small random offset:</para>
/// <code>
/// distX = 1050 / width,  distY = 2325 / (height − 1)  (× 0.9 when the act has a second boss)
/// position = (col·distX − 500, 740 − row·distY) + (rng.NextFloat(−21, 21), rng.NextFloat(−25, 25))
/// </code>
/// <para>
/// The jitter looks random but is not: the game draws it from <c>new Rng(runSeed + hash("map_jitter_" + actIndex))</c>,
/// two floats per node, nodes in column-major order. That is the same xoshiro256** generator already ported in
/// <see cref="GameRng"/>, so the exact offsets follow from the seed in the save. Rows grow UP the screen (boss on top).
/// </para>
/// <para>
/// The start and boss nodes are separate scenes with fixed positions and no jitter.
/// </para>
/// </summary>
public static class MapScreenGeometry
{
    private const float DesignW = 1920, DesignH = 1080;

    /// <summary>Layout for <paramref name="map"/>, or null when the seed is unknown (jitter can't be reproduced).</summary>
    /// <param name="seed">Run seed string (<c>rng.seed</c>, the log's <c>Seed:</c>).</param>
    /// <param name="scheme">The run's seed arithmetic (<see cref="RngSeeds"/>; save schema / game version).</param>
    /// <param name="centeredRows">Use the 0.113 layout, which reserves space for each occupied row before positioning nodes.</param>
    public static MapScreenLayout? Compute(MapGraph map, string? seed, RngScheme scheme = RngSeeds.Default, bool centeredRows = false)
    {
        if (string.IsNullOrEmpty(seed) || map.Width <= 0 || map.Height <= 1) return null;
        centeredRows |= map.CenteredRows; // a v0.113 save says so itself, whatever the caller knows about the game
        int actIndex = map.Act - 1;
        bool secondBoss = map.SecondBoss is not null;
        float k = secondBoss ? 0.9f : 1f;
        float distY = 2325f / (map.Height - 1) * k;
        float distX = 1050f / map.Width;

        // NMapScreen.SetMap(map, runState.Rng.Seed): new Rng(seed, $"map_jitter_{CurrentActIndex}").
        var rng = new GameRng(RngSeeds.RunStream(scheme, seed, $"map_jitter_{actIndex}"));

        var centers = new Dictionary<MapCoord, DesignPoint>(map.Nodes.Count);
        // ActMap.GetAllMapPoints: the grid nodes (not start/boss), column by column, each column bottom to top.
        MapCoord[] grid = map.Nodes.Keys
            .Where(c => c != map.Start && c != map.Boss && c != map.SecondBoss)
            .OrderBy(c => c.Col).ThenBy(c => c.Row).ToArray();
        // From 0.113 every occupied grid row reserves one normal node's 56 px height. Only
        // the space left between those rows is distributed evenly. Special nodes are separate scenes.
        const float nodeHeight = 56f;
        int[] occupiedRows = centeredRows ? grid.Select(c => c.Row).Distinct().Order().ToArray() : [];
        float rowGap = centeredRows ? (2325f * k - nodeHeight * occupiedRows.Length) / (map.Height - 1) : distY;
        float originX = map.IsBlitz ? 530f : 460f;
        float jitterY = centeredRows && map.IsBlitz ? 12.5f : 25f;
        foreach (MapCoord c in grid)
        {
            float jx = NextFloat(rng, -21f, 21f), jy = NextFloat(rng, -jitterY, jitterY);
            if (centeredRows)
            {
                float rowY = 1280f - (c.Row * rowGap + nodeHeight * occupiedRows.Count(row => row < c.Row) + nodeHeight / 2);
                centers[c] = new DesignPoint(originX + c.Col * distX + jx, rowY + jy);
            }
            else
            {
                centers[c] = new DesignPoint(DesignW / 2 + c.Col * distX - 500f + jx + 28f,
                                             DesignH / 2 + 740f - c.Row * distY + jy + 28f);
            }
        }

        // Start: NAncientMapPoint 208x208 (or a 56x56 normal point), placed at (−80, 720/800 − row·distY).
        bool ancient = map.Nodes.TryGetValue(map.Start, out MapNode? start) && start.Type == MapNodeType.Ancient;
        float startSize = ancient ? 208f : 56f;
        centers[map.Start] = new DesignPoint(DesignW / 2 - 80f + startSize / 2,
                                             DesignH / 2 - map.Start.Row * rowGap + (ancient ? 720f : 800f) + startSize / 2);

        // Boss: NBossMapPoint 374x306 with pivot (187,153) at (−200, −1980·k); a second boss above it at −2280·k.
        centers[map.Boss] = new DesignPoint(DesignW / 2 - 200f + 187f, DesignH / 2 - 1980f * k + 153f);
        if (map.SecondBoss is { } sb) centers[sb] = new DesignPoint(DesignW / 2 - 200f + 187f, DesignH / 2 - 2280f * k + 153f);

        int currentRow = map.Current?.Row ?? map.Start.Row;
        float defaultScroll = -600f + currentRow * distY;
        if (centeredRows && map.Current is not null)
        {
            var visibleRow = centers.Where(kv => kv.Key.Row == currentRow).Select(kv => kv.Value.Y);
            defaultScroll = visibleRow.Any() ? DesignH / 2 + 150f - visibleRow.Average() : MapScreenLayout.MinScroll;
        }
        return new MapScreenLayout
        {
            Centers = centers,
            RowSpacing = distY,
            DefaultScroll = Math.Clamp(defaultScroll, MapScreenLayout.MinScroll, MapScreenLayout.MaxScroll),
            ActIndex = actIndex,
            Seed = seed,
        };
    }

    /// <summary>Rng.NextFloat(min, max): computed in double, then narrowed (bit-exact with the game).</summary>
    private static float NextFloat(GameRng rng, float min, float max)
        => (float)(rng.NextDouble() * (max - min) + min);
}
