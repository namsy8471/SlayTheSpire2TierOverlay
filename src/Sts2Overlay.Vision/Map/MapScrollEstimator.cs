using System.Globalization;

namespace Sts2Overlay.Vision.Map;

/// <summary>A map node's centre in the game's 1920x1080 design space at scroll 0 (from the save, see MapScreenGeometry).</summary>
public readonly record struct MapMark(float X, float Y0);

/// <summary>Result of looking for the act map on one frame.</summary>
/// <param name="IsMap">The map screen is open and the node layout fits it.</param>
/// <param name="Scroll">Vertical scroll in design px: node centre y = <c>Y0 + Scroll</c>.</param>
/// <param name="Score">Mean "ink" (0..60) at the fitted node positions; empty paper scores ~0.</param>
/// <param name="Visible">Nodes whose fitted position is on screen and not under the game's own UI.</param>
/// <param name="Matched">Of those, nodes with an icon at the fitted position.</param>
/// <param name="Runner">Best score at a scroll at least half a row away (how ambiguous the fit is).</param>
/// <param name="Paper">Brightness of the empty parchment (0..255).</param>
/// <param name="Detail">Probe values for the log / bench.</param>
public sealed record MapFit(bool IsMap, float Scroll, float Score, int Visible, int Matched, float Runner, float Paper, string Detail)
{
    public static MapFit None(string why) => new(false, 0, 0, 0, 0, 0, 0, why);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"map={IsMap} scroll={Scroll:F1} score={Score:F1} runner={Runner:F1} matched={Matched}/{Visible} paper={Paper:F0} {Detail}");
}

/// <summary>
/// Finds how far the act map is scrolled, which is the only thing about node positions the save can't tell us.
///
/// <para><b>Idea.</b> The node centres are known exactly up to one shared vertical offset. Every node icon is a
/// drawing on plain parchment, so at the right offset every predicted centre lands on "ink", while at a wrong offset
/// most of them land on empty paper (the grid is sparse and every node is jittered differently). So: try every offset
/// and keep the one where the predicted spots carry the most ink on average. That is a 1-D search over ~1400
/// candidates × ~60 nodes, and each spot is one O(1) box sum from an integral image, so it costs about 2 ms.</para>
///
/// <para><b>Ink, not darkness.</b> "Ink" is the colour distance from the paper colour. Plain darkness was tried first and
/// put every node ~11 design px too low: icons have LIGHT parts in their upper half (the cream "?", the monster's white
/// face), so the darkest box sits below the icon's centre. Distance from the paper counts light and dark strokes alike,
/// which makes the peak symmetric.</para>
///
/// <para><b>Why not template matching.</b> The game rotates every icon randomly (Gaussian, σ 8°), makes reachable icons
/// pulse between 0.95× and 1.45×, enlarges the hovered one and tints icons with the act's colours. A box mean ignores
/// all of that; the shared offset across dozens of nodes gives the precision.</para>
///
/// <para>Areas where the game's own UI covers the map (top bar, legend, back button, drawing tools) are skipped, so a
/// node hidden under them neither helps nor hurts.</para>
/// </summary>
public sealed class MapScrollEstimator
{
    // Work on a half-resolution design grid (960x540 cells of 2x2 design px): 2 design px = 4 px at 4K.
    private const int Cell = 2;
    private const int GridW = 1920 / Cell, GridH = 1080 / Cell;

    /// <summary>Half-size of the box sampled at each node centre (design px). Icons are drawn ~60 px wide on the map.</summary>
    private const float SpotHalf = 14f;

    /// <summary>A node counts as matched when its spot carries at least this much ink (mean colour distance, 0..255).</summary>
    public const float MatchInk = 16f;

    /// <summary>Minimum mean ink of a fit to call the screen "the map".</summary>
    public const float MinScore = 20f;

    /// <summary>Minimum share of visible nodes that must be matched.</summary>
    public const float MinMatchedShare = 0.8f;

    /// <summary>The best wrong offset must score at most this share of the best one (otherwise the fit is ambiguous).</summary>
    public const float MaxRunnerShare = 0.75f;

    private readonly int[] _lumIntegral = new int[(GridW + 1) * (GridH + 1)];
    private readonly int[] _inkIntegral = new int[(GridW + 1) * (GridH + 1)];
    private readonly byte[] _r = new byte[GridW * GridH], _g = new byte[GridW * GridH], _b = new byte[GridW * GridH];

