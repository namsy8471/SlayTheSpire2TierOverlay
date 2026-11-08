namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// Shop price reading on synthetic labels: digits are drawn from the committed templates themselves (no game
/// images), with a dark outline, a gold coin on the left and the teal rug behind, where the game puts them.
/// </summary>
public class ShopPriceTests
{
    // Slot 0 of the shop layout, resting scale.
    private const float Cx = 437f, Cy = 383f, Scale = 0.65f;

    internal static readonly (byte R, byte G, byte B) CreamText = (255, 246, 226), RedText = (255, 85, 85), GreenText = (127, 255, 0);

    /// <summary>
    /// Kreon Bold's digit advance (+ spacing_glyph 2) / digit height: proportional figures, unlike GyeonggiBatang's
    /// fixed 0.70 (TitleBench digits).
    /// </summary>
    private static float KreonPitch(char d) => d switch
    {
        '0' => 0.94f, '1' => 0.61f, '2' => 0.86f, '3' => 0.84f, '4' => 0.86f, '5' => 0.83f, '6' => 0.89f, '7' => 0.74f, _ => 0.87f,
    };

    /// <summary>
    /// Draws the price label of the card at (cx, cy, scale) like merchant_card.tscn does, in the font of
    /// <paramref name="glyphs"/> (default: the Korean game's GyeonggiBatang set).
    /// </summary>
    internal static void DrawPrice(Canvas canvas, string digits, (byte R, byte G, byte B) color, float cx = Cx, float cy = Cy, float scale = Scale,
        PriceGlyphs? glyphs = null, Func<char, float>? pitch = null)
    {
        glyphs ??= PriceGlyphs.Default;
        pitch ??= _ => 0.70f;                                            // digit pitch (measured on captures)
        RefTransform t = canvas.T;
        float h = ShopPriceReader.DigitHeight * scale * t.Scale;       // digit height in pixels
        float blockCx = t.ToPixelX(cx + ShopPriceReader.DigitBlockCenterX * scale);
        float blockCy = t.ToPixelY(cy + ShopPriceReader.DigitCenterY * scale);
        float total = digits.Sum(d => pitch(d)) * h;
        float left = blockCx - total / 2;
        float top = blockCy - h / 2;

        // rug
        canvas.FillPixels(new RectF(blockCx - 4 * h, top - h, 8 * h, 3 * h), 62, 104, 98);
        // coin: a gold disc just left of the first digit
        float coinR = 0.62f * h, coinCx = left - 0.3f * h - coinR, coinCy = blockCy;
        for (int y = (int)(coinCy - coinR); y <= coinCy + coinR; y++)
            for (int x = (int)(coinCx - coinR); x <= coinCx + coinR; x++)
                if ((x - coinCx) * (x - coinCx) + (y - coinCy) * (y - coinCy) <= coinR * coinR) canvas.Set(x, y, 222, 178, 48);

        // digits: the template grid, bilinearly scaled to the digit size and thresholded at half coverage. Each
        // glyph is placed so that its own bounding box is centred on its pitch position (the reader centres the
        // sampling window on the glyph's box too).
        var mask = new bool[canvas.Width, canvas.Height];
        float ww = PriceGlyphs.Aspect * h;
        float pos = left;
        for (int i = 0; i < digits.Length; i++)
        {
            float advance = pitch(digits[i]) * h;
            float[] g = glyphs.Raw[digits[i] - '0'];
            int c0 = PriceGlyphs.Width, c1 = -1;
            for (int k = 0; k < g.Length; k++)
                if (g[k] >= 0.5f)
                {
                    c0 = Math.Min(c0, k % PriceGlyphs.Width);
                    c1 = Math.Max(c1, k % PriceGlyphs.Width);
                }
            float boxCenter = (c0 + c1 + 1) / 2f / PriceGlyphs.Width * ww; // glyph box centre inside the window
            float wx0 = pos + 0.5f * advance - boxCenter;
            pos += advance;
            for (int y = (int)top; y < top + h; y++)
                for (int x = (int)wx0; x < wx0 + ww; x++)
                {
                    float gx = (x + 0.5f - wx0) / ww * PriceGlyphs.Width - 0.5f, gy = (y + 0.5f - top) / h * PriceGlyphs.Height - 0.5f;
                    if (Bilinear(g, gx, gy) >= 0.5f) mask[x, y] = true;
                }
        }
        int outline = Math.Max(1, (int)MathF.Round(h / 6));
        for (int y = (int)top - outline; y < top + h + outline; y++)
            for (int x = (int)left - outline; x < left + total + outline; x++)
            {
                if (mask[x, y]) canvas.Set(x, y, color.R, color.G, color.B);
                else if (Near(mask, x, y, outline)) canvas.Set(x, y, 25, 20, 20);
            }
    }

