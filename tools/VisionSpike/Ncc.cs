using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VisionSpike;

/// <summary>
/// Template matching on small colour thumbnails with normalised cross-correlation (NCC).
///
/// pHash throws colour away and keeps only 64 bits, so different cards can land close together.
/// Here we know the screen shows EXACTLY the game's artwork, just scaled and partly covered by the
/// card frame. So we compare the same sub-region of both images pixel by pixel:
///   NCC = sum((a - mean_a) * (b - mean_b)) / (|a - mean_a| * |b - mean_b|)
/// 1.0 = identical up to brightness/contrast, ~0 = unrelated. Subtracting the mean and dividing by
/// the length makes it ignore the game's darkening/brightening of the art.
/// </summary>
public static class Ncc
{
    public const int W = 48, H = 36; // same 1.316 aspect ratio as the 250x190 art box

    /// <summary>Inner part of the art box we trust (the frame and title banner cover the edges).</summary>
    public static readonly RectangleF Inner = new(0.08f, 0.16f, 0.84f, 0.74f);

    public static float[] Vector(Image<Rgba32> img)
    {
        using Image<Rgba32> s = img.Clone(c => c.Resize(W, H));
        var v = new float[W * H * 3];
        int i = 0;
        s.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < H; y++)
            {
                Span<Rgba32> row = rows.GetRowSpan(y);
                for (int x = 0; x < W; x++)
                {
                    v[i++] = row[x].R; v[i++] = row[x].G; v[i++] = row[x].B;
                }
            }
        });
        Normalise(v);
        return v;
    }

    /// <summary>Vector of the <see cref="Inner"/> part of a full art image (reference side).</summary>
    public static float[] InnerVector(Image<Rgba32> art)
    {
        var r = new Rectangle(
            (int)(Inner.X * art.Width), (int)(Inner.Y * art.Height),
            (int)(Inner.Width * art.Width), (int)(Inner.Height * art.Height));
        using Image<Rgba32> crop = art.Clone(c => c.Crop(r));
        return Vector(crop);
    }

    /// <summary>Vector of the <see cref="Inner"/> part of an on-screen art box (capture side).</summary>
    public static float[] ScreenVector(Image<Rgba32> capture, RectangleF box)
    {
        var r = new Rectangle(
            (int)Math.Round(box.X + Inner.X * box.Width), (int)Math.Round(box.Y + Inner.Y * box.Height),
            (int)Math.Round(Inner.Width * box.Width), (int)Math.Round(Inner.Height * box.Height));
        r.Intersect(new Rectangle(0, 0, capture.Width, capture.Height));
        using Image<Rgba32> crop = capture.Clone(c => c.Crop(r));
        return Vector(crop);
    }

    public static float Score(float[] a, float[] b)
    {
        double dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return (float)dot;
    }

    private static void Normalise(float[] v)
    {
        double mean = v.Average(x => (double)x);
        double len = 0;
        for (int i = 0; i < v.Length; i++) { v[i] -= (float)mean; len += v[i] * v[i]; }
        len = Math.Sqrt(len);
        if (len < 1e-6) return;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / len);
    }
}