    /// <summary>Design-space rectangles covered by the game's map UI (x, y, w, h).</summary>
    private static readonly RectF[] UiMasks =
    [
        new(0, 0, 1920, 90),       // top bar (HP, gold, potions, map/deck/settings buttons)
        new(0, 90, 360, 70),       // relic row under the top bar
        new(1555, 270, 365, 470),  // legend
        new(0, 715, 165, 130),     // back button
        new(40, 950, 240, 110),    // drawing tools
    ];

    /// <param name="frame">The captured game frame (any 16:9 or letterboxed size).</param>
    /// <param name="marks">Normal grid nodes (start and boss are big scenes; leave them out).</param>
    /// <param name="minScroll">Lowest scroll to try (design px). The game clamps to −600..1800; dragging can overshoot briefly.</param>
    /// <param name="maxScroll">Highest scroll to try.</param>
    public MapFit Estimate(in ScreenFrame frame, IReadOnlyList<MapMark> marks, float minScroll = -800f, float maxScroll = 2000f)
    {
        frame.Validate();
        if (marks.Count < 6) return MapFit.None("too few nodes");
        (byte pr, byte pg, byte pb) = BuildGrid(frame);
        float paper = (pr * 77 + pg * 150 + pb * 29) / 256f;

        // The map is a light parchment between dark margins. Anything else is not the map (no need to fit).
        float margin = LumMean(150, 200, 300, 700);
        if (paper < 90) return MapFit.None(Inv($"paper {paper:F0} too dark")) with { Paper = paper };
        if (paper - margin < 40) return MapFit.None(Inv($"no dark margin (paper {paper:F0}, margin {margin:F0})")) with { Paper = paper };

        int steps = (int)((maxScroll - minScroll) / Cell) + 1;
        Span<float> scores = steps <= 4096 ? stackalloc float[steps] : new float[steps];
        int best = -1;
        for (int i = 0; i < steps; i++)
        {
            scores[i] = Score(marks, minScroll + i * Cell, out int visible, out _);
            if (visible < 5) scores[i] = 0;
            if (best < 0 || scores[i] > scores[best]) best = i;
        }
        if (best < 0 || scores[best] <= 0) return MapFit.None("no fit") with { Paper = paper };

        // Sub-cell refinement: vertex of the parabola through the peak and its neighbours.
        float scroll = minScroll + best * Cell;
        if (best > 0 && best < steps - 1)
        {
            float a = scores[best - 1], b = scores[best], c = scores[best + 1];
            float den = a - 2 * b + c;
            if (den < 0) scroll += Math.Clamp(0.5f * (a - c) / den, -0.5f, 0.5f) * Cell;
        }

        // Ambiguity: best score at least ~half a row (60 design px) away from the winner.
        float runner = 0;
        for (int i = 0; i < steps; i++)
            if (Math.Abs(i - best) * Cell >= 60) runner = Math.Max(runner, scores[i]);

        float score = Score(marks, scroll, out int vis, out int matched);
        bool isMap = score >= MinScore && vis >= 5 && matched >= MinMatchedShare * vis && runner <= MaxRunnerShare * score;
        return new MapFit(isMap, scroll, score, vis, matched, runner, paper, Inv($"margin={margin:F0} paperRGB={pr},{pg},{pb}"));
    }

    /// <summary>Mean clamped ink of the spots at <paramref name="scroll"/> over visible, unmasked nodes.</summary>
    private float Score(IReadOnlyList<MapMark> marks, float scroll, out int visible, out int matched)
    {
        visible = 0;
        matched = 0;
        float sum = 0;
        for (int i = 0; i < marks.Count; i++)
        {
            float x = marks[i].X, y = marks[i].Y0 + scroll;
            if (y < 20 || y > 1060 || IsMasked(x, y)) continue;
            visible++;
            float ink = BoxMean(_inkIntegral, x - SpotHalf, y - SpotHalf, x + SpotHalf, y + SpotHalf);
            if (ink >= MatchInk) matched++;
            sum += Math.Min(ink, 60);
        }
        return visible == 0 ? 0 : sum / visible;
    }

    /// <summary>
    /// After <see cref="Estimate"/>: where node <paramref name="m"/>'s ink peaks within ±<paramref name="radius"/> design px of
    /// its fitted position (dx, dy, ink). Used by the bench to check the layout node by node.
    /// </summary>
    public (float Dx, float Dy, float Ink) LocalPeak(MapMark m, float scroll, float radius = 30)
    {
        float bx = 0, by = 0, bi = -1;
        for (float dy = -radius; dy <= radius; dy += Cell)
        for (float dx = -radius; dx <= radius; dx += Cell)
        {
            float x = m.X + dx, y = m.Y0 + scroll + dy;
            float ink = BoxMean(_inkIntegral, x - SpotHalf, y - SpotHalf, x + SpotHalf, y + SpotHalf);
            if (ink > bi) (bx, by, bi) = (dx, dy, ink);
        }
        return (bx, by, bi);
    }

