using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sts2Overlay.Vision;

/// <summary>
/// A partition of the 48x36 RGB NCC thumbnail into rectangular tiles, and per-tile statistics on it.
///
/// <para>For every tile we keep the sum Σx and the sum of squares Σx² of its values, and for a query/entry pair
/// the cross sum Σxy. From these three numbers the NCC of ANY union of tiles follows exactly, without touching
/// the pixels again:</para>
/// <code>
/// cov = Σxy - Σx·Σy/n,  var_x = Σx² - (Σx)²/n,  NCC = cov / sqrt(var_x · var_y)
/// </code>
/// <para>NCC is invariant to a positive gain and an offset, so it does not matter that the stored vectors were
/// normalised over the whole thumbnail: restricted to a tile they are an affine copy of the raw pixels.</para>
///
/// <para>Two grids are used: <see cref="Blocks"/> (6x4 tiles of 8x9 cells) for covers of any shape (the
/// merchant's hand, a corner tooltip) and <see cref="Rows"/> (36 one-cell-high bands) for the stacked hover
/// tooltips, which can leave only a thin horizontal strip of a card visible.</para>
/// </summary>
internal sealed class TileGrid
{
    public static readonly TileGrid Blocks = new(6, 4);
    public static readonly TileGrid Rows = new(1, Ncc.FullH);

    public int TilesX { get; }
    public int TilesY { get; }
    public int Count { get; }
    public int CellsX { get; }
    public int CellsY { get; }
    /// <summary>Floats per row segment of one tile (CellsX cells x RGB) and per tile.</summary>
    public int SegmentLength { get; }
    public int TileLength { get; }

    private readonly int[] _tileOfSegment;

    private TileGrid(int tilesX, int tilesY)
    {
        TilesX = tilesX;
        TilesY = tilesY;
        Count = tilesX * tilesY;
        CellsX = Ncc.FullW / tilesX;
        CellsY = Ncc.FullH / tilesY;
        SegmentLength = CellsX * 3;
        TileLength = CellsX * CellsY * 3;
        if (Count > 64 || SegmentLength % 8 != 0) throw new ArgumentException("Grid must have <= 64 tiles and segments of whole 8-float vectors.");
        // Row segment index (grid row y, tile column tx) → tile index; precomputed so the hot loop has no division.
        _tileOfSegment = new int[Ncc.FullH * TilesX];
        for (int seg = 0; seg < _tileOfSegment.Length; seg++)
        {
            int y = seg / TilesX, tx = seg % TilesX;
            _tileOfSegment[seg] = y / CellsY * TilesX + tx;
        }
    }

    /// <summary>Σx and Σx² per tile.</summary>
    public void SumsOf(ReadOnlySpan<float> v, Span<float> sum, Span<float> sq)
    {
        sum[..Count].Clear();
        sq[..Count].Clear();
        for (int seg = 0, i = 0; i < Ncc.FullLength; seg++, i += SegmentLength)
        {
            int tile = _tileOfSegment[seg];
            float s = 0, q = 0;
            for (int k = 0; k < SegmentLength; k++)
            {
                float x = v[i + k];
                s += x;
                q += x * x;
            }
            sum[tile] += s;
            sq[tile] += q;
        }
    }

    /// <summary>
    /// Per tile, the variance of each colour channel around its own mean, summed over R, G and B: the tile's
    /// texture. Unlike <see cref="Var"/> on the interleaved values it is ~0 for a smooth single-colour patch (a
    /// pale-blue hand: R far below B), whose constant gap between the channels is colour, not texture.
    /// </summary>
    public void TextureEnergy(ReadOnlySpan<float> v, Span<float> energy)
    {
        Span<float> s = stackalloc float[Count * 3], q = stackalloc float[Count * 3];
        s.Clear();
        q.Clear();
        for (int seg = 0, i = 0; i < Ncc.FullLength; seg++, i += SegmentLength)
        {
            int tile = _tileOfSegment[seg] * 3;
            for (int k = 0; k < SegmentLength; k += 3)
                for (int c = 0; c < 3; c++)
                {
                    float x = v[i + k + c];
                    s[tile + c] += x;
                    q[tile + c] += x * x;
                }
        }
        int n = CellsX * CellsY;
        for (int t = 0; t < Count; t++)
            energy[t] = Var(s[3 * t], q[3 * t], n) + Var(s[3 * t + 1], q[3 * t + 1], n) + Var(s[3 * t + 2], q[3 * t + 2], n);
    }

