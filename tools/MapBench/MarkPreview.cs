using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Map;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.Vision;

namespace Sts2Overlay.Tools.MapBench;

/// <summary>
/// "--preview": draws what the map layer would show (recommended path, "추천!" node, "?" odds) onto a captured map
/// frame, with the run state of the newest save written before the frame. Same geometry as the overlay window:
/// <see cref="MapMarkLayout"/> in design px, mapped to the frame with <see cref="RefTransform"/>. Style is an
/// approximation of the WPF drawing; positions are exact.
/// </summary>
internal static partial class MarkPreview
{
    [GeneratedRegex(@"(\d{8})_(\d{6})")]
    private static partial Regex Stamp();

    public static DateTime? TimeOf(string name) =>
        Stamp().Match(name) is { Success: true } m
            ? DateTime.ParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            : null;

    /// <summary>Newest save at or before <paramref name="at"/> for this seed/act (the run state the overlay had then).</summary>
    public static ParsedSave? SaveAt(IReadOnlyList<(DateTime At, ParsedSave Save)> saves, DateTime at, string seed, int act) =>
        saves.Where(s => s.At <= at && s.Save.Seed == seed && s.Save.Map?.Act == act).OrderBy(s => s.At).LastOrDefault().Save;

    public static void Render(string file, ScreenFrame frame, MapMarkPlan plan, string caption, string outPath)
    {
        using var bmp = SKBitmap.Decode(file);
        using var canvas = new SKCanvas(bmp);
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        float s = t.Scale;
        SKPoint P(float x, float y) => new(t.ToPixelX(x), t.ToPixelY(y));

        canvas.Save();
        canvas.ClipRect(new SKRect(0, t.ToPixelY(MapMarkLayout.VisibleTop), bmp.Width, t.ToPixelY(MapMarkLayout.VisibleBottom)));
        var gold = new SKColor(0xFF, 0xD5, 0x4F);
        using var alt = new SKPaint { Color = new SKColor(0xFF, 0xF4, 0xD6, 0xB0), StrokeWidth = 3 * s, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, PathEffect = SKPathEffect.CreateDash([9 * s, 7.5f * s], 0) };
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 0x99), StrokeWidth = 11 * s, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        using var best = new SKPaint { Color = gold, StrokeWidth = 6 * s, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        foreach (var l in plan.Lines.Where(l => l.Kind == MapLineKind.Alternative)) canvas.DrawLine(P(l.X1, l.Y1), P(l.X2, l.Y2), alt);
        foreach (var l in plan.Lines.Where(l => l.Kind == MapLineKind.Best))
        {
            canvas.DrawLine(P(l.X1, l.Y1), P(l.X2, l.Y2), shadow);
            canvas.DrawLine(P(l.X1, l.Y1), P(l.X2, l.Y2), best);
        }

        using var face = SKTypeface.FromFamilyName("Malgun Gothic", SKFontStyle.Bold);
        foreach (var n in plan.Nodes)
        {
            var c = P(n.X, n.Y);
            switch (n.Kind)
            {
                case MapNodeMarkKind.Recommended:
                    using (var ring = new SKPaint { Color = new SKColor(0, 0, 0, 0x99), StrokeWidth = 9 * s, IsAntialias = true, Style = SKPaintStyle.Stroke })
                        canvas.DrawCircle(c, 42 * s, ring);
                    using (var ring = new SKPaint { Color = gold, StrokeWidth = 5 * s, IsAntialias = true, Style = SKPaintStyle.Stroke })
                        canvas.DrawCircle(c, 42 * s, ring);
                    Label(canvas, face, n.Caption ?? "추천!", c.X, c.Y - 58 * s, 26 * s, new SKColor(0x3A, 0x2A, 0x05), gold, bottom: true);
                    break;
                case MapNodeMarkKind.UnknownOdds:
                    Label(canvas, face, n.Caption ?? "", c.X, c.Y + 40 * s, 14 * s, SKColors.White, new SKColor(0x1E, 0x1B, 0x16, 0xE6), bottom: false);
                    break;
                case MapNodeMarkKind.Partner:
                    using (var ring = new SKPaint { Color = new SKColor(0x4F, 0xC3, 0xF7), StrokeWidth = 3.5f * s, IsAntialias = true, Style = SKPaintStyle.Stroke })
                        canvas.DrawCircle(c, 36 * s, ring);
                    break;
            }
        }
        canvas.Restore();

        using var text = new SKPaint { Color = SKColors.Yellow, IsAntialias = true };
        using var font = new SKFont(face, 22 * s);
        canvas.DrawText(caption, 380 * s, 125 * s, font, text);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var fs = File.Create(outPath);
        data.SaveTo(fs);
    }

    private static void Label(SKCanvas canvas, SKTypeface face, string text, float cx, float y, float size, SKColor fg, SKColor bg, bool bottom)
    {
        using var font = new SKFont(face, size);
        float w = font.MeasureText(text), h = size * 1.3f;
        float padX = size * 0.45f;
        var rect = SKRect.Create(cx - w / 2 - padX, bottom ? y - h : y, w + 2 * padX, h);
        using var back = new SKPaint { Color = bg, IsAntialias = true };
        canvas.DrawRoundRect(rect, size * 0.35f, size * 0.35f, back);
        using var paint = new SKPaint { Color = fg, IsAntialias = true };
        canvas.DrawText(text, rect.Left + padX, rect.Top + size * 1.02f, font, paint);
    }
}
