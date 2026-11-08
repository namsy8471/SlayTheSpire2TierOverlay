namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// <see cref="CardRemovalProbe"/> on synthetic shops: a flat gold disc (the coin) with a dark "torn card" in the
/// middle, and a small gold dot under it (the price label's coin icon). Real-capture accuracy is measured by
/// <c>VisionBench removal</c> (the captures cannot live in the repo).
/// </summary>
public class CardRemovalProbeTests
{
    private static readonly (byte R, byte G, byte B) Rug = (73, 106, 97), Gold = (215, 170, 60), Icon = (60, 40, 20), Cross = (210, 40, 40);

    /// <summary>Draws the service. <paramref name="cx"/>/<paramref name="cy"/> and the radius are reference units.</summary>
    private static Canvas Shop(int width, int height, bool coin = true, bool price = true, bool cross = false, bool dim = false,
        float cx = CardRemovalProbe.CenterX, float cy = CardRemovalProbe.CenterY, float radius = CardRemovalProbe.Radius)
    {
        var c = new Canvas(width, height);
        c.FillRef(new RectF(0, 0, 1920, 1080), Rug.R, Rug.G, Rug.B);
        if (coin)
        {
            Disc(c, cx, cy, radius, Gold);
            // The torn-card icon: a dark rectangle (the real one is crossed by gold lines, so it darkens less than its size).
            c.FillRef(RectF.FromCenter(cx, cy, 0.6f * radius, 0.8f * radius), Icon.R, Icon.G, Icon.B);
        }
        if (cross)
        {
            for (float d = -0.8f * radius; d <= 0.8f * radius; d += 1)
            {
                c.FillRef(RectF.FromCenter(cx + d, cy + d, 22, 22), Cross.R, Cross.G, Cross.B);
                c.FillRef(RectF.FromCenter(cx + d, cy - d, 22, 22), Cross.R, Cross.G, Cross.B);
            }
        }
        if (price) Disc(c, cx - 12, cy + 1.42f * radius, 13, Gold);
        if (dim)
        {
            // A modal on top halves every channel (the game's black backstop at ~50 %).
            for (int i = 0; i < c.Pixels.Length; i++)
                if (i % 4 != 3) c.Pixels[i] /= 2;
        }
        return c;
    }

    private static void Disc(Canvas c, float cx, float cy, float r, (byte R, byte G, byte B) col)
    {
        for (float y = cy - r; y <= cy + r; y += 0.5f)
        {
            float half = MathF.Sqrt(Math.Max(0, r * r - (y - cy) * (y - cy)));
            c.FillRef(new RectF(cx - half, y, 2 * half, 0.5f), col.R, col.G, col.B);
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1440)]
    public void Available_coin_with_price_is_found_at_any_resolution(int w, int h)
    {
        CardRemovalReading r = CardRemovalProbe.Read(Shop(w, h).Frame);
        Assert.True(r.Available, r.ToString());
        // The reported box is the coin, in frame pixels.
        RefTransform t = RefTransform.For(w, h);
        Assert.Equal(t.ToPixelRect(CardRemovalProbe.CoinRef), r.Bounds);
    }

    [Fact]
    public void Hovered_coin_still_counts()
    {
        // Hovering grows the coin about 1.2x and pushes the price label down.
        Canvas c = Shop(1920, 1080, price: false, radius: 1.2f * CardRemovalProbe.Radius);
        Disc(c, CardRemovalProbe.CenterX - 12, CardRemovalProbe.CenterY + 1.75f * CardRemovalProbe.Radius, 15, Gold);
        Assert.True(CardRemovalProbe.Read(c.Frame).Available);
    }

    [Fact]
    public void Used_service_with_the_red_cross_is_not_available()
    {
        CardRemovalReading r = CardRemovalProbe.Read(Shop(1920, 1080, cross: true).Frame);
        Assert.False(r.Available);
        Assert.True(r.FaceRed > CardRemovalProbe.MaxFaceRed, r.ToString());
    }

    [Fact]
    public void Coin_without_its_price_label_was_just_bought()
    {
        CardRemovalReading r = CardRemovalProbe.Read(Shop(1920, 1080, price: false).Frame);
        Assert.False(r.Available);
        Assert.True(r.FaceGold >= CardRemovalProbe.MinFaceGold, "the coin itself is still there");
        Assert.True(r.PriceIcon < CardRemovalProbe.MinPriceIcon);
    }

    [Theory]
    [InlineData(false, false)] // no coin at all: another screen
    [InlineData(true, true)]   // a modal dims the whole shop
    public void Missing_or_dimmed_coin_is_not_available(bool coin, bool dim)
        => Assert.False(CardRemovalProbe.Read(Shop(1920, 1080, coin: coin, price: coin, dim: dim).Frame).Available);
}