    /// <summary>
    /// Σxy per tile. One linear pass over both vectors (same memory traffic as one full dot product): each row
    /// segment belongs to exactly one tile and is a whole number of 8-float AVX vectors.
    /// </summary>
    public void Dots(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dots)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            // One 8-wide accumulator per tile; summed horizontally once at the end (not once per segment).
            Span<Vector256<float>> acc = stackalloc Vector256<float>[Count];
            acc.Clear();
            ref float ra = ref MemoryMarshal.GetReference(a);
            ref float rb = ref MemoryMarshal.GetReference(b);
            for (int seg = 0, i = 0; i < Ncc.FullLength; seg++, i += SegmentLength)
            {
                Vector256<float> p = Vector256<float>.Zero;
                for (int k = 0; k < SegmentLength; k += 8)
                    p += Vector256.LoadUnsafe(ref ra, (nuint)(i + k)) * Vector256.LoadUnsafe(ref rb, (nuint)(i + k));
                acc[_tileOfSegment[seg]] += p;
            }
            for (int t = 0; t < Count; t++) dots[t] = Vector256.Sum(acc[t]);
            return;
        }
        dots[..Count].Clear();
        for (int seg = 0, i = 0; i < Ncc.FullLength; seg++, i += SegmentLength)
        {
            float s = 0;
            for (int k = 0; k < SegmentLength; k++) s += a[i + k] * b[i + k];
            dots[_tileOfSegment[seg]] += s;
        }
    }

    /// <summary>Variance term Σx² - (Σx)²/n of a set of tiles (n = values in those tiles).</summary>
    public static float Var(float sum, float sq, int n) => Math.Max(0, sq - sum * sum / n);
}

/// <summary>Thresholds of one partial-match stage (see <see cref="TileMatcher"/>).</summary>
/// <param name="Agree">Tile NCC at which a tile counts as "this card is visible here".</param>
/// <param name="FlatEnergy">Tiles with less than this share of the average tile energy are flat and ignored.</param>
/// <param name="MinTiles">Minimum agreeing tiles.</param>
/// <param name="AcceptScore">Minimum NCC over the agreeing tiles.</param>
/// <param name="AcceptMargin">Minimum gap to the best other card scored on the same tiles.</param>
/// <param name="WholeTopK">If &gt; 0, the winner must also be among the first k cards of the whole-art ranking.</param>
/// <param name="FreeMargin">Margin at which the <paramref name="WholeTopK"/> condition is waived (a heavily covered
/// card cannot win the whole-art ranking, and a gap this wide is evidence enough on its own).</param>
/// <param name="HintedPoolOnly">With a character hint, only that character's pool and the colourless pool compete
/// (what rewards and shops actually offer). Off by default: VisionBench stress showed that with fewer competitors
/// the margin over the runner-up is inflated, and wrong cards passed it far more often (31-177 vs 0).</param>
public sealed record PartialStage(float Agree, float FlatEnergy, int MinTiles, float AcceptScore, float AcceptMargin, int WholeTopK, bool HintedPoolOnly = false,
    float FreeMargin = float.PositiveInfinity);

/// <summary>Outcome of the occlusion-robust (partial) match of one slot; kept on the card for debugging.</summary>
/// <param name="CardId">Best card on its own visible tiles.</param>
/// <param name="Score">NCC over the visible tiles (1 = identical there).</param>
/// <param name="Margin">Gap to the best other (non-twin) card scored on the SAME tiles.</param>
/// <param name="VisibleTiles">Tiles that agree with <paramref name="CardId"/> (out of <paramref name="TotalTiles"/>).</param>
/// <param name="TotalTiles">Tiles in the grid (24 blocks or 36 rows).</param>
/// <param name="Accepted">Passed the partial-match thresholds.</param>
/// <param name="Stage">"blocks" or "rows".</param>
public sealed record PartialMatch(string CardId, float Score, float Margin, int VisibleTiles, int TotalTiles, bool Accepted, string Stage = "blocks")
{
    public float VisibleFraction => VisibleTiles / (float)TotalTiles;

    public override string ToString() =>
        $"{CardId} {Stage} p={Score:F3} m={Margin:F3} vis={VisibleTiles}/{TotalTiles}{(Accepted ? "" : " (rejected)")}";
}

