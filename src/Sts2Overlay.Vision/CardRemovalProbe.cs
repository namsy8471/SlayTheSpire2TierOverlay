using System.Globalization;

namespace Sts2Overlay.Vision;

/// <summary>What <see cref="CardRemovalProbe"/> saw at the merchant's card-removal service.</summary>
/// <param name="Available">The service can be bought right now: the gold coin is fully visible, has no red "used" cross
/// and its price label is showing. Only then should the overlay point at it.</param>
/// <param name="Bounds">The coin (normal, not hovered size) in frame pixels. Valid even when not available.</param>
/// <param name="FaceGold">Fraction of the coin face that is coin-gold (0..1).</param>
/// <param name="FaceRed">Fraction of the coin face that is the red cross drawn once the service was used (0..1).</param>
/// <param name="PriceIcon">Gold samples in the price row under the coin (the small gold-coin icon of the price label).</param>
public sealed record CardRemovalReading(bool Available, PixelRect Bounds, float FaceGold, float FaceRed, int PriceIcon)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{(Available ? "available" : "absent")} gold={FaceGold:F3} red={FaceRed:F3} price={PriceIcon}");
}

/// <summary>
/// Is the shop's card-removal service (the big gold coin with a torn card, right of the potions) on screen and still
/// for sale? A colour probe at a FIXED place: the service is a fixed node of the merchant inventory
/// (merchant_inventory.tscn "MerchantCardRemoval", SlotsContainer (118, 80) + (1370, 678), scale 0.65), so unlike
/// the cards it never moves, apart from growing a little while hovered.
///
/// <para><b>Why colours and not template matching:</b> three plain questions separate every case we have seen, and
/// they need only a few numbers (no game image in the repo):</para>
/// <list type="number">
/// <item><b>Is the coin there?</b> Most of the coin face is a saturated gold. A modal (dimmed screen), a card or
/// tooltip covering the coin, combat or the map all make that fraction collapse.</item>
/// <item><b>Was it used?</b> The game plays the "Used" animation and leaves a red cross over the coin.</item>
/// <item><b>Is it still for sale?</b> The moment removal is bought the price label under the coin disappears (the
/// cross comes a few frames later, after the card was picked), so "coin but no price" also means "used". The label
/// starts with a small gold-coin icon, which is easy to find whatever the digits are or their colour (cream, or red
/// when the player cannot afford it).</item>
/// </list>
///
/// <para>Everything is sampled on the 1920x1080 reference grid (one sample per reference unit, mapped with
/// <see cref="RefTransform"/>), so the fractions and the icon count mean the same at FHD and 4K, and the whole probe
/// reads about 25 000 pixels (~0.1 ms).</para>
///
/// <para>Measured on 381 saved shop captures (FHD, resized to 1440p and upscaled 2x = 4K; all correct): see
/// <c>VisionBench removal</c>. Available coins have gold ≥ 0.60, red 0 and ≥ 376 icon samples; a used coin has red
/// ≈ 0.40 or 0 icon samples; everything else has gold ≤ 0.52 or no icon. The thresholds sit in those gaps.</para>
/// </summary>
public static class CardRemovalProbe
{
    /// <summary>Coin centre and radius in reference units, measured on the captures. (The scene's node origin is
    /// (1488, 758); the visible coin sits ~9 units lower inside its texture.)</summary>
    public const float CenterX = 1488f, CenterY = 767f, Radius = 85f;

    /// <summary>The coin at its normal (not hovered) size, reference units.</summary>
    public static RectF CoinRef => RectF.FromCenter(CenterX, CenterY, 2 * Radius, 2 * Radius);

    /// <summary>Only the inner part of the face is sampled, so a hovered coin (about 1.2x bigger) reads the same.</summary>
    private const float FaceRadius = 0.85f * Radius;

    /// <summary>The price row under the coin, tall enough for both the normal and the hovered (lower) label.</summary>
    private static readonly RectF PriceRow = new(1410, 872, 160, 73);

    /// <summary>Price-row samples closer than this to the coin centre are skipped: they would be the bottom rim of a
    /// hovered coin, which is the same gold as the icon.</summary>
    private const float PriceRowExclusion = 1.27f * Radius;

    public const float MinFaceGold = 0.50f;
    public const float MaxFaceRed = 0.15f;
    public const int MinPriceIcon = 150;

    public static CardRemovalReading Read(in ScreenFrame frame)
    {
        frame.Validate();
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        ReadOnlySpan<byte> px = frame.Bgra.Span;

        int face = 0, gold = 0, red = 0;
        for (float y = CenterY - FaceRadius; y <= CenterY + FaceRadius; y++)
            for (float x = CenterX - FaceRadius; x <= CenterX + FaceRadius; x++)
            {
                float dx = x - CenterX, dy = y - CenterY;
                if (dx * dx + dy * dy > FaceRadius * FaceRadius) continue;
                if (!TrySample(frame, px, t, x, y, out byte r, out byte g, out byte b)) continue;
                face++;
                if (IsGold(r, g, b)) gold++;
                else if (IsRed(r, g, b)) red++;
            }

        int icon = 0;
        for (float y = PriceRow.Y; y < PriceRow.Bottom; y++)
            for (float x = PriceRow.X; x < PriceRow.Right; x++)
            {
                float dx = x - CenterX, dy = y - CenterY;
                if (dx * dx + dy * dy < PriceRowExclusion * PriceRowExclusion) continue;
                if (TrySample(frame, px, t, x, y, out byte r, out byte g, out byte b) && IsGold(r, g, b)) icon++;
            }

        float goldFraction = face == 0 ? 0 : gold / (float)face;
        float redFraction = face == 0 ? 0 : red / (float)face;
        bool available = goldFraction >= MinFaceGold && redFraction <= MaxFaceRed && icon >= MinPriceIcon;
        return new CardRemovalReading(available, t.ToPixelRect(CoinRef), goldFraction, redFraction, icon);
    }

    /// <summary>
    /// Coin gold (about RGB 215, 170, 60 on the face, darker on the rim): bright red and green, little blue. The teal
    /// rug, cream or red price digits, card frames and a dimmed coin all fail it.
    /// </summary>
    internal static bool IsGold(byte r, byte g, byte b) => r > 150 && g > 110 && b < 110 && r - b > 80 && r >= g;

    /// <summary>The red of the "used" cross.</summary>
    internal static bool IsRed(byte r, byte g, byte b) => r > 160 && g < 90 && b < 90;

    /// <summary>Nearest pixel of a reference point (BGRA). False outside the frame (a cropped or letterboxed capture).</summary>
    private static bool TrySample(in ScreenFrame frame, ReadOnlySpan<byte> px, RefTransform t, float refX, float refY,
        out byte r, out byte g, out byte b)
    {
        int x = (int)t.ToPixelX(refX + 0.5f), y = (int)t.ToPixelY(refY + 0.5f);
        if ((uint)x >= (uint)frame.Width || (uint)y >= (uint)frame.Height)
        {
            r = g = b = 0;
            return false;
        }
        int p = y * frame.Stride + x * 4;
        b = px[p];
        g = px[p + 1];
        r = px[p + 2];
        return true;
    }
}
