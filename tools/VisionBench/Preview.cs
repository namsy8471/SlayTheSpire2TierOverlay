using System.Globalization;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using Sts2Overlay.App.Views;
using Sts2Overlay.Vision;

namespace VisionBench;

/// <summary>
/// Offline badge preview: recognises a capture, places the badges with the App's own <see cref="BadgeLayout"/>
/// (the source file is linked into this tool) and draws them over the capture the way the overlay's badge window
/// would, plus the debug outlines (cards blue, price labels orange). Lets badge placement be checked at 4K/150 % and
/// FHD/100 % without running the overlay over a game.
///
/// <para>The badge content is a stand-in: the tier letter comes from the data pack, the score is a fixed number per
/// tier and the best tier gets the crown. Only the geometry is the App's.</para>
/// </summary>
internal static class Preview
{
    /// <summary>A display setup: how big the game frame is and the Windows DPI scale the badge layer runs at.</summary>
    public sealed record Profile(string Name, int Upscale, double Dpi);

    public static readonly Profile[] Profiles = [new("4k-150", 2, 1.5), new("fhd-100", 1, 1.0)];

    public static string Run(IReadOnlyList<string> captures, CardRecognizer rec, Func<ScreenFrame, int, ScreenFrame> upscale,
        IReadOnlyDictionary<string, string> tiers, string outDir, Action<string> log)
    {
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder("# Badge preview\n\nBadges placed by Sts2Overlay.App.Views.BadgeLayout (linked source). " +
                                       "Checks: badge inside the screen, not over another badge, another card or a price label.\n\n");
        int problems = 0;
        foreach (string path in captures)
        {
            ScreenFrame original = ImageLoader.Load(path);
            foreach (Profile p in Profiles)
            {
                ScreenFrame frame = p.Upscale > 1 ? upscale(original, p.Upscale) : original;
                ScreenRecognition r = rec.Recognize(frame);
                RecognizedCard[] cards = r.Cards.ToArray();
                var layer = new PxBox(0, 0, frame.Width, frame.Height);
                BadgeFrame layout = BadgeLayout.Compute(
                    cards.Select(c => Px(c.CardBounds)).ToArray(),
                    cards.Select(c => c.PriceBounds is { } pb ? Px(pb) : (PxBox?)null).ToArray(),
                    layer, p.Dpi, multiplier: 1, skip: false, outlines: true);

                string[] tierOf = cards.Select(c => tiers.GetValueOrDefault(c.CardId!, "?")).ToArray();
                int best = Array.IndexOf(tierOf, tierOf.OrderBy(TierRank).FirstOrDefault());
                List<string> issues = Check(layout, layer.Width / p.Dpi, layer.Height / p.Dpi);
                problems += issues.Count;

                string name = $"{Path.GetFileNameWithoutExtension(path)}-{p.Name}.png";
                Render(frame, layout, cards, tierOf, best, p.Dpi, Path.Combine(outDir, name));
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"- `{name}`: {r.Kind}, {cards.Length} cards, badge scale {(layout.Badges.Count > 0 ? layout.Badges.Average(b => b.Scale) : 0):F2}" +
                    $" ({(layout.Badges.Count > 0 ? layout.Badges[0].Box.Width * p.Dpi : 0):F0} px wide){(issues.Count == 0 ? ", no overlap" : "")}");
                foreach (string i in issues) report.AppendLine(CultureInfo.InvariantCulture, $"  - {i}");
                log($"{name}: {r.Kind} {cards.Length} cards, {issues.Count} issues");
            }
        }
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Problems: {problems}");
        File.WriteAllText(Path.Combine(outDir, "preview.md"), report.ToString());
        return report.ToString();
    }

    /// <summary>Badge placement problems (layer DIPs): off screen, over another badge, card or price label.</summary>
    public static List<string> Check(BadgeFrame f, double areaWidth, double areaHeight)
    {
        var issues = new List<string>();
        for (int i = 0; i < f.Badges.Count; i++)
        {
            DipRect b = f.Badges[i].Box;
            if (b.X < 0 || b.Y < 0 || b.Right > areaWidth || b.Bottom > areaHeight) issues.Add($"badge {i} leaves the screen");
            for (int j = i + 1; j < f.Badges.Count; j++)
                if (b.Intersects(f.Badges[j].Box)) issues.Add($"badge {i} overlaps badge {j}");
            for (int j = 0; j < f.CardOutlines.Count; j++)
                if (j != i && b.Intersects(f.CardOutlines[j])) issues.Add($"badge {i} covers card {j}");
            for (int j = 0; j < f.PriceOutlines.Count; j++)
                if (b.Intersects(f.PriceOutlines[j])) issues.Add($"badge {i} covers price label {j}");
        }
        return issues;
    }

    /// <summary>Tier letter per card id from the data pack's tier files (comments allowed).</summary>
    public static Dictionary<string, string> LoadTiers(string tiersDir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(tiersDir)) return map;
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (string f in Directory.GetFiles(tiersDir, "*.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f), options);
            if (!doc.RootElement.TryGetProperty("cards", out JsonElement cards)) continue;
            foreach (JsonProperty c in cards.EnumerateObject())
                if (c.Value.ValueKind == JsonValueKind.Object && c.Value.TryGetProperty("tier", out JsonElement t) && t.GetString() is { } tier)
                    map.TryAdd(c.Name, tier);
        }
        return map;
    }

    private static int TierRank(string t) => t switch { "S" => 0, "A" => 1, "B" => 2, "C" => 3, "D" => 4, _ => 5 };

    private static string ScoreFor(string t) => t switch { "S" => "92", "A" => "81", "B" => "68", "C" => "54", "D" => "38", _ => "" };

    private static PxBox Px(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    // ------------------------------------------------------------------ drawing (mirrors BadgeWindow.xaml)

    private static SKColor TierColor(string t) => t switch
    {
        "S" => new SKColor(0xF1, 0xC4, 0x0F),
        "A" => new SKColor(0x9B, 0x59, 0xB6),
        "B" => new SKColor(0x34, 0x98, 0xDB),
        "C" => new SKColor(0x95, 0xA5, 0xA6),
        "D" => new SKColor(0x8E, 0x2B, 0x2B),
        _ => new SKColor(0x55, 0x5A, 0x64),
    };

    private static readonly SKColor Gold = new(0xF1, 0xC4, 0x0F);

    private static void Render(ScreenFrame frame, BadgeFrame layout, RecognizedCard[] cards, string[] tiers, int best, double dpi, string outPath)
    {
        using var bmp = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        ReadOnlySpan<byte> src = frame.Bgra.Span;
        Span<byte> dst = bmp.GetPixelSpan();
        for (int y = 0; y < frame.Height; y++)
            src.Slice(y * frame.Stride, frame.Width * 4).CopyTo(dst.Slice(y * bmp.RowBytes, frame.Width * 4));

        using var canvas = new SKCanvas(bmp);
        float k = (float)dpi; // DIP → physical px
        using var dash = SKPathEffect.CreateDash([4 * k, 3 * k], 0);
        foreach (DipRect r in layout.CardOutlines)
            DrawOutline(canvas, r, k, new SKColor(0x5D, 0xAD, 0xE2, 0xAA), new SKColor(0x5D, 0xAD, 0xE2, 0x14), 8, dash);
        foreach (DipRect r in layout.PriceOutlines)
            DrawOutline(canvas, r, k, new SKColor(0xF3, 0x9C, 0x12, 0xCC), new SKColor(0xF3, 0x9C, 0x12, 0x18), 3, dash);

        using SKTypeface face = SKTypeface.FromFamilyName("Malgun Gothic", SKFontStyle.Bold) ?? SKTypeface.Default;
        for (int i = 0; i < layout.Badges.Count; i++)
            DrawBadge(canvas, layout.Badges[i], k, tiers[i], ScoreFor(tiers[i]), i == best, cards[i].Upgraded, face);

        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 90);
        using FileStream fs = File.Create(outPath);
        data.SaveTo(fs);
    }

    private static void DrawOutline(SKCanvas c, DipRect r, float k, SKColor stroke, SKColor fill, float radius, SKPathEffect dash)
    {
        var rect = new SKRect((float)r.X * k, (float)r.Y * k, (float)r.Right * k, (float)r.Bottom * k);
        using var f = new SKPaint { Color = fill, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var s = new SKPaint { Color = stroke, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * k, PathEffect = dash, IsAntialias = true };
        c.DrawRoundRect(rect, radius * k, radius * k, f);
        c.DrawRoundRect(rect, radius * k, radius * k, s);
    }

    /// <summary>The 170x40 pill of BadgeWindow.xaml, scaled by the badge scale and the DPI.</summary>
    private static void DrawBadge(SKCanvas c, PlacedBadge b, float k, string tier, string score, bool isBest, bool upgraded, SKTypeface face)
    {
        float s = (float)b.Scale * k; // design unit → physical px
        float x = (float)b.Box.X * k, y = (float)b.Box.Y * k;
        float border = isBest ? 3 : 2;
        var pill = new SKRect(x, y, x + 170 * s, y + 40 * s);
        using (var bg = new SKPaint { Color = isBest ? new SKColor(0x23, 0x1E, 0x0C, 0xF2) : new SKColor(0x15, 0x16, 0x1C, 0xEC), IsAntialias = true })
            c.DrawRoundRect(pill, 9 * s, 9 * s, bg);
        using (var bd = new SKPaint { Color = isBest ? Gold : TierColor(tier), Style = SKPaintStyle.Stroke, StrokeWidth = border * s, IsAntialias = true })
            c.DrawRoundRect(Inset(pill, border * s / 2), 9 * s, 9 * s, bd);

        // Tier tile: 31x31 at the left (border + margin 3), vertically centred.
        float tx = x + (border + 3) * s, ty = y + (40 - 31) / 2f * s;
        var tile = new SKRect(tx, ty, tx + 31 * s, ty + 31 * s);
        using (var tp = new SKPaint { Color = TierColor(tier), IsAntialias = true }) c.DrawRoundRect(tile, 6 * s, 6 * s, tp);
        using var font = new SKFont(face, 21 * s);
        using var text = new SKPaint { Color = tier is "S" or "C" ? new SKColor(0x1A, 0x1A, 0x1A) : SKColors.White, IsAntialias = true };
        DrawCentered(c, tier, tile, font, text);

        // Score (+ upgrade "+").
        using var scoreFont = new SKFont(face, 20 * s);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float sx = tile.Right + 7 * s, baseline = y + 20 * s + scoreFont.Size * 0.35f;
        c.DrawText(score, sx, baseline, SKTextAlign.Left, scoreFont, white);
        if (upgraded)
        {
            using var green = new SKPaint { Color = new SKColor(0x58, 0xD6, 0x8D), IsAntialias = true };
            c.DrawText("+", sx + scoreFont.MeasureText(score) + 2 * s, baseline, SKTextAlign.Left, scoreFont, green);
        }
        if (isBest)
        {
            using var small = new SKFont(face, 13 * s);
            using var gold = new SKPaint { Color = Gold, IsAntialias = true };
            float right = pill.Right - (border + 6) * s;
            float textLeft = right - small.MeasureText("추천");
            c.DrawText("추천", right, y + 20 * s + small.Size * 0.35f, SKTextAlign.Right, small, gold);
            // App.xaml's CrownGeometry (16x14 design units), drawn 17x15 to the left of the text.
            using SKPath crown = SKPath.ParseSvgPathData("M0,11 L1.5,3 L5,7 L8,0 L11,7 L14.5,3 L16,11 Z M0.5,12.5 L15.5,12.5 L15.5,14 L0.5,14 Z");
            c.Save();
            c.Translate(textLeft - 3 * s - 17 * s, y + (40 - 15) / 2f * s);
            c.Scale(17f / 16 * s, 15f / 14 * s);
            c.DrawPath(crown, gold);
            c.Restore();
        }
    }

    private static SKRect Inset(SKRect r, float d) => new(r.Left + d, r.Top + d, r.Right - d, r.Bottom - d);

    private static void DrawCentered(SKCanvas c, string s, SKRect box, SKFont font, SKPaint paint)
    {
        font.MeasureText(s, out SKRect bounds);
        c.DrawText(s, box.MidX - bounds.MidX, box.MidY - bounds.MidY, SKTextAlign.Left, font, paint);
    }
}