/// <summary>
/// Identifies a card when part of it is hidden (the hover tooltip of a neighbour, the merchant's hand, a
/// cross-fading panel) - the cases where one NCC over the whole art is dragged down by the covered part.
///
/// <para>Idea: the covered part does not resemble the true card, the visible part does almost perfectly.
/// For every candidate we find the tiles where it agrees with the screen (tile NCC ≥ <see cref="PartialStage.Agree"/>)
/// and score it by ONE NCC over the union of those tiles. A wrong card only agrees on a few scattered tiles,
/// and an NCC over a union of tiles is far harder to fake than any single tile.</para>
///
/// <para>Fairness check: choosing tiles per candidate flatters every candidate. So the winner's tiles are then
/// used to score ALL other cards, and the winner must beat the best of them by a wide margin on exactly the
/// same pixels. Only then is the match accepted.</para>
/// </summary>
internal sealed class TileMatcher
{
    private readonly CardArtDatabase _db;
    private readonly float[] _sum; // entry-major, Grid.Count floats per entry
    private readonly float[] _sq;

    public TileGrid Grid { get; }

    public TileMatcher(CardArtDatabase db, TileGrid grid)
    {
        _db = db;
        Grid = grid;
        int T = grid.Count;
        _sum = new float[db.Count * T];
        _sq = new float[db.Count * T];
        for (int e = 0; e < db.Count; e++)
            grid.SumsOf(db.FullVector(e), _sum.AsSpan(e * T, T), _sq.AsSpan(e * T, T));
    }

    /// <summary>
    /// Ranks the allowed entries on the visible part of <paramref name="query"/> (a normalised full vector).
    /// Returns null when no candidate agrees on enough tiles. <paramref name="ranking"/> receives every
    /// allowed card scored on the winner's tiles (best first, one entry per card id).
    /// </summary>
    public (int Entry, ulong Mask, int Visible)? Best(ReadOnlySpan<float> query, Func<ArtEntry, bool> allowed, PartialStage o,
        out ArtMatch[] ranking)
    {
        int T = Grid.Count, n = Grid.TileLength;
        Span<float> qs = stackalloc float[T], qq = stackalloc float[T], qVar = stackalloc float[T], tex = stackalloc float[T];
        Grid.SumsOf(query, qs, qq);
        Grid.TextureEnergy(query, tex);
        // The whole normalised vector has energy 1, so a tile carries 1/T of it on average. Tiles far below
        // that are flat on screen (dark tooltip panel, black fade): they cannot confirm anything.
        float flat = o.FlatEnergy / T;
        // A smooth single-colour tile (the hand, a fade panel) has no texture to confirm anything with: mark it
        // flat even though its interleaved R,G,B values differ.
        for (int t = 0; t < T; t++) qVar[t] = tex[t] < flat ? 0 : TileGrid.Var(qs[t], qq[t], n);

        int count = _db.Count;
        float[] dots = new float[count * T];
        int bestEntry = -1, bestVisible = 0;
        ulong bestMask = 0;
        float bestScore = float.NegativeInfinity;
        for (int e = 0; e < count; e++)
        {
            if (!allowed(_db.Entries[e])) continue;
            Span<float> d = dots.AsSpan(e * T, T);
            Grid.Dots(query, _db.FullVector(e), d);
            ReadOnlySpan<float> es = _sum.AsSpan(e * T, T), eq = _sq.AsSpan(e * T, T);
            ulong mask = 0;
            int visible = 0;
            for (int t = 0; t < T; t++)
            {
                float ev = TileGrid.Var(es[t], eq[t], n);
                if (qVar[t] < flat || ev < flat) continue;
                float ncc = (d[t] - qs[t] * es[t] / n) / MathF.Sqrt(qVar[t] * ev);
                if (ncc < o.Agree) continue;
                mask |= 1UL << t;
                visible++;
            }
            if (visible < o.MinTiles) continue;
            float score = MaskedNcc(e, mask, d, qs, qq);
            if (score > bestScore)
            {
                bestScore = score;
                bestEntry = e;
                bestMask = mask;
                bestVisible = visible;
            }
        }
        if (bestEntry < 0)
        {
            ranking = [];
            return null;
        }

        // Fairness: every allowed card on the winner's tiles.
        var all = new List<ArtMatch>();
        for (int e = 0; e < count; e++)
        {
            if (!allowed(_db.Entries[e])) continue;
            all.Add(new ArtMatch(e, _db.Entries[e].CardId, MaskedNcc(e, bestMask, dots.AsSpan(e * T, T), qs, qq)));
        }
        ranking = all.OrderByDescending(m => m.Score).DistinctBy(m => m.CardId).Take(8).ToArray();
        return (bestEntry, bestMask, bestVisible);
    }