    internal static bool IsMasked(float x, float y)
    {
        foreach (RectF r in UiMasks)
            if (x >= r.X - SpotHalf && x <= r.Right + SpotHalf && y >= r.Y - SpotHalf && y <= r.Bottom + SpotHalf) return true;
        return false;
    }

    /// <summary>
    /// Samples the frame onto the design grid (nearest pixel at each cell centre), finds the paper colour, and builds
    /// the luminance and ink integral images.
    /// </summary>
    private (byte R, byte G, byte B) BuildGrid(in ScreenFrame frame)
    {
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        ReadOnlySpan<byte> px = frame.Bgra.Span;
        Span<int> xs = stackalloc int[GridW];
        for (int gx = 0; gx < GridW; gx++) xs[gx] = Math.Clamp((int)t.ToPixelX((gx + 0.5f) * Cell), 0, frame.Width - 1) * 4;
        for (int gy = 0; gy < GridH; gy++)
        {
            int row = Math.Clamp((int)t.ToPixelY((gy + 0.5f) * Cell), 0, frame.Height - 1) * frame.Stride;
            int o = gy * GridW;
            for (int gx = 0; gx < GridW; gx++)
            {
                int p = row + xs[gx];
                _b[o + gx] = px[p];
                _g[o + gx] = px[p + 1];
                _r[o + gx] = px[p + 2];
            }
        }

        (byte pr, byte pg, byte pb) = PaperColour();
        const int w = GridW + 1;
        for (int gy = 0; gy < GridH; gy++)
        {
            int lumAcc = 0, inkAcc = 0;
            int o = gy * GridW, irow = (gy + 1) * w, iprev = gy * w;
            for (int gx = 0; gx < GridW; gx++)
            {
                int r = _r[o + gx], g = _g[o + gx], b = _b[o + gx];
                lumAcc += (r * 77 + g * 150 + b * 29) >> 8;  // Rec. 601 luma
                inkAcc += (Math.Abs(r - pr) + Math.Abs(g - pg) + Math.Abs(b - pb)) / 3;
                _lumIntegral[irow + gx + 1] = _lumIntegral[iprev + gx + 1] + lumAcc;
                _inkIntegral[irow + gx + 1] = _inkIntegral[iprev + gx + 1] + inkAcc;
            }
        }
        return (pr, pg, pb);
    }

    /// <summary>
    /// Colour of the empty paper: per-channel median of the parchment's central band. Icons, paths and the drawn circles
    /// are a minority there, so the median is the paper.
    /// </summary>
    private (byte R, byte G, byte B) PaperColour()
    {
        Span<int> hr = stackalloc int[256], hg = stackalloc int[256], hb = stackalloc int[256];
        int n = 0;
        for (int gy = 150 / Cell; gy < 1000 / Cell; gy += 2)
        for (int gx = 420 / Cell; gx < 1500 / Cell; gx += 2)
        {
            int i = gy * GridW + gx;
            hr[_r[i]]++;
            hg[_g[i]]++;
            hb[_b[i]]++;
            n++;
        }
        return (Median(hr, n), Median(hg, n), Median(hb, n));
    }

    private static byte Median(Span<int> hist, int n)
    {
        int cum = 0;
        for (int v = 0; v < 256; v++)
        {
            cum += hist[v];
            if (cum * 2 >= n) return (byte)v;
        }
        return 255;
    }

    /// <summary>Mean of an integral image over a design-space box (clipped to the screen).</summary>
    private static float BoxMean(int[] integral, float x0, float y0, float x1, float y1)
    {
        int gx0 = Math.Clamp((int)MathF.Round(x0 / Cell), 0, GridW), gx1 = Math.Clamp((int)MathF.Round(x1 / Cell), 0, GridW);
        int gy0 = Math.Clamp((int)MathF.Round(y0 / Cell), 0, GridH), gy1 = Math.Clamp((int)MathF.Round(y1 / Cell), 0, GridH);
        int n = (gx1 - gx0) * (gy1 - gy0);
        if (n <= 0) return 0;
        const int w = GridW + 1;
        int s = integral[gy1 * w + gx1] - integral[gy0 * w + gx1] - integral[gy1 * w + gx0] + integral[gy0 * w + gx0];
        return (float)s / n;
    }

    private float LumMean(float x0, float y0, float x1, float y1) => BoxMean(_lumIntegral, x0, y0, x1, y1);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
