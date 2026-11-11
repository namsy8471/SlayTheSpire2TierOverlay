using Sts2Overlay.Core.Map;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// Reproduces how the game lays out the act map on screen (decompiled <c>NMapScreen.SetMap</c>, v0.107.1; unchanged in
/// v0.111.0 apart from the seed arithmetic, <see cref="RngScheme"/>), so the
/// overlay can draw on top of a node without searching the whole screen for it.
///
/// <para>Every normal node sits on a grid plus a small random offset ("jitter") that makes the map look hand-drawn:</para>
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
/// Positions are offsets from the centre of the 1920x1080 design screen and of the node's 56x56 box, hence the
/// <c>960 + … + 28</c> below. The start and boss nodes are bigger scenes with fixed positions and no jitter.
/// </para>
/// </summary>
public static class MapScreenGeometry
{
    private const float DesignW = 1920, DesignH = 1080;

    /// <summary>Layout for <paramref name="map"/>, or null when the seed is unknown (jitter can't be reproduced).</summary>
    /// <param name="seed">Run seed string (<c>rng.seed</c>, the log's <c>Seed:</c>).</param>
    /// <param name="scheme">The run's seed arithmetic (<see cref="RngSeeds"/>; save schema / game version).</param>
    public static MapScreenLayout? Compute(MapGraph map, string? seed, RngScheme scheme = RngSeeds.Default)
    {
        if (string.IsNullOrEmpty(seed) || map.Width <= 0 || map.Height <= 1) return null;
        int actIndex = map.Act - 1;
        bool secondBoss = map.SecondBoss is not null;
        float k = secondBoss ? 0.9f : 1f;
        float distY = 2325f / (map.Height - 1) * k;
        float distX = 1050f / map.Width;

        // NMapScreen.SetMap(map, runState.Rng.Seed): new Rng(seed, $"map_jitter_{CurrentActIndex}").
        var rng = new GameRng(RngSeeds.RunStream(scheme, seed, $"map_jitter_{actIndex}"));

        var centers = new Dictionary<MapCoord, DesignPoint>(map.Nodes.Count);
        // ActMap.GetAllMapPoints: the grid nodes (not start/boss), column by column, each column bottom to top.
        IEnumerable<MapCoord> grid = map.Nodes.Keys
            .Where(c => c != map.Start && c != map.Boss && c != map.SecondBoss)
            .OrderBy(c => c.Col).ThenBy(c => c.Row);
        foreach (MapCoord c in grid)
        {
            float jx = NextFloat(rng, -21f, 21f), jy = NextFloat(rng, -25f, 25f);
            centers[c] = new DesignPoint(DesignW / 2 + c.Col * distX - 500f + jx + 28f,
                                         DesignH / 2 + 740f - c.Row * distY + jy + 28f);
        }

        // Start: NAncientMapPoint 208x208 (or a 56x56 normal point), placed at (−80, 720/800 − row·distY).
        bool ancient = map.Nodes.TryGetValue(map.Start, out MapNode? start) && start.Type == MapNodeType.Ancient;
        float startSize = ancient ? 208f : 56f;
        centers[map.Start] = new DesignPoint(DesignW / 2 - 80f + startSize / 2,
                                             DesignH / 2 - map.Start.Row * distY + (ancient ? 720f : 800f) + startSize / 2);

        // Boss: NBossMapPoint 374x306 with pivot (187,153) at (−200, −1980·k); a second boss above it at −2280·k.
        centers[map.Boss] = new DesignPoint(DesignW / 2 - 200f + 187f, DesignH / 2 - 1980f * k + 153f);
        if (map.SecondBoss is { } sb) centers[sb] = new DesignPoint(DesignW / 2 - 200f + 187f, DesignH / 2 - 2280f * k + 153f);

        int currentRow = map.Current?.Row ?? map.Start.Row;
        return new MapScreenLayout
        {
            Centers = centers,
            RowSpacing = distY,
            DefaultScroll = Math.Clamp(-600f + currentRow * distY, MapScreenLayout.MinScroll, MapScreenLayout.MaxScroll),
            ActIndex = actIndex,
            Seed = seed,
        };
    }

    /// <summary>Rng.NextFloat(min, max): computed in double, then narrowed (bit-exact with the game).</summary>
    private static float NextFloat(GameRng rng, float min, float max)
        => (float)(rng.NextDouble() * (max - min) + min);
}
