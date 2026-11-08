using System.Numerics;
using System.Runtime.InteropServices;

namespace Sts2Overlay.Vision;

/// <summary>
/// Normalised cross-correlation (NCC) on small colour thumbnails of a card's artwork.
///
/// The screen shows EXACTLY the game's artwork, only scaled, darkened (fade-in, dim backstop) and partly
/// covered by the card frame. So we compare the same sub-region of both images cell by cell:
/// <code>NCC = sum((a - mean_a) * (b - mean_b)) / (|a - mean_a| * |b - mean_b|)</code>
/// 1.0 = identical up to brightness/contrast, ~0 = unrelated. We store every vector already mean-subtracted
/// and divided by its length ("normalised"), so at match time NCC is a plain dot product.
///
/// Two resolutions:
/// <list type="bullet">
/// <item><b>Full</b> 48x36 RGB (5184 floats): final identification.</item>
/// <item><b>Coarse</b> 16x12 RGB (576 floats, 9x cheaper): used while searching for the card's exact box,
/// where we evaluate ~100 candidate boxes per card.</item>
/// </list>
/// </summary>
public static class Ncc
{
    public const int FullW = 48, FullH = 36, FullLength = FullW * FullH * 3;
    public const int CoarseW = 16, CoarseH = 12, CoarseLength = CoarseW * CoarseH * 3;

    /// <summary>
    /// Part of the art box we trust, as fractions of the 250x190 portrait: the title banner covers the top
    /// (curved, ~16% at the sides) and the type plaque/border the bottom and edges.
    /// </summary>
    public static readonly RectF Inner = new(0.08f, 0.16f, 0.84f, 0.74f);

    /// <summary>Minimum RMS contrast (grey levels) of a patch before it is compared at all.</summary>
    public const float MinContrast = 3f;

    /// <summary>Subtracts the mean and scales to unit length. Returns false for a flat (featureless) patch.</summary>
    public static bool Normalise(Span<float> v)
    {
        double sum = 0;
        foreach (float x in v) sum += x;
        float mean = (float)(sum / v.Length);
        double len2 = 0;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] -= mean;
            len2 += v[i] * v[i];
        }
        // Below ~3 grey levels of RMS contrast the patch is (nearly) flat: an empty dark slot, or a card that has
        // only just started fading in from black. Normalising it would blow up noise and JPEG-like quantisation
        // into a random vector that can correlate 0.7+ with some dark artwork, so treat it as "no card".
        if (len2 < v.Length * MinContrast * MinContrast)
        {
            v.Clear();
            return false;
        }
        float inv = (float)(1.0 / Math.Sqrt(len2));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
        return true;
    }

    /// <summary>
    /// Dot product with SIMD (<see cref="Vector{T}"/>: 8 floats per instruction with AVX2).
    /// Four independent accumulators break the add-latency dependency chain: each vector add takes ~4 cycles
    /// before its result can be reused, so with a single accumulator the loop would wait on itself; with four
    /// the CPU can keep several multiply-adds in flight at once.
    /// </summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Math.Min(a.Length, b.Length);
        ReadOnlySpan<Vector<float>> va = MemoryMarshal.Cast<float, Vector<float>>(a[..n]);
        ReadOnlySpan<Vector<float>> vb = MemoryMarshal.Cast<float, Vector<float>>(b[..n]);
        Vector<float> s0 = Vector<float>.Zero, s1 = s0, s2 = s0, s3 = s0;
        int i = 0;
        for (; i + 4 <= va.Length; i += 4)
        {
            s0 += va[i] * vb[i];
            s1 += va[i + 1] * vb[i + 1];
            s2 += va[i + 2] * vb[i + 2];
            s3 += va[i + 3] * vb[i + 3];
        }
        for (; i < va.Length; i++) s0 += va[i] * vb[i];
        float sum = Vector.Sum(s0 + s1 + s2 + s3);
        for (int j = va.Length * Vector<float>.Count; j < n; j++) sum += a[j] * b[j];
        return sum;
    }

    /// <summary>Thumbnail of the trusted inner part of an art box (box in frame pixels of <paramref name="img"/>).</summary>
    internal static bool Extract(IntegralImage img, RectF artBoxPixels, int w, int h, Span<float> dst)
    {
        RectF inner = artBoxPixels.Fraction(Inner.X, Inner.Y, Inner.Width, Inner.Height);
        img.SampleGrid(inner, w, h, dst);
        return Normalise(dst);
    }
}