    private static float Bilinear(float[] g, float x, float y)
    {
        x = Math.Clamp(x, 0, PriceGlyphs.Width - 1);
        y = Math.Clamp(y, 0, PriceGlyphs.Height - 1);
        int x0 = Math.Min((int)x, PriceGlyphs.Width - 2), y0 = Math.Min((int)y, PriceGlyphs.Height - 2);
        float fx = x - x0, fy = y - y0;
        float V(int xx, int yy) => g[yy * PriceGlyphs.Width + xx];
        return (1 - fy) * ((1 - fx) * V(x0, y0) + fx * V(x0 + 1, y0)) + fy * ((1 - fx) * V(x0, y0 + 1) + fx * V(x0 + 1, y0 + 1));
    }

    private static bool Near(bool[,] m, int x, int y, int r)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < m.GetLength(0) && ny < m.GetLength(1) && m[nx, ny]) return true;
            }
        return false;
    }

    [Theory]
    [InlineData(1920, 1080, "49", CardSlotKind.ShopCharacter)]
    [InlineData(1920, 1080, "146", CardSlotKind.ShopCharacter)]
    [InlineData(1920, 1080, "175", CardSlotKind.ShopColorless)]
    [InlineData(3840, 2160, "73", CardSlotKind.ShopCharacter)]
    [InlineData(3840, 2160, "82", CardSlotKind.ShopColorless)]
    [InlineData(3842, 2160, "150", CardSlotKind.ShopCharacter)]
    public void Reads_synthetic_labels_at_1080p_and_4K(int w, int h, string digits, CardSlotKind kind)
    {
        var canvas = new Canvas(w, h);
        DrawPrice(canvas, digits, CreamText);
        PriceReading? r = new ShopPriceReader().Read(canvas.Frame, Cx, Cy, Scale, kind);
        Assert.NotNull(r);
        var row = ShopPriceReader.Segment(canvas.Frame, Cx, Cy, Scale)!;
        Assert.True(r.Value != null, $"{r} x={row.CenterX:F1} coin={row.CoinFraction:F2} occl={row.OccluderFraction:F3} widths={string.Join(",", row.Widths.Select(x => x.ToString("F3")))}");
        Assert.Equal(digits, r.Text);
        Assert.Equal(int.Parse(digits), r.Value);
        Assert.Equal(PriceTint.Cream, r.Tint);
        Assert.True(r.Plausible);
    }

    [Theory]
    [InlineData(1920, 1080, "49", CardSlotKind.ShopCharacter)]
    [InlineData(1920, 1080, "146", CardSlotKind.ShopCharacter)]
    [InlineData(1920, 1080, "175", CardSlotKind.ShopColorless)]
    [InlineData(3840, 2160, "74", CardSlotKind.ShopCharacter)]
    [InlineData(3840, 2160, "82", CardSlotKind.ShopColorless)]
    [InlineData(3842, 2160, "150", CardSlotKind.ShopCharacter)]
    public void Reads_english_kreon_labels(int w, int h, string digits, CardSlotKind kind)
    {
        // An English game keeps CostLabel's Kreon Bold: wider "0"/"2"/"4" and an open "4" that the Korean set reads poorly.
        var canvas = new Canvas(w, h);
        DrawPrice(canvas, digits, CreamText, glyphs: PriceGlyphs.Kreon, pitch: KreonPitch);
        PriceReading? r = new ShopPriceReader().Read(canvas.Frame, Cx, Cy, Scale, kind);
        Assert.NotNull(r);
        Assert.True(r.Value != null, r.ToString());
        Assert.Equal(int.Parse(digits), r.Value);
        Assert.Equal("kreon", r.Font);
    }

    [Fact]
    public void Kreon_labels_need_the_kreon_set()
    {
        // "74" in Kreon: Kreon's open "4" matches the Korean "4" template at NCC ~0.5, below MinDigitScore, so a
        // Korean-only reader leaves the price unknown; with both sets the Kreon set wins clearly.
        var canvas = new Canvas(1920, 1080);
        DrawPrice(canvas, "74", CreamText, glyphs: PriceGlyphs.Kreon, pitch: KreonPitch);
        PriceReading? korean = new ShopPriceReader(PriceGlyphs.Default).Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopCharacter);
        Assert.Null(korean?.Value);
        Assert.True(korean!.MinScore < new PriceReaderOptions().MinDigitScore);
        PriceReading? both = new ShopPriceReader().Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopCharacter);
        Assert.Equal(74, both?.Value);
        Assert.Equal("kreon", both!.Font);
        Assert.True(both.FontGap >= new PriceReaderOptions().MinFontGap);
    }

    [Fact]
    public void Hovered_card_label_is_bigger_and_lower_and_still_read()
    {
        var canvas = new Canvas(1920, 1080);
        DrawPrice(canvas, "78", CreamText, Cx, Cy, 0.8f);
        Assert.Equal(78, new ShopPriceReader().Read(canvas.Frame, Cx, Cy, 0.8f, CardSlotKind.ShopCharacter)?.Value);
    }

    [Fact]
    public void Red_and_green_labels_report_affordability_and_sale()
    {
        var red = new Canvas(1920, 1080);
        DrawPrice(red, "172", RedText);
        PriceReading? r = new ShopPriceReader().Read(red.Frame, Cx, Cy, Scale, CardSlotKind.ShopColorless);
        Assert.Equal(172, r?.Value);
        Assert.True(r!.Unaffordable);

        var green = new Canvas(1920, 1080);
        DrawPrice(green, "36", GreenText);
        PriceReading? g = new ShopPriceReader().Read(green.Frame, Cx, Cy, Scale, CardSlotKind.ShopCharacter);
        Assert.Equal(36, g?.Value);
        Assert.True(g!.OnSale);
        Assert.False(g.Unaffordable);
    }

    [Fact]
    public void Impossible_numbers_are_returned_as_text_but_not_as_a_price()
    {
        var canvas = new Canvas(1920, 1080);
        DrawPrice(canvas, "99", CreamText); // no rarity/sale/relic combination gives 99
        PriceReading? r = new ShopPriceReader().Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopCharacter);
        Assert.NotNull(r);
        Assert.Equal("99", r.Text);
        Assert.False(r.Plausible);
        Assert.Null(r.Value);
    }

    [Fact]
    public void Rarity_tightens_the_check()
    {
        var canvas = new Canvas(1920, 1080);
        DrawPrice(canvas, "73", CreamText);
        var reader = new ShopPriceReader();
        Assert.Equal(73, reader.Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopCharacter, "Uncommon")?.Value);
        Assert.Null(reader.Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopCharacter, "Common")?.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_hand_over_the_first_digit_gives_unknown_not_a_shorter_number(bool coverCoin)
    {
        // "146" with the "1" hidden would read "46", a price the rules cannot exclude without rarity.
        var canvas = new Canvas(1920, 1080);
        DrawPrice(canvas, "146", CreamText);
        PriceReading? clean = new ShopPriceReader().Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopCharacter);
        Assert.Equal(146, clean?.Value);
        PixelRect b = clean!.Bounds;
        float x0 = coverCoin ? b.X - 2f * b.Height : b.X - 1;
        canvas.FillPixels(new RectF(x0, b.Y - 6, b.X + b.Width / 3f + 1 - x0, b.Height + 12), 140, 200, 225);
        PriceReading? covered = new ShopPriceReader().Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopCharacter);
        Assert.Null(covered?.Value);
    }

    [Fact]
    public void A_dark_panel_over_half_of_the_last_digit_gives_unknown()
    {
        var canvas = new Canvas(1920, 1080);
        DrawPrice(canvas, "172", RedText);
        PriceReading? clean = new ShopPriceReader().Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopColorless);
        Assert.Equal(172, clean?.Value);
        PixelRect b = clean!.Bounds;
        canvas.FillPixels(new RectF(b.Right - b.Width / 6f, b.Y - 6, b.Width, b.Height + 12), 30, 34, 40);
        Assert.Null(new ShopPriceReader().Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopColorless)?.Value);
    }

    [Fact]
    public void Empty_slot_reads_nothing()
    {
        var canvas = new Canvas(1920, 1080);
        canvas.FillRef(new RectF(0, 0, 1920, 1080), 62, 104, 98);
        Assert.Null(new ShopPriceReader().Read(canvas.Frame, Cx, Cy, Scale, CardSlotKind.ShopCharacter));
    }

    [Theory]
    [InlineData(255, 246, 226, 0)] // cream
    [InlineData(255, 85, 85, 1)]   // red
    [InlineData(127, 255, 0, 2)]   // green
    [InlineData(222, 178, 48, 3)]  // coin gold
    [InlineData(140, 200, 225, 4)] // the merchant's hand
    [InlineData(62, 104, 98, -1)]  // rug
    [InlineData(165, 80, 40, -1)]  // the coin's brown emblem must not look like red text
    public void Pixel_classes(int r, int g, int b, int expected) => Assert.Equal(expected, ShopPriceReader.ClassOf(r, g, b));

    [Theory]
    [InlineData("gyeonggi")]
    [InlineData("kreon")]
    public void Templates_round_trip_and_each_digit_matches_itself_best(string font)
    {
        PriceGlyphs glyphs = PriceGlyphs.All.Single(g => g.Name == font);
        Assert.Equal(font == "kreon" ? PriceGlyphs.KreonData : PriceGlyphs.Data, glyphs.Format());
        for (int d = 0; d < 10; d++)
        {
            (int got, float score, float margin) = glyphs.Classify(glyphs.Raw[d]);
            Assert.Equal(d, got);
            Assert.True(score > 0.999f);
            Assert.True(margin > 0.1f, $"digit {d} is too close to another template ({margin:F3})");
        }
    }
}

