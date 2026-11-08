using System.Text.RegularExpressions;
using SkiaSharp;

namespace Sts2Overlay.Tools.TitleBench;

/// <summary>
/// Event option text as scenes/events/event_option_button.tscn draws it: a MegaRichTextLabel at node x 42..764,
/// y 13..87 (722 x 74), "[gold][b]Title[/b][/gold]\nDescription", word-wrapped, auto-sized 12..24, line_separation
/// -4, vertically centred. English: bold = Kreon Bold + spacing_glyph 1, normal = Kreon Regular; Korean swaps both
/// for GyeonggiBatang Bold. Used to see how close the text gets to the detector's fill stripes and how distinct the
/// text signatures of different pages are.
/// </summary>
internal static partial class EventText
{
    public const float LabelX = 42, LabelY = 13, LabelW = 722, LabelH = 74, LineSeparation = -4;
    public const int MinSize = 12, MaxSize = 24;

    /// <summary>Resolves SmartFormat placeholders roughly (numbers → "10", names → a typical card name) and strips BBCode.</summary>
    public static string Plain(string s, IReadOnlyDictionary<string, double> vars)
    {
        s = Plural().Replace(s, m => m.Groups[3].Value);
        s = Choose().Replace(s, m => m.Groups[2].Value.Split('|').OrderByDescending(x => x.Length).First());
        s = Placeholder().Replace(s, m =>
        {
            string name = m.Groups[1].Value;
            if (vars.TryGetValue(name, out double v)) return ((int)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return name.Contains("Card", StringComparison.OrdinalIgnoreCase) || name.Contains("Relic", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Potion", StringComparison.OrdinalIgnoreCase) || name.Contains("Name", StringComparison.OrdinalIgnoreCase)
                ? "Perfected Strike" : "10";
        });
        return Bbcode().Replace(s, "");
    }

    [GeneratedRegex(@"\{(\w+):plural:([^|}]*)\|([^}]*)\}")]
    private static partial Regex Plural();
    [GeneratedRegex(@"\{(\w+):(?:choose|cond)[^:]*:([^}]*)\}")]
    private static partial Regex Choose();
    [GeneratedRegex(@"\{(\w+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();
    [GeneratedRegex(@"\[/?[a-zA-Z_]+(?:=[^\]]*)?\]")]
    private static partial Regex Bbcode();

    private sealed record Line(TitleFont Font, string Text);

    private static List<Line> Wrap(TitleFont bold, TitleFont regular, string title, string description, int size)
    {
        var lines = new List<Line>();
        void Paragraph(TitleFont font, string text)
        {
            string current = "";
            foreach (string word in text.Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && TitleLayout.LineWidth(font, candidate, size, out _, out _) > LabelW)
                {
                    lines.Add(new Line(font, current));
                    current = word;
                }
                else current = candidate;
            }
            lines.Add(new Line(font, current));
        }
        if (!string.IsNullOrEmpty(title)) Paragraph(bold, title);
        foreach (string p in description.Split('\n'))
            if (p.Length > 0 || string.IsNullOrEmpty(title)) Paragraph(regular, p);
        return lines;
    }

    /// <summary>Glyph outlines in button-node units (the node is 800 x 100; its visible texture starts 8 below the top).</summary>
    public static (SKPath Path, int Size, int Lines) Layout(TitleFont bold, TitleFont regular, string title, string description)
    {
        // MegaRichTextLabel.AdjustFontSize: largest size whose wrapped text fits (EstimateTextSize adds
        // (line_separation + 3) per extra line).
        bool TooBig(int s)
        {
            var l = Wrap(bold, regular, title, description, s);
            float h = l.Sum(x => x.Font.LineHeight(s)) + (LineSeparation + 3) * (l.Count - 1);
            return h > LabelH || l.Any(x => TitleLayout.LineWidth(x.Font, x.Text, s, out _, out _) > LabelW);
        }
        int size = MaxSize;
        if (TooBig(MaxSize))
        {
            int lo = MinSize, hi = MaxSize;
            while (hi >= lo)
            {
                int mid = lo + (hi - lo) / 2;
                if (TooBig(mid)) hi = mid - 1;
                else lo = mid + 1;
            }
            size = Math.Max(MinSize, Math.Min(lo, hi));
        }
        var lines = Wrap(bold, regular, title, description, size);
        float total = lines.Sum(x => x.Font.LineHeight(size)) + LineSeparation * (lines.Count - 1);
        float y = LabelY + (LabelH - total) / 2;
        var all = new SKPath();
        foreach (Line line in lines)
        {
            float x = LabelX;
            float lineW = TitleLayout.LineWidth(line.Font, line.Text, size, out float[] adv, out var shaped);
            _ = lineW; // left aligned (RichTextLabel default)
            float baseline = y + line.Font.Ascent(size);
            for (int i = 0; i < shaped.G.Length; i++)
            {
                using SKPath g = line.Font.GlyphPath(shaped.G[i], size * line.Font.Transform);
                g.Offset(x + line.Font.Px(shaped.Ox[i], size), baseline - line.Font.Px(shaped.Oy[i], size));
                all.AddPath(g);
                x += adv[i];
            }
            y += line.Font.LineHeight(size) + LineSeparation;
        }
        return (all, size, lines.Count);
    }

    /// <summary>Mean glyph coverage (0..1) per reference pixel inside a node-unit rectangle.</summary>
    public static float[] Coverage(SKPath path, SKRect r)
    {
        int w = (int)r.Width, h = (int)r.Height;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White })
        {
            c.Clear(SKColors.Transparent);
            c.Translate(-r.Left, -r.Top);
            c.DrawPath(path, paint);
        }
        var a = new float[w * h];
        ReadOnlySpan<byte> px = bmp.GetPixelSpan();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                a[y * w + x] = px[y * bmp.RowBytes + x] / 255f;
        return a;
    }

    /// <summary>Share of a rectangle's pixels that text makes non-teal (coverage above one third).</summary>
    public static float InkShare(SKPath path, SKRect r) => Coverage(path, r).Count(v => v > 1f / 3) / (r.Width * r.Height);

    /// <summary>EventButtonDetector's text signature (24 x 4 cells of x 950..1690, texture y 14..70) on the rendering.</summary>
    public static float[] Signature(SKPath path)
    {
        // Screen x 950..1690 = node x 28..768; texture y 14..70 = node y 22..78.
        var r = new SKRect(28, 22, 768, 78);
        float[] cov = Coverage(path, r);
        int w = (int)r.Width, h = (int)r.Height;
        var sig = new float[24 * 4];
        for (int cy = 0; cy < 4; cy++)
            for (int cx = 0; cx < 24; cx++)
            {
                int x0 = cx * w / 24, x1 = (cx + 1) * w / 24, y0 = cy * h / 4, y1 = (cy + 1) * h / 4, n = 0, light = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++, n++)
                        if (cov[y * w + x] > 0.5f) light++;
                sig[cy * 24 + cx] = (float)light / n;
            }
        return sig;
    }
}
