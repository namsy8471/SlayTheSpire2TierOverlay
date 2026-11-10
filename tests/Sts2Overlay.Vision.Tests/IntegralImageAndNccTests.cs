namespace Sts2Overlay.Vision.Tests;

public class IntegralImageAndNccTests
{
    private static Canvas RandomCanvas(int w, int h, int seed)
    {
        var c = new Canvas(w, h);
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                c.Set(x, y, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        return c;
    }

    private static (double R, double G, double B) BruteMean(Canvas c, int x0, int y0, int w, int h)
    {
        double r = 0, g = 0, b = 0;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int p = (y * c.Width + x) * 4;
                b += c.Pixels[p];
                g += c.Pixels[p + 1];
                r += c.Pixels[p + 2];
            }
        }
        int n = w * h;
        return (r / n, g / n, b / n);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Box_mean_equals_brute_force(int step)
    {
        Canvas c = RandomCanvas(97, 61, 7);
        // Region offset from the frame origin checks the origin bookkeeping too.
        using IntegralImage img = IntegralImage.Build(c.Frame, new PixelRect(5, 3, 90, 57), step);
        // Boxes aligned to whole cells, in frame pixels.
        foreach ((int x, int y, int w, int h) in new[] { (5, 3, step, step), (5 + step * 4, 3 + step * 2, step * 10, step * 7), (5, 3, step * 29, step * 18) })
        {
            (float r, float g, float b) = img.Mean(new RectF(x, y, w, h));
            (double er, double eg, double eb) = BruteMean(c, x, y, w, h);
            Assert.Equal(er, r, 2);
            Assert.Equal(eg, g, 2);
            Assert.Equal(eb, b, 2);
        }
    }

    [Fact]
    public void Fractional_box_edges_give_the_exact_area_weighted_mean()
    {
        // Horizontal ramp: pixel column x has value 10x. Box [0.5, 3.5) covers half of column 0, columns 1-2,
        // half of column 3: (0.5*0 + 10 + 20 + 0.5*30) / 3 = 15.
        var c = new Canvas(8, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
                c.Set(x, y, (byte)(10 * x), 0, 0);
        using IntegralImage img = IntegralImage.Build(c.Frame, new PixelRect(0, 0, 8, 4), 1);
        Assert.Equal(15f, img.Mean(new RectF(0.5f, 0, 3, 4)).R, 4);
        // Moving the box by 0.25 px moves the mean by exactly 2.5 (no rounding jitter).
        Assert.Equal(17.5f, img.Mean(new RectF(0.75f, 0, 3, 4)).R, 4);
    }

    [Fact]
    public void SampleGrid_returns_cell_means_row_major()
    {
        // Left half red, right half blue: a 2x1 grid must split them cleanly.
        var c = new Canvas(20, 10);
        c.FillPixels(new RectF(0, 0, 10, 10), 200, 0, 0);
        c.FillPixels(new RectF(10, 0, 10, 10), 0, 0, 100);
        using IntegralImage img = IntegralImage.Build(c.Frame, new PixelRect(0, 0, 20, 10), 1);
        float[] dst = new float[6];
        img.SampleGrid(new RectF(0, 0, 20, 10), 2, 1, dst);
        Assert.Equal([200f, 0f, 0f, 0f, 0f, 100f], dst.Select(v => MathF.Round(v, 3)));
    }

    private struct CollectRows(List<float> into) : IntegralImage.IRowSink
    {
        public readonly void Row(ReadOnlySpan<float> rgb) => into.AddRange(rgb.ToArray());
    }

    [Theory]
    [InlineData(16, 12)]
    [InlineData(48, 36)]
    [InlineData(1, 1)]
    [InlineData(600, 3)] // wider than the two stack rows: the heap path
    public void SampleGridRows_is_bit_identical_to_SampleGrid(int gw, int gh)
    {
        Canvas c = RandomCanvas(700, 90, 11);
        using IntegralImage img = IntegralImage.Build(c.Frame, new PixelRect(3, 2, 690, 86), 2);
        var box = new RectF(10.3f, 7.7f, 601.9f, 61.4f);
        float[] whole = new float[gw * gh * 3];
        img.SampleGrid(box, gw, gh, whole);
        var rows = new List<float>();
        var sink = new CollectRows(rows);
        img.SampleGridRows(box, gw, gh, new float[gw * 3], ref sink);
        Assert.Equal(whole.Select(BitConverter.SingleToInt32Bits), rows.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void Normalise_makes_zero_mean_unit_length()
    {
        float[] v = [1, 2, 3, 4, 50, 6, 7, 8, 9, 10, 11];
        Assert.True(Ncc.Normalise(v));
        Assert.Equal(0, v.Sum(), 4);
        Assert.Equal(1, v.Sum(x => x * x), 4);
    }

    [Fact]
    public void Normalise_rejects_flat_patches()
    {
        // RMS contrast 1 grey level (< MinContrast): an empty dark slot or a card that just started fading in.
        float[] v = Enumerable.Range(0, 64).Select(i => 20f + (i % 2 == 0 ? 1 : -1)).ToArray();
        Assert.False(Ncc.Normalise(v));
        Assert.All(v, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void Ncc_ignores_brightness_and_contrast()
    {
        var rng = new Random(3);
        // Length 101 is not a multiple of the SIMD width, so the scalar tail is exercised too.
        float[] a = Enumerable.Range(0, 101).Select(_ => (float)rng.NextDouble() * 255).ToArray();
        float[] dim = a.Select(x => 0.3f * x + 5).ToArray(); // the same art while fading in
        float[] inverted = a.Select(x => 255 - x).ToArray();
        float[] other = Enumerable.Range(0, 101).Select(_ => (float)rng.NextDouble() * 255).ToArray();
        Assert.True(Ncc.Normalise(a) && Ncc.Normalise(dim) && Ncc.Normalise(inverted) && Ncc.Normalise(other));
        Assert.Equal(1f, Ncc.Dot(a, dim), 4);
        Assert.Equal(-1f, Ncc.Dot(a, inverted), 4);
        Assert.InRange(Ncc.Dot(a, other), -0.4f, 0.4f);
    }

    [Fact]
    public void Simd_dot_matches_scalar_dot()
    {
        var rng = new Random(5);
        foreach (int n in new[] { 1, 7, 8, 31, 32, 33, Ncc.CoarseLength, Ncc.FullLength })
        {
            float[] a = Enumerable.Range(0, n).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
            float[] b = Enumerable.Range(0, n).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
            double expected = 0;
            for (int i = 0; i < n; i++) expected += a[i] * b[i];
            Assert.Equal(expected, Ncc.Dot(a, b), 3);
        }
    }
}
