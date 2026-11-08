using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VisionSpike;

/// <summary>
/// Perceptual hash (pHash), the classic DCT version:
///  1. shrink to 32x32 grayscale (throws away detail, keeps overall structure)
///  2. 2D DCT (turns the picture into "how much of each frequency pattern")
///  3. keep the 8x8 lowest frequencies (the coarse shapes), skip [0,0] = average brightness
///  4. each of the 64 values -> 1 bit: above the median or not
/// Two images that look alike get hashes that differ in only a few bits (small Hamming distance),
/// even after resizing, slight blur, colour shifts or JPEG-like noise.
/// </summary>
public static class PHash
{
    private const int Size = 32;
    private const int Low = 8;

    // DCT basis is the same for every image, so compute it once.
    private static readonly double[,] Cos = BuildCos();

    public static ulong Compute(Image<Rgba32> image)
    {
        using Image<Rgba32> small = image.Clone(c => c.Resize(Size, Size).Grayscale());
        var px = new double[Size, Size];
        small.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < Size; y++)
            {
                Span<Rgba32> row = rows.GetRowSpan(y);
                for (int x = 0; x < Size; x++) px[y, x] = row[x].R;
            }
        });

        // Only the 8x8 low-frequency block is needed, so compute just those DCT coefficients.
        var dct = new double[Low * Low];
        for (int v = 0; v < Low; v++)
        for (int u = 0; u < Low; u++)
        {
            double sum = 0;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                sum += px[y, x] * Cos[u, x] * Cos[v, y];
            dct[v * Low + u] = sum;
        }

        // Median of the 63 AC terms (skip index 0 = DC / average brightness).
        double[] ac = dct[1..].ToArray();
        Array.Sort(ac);
        double median = ac[ac.Length / 2];

        ulong hash = 0;
        for (int i = 1; i < Low * Low; i++)
        {
            if (dct[i] > median) hash |= 1UL << (i - 1);
        }
        return hash;
    }

    /// <summary>Number of differing bits. 0 = identical, ~32 = unrelated.</summary>
    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    private static double[,] BuildCos()
    {
        var c = new double[Low, Size];
        for (int u = 0; u < Low; u++)
        for (int x = 0; x < Size; x++)
            c[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Size));
        return c;
    }
}
