using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Sts2Overlay.Vision;

/// <summary>
/// Summed-area table ("integral image") of an RGB region of a frame.
///
/// Why: card recognition samples the same screen area many times at slightly different positions and
/// scales (local search). Averaging the pixels of a box directly costs O(box area) per sample, so a
/// 300x220 px box at 4K costs ~66k reads, and a hundred candidate boxes per card would cost millions.
/// With a summed-area table <c>S(x, y) = sum of all pixels above and left of (x, y)</c>, the sum over ANY
/// rectangle is four lookups: <c>S(x1,y1) - S(x0,y1) - S(x1,y0) + S(x0,y0)</c>. We pay one pass over the
/// region to build it, then every box average is O(1) regardless of its size.
///
/// Fractional box edges: inside one pixel cell the image is constant, so S is exactly bilinear there
/// (a + b*x + c*y + d*x*y). Bilinear interpolation of S therefore gives the EXACT area integral for boxes
/// with sub-pixel edges - no rounding jitter when the search moves a box by 0.3 px or scales it by 1%.
///
/// <para><see cref="Step"/> &gt; 1 first sums step x step source pixels into one cell (e.g. 2 for a 4K
/// frame, so cells are ~1 reference pixel). That halves the table in each direction (4x less memory and
/// cache traffic) without losing anything we use: card thumbnails are 48x36.</para>
///
/// Memory comes from a pool so polling several frames per second does not churn the GC: the per-frame recognisers pass
/// <see cref="VisionScratch.Tables"/> (exact sizes, two tables kept), others get <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
internal sealed class IntegralImage : IDisposable
{
    private int[]? _sat;
    private readonly ScratchPool<int>? _pool;
    private readonly int _rowLen; // ints per table row = (Width + 1) * 3

    /// <summary>Number of cells horizontally/vertically.</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>Source pixels per cell side.</summary>
    public int Step { get; }

    /// <summary>Frame pixel coordinate of the top-left corner of cell (0, 0).</summary>
    public int OriginX { get; }
    public int OriginY { get; }

    private IntegralImage(int[] sat, ScratchPool<int>? pool, int width, int height, int step, int originX, int originY)
    {
        _sat = sat;
        _pool = pool;
        Width = width;
        Height = height;
        Step = step;
        OriginX = originX;
        OriginY = originY;
        _rowLen = (width + 1) * 3;
    }

    public void Dispose()
    {
        if (_sat is { } a)
        {
            if (_pool is not null) _pool.Return(a);
            else ArrayPool<int>.Shared.Return(a);
        }
        _sat = null;
    }

