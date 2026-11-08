using System.Globalization;
using System.Text;

namespace Sts2Overlay.Vision;

/// <summary>
/// Cheap first pass: a handful of colour probes at fixed reference positions decide which layouts are worth
/// matching. Costs well under a millisecond (a few small boxes, sub-sampled) so it can run on every poll.
///
/// Probe colours were measured on the user's captures (Korean UI, 4K) and are plain numbers, not game
/// imagery. Text-free parts of the UI are used so the probes do not depend on the language:
/// <list type="bullet">
/// <item>Reward: the parchment ribbon ends and its top edge (the banner text sits in the middle), plus two
/// boxes beside the ribbon that are dark because the screen uses the shared black backstop.
/// The "loot" reward list (전리품!) uses the same banner, so the probes say only "reward-like";
/// card matching makes the final call.</item>
/// <item>Shop: three patches of the merchant's teal rug around the slots.</item>
/// </list>
/// Measured separation (159 captures): reward probes within 12 levels on every reward screen, ≥ 44 on
/// every other screen; rug probes within 3 levels on every open shop, ≥ 46 elsewhere.
/// </summary>
public static class ScreenClassifier
{
    private readonly record struct ColourProbe(string Name, RectF Box, float R, float G, float B);
    private readonly record struct DarkProbe(string Name, RectF Box, float MaxBrightness);

    private static readonly ColourProbe[] RewardBanner =
    [
        new("banL", new RectF(715, 250, 75, 50), 161, 133, 101),
        new("banR", new RectF(1135, 250, 70, 50), 161, 134, 104),
        new("banTop", new RectF(820, 240, 280, 10), 165, 140, 111),
    ];

    private static readonly DarkProbe[] RewardBackdrop =
    [
        new("darkL", new RectF(540, 250, 60, 50), 40),
        new("darkR", new RectF(1320, 250, 60, 50), 40),
    ];

    private static readonly ColourProbe[] ShopRug =
    [
        new("rugL", new RectF(250, 250, 70, 250), 73, 106, 97),
        new("rugB", new RectF(300, 940, 1000, 50), 77, 112, 99),
        new("rugR", new RectF(1600, 300, 100, 200), 80, 114, 97),
    ];

    /// <summary>Max per-channel difference (0..255) for a colour probe to count as matching.</summary>
    public const float ColourTolerance = 32f;

    public static ScreenGate Evaluate(in ScreenFrame frame)
    {
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        var detail = new StringBuilder();

        bool banner = true;
        foreach (ColourProbe p in RewardBanner) banner &= Check(frame, t, p, detail);
        bool dark = true;
        foreach (DarkProbe p in RewardBackdrop)
        {
            (float r, float g, float b) = MeanRgb(frame, t.ToPixelRect(p.Box));
            float v = (r + g + b) / 3;
            detail.Append(CultureInfo.InvariantCulture, $"{p.Name}={v:F0} ");
            dark &= v <= p.MaxBrightness;
        }

        int rug = 0;
        foreach (ColourProbe p in ShopRug)
            if (Check(frame, t, p, detail)) rug++;

        // Rug: 2 of 3 so a hover tooltip or the merchant's speech bubble over one patch does not hide the shop.
        return new ScreenGate(banner && dark, rug >= 2, detail.ToString().TrimEnd());
    }

    private static bool Check(in ScreenFrame frame, RefTransform t, ColourProbe p, StringBuilder detail)
    {
        (float r, float g, float b) = MeanRgb(frame, t.ToPixelRect(p.Box));
        float d = Math.Max(Math.Abs(r - p.R), Math.Max(Math.Abs(g - p.G), Math.Abs(b - p.B)));
        detail.Append(CultureInfo.InvariantCulture, $"{p.Name}={d:F0} ");
        return d <= ColourTolerance;
    }

    /// <summary>
    /// Mean colour of a pixel rectangle. Probes are large, flat areas, so reading every other pixel in both
    /// directions (1/4 of the memory traffic) gives the same mean.
    /// </summary>
    internal static (float R, float G, float B) MeanRgb(in ScreenFrame frame, PixelRect rect)
    {
        rect = rect.Intersect(frame.Width, frame.Height);
        if (rect.Width == 0 || rect.Height == 0) return (0, 0, 0);
        int step = rect.Width >= 8 && rect.Height >= 8 ? 2 : 1;
        ReadOnlySpan<byte> px = frame.Bgra.Span;
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = rect.Y; y < rect.Bottom; y += step)
        {
            int p = y * frame.Stride + rect.X * 4;
            for (int x = rect.X; x < rect.Right; x += step, p += 4 * step)
            {
                b += px[p];
                g += px[p + 1];
                r += px[p + 2];
                n++;
            }
        }
        return (r / (float)n, g / (float)n, b / (float)n);
    }
}
