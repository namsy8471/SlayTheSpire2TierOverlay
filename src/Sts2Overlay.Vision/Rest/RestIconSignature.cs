namespace Sts2Overlay.Vision.Rest;

/// <summary>
/// A 4x3 grid of mean colours that summarises one option icon ("signature", 36 numbers).
///
/// WHY so coarse: we only have to tell ~10 very differently coloured pictures apart (green bed, red anvil,
/// blue shovel...) and to say "this is not an icon at all". Twelve averages are enough for that, they are
/// cheap (a few hundred pixel reads per cell even at 4K), and averaging makes them immune to JPEG noise,
/// to the 2x downscale between 4K and FHD captures and to a few pixels of position error.
/// It also means the repo stores 36 numbers per icon instead of any game image.
/// </summary>
public static class RestIconSignature
{
    /// <summary>Numbers per signature: cells x RGB.</summary>
    public const int Length = RestSiteLayout.CellColumns * RestSiteLayout.CellRows * 3;

    /// <summary>
    /// Signature of a whole icon image (used by tools/RestBench to regenerate <see cref="RestIconSignatures"/>
    /// from images/ui/rest_site/option_*.png, and by tests). Every pixel is read.
    /// </summary>
    /// <param name="icon">The icon texture as a frame (256x169 in v0.107.1).</param>
    /// <param name="brightness">
    /// The game's hsv shader multiplies the colour by <c>v</c> (1.2 while hovered) and the result is clipped at
    /// 255 per pixel. Clipping must happen before averaging, which is why the bright variant is computed here
    /// from the pixels and not by scaling the normal signature.
    /// </param>
    public static float[] FromImage(in ScreenFrame icon, float brightness = 1f)
    {
        var sig = new float[Length];
        var whole = new RectF(0, 0, icon.Width, icon.Height);
        ReadOnlySpan<byte> px = icon.Bgra.Span;
        for (int row = 0; row < RestSiteLayout.CellRows; row++)
        {
            for (int col = 0; col < RestSiteLayout.CellColumns; col++)
            {
                PixelRect r = ToPixelRect(RestSiteLayout.Cell(whole, col, row)).Intersect(icon.Width, icon.Height);
                double sr = 0, sg = 0, sb = 0;
                long n = 0;
                for (int y = r.Y; y < r.Bottom; y++)
                {
                    for (int x = r.X; x < r.Right; x++)
                    {
                        int p = y * icon.Stride + x * 4;
                        sb += Math.Min(255f, px[p] * brightness);
                        sg += Math.Min(255f, px[p + 1] * brightness);
                        sr += Math.Min(255f, px[p + 2] * brightness);
                        n++;
                    }
                }
                int o = (row * RestSiteLayout.CellColumns + col) * 3;
                sig[o] = (float)(sr / n);
                sig[o + 1] = (float)(sg / n);
                sig[o + 2] = (float)(sb / n);
            }
        }
        return sig;
    }

    /// <summary>
    /// Signature of the icon drawn at <paramref name="iconPixels"/> in a captured frame. Reads roughly one pixel
    /// every 2 design units in each direction (every 4th pixel at 4K): the cells are flat averages, so reading
    /// all pixels would only cost time.
    /// </summary>
    internal static void FromFrame(in ScreenFrame frame, RectF iconPixels, float scale, Span<float> sig)
    {
        int step = Math.Max(1, (int)MathF.Round(2 * scale));
        ReadOnlySpan<byte> px = frame.Bgra.Span;
        for (int row = 0; row < RestSiteLayout.CellRows; row++)
        {
            for (int col = 0; col < RestSiteLayout.CellColumns; col++)
            {
                PixelRect r = ToPixelRect(RestSiteLayout.Cell(iconPixels, col, row)).Intersect(frame.Width, frame.Height);
                long sr = 0, sg = 0, sb = 0, n = 0;
                for (int y = r.Y; y < r.Bottom; y += step)
                {
                    int p = y * frame.Stride + r.X * 4;
                    for (int x = r.X; x < r.Right; x += step, p += 4 * step)
                    {
                        sb += px[p];
                        sg += px[p + 1];
                        sr += px[p + 2];
                        n++;
                    }
                }
                int o = (row * RestSiteLayout.CellColumns + col) * 3;
                if (n == 0)
                {
                    // Off-screen cell (a slot of a 6-button row on a narrow frame): impossible colour, never matches.
                    sig[o] = sig[o + 1] = sig[o + 2] = -1000f;
                    continue;
                }
                sig[o] = sr / (float)n;
                sig[o + 1] = sg / (float)n;
                sig[o + 2] = sb / (float)n;
            }
        }
    }

    /// <summary>
    /// Grey version of a signature, as the game draws a disabled option: the hsv shader with s = 0 keeps only
    /// the YIQ luma Y = 0.2989 R + 0.587 G + 0.114 B, and v = 0.6 darkens it. Both steps are linear, so they can
    /// be applied to the cell means directly (unlike the clipped hover brightening).
    /// </summary>
    public static float[] Disabled(ReadOnlySpan<float> normal)
    {
        var sig = new float[normal.Length];
        for (int i = 0; i < normal.Length; i += 3)
        {
            float y = 0.6f * (0.2989f * normal[i] + 0.587f * normal[i + 1] + 0.114f * normal[i + 2]);
            sig[i] = sig[i + 1] = sig[i + 2] = y;
        }
        return sig;
    }

    /// <summary>Pixels whose centre lies inside the rectangle (same rounding for icons and frames).</summary>
    private static PixelRect ToPixelRect(RectF r)
    {
        int x0 = (int)MathF.Round(r.X), y0 = (int)MathF.Round(r.Y);
        int x1 = (int)MathF.Round(r.Right), y1 = (int)MathF.Round(r.Bottom);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}
