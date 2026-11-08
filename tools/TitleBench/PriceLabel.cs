using SkiaSharp;
using Sts2Overlay.Vision;

namespace Sts2Overlay.Tools.TitleBench;

/// <summary>
/// The shop price under a card as scenes/merchant/merchant_card.tscn draws it: an HBoxContainer "Cost" at card
/// x -148..149, y 220..278 (alignment centre, separation 6) holding the 54 x 54 gold coin and %CostLabel, a MegaLabel
/// with kreon_bold_glyph_space_two (Kreon Bold, spacing_glyph 2), size 39, cream, outline_size 15 in
/// Color(0.067, 0, 0, 0.43). On a Korean game the label font is swapped for GyeonggiBatang Bold (no glyph spacing).
/// </summary>
internal static class PriceLabel
{
    public const float BoxLeft = -148, BoxRight = 149, Coin = 54, Separation = 6, FontSize = 39, OutlineSize = 15;
    public static readonly SKColor Cream = new(0xFF, 0xF6, 0xE2), Outline = new(17, 0, 0), Rug = new(62, 104, 98), Gold = new(222, 178, 48);
    public const float OutlineAlpha = 0.431f;

    /// <summary>The digits' glyph outlines in card units relative to the card centre, ink centred on y 249.</summary>
    public static SKPath Layout(TitleFont font, string digits, out float coinLeft)
    {
        float labelW = TitleLayout.LineWidth(font, digits, (int)FontSize, out float[] adv, out var shaped);
        float groupW = Coin + Separation + labelW;
        coinLeft = (BoxLeft + BoxRight) / 2 - groupW / 2;
        float x = coinLeft + Coin + Separation;
        var path = new SKPath();
        for (int i = 0; i < shaped.G.Length; i++)
        {
            using SKPath g = font.GlyphPath(shaped.G[i], FontSize * font.Transform);
            g.Offset(x + font.Px(shaped.Ox[i], FontSize), 0);
            path.AddPath(g);
            x += adv[i];
        }
        // Lining figures all sit on the baseline: centre the ink box vertically on the label's middle line.
        SKRect b = path.TightBounds;
        path.Offset(0, ShopPriceReader.DigitCenterY - (b.Top + b.Bottom) / 2);
        return path;
    }

    /// <summary>Draws rug, coin and digits of the card at (cx, cy) with node scale onto the canvas; returns the touched region.</summary>
    public static PixelRect Draw(TitleCanvas canvas, TitleFont font, string digits, float cx, float cy, float scale)
    {
        RefTransform t = canvas.T;
        PixelRect region = t.ToPixelRect(CardGeometry.Place(new RectF(-160, 200, 320, 100), cx, cy, scale)).Intersect(canvas.Width, canvas.Height);
        canvas.Fill(region, Rug);
        using SKPath path = Layout(font, digits, out float coinLeft);
        float k = scale * t.Scale;
        // coin: a gold disc in its 54 x 54 box (the texture is a round coin with a darker rim; only "gold" counts)
        float ccx = t.ToPixelX(cx + (coinLeft + Coin / 2) * scale), ccy = t.ToPixelY(cy + ShopPriceReader.DigitCenterY * scale), rad = 0.42f * Coin * k;
        for (int y = region.Y; y < region.Bottom; y++)
            for (int x = region.X; x < region.Right; x++)
                if ((x + 0.5f - ccx) * (x + 0.5f - ccx) + (y + 0.5f - ccy) * (y + 0.5f - ccy) <= rad * rad)
                {
                    int p = (y * canvas.Width + x) * 4;
                    canvas.Pixels[p] = Gold.Blue;
                    canvas.Pixels[p + 1] = Gold.Green;
                    canvas.Pixels[p + 2] = Gold.Red;
                }
        var origin = new RectF(t.ToPixelX(cx), t.ToPixelY(cy), 0, 0); // label units = card units around the card centre
        canvas.DrawLabel(path, origin, k, region, Cream, Outline, OutlineSize / 4, OutlineAlpha);
        return region;
    }
}
