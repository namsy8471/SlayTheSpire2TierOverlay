using Sts2Overlay.Vision.Relics;

namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// The merchant relic reader on synthetic screens: made-up "relics" (smooth random colour blobs on a transparent
/// square, no game art) drawn on the teal rug at the three layout slots, with price labels drawn from the committed
/// digit templates. Names are placeholders.
/// </summary>
public class RelicRecognizerTests
{
    private const float Rest = RelicRecognizer.SmallScale;

    /// <summary>An 85x85 straight-alpha icon: an ellipse (size depends on the seed) filled with a seeded texture.</summary>
    private static RgbaImage Icon(int seed)
    {
        const int n = 85;
        ScreenFrame tex = Canvas.Texture(seed, n, n);
        ReadOnlySpan<byte> src = tex.Bgra.Span;
        float rx = 28 + seed % 9, ry = 38 - seed % 7;
        byte[] rgba = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f - n / 2f) / rx, dy = (y + 0.5f - n / 2f) / ry;
                int p = (y * n + x) * 4, s = y * tex.Stride + x * 4;
                rgba[p] = src[s + 2];
                rgba[p + 1] = src[s + 1];
                rgba[p + 2] = src[s];
                rgba[p + 3] = dx * dx + dy * dy <= 1 ? (byte)255 : (byte)0;
            }
        return new RgbaImage(rgba, n, n);
    }

    private static string Id(int seed) => $"RELIC.R{seed}";

    /// <summary>Eight placeholder relics (seeds 1..8), all of the Shop rarity (base price 200).</summary>
    private static readonly RelicIconDatabase Db = RelicIconDatabase.FromImages(
        Enumerable.Range(1, 8).Select(s => (Id(s), "Shop", Icon(s))).ToList());

    private static Canvas Rug(int w, int h)
    {
        var c = new Canvas(w, h);
        c.FillRef(new RectF(900, 560, 480, 230), 75, 110, 98);
        return c;
    }

    /// <summary>Alpha-blends <paramref name="icon"/> into the icon square of slot <paramref name="slot"/> (nearest sample).</summary>
    private static void DrawRelic(Canvas c, int slot, RgbaImage icon, float scale = Rest)
    {
        (float ox, float oy) = RelicRecognizer.Origin(slot);
        RectF box = c.T.ToPixels(RelicRecognizer.IconAt(ox, oy, scale));
        DrawIcon(c, icon, box);
    }

    private static void DrawIcon(Canvas c, RgbaImage icon, RectF box)
    {
        for (int y = (int)box.Y; y < box.Bottom; y++)
            for (int x = (int)box.X; x < box.Right; x++)
            {
                int u = Math.Clamp((int)((x + 0.5f - box.X) / box.Width * icon.Width), 0, icon.Width - 1);
                int v = Math.Clamp((int)((y + 0.5f - box.Y) / box.Height * icon.Height), 0, icon.Height - 1);
                int q = (v * icon.Width + u) * 4;
                float a = icon.Rgba[q + 3] / 255f;
                if (a <= 0) continue;
                int p = (y * c.Width + x) * 4;
                byte Mix(int channel, int bgr) => (byte)(icon.Rgba[q + channel] * a + c.Pixels[p + bgr] * (1 - a) + 0.5f);
                c.Set(x, y, Mix(0, 2), Mix(1, 1), Mix(2, 0));
            }
    }

    private static void DrawRelicPrice(Canvas c, int slot, string digits, (byte R, byte G, byte B) colour, float scale = Rest)
    {
        (float ox, float oy) = RelicRecognizer.Origin(slot);
        ShopPriceTests.DrawPrice(c, digits, colour, ox, oy - ShopPriceReader.RelicLabelShift * scale, scale);
    }

    [Fact]
    public void Relics_follow_the_installed_merchant_scene_when_the_row_moves()
    {
        var c = Rug(1920, 1080);
        const float ox = 957, oy = 683;
        DrawIcon(c, Icon(5), c.T.ToPixels(RelicRecognizer.IconAt(ox, oy, Rest)));
        ShopPriceTests.DrawPrice(c, "199", ShopPriceTests.CreamText, ox, oy - ShopPriceReader.RelicLabelShift * Rest, Rest);
        var slot = Assert.Single(RelicRecognizer.ForRelics(Db, ox, oy).Recognize(c.Frame), s => s.Accepted);
        Assert.Equal(0, slot.Slot);
        Assert.Equal(Id(5), slot.RelicId);
        Assert.Equal(199, slot.Price);
    }

    [Theory]
    [InlineData(1920, 1080, 0.65f, false)]
    [InlineData(1280, 720, 0.8f, true)]
    public void Potion_shelf_reads_id_slot_current_price_and_affordability(int w, int h, float scale, bool red)
    {
        var c = new Canvas(w, h);
        c.FillRef(new RectF(900, 760, 480, 180), 75, 110, 98);
        float ox = 989 + 150, oy = 819;
        DrawIcon(c, Icon(3), c.T.ToPixels(new RectF(ox - 44 * scale, oy - 50 * scale, 90 * scale, 90 * scale)));
        ShopPriceTests.DrawPrice(c, "75", red ? ShopPriceTests.RedText : ShopPriceTests.CreamText, ox, oy - (249 - 74.7f) * scale, scale);
        var db = RelicIconDatabase.FromImages(Enumerable.Range(1, 4).Select(i => ($"POTION.TEST{i}", "", Icon(i))).ToArray());
        var slots = RelicRecognizer.ForPotions(db).Recognize(c.Frame);
        var potion = Assert.Single(slots, s => s.Accepted);
        Assert.Equal("POTION.TEST3", potion.RelicId);
        Assert.Equal(1, potion.Slot);
        Assert.Equal(75, potion.Price);
        Assert.Equal(red, potion.Unaffordable);
        Assert.True(potion.IconBounds.Width > 0);
        Assert.Equal(RelicSlotState.Empty, slots[0].State);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void Identifies_three_relics_and_reads_their_prices(int w, int h)
    {
        Canvas c = Rug(w, h);
        (int Seed, string Price)[] shown = [(3, "181"), (5, "199"), (8, "226")];
        for (int i = 0; i < 3; i++)
        {
            DrawRelic(c, i, Icon(shown[i].Seed));
            DrawRelicPrice(c, i, shown[i].Price, ShopPriceTests.CreamText);
        }
        IReadOnlyList<RecognizedRelic> got = new RelicRecognizer(Db).Recognize(c.Frame);
        Assert.Equal(3, got.Count);
        for (int i = 0; i < 3; i++)
        {
            RecognizedRelic r = got[i];
            Assert.True(r.State == RelicSlotState.Identified, r.ToString());
            Assert.Equal(Id(shown[i].Seed), r.RelicId);
            Assert.Equal("Shop", r.Rarity);
            Assert.Equal(int.Parse(shown[i].Price), r.Price);
            Assert.False(r.Unaffordable);
            Assert.True(r.Score > 0.95f && r.Margin >= 0.15f, r.ToString());
        }
    }

    [Fact]
    public void Red_label_marks_the_relic_unaffordable()
    {
        Canvas c = Rug(1920, 1080);
        DrawRelic(c, 1, Icon(2));
        DrawRelicPrice(c, 1, "210", ShopPriceTests.RedText);
        RecognizedRelic r = new RelicRecognizer(Db).Recognize(c.Frame)[1];
        Assert.Equal(RelicSlotState.Identified, r.State);
        Assert.Equal(210, r.Price);
        Assert.True(r.Unaffordable);
    }

    [Fact]
    public void A_relic_picture_without_its_price_label_is_not_named()
    {
        // Every merchant relic has a price under it; the same picture elsewhere (a dimmed shop behind the deck view,
        // a relic-like drawing on another screen) must not be reported.
        Canvas c = Rug(1920, 1080);
        DrawRelic(c, 0, Icon(4));
        RecognizedRelic r = new RelicRecognizer(Db).Recognize(c.Frame)[0];
        Assert.NotEqual(RelicSlotState.Identified, r.State);
        Assert.Equal(Id(4), r.Candidates[0].Id); // the picture itself still matched best

        // The check can be switched off (bench / debugging).
        RecognizedRelic loose = new RelicRecognizer(Db, new RelicRecognizerOptions { RequirePriceLabel = false }).Recognize(c.Frame)[0];
        Assert.Equal(RelicSlotState.Identified, loose.State);
        Assert.Null(loose.Price);
    }

    [Fact]
    public void Unknown_relic_is_not_identified_and_bare_rug_is_empty()
    {
        Canvas c = Rug(1920, 1080);
        DrawRelic(c, 0, Icon(40)); // not in the database
        DrawRelicPrice(c, 0, "205", ShopPriceTests.CreamText);
        // slot 1: bought (nothing drawn); slot 2: a known relic
        DrawRelic(c, 2, Icon(6));
        DrawRelicPrice(c, 2, "190", ShopPriceTests.CreamText);
        IReadOnlyList<RecognizedRelic> got = new RelicRecognizer(Db).Recognize(c.Frame);
        Assert.Equal(RelicSlotState.Unknown, got[0].State);
        Assert.Equal(RelicSlotState.Empty, got[1].State);
        Assert.True(got[1].RugShare >= 0.9f);
        Assert.Equal(RelicSlotState.Identified, got[2].State);
        Assert.Equal(Id(6), got[2].RelicId);
    }

    [Fact]
    public void Hovered_relic_is_found_at_the_bigger_scale()
    {
        Canvas c = Rug(1920, 1080);
        DrawRelic(c, 2, Icon(7), RelicRecognizer.HoverScale);
        DrawRelicPrice(c, 2, "222", ShopPriceTests.CreamText, RelicRecognizer.HoverScale);
        RecognizedRelic r = new RelicRecognizer(Db).Recognize(c.Frame)[2];
        Assert.Equal(RelicSlotState.Identified, r.State);
        Assert.Equal(Id(7), r.RelicId);
        Assert.InRange(r.NodeScale, 0.78f, 0.82f);
        Assert.Equal(222, r.Price);
    }

    [Fact]
    public void Nothing_is_identified_on_a_screen_without_the_rug()
    {
        var c = new Canvas(1920, 1080);
        c.DrawImageRef(new RectF(0, 0, 1920, 1080), Canvas.Texture(99, 640, 360));
        Assert.All(new RelicRecognizer(Db).Recognize(c.Frame), r => Assert.NotEqual(RelicSlotState.Identified, r.State));
    }

    // ---- RecognitionTracker: relic slots over frames ----

    private static RecognizedRelic Slot(int slot, RelicSlotState state, string? id = null, int? price = null) =>
        new(slot, state, id, id == null ? null : "Shop", 1, 1, Rest, default, default, price, []);

    private static ScreenRecognition ShopFrame(params RecognizedRelic[] relics) =>
        new(ScreenKind.Shop, [], [], new ScreenGate(false, true, ""), 0, "shop") { Relics = relics };

    [Fact]
    public void Tracker_keeps_covered_relics_drops_bought_ones_and_takes_restocks()
    {
        var tracker = new RecognitionTracker();
        var t0 = new DateTime(2026, 1, 1);
        tracker.Update(ShopFrame(Slot(0, RelicSlotState.Identified, "RELIC.A", 180), Slot(1, RelicSlotState.Identified, "RELIC.B", 200),
            Slot(2, RelicSlotState.Identified, "RELIC.C", 230)), t0);

        // A tooltip covers slot 1 and slot 2's label: keep slot 1, keep slot 2's last clear price.
        TrackedScreen s = tracker.Update(ShopFrame(Slot(0, RelicSlotState.Identified, "RELIC.A", 180), Slot(1, RelicSlotState.Unknown),
            Slot(2, RelicSlotState.Identified, "RELIC.C")), t0.AddSeconds(0.3));
        Assert.Equal(new[] { "RELIC.A", "RELIC.B", "RELIC.C" }, s.Relics.Select(r => r.RelicId));
        Assert.Equal(230, s.Relics[2].Price);

        // Slot 0 bought: the node is hidden.
        s = tracker.Update(ShopFrame(Slot(0, RelicSlotState.Empty), Slot(1, RelicSlotState.Identified, "RELIC.B", 200),
            Slot(2, RelicSlotState.Identified, "RELIC.C", 230)), t0.AddSeconds(0.6));
        Assert.Equal(new[] { "RELIC.B", "RELIC.C" }, s.Relics.Select(r => r.RelicId));

        // The Courier restocks slot 0 with a new relic.
        s = tracker.Update(ShopFrame(Slot(0, RelicSlotState.Identified, "RELIC.D", 150)), t0.AddSeconds(0.9));
        Assert.Equal(new[] { "RELIC.D", "RELIC.B", "RELIC.C" }, s.Relics.Select(r => r.RelicId));
    }
}