    /// <summary>
    /// Builds the table for <paramref name="region"/> (frame pixels, clipped to the frame). <paramref name="pool"/> is
    /// where the table memory comes from; null = <see cref="ArrayPool{T}.Shared"/> (the art database build).
    /// </summary>
    public static IntegralImage Build(in ScreenFrame frame, PixelRect region, int step, ScratchPool<int>? pool = null)
    {
        step = Math.Max(1, step);
        region = region.Intersect(frame.Width, frame.Height);
        // Table entries are plain sums of 0..255 values; int is exact up to ~8.4 M pixels per region.
        if ((long)region.Width * region.Height * 255 > int.MaxValue)
            throw new ArgumentException($"Region {region} is too large for an int summed-area table.");
        int w = region.Width / step, h = region.Height / step;
        int rowLen = (w + 1) * 3;
        int length = Math.Max(1, rowLen * (h + 1));
        int[] sat = pool?.Rent(length) ?? ArrayPool<int>.Shared.Rent(length);
        Array.Clear(sat, 0, rowLen); // row 0 is all zeros (sum over nothing)

        // A 2-D prefix sum is separable: (1) prefix-sum every row on its own, (2) add each row to the one below.
        // Pass 1 rows are independent, so they run in parallel for big regions (a 4K reward band is ~2 M pixels).
        ReadOnlyMemory<byte> memory = frame.Bgra;
        int stride = frame.Stride;
        void RowPass(int cy)
        {
            ReadOnlySpan<byte> px = memory.Span;
            int dst = (cy + 1) * rowLen;
            sat[dst] = sat[dst + 1] = sat[dst + 2] = 0;
            int r = 0, g = 0, b = 0;
            int sy0 = region.Y + cy * step;
            if (step == 2)
            {
                // The common 4K case, unrolled: one cell = 2 pixels from each of 2 rows (BGRA → +0 B, +1 G, +2 R).
                ReadOnlySpan<byte> r0 = px.Slice(sy0 * stride + region.X * 4, w * 8);
                ReadOnlySpan<byte> r1 = px.Slice((sy0 + 1) * stride + region.X * 4, w * 8);
                for (int cx = 0, i = 0; cx < w; cx++, i += 8)
                {
                    b += r0[i] + r0[i + 4] + r1[i] + r1[i + 4];
                    g += r0[i + 1] + r0[i + 5] + r1[i + 1] + r1[i + 5];
                    r += r0[i + 2] + r0[i + 6] + r1[i + 2] + r1[i + 6];
                    int o = dst + (cx + 1) * 3;
                    sat[o] = r;
                    sat[o + 1] = g;
                    sat[o + 2] = b;
                }
                return;
            }
            for (int cx = 0; cx < w; cx++)
            {
                int sx0 = region.X + cx * step;
                for (int dy = 0; dy < step; dy++)
                {
                    // Walks memory contiguously along a row: friendly to the CPU cache and prefetcher.
                    int p = (sy0 + dy) * stride + sx0 * 4;
                    for (int dx = 0; dx < step; dx++, p += 4)
                    {
                        b += px[p];
                        g += px[p + 1];
                        r += px[p + 2];
                    }
                }
                int o = dst + (cx + 1) * 3;
                sat[o] = r;
                sat[o + 1] = g;
                sat[o + 2] = b;
            }
        }
        if ((long)w * h >= 100_000) Parallel.For(0, h, RowPass);
        else for (int cy = 0; cy < h; cy++) RowPass(cy);

        // Pass 2: row y += row y-1. Whole rows at once with SIMD (8 ints per AVX2 instruction).
        for (int y = 2; y <= h; y++)
        {
            Span<int> cur = sat.AsSpan(y * rowLen, rowLen);
            ReadOnlySpan<int> above = sat.AsSpan((y - 1) * rowLen, rowLen);
            Span<Vector<int>> vc = MemoryMarshal.Cast<int, Vector<int>>(cur);
            ReadOnlySpan<Vector<int>> va = MemoryMarshal.Cast<int, Vector<int>>(above);
            for (int i = 0; i < vc.Length; i++) vc[i] += va[i];
            for (int i = vc.Length * Vector<int>.Count; i < rowLen; i++) cur[i] += above[i];
        }
        return new IntegralImage(sat, pool, w, h, step, region.X, region.Y);
    }

    /// <summary>Frame pixel → cell coordinate.</summary>
    public double ToCellX(double frameX) => (frameX - OriginX) / Step;
    public double ToCellY(double frameY) => (frameY - OriginY) / Step;

    /// <summary>Bilinearly interpolated table value S(x, y) for the three channels (x, y in cells, clamped).</summary>
    private void Sample(double x, double y, out double r, out double g, out double b)
    {
        int[] s = _sat!;
        x = Math.Clamp(x, 0, Width);
        y = Math.Clamp(y, 0, Height);
        int x0 = Math.Min((int)x, Math.Max(0, Width - 1));
        int y0 = Math.Min((int)y, Math.Max(0, Height - 1));
        double fx = x - x0, fy = y - y0;
        int i00 = y0 * _rowLen + x0 * 3, i01 = i00 + 3, i10 = i00 + _rowLen, i11 = i10 + 3;
        double w00 = (1 - fx) * (1 - fy), w01 = fx * (1 - fy), w10 = (1 - fx) * fy, w11 = fx * fy;
        r = w00 * s[i00] + w01 * s[i01] + w10 * s[i10] + w11 * s[i11];
        g = w00 * s[i00 + 1] + w01 * s[i01 + 1] + w10 * s[i10 + 1] + w11 * s[i11 + 1];
        b = w00 * s[i00 + 2] + w01 * s[i01 + 2] + w10 * s[i10 + 2] + w11 * s[i11 + 2];
    }