public class ShopPriceRulesTests
{
    [Theory]
    [InlineData("Common", false, 50)]
    [InlineData("Uncommon", false, 75)]
    [InlineData("Rare", false, 150)]
    [InlineData("Uncommon", true, 86)]
    [InlineData("Rare", true, 172)]
    [InlineData("Basic", false, null)]
    [InlineData(null, false, null)]
    public void Base_prices(string? rarity, bool colorless, int? expected) => Assert.Equal(expected, ShopPriceRules.BasePrice(rarity, colorless));

    [Fact]
    public void Ranges_include_jitter_and_sale()
    {
        Assert.Equal((48, 52), ShopPriceRules.Range(50, onSale: false));
        Assert.Equal((71, 79), ShopPriceRules.Range(75, onSale: false));
        Assert.Equal((142, 158), ShopPriceRules.Range(150, onSale: false));
        Assert.Equal((24, 26), ShopPriceRules.Range(50, onSale: true));
        Assert.Equal((163, 181), ShopPriceRules.Range(172, onSale: false));
    }

    [Theory]
    // every price hand-labelled on the real captures (12 shop visits) must pass
    [InlineData(49, CardSlotKind.ShopCharacter)]
    [InlineData(24, CardSlotKind.ShopCharacter)]
    [InlineData(39, CardSlotKind.ShopCharacter)]
    [InlineData(79, CardSlotKind.ShopCharacter)]
    [InlineData(143, CardSlotKind.ShopCharacter)]
    [InlineData(82, CardSlotKind.ShopColorless)]
    [InlineData(178, CardSlotKind.ShopColorless)]
    public void Real_prices_are_plausible(int price, CardSlotKind kind) => Assert.True(ShopPriceRules.IsPlausible(price, kind));