    /// <summary>
    /// How well the best of a few candidates fits the query on its visible part: the sum of the tile NCCs of the
    /// agreeing tiles. Rewards both more visible tiles and better fits, so a box search maximising it moves the
    /// box onto the card instead of shrinking it to a few lucky tiles. Used to refine the box of a covered card.
    /// </summary>
    public float Agreement(ReadOnlySpan<float> query, ReadOnlySpan<int> entries, PartialStage o)
    {
        int T = Grid.Count, n = Grid.TileLength;
        Span<float> qs = stackalloc float[T], qq = stackalloc float[T], d = stackalloc float[T], tex = stackalloc float[T];
        Grid.SumsOf(query, qs, qq);
        Grid.TextureEnergy(query, tex);
        float flat = o.FlatEnergy / T;
        float best = 0;
        foreach (int e in entries)
        {
            Grid.Dots(query, _db.FullVector(e), d);
            ReadOnlySpan<float> es = _sum.AsSpan(e * T, T), eq = _sq.AsSpan(e * T, T);
            float sum = 0;
            for (int t = 0; t < T; t++)
            {
                float qv = TileGrid.Var(qs[t], qq[t], n), ev = TileGrid.Var(es[t], eq[t], n);
                if (qv < flat || ev < flat || tex[t] < flat) continue;
                float ncc = (d[t] - qs[t] * es[t] / n) / MathF.Sqrt(qv * ev);
                if (ncc >= o.Agree) sum += ncc;
            }
            best = Math.Max(best, sum);
        }
        return best;
    }

    /// <summary>
    /// How one entry fits the query tile by tile: the number of textured tiles where it agrees (as in
    /// <see cref="Best"/>) and the NCC over the union of those tiles.
    /// </summary>
    public (int Visible, float Score) Fit(ReadOnlySpan<float> query, int entry, PartialStage o)
    {
        int T = Grid.Count, n = Grid.TileLength;
        Span<float> qs = stackalloc float[T], qq = stackalloc float[T], d = stackalloc float[T], tex = stackalloc float[T];
        Grid.SumsOf(query, qs, qq);
        // Only textured tiles can confirm a card: two smooth patches of the same colour (a hand over the screen, a
        // sky in the art) "agree" perfectly, which is exactly the coincidence this check must not count.
        Grid.TextureEnergy(query, tex);
        Grid.Dots(query, _db.FullVector(entry), d);
        ReadOnlySpan<float> es = _sum.AsSpan(entry * T, T), eq = _sq.AsSpan(entry * T, T);
        float flat = o.FlatEnergy / T;
        ulong mask = 0;
        int visible = 0;
        for (int t = 0; t < T; t++)
        {
            float qv = TileGrid.Var(qs[t], qq[t], n), ev = TileGrid.Var(es[t], eq[t], n);
            if (qv < flat || ev < flat || tex[t] < flat) continue;
            if ((d[t] - qs[t] * es[t] / n) / MathF.Sqrt(qv * ev) < o.Agree) continue;
            mask |= 1UL << t;
            visible++;
        }
        return (visible, visible == 0 ? 0 : MaskedNcc(entry, mask, d, qs, qq));
    }

    /// <summary>NCC between the query and entry <paramref name="e"/> over the union of the tiles in <paramref name="mask"/>.</summary>
    private float MaskedNcc(int e, ulong mask, ReadOnlySpan<float> dots, ReadOnlySpan<float> qs, ReadOnlySpan<float> qq)
    {
        int T = Grid.Count;
        ReadOnlySpan<float> es = _sum.AsSpan(e * T, T), eq = _sq.AsSpan(e * T, T);
        float sxy = 0, sx = 0, sxx = 0, sy = 0, syy = 0;
        int tiles = 0;
        for (int t = 0; t < T; t++)
        {
            if ((mask & (1UL << t)) == 0) continue;
            tiles++;
            sxy += dots[t];
            sx += qs[t];
            sxx += qq[t];
            sy += es[t];
            syy += eq[t];
        }
        int n = tiles * Grid.TileLength;
        if (n == 0) return 0;
        float vx = TileGrid.Var(sx, sxx, n), vy = TileGrid.Var(sy, syy, n);
        return vx <= 0 || vy <= 0 ? 0 : (sxy - sx * sy / n) / MathF.Sqrt(vx * vy);
    }
}