    /// <summary>
    /// Splits a box (given in FRAME pixels) into a gw x gh grid and writes each cell's mean colour as
    /// interleaved R,G,B (0..255) into <paramref name="dst"/> (length gw*gh*3), row-major.
    /// Neighbouring grid cells share corners, so we evaluate S only at the (gw+1)*(gh+1) grid points.
    /// Parts of the box outside the table read as black.
    /// </summary>
    public void SampleGrid(RectF frameBox, int gw, int gh, Span<float> dst)
    {
        var none = new NoRows();
        SampleRows(frameBox, gw, gh, dst, wholeGrid: true, ref none);
    }

    /// <summary>Receives a grid one output row at a time (gw cells, interleaved R,G,B), top to bottom.</summary>
    internal interface IRowSink
    {
        void Row(ReadOnlySpan<float> rgb);
    }

    private struct NoRows : IRowSink
    {
        public readonly void Row(ReadOnlySpan<float> rgb)
        {
        }
    }

    /// <summary>
    /// <see cref="SampleGrid"/> for a caller that only looks at each row once (the upgrade-green title count): the row
    /// lands in <paramref name="row"/> (length gw*3), is handed to <paramref name="sink"/>, then overwritten by the next.
    /// The values are exactly those SampleGrid writes, without a gw x gh buffer.
    /// </summary>
    internal void SampleGridRows<TSink>(RectF frameBox, int gw, int gh, Span<float> row, ref TSink sink) where TSink : struct, IRowSink
        => SampleRows(frameBox, gw, gh, row, wholeGrid: false, ref sink);

    /// <summary>Rows of S at grid points: a cell row needs only the grid row above and below it, so two are kept.</summary>
    private const int StackRowDoubles = 1536;

    private void SampleRows<TSink>(RectF frameBox, int gw, int gh, Span<float> dst, bool wholeGrid, ref TSink sink) where TSink : struct, IRowSink
    {
        int gx = gw + 1, rowLen = gx * 3;
        // Two grid rows (title boxes at 4K: ~2 x 700 doubles) on the stack, instead of the whole (gw+1) x (gh+1) grid in a
        // pooled array (every Parallel.For worker kept its own copy of each size class).
        Span<double> rows = rowLen <= StackRowDoubles ? stackalloc double[2 * rowLen] : new double[2 * rowLen];
        Span<double> above = rows[..rowLen], below = rows[rowLen..];
        double x0 = ToCellX(frameBox.X), y0 = ToCellY(frameBox.Y);
        double cw = frameBox.Width / Step / gw, ch = frameBox.Height / Step / gh;
        // Cell sums cover Step*Step source pixels each, so divide by that too to get a per-pixel mean.
        double inv = cw * ch > 1e-9 ? 1.0 / (cw * ch * Step * Step) : 0;
        SampleGridRow(x0, cw, y0 + 0 * ch, gx, above);
        for (int j = 0; j < gh; j++)
        {
            SampleGridRow(x0, cw, y0 + (j + 1) * ch, gx, below);
            Span<float> o = wholeGrid ? dst.Slice(j * gw * 3, gw * 3) : dst[..(gw * 3)];
            for (int i = 0; i < gw; i++)
            {
                // Cell (i, j): corners a = above[i], b = above[i + 1], c = below[i], d = below[i + 1].
                int a = i * 3, b = a + 3;
                for (int k = 0; k < 3; k++)
                    o[a + k] = (float)((below[b + k] - above[b + k] - below[a + k] + above[a + k]) * inv);
            }
            sink.Row(o);
            Span<double> t = above;
            above = below;
            below = t;
        }
    }

    private void SampleGridRow(double x0, double cw, double y, int gx, Span<double> dst)
    {
        for (int i = 0; i < gx; i++)
        {
            int o = i * 3;
            Sample(x0 + i * cw, y, out dst[o], out dst[o + 1], out dst[o + 2]);
        }
    }

    /// <summary>Mean R,G,B of a box given in frame pixels.</summary>
    public (float R, float G, float B) Mean(RectF frameBox)
    {
        Span<float> c = stackalloc float[3];
        SampleGrid(frameBox, 1, 1, c);
        return (c[0], c[1], c[2]);
    }
}