    [Theory]
    [InlineData(99, CardSlotKind.ShopCharacter)]
    [InlineData(44, CardSlotKind.ShopCharacter)]
    [InlineData(5, CardSlotKind.ShopCharacter)]
    [InlineData(50, CardSlotKind.ShopColorless)]   // colourless cards are never common
    [InlineData(36, CardSlotKind.ShopColorless)]   // and never on sale (sale is a character card)
    [InlineData(50, CardSlotKind.Reward)]
    public void Impossible_prices_are_rejected(int price, CardSlotKind kind) =>
        Assert.False(ShopPriceRules.IsPlausible(price, kind, factors: [1.0]));

    [Fact]
    public void Relic_discounts_widen_the_range_only_when_allowed()
    {
        // Membership Card: a colourless rare (172) costs 81..90
        Assert.True(ShopPriceRules.IsPlausible(86, CardSlotKind.ShopColorless, "Rare"));
        Assert.False(ShopPriceRules.IsPlausible(86, CardSlotKind.ShopColorless, "Rare", [1.0]));
    }

    [Theory]
    [InlineData("Common", 148, 202)]   // 175 × 0.85..1.15 (floor / ceil)
    [InlineData("Uncommon", 191, 259)] // 225
    [InlineData("Rare", 233, 317)]     // 275
    [InlineData("Shop", 170, 230)]     // 200
    public void Relic_ranges_by_rarity(string rarity, int lo, int hi)
    {
        Assert.Equal((lo, hi), ShopPriceRules.RelicRange(ShopPriceRules.RelicBasePrice(rarity)!.Value));
        Assert.True(ShopPriceRules.IsPlausibleRelic(lo, rarity, [1.0]));
        Assert.True(ShopPriceRules.IsPlausibleRelic(hi, rarity, [1.0]));
        Assert.False(ShopPriceRules.IsPlausibleRelic(lo - 1, rarity, [1.0]));
        Assert.False(ShopPriceRules.IsPlausibleRelic(hi + 1, rarity, [1.0]));
    }

    [Fact]
    public void Relic_prices_allow_the_discount_relics_and_unknown_rarity()
    {
        Assert.True(ShopPriceRules.IsPlausibleRelic(80, "Shop"));          // Membership Card + The Courier: 200 × 0.4
        Assert.False(ShopPriceRules.IsPlausibleRelic(80, "Shop", [1.0]));
        Assert.True(ShopPriceRules.IsPlausibleRelic(300));                  // a rare, rarity unknown
        Assert.False(ShopPriceRules.IsPlausibleRelic(400));
        Assert.Null(ShopPriceRules.RelicBasePrice("Ancient"));              // never sold by the merchant
    }
}
