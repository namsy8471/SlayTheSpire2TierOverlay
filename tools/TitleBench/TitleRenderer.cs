using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;
using Sts2Overlay.Vision;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace Sts2Overlay.Tools.TitleBench;

/// <summary>
/// A font as the card's %TitleLabel uses it in one language (v0.107.1):
/// - English: scenes/cards/card.tscn FontVariation_2eadq = themes/kreon_regular_shared.tres (Kreon Regular) with
///   spacing_glyph = 1.
/// - Korean: MegaLabel._Ready → RefreshFont → FontControlUtils.ApplyLocaleFontSubstitution replaces the theme font
///   with KorFontPathSet's themes/fonts/kor/gyeonggi_cheonnyeon_batang_bold_shared.tres (GyeonggiCheonnyeonBatangBold,
///   variation_transform 0.96, spacing_top 1, spacing_bottom 3, no glyph spacing).
/// </summary>
internal sealed class TitleFont : IDisposable
{
    public string Name { get; }
    public float Transform { get; }
    public float GlyphSpacing { get; }
    public float SpacingTop { get; }
    public float SpacingBottom { get; }

    private readonly SKTypeface _typeface;
    private readonly Face _face;
    private readonly HbFont _font;
    private readonly Blob _blob;
    private readonly int _upem;
    private readonly float _ascender, _descender; // font units (hhea), descender positive

    public TitleFont(string name, byte[] ttf, float transform, float glyphSpacing, float spacingTop = 0, float spacingBottom = 0)
    {
        Name = name;
        Transform = transform;
        GlyphSpacing = glyphSpacing;
        SpacingTop = spacingTop;
        SpacingBottom = spacingBottom;
        _typeface = SKTypeface.FromData(SKData.CreateCopy(ttf)) ?? throw new InvalidDataException($"Skia cannot load {name}");
        // HarfBuzz keeps reading the font data while shaping: give it unmanaged memory that the GC cannot move.
        IntPtr mem = Marshal.AllocHGlobal(ttf.Length);
        Marshal.Copy(ttf, 0, mem, ttf.Length);
        _blob = new Blob(mem, ttf.Length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(mem));
        _face = new Face(_blob, 0);
        _font = new HbFont(_face);
        _font.SetFunctionsOpenType();
        _upem = _face.UnitsPerEm;
        _font.SetScale(_upem, _upem);
        using var probe = new SKFont(_typeface, _upem);
        _ascender = -probe.Metrics.Ascent;
        _descender = probe.Metrics.Descent;
    }

    public void Dispose()
    {
        _font.Dispose();
        _face.Dispose();
        _blob.Dispose();
        _typeface.Dispose();
    }

    /// <summary>HarfBuzz shaping (Godot's TextServerAdvanced shapes with HarfBuzz too): glyph ids and advances in font units.</summary>
    public (ushort[] Glyphs, float[] Advance, float[] OffsetX, float[] OffsetY) Shape(string text)
    {
        using var buf = new HbBuffer();
        buf.AddUtf16(text);
        buf.GuessSegmentProperties();
        _font.Shape(buf);
        GlyphInfo[] info = buf.GlyphInfos;
        GlyphPosition[] pos = buf.GlyphPositions;
        var g = new ushort[info.Length];
        var a = new float[info.Length];
        var ox = new float[info.Length];
        var oy = new float[info.Length];
        for (int i = 0; i < info.Length; i++)
        {
            g[i] = (ushort)info[i].Codepoint;
            a[i] = pos[i].XAdvance;
            ox[i] = pos[i].XOffset;
            oy[i] = pos[i].YOffset;
        }
        return (g, a, ox, oy);
    }

    public float Px(float fontUnits, float size) => fontUnits * size * Transform / _upem;

    /// <summary>Line height at a font size (label units), as Godot's shaped text reports it.</summary>
    public float LineHeight(float size) => Px(_ascender + _descender, size) + SpacingTop + SpacingBottom;

    public float Ascent(float size) => Px(_ascender, size) + SpacingTop;

    public SKPath GlyphPath(ushort glyph, float pixelSize)
    {
        using var f = new SKFont(_typeface, pixelSize) { Subpixel = true, Hinting = SKFontHinting.None, LinearMetrics = true };
        return f.GetGlyphPath(glyph) ?? new SKPath();
    }

    public bool HasAllGlyphs(string text)
    {
        foreach (ushort g in Shape(text).Glyphs)
            if (g == 0) return false;
        return true;
    }
}

/// <summary>How the game draws a card title (scenes/cards/card.tscn %TitleLabel + MegaLabel auto-size + NCard.UpdateTitleLabel).</summary>
internal static class TitleLayout
{
    public const float Width = 210, Height = 54; // TitleLabel offsets -105..105, -204..-150
    public const int MaxFontSize = 26, MinFontSize = 8; // MaxFontSize from the scene, MinFontSize = MegaLabel default
    public const float OutlineSize = 12; // theme_override_constants/outline_size

    public static readonly SKColor Green = new(0x7F, 0xFF, 0x00);  // StsColors.green (upgraded)
    public static readonly SKColor Cream = new(0xFF, 0xF6, 0xE2);  // StsColors.cream
    public static readonly SKColor OutlineSpecial = new(0x1B, 0x61, 0x31); // cardTitleOutlineSpecial (upgraded, Event)
    public static readonly SKColor OutlineCommon = new(0x4D, 0x4B, 0x40);
    public static readonly SKColor OutlineUncommon = new(0x00, 0x5C, 0x75);
    public static readonly SKColor OutlineRare = new(0x6B, 0x4B, 0x00);

    /// <summary>Line width of the shaped text at a font size (label units). MSDF fonts are positioned unrounded.</summary>
    public static float LineWidth(TitleFont font, string text, int size, out float[] advances, out (ushort[] G, float[] A, float[] Ox, float[] Oy) shaped)
    {
        shaped = font.Shape(text);
        advances = new float[shaped.G.Length];
        float w = 0;
        for (int i = 0; i < advances.Length; i++)
        {
            advances[i] = font.Px(shaped.A[i], size) + font.GlyphSpacing;
            w += advances[i];
        }
        return w;
    }

    /// <summary>MegaLabel.AdjustFontSize: the largest size in [min, max] whose single line fits a w x h label.</summary>
    public static int FitFontSize(TitleFont font, string text, float w = Width, float h = Height, int min = MinFontSize, int max = MaxFontSize)
    {
        bool TooBig(int s) => LineWidth(font, text, s, out _, out _) > w || font.LineHeight(s) > h;
        if (!TooBig(max)) return max;
        int lo = min, hi = max;
        while (hi >= lo)
        {
            int mid = lo + (hi - lo) / 2;
            if (mid == max || TooBig(mid)) hi = mid - 1;
            else lo = mid + 1;
        }
        return Math.Min(lo, hi);
    }

    /// <summary>
    /// The title's glyph outlines in label-local units (origin = the label's top-left corner): auto-sized, centred
    /// horizontally and vertically like Label does.
    /// </summary>
    public static (SKPath Path, int Size) Layout(TitleFont font, string text) => Layout(font, text, Width, Height, MinFontSize, MaxFontSize);

    /// <summary>Single-line, centred MegaLabel layout in a w x h label (no autowrap).</summary>
    public static (SKPath Path, int Size) Layout(TitleFont font, string text, float w, float h, int min, int max)
    {
        int size = FitFontSize(font, text, w, h, min, max);
        float lineW = LineWidth(font, text, size, out float[] adv, out var shaped);
        float x = (int)(w - lineW) / 2; // Label HORIZONTAL_ALIGNMENT_CENTER
        float baseline = (h - font.LineHeight(size)) / 2 + font.Ascent(size);
        var all = new SKPath();
        for (int i = 0; i < shaped.G.Length; i++)
        {
            using SKPath glyph = font.GlyphPath(shaped.G[i], size * font.Transform);
            glyph.Offset(x + font.Px(shaped.Ox[i], size), baseline - font.Px(shaped.Oy[i], size));
            all.AddPath(glyph);
            x += adv[i];
        }
        return (all, size);
    }
}

/// <summary>A screen-sized BGRA frame that card titles are composited into, plus the upgrade detector's measurement.</summary>
internal sealed class TitleCanvas
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public RefTransform T { get; }
    public int Step => Math.Max(1, (int)MathF.Floor(T.Scale + 1e-3f));

    /// <summary>
    /// Extra growth of the fill edge in frame pixels. The game's MSDF edge ends up slightly bolder than the exact outline
    /// (distance-field interpolation at 48 px source size); calibrated on real captures by <c>validate</c>.
    /// </summary>
    public static float FillGrowPx { get; set; }

    public TitleCanvas(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        T = RefTransform.For(width, height);
    }

    public ScreenFrame Frame => ScreenFrame.FromPacked(Pixels, Width, Height);

    /// <summary>The title box of a card at (cx, cy) with node scale, in frame pixels, padded.</summary>
    public PixelRect Region(float cx, float cy, float scale, int pad = 8)
    {
        PixelRect r = T.ToPixelRect(CardGeometry.Place(CardGeometry.Title, cx, cy, scale));
        return new PixelRect(r.X - pad, r.Y - pad, r.Width + 2 * pad, r.Height + 2 * pad).Intersect(Width, Height);
    }

    public void Fill(PixelRect r, SKColor c)
    {
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                int p = (y * Width + x) * 4;
                Pixels[p] = c.Blue;
                Pixels[p + 1] = c.Green;
                Pixels[p + 2] = c.Red;
                Pixels[p + 3] = 255;
            }
    }

    /// <summary>
    /// Draws a title for a card at (cx, cy) with node scale. Both title fonts are imported as MSDF
    /// (multichannel_signed_distance_field, msdf_pixel_range 24), which Godot's canvas shader turns into a one-pixel
    /// linear edge ramp at the final screen scale (alpha = clamp(distance + 0.5)) - resolution independent, no glyph
    /// cache raster or pixel snapping - so the outlines are filled with analytic anti-aliasing straight in frame
    /// pixels. The outline pass (drawn first) is the glyph grown by outline_size / 4 label units
    /// (RendererCanvasCull: outline = outline_size / scale / 4 texels), then the fill on top.
    /// </summary>
    public void DrawTitle(SKPath labelPath, float cx, float cy, float scale, SKColor fillColor, SKColor outlineColor)
    {
        RectF label = T.ToPixels(CardGeometry.Place(CardGeometry.Title, cx, cy, scale));
        DrawLabel(labelPath, label, scale * T.Scale, Region(cx, cy, scale), fillColor, outlineColor, TitleLayout.OutlineSize / 4, 1f);
    }

    /// <summary>
    /// Composites a label's glyph outlines (label units, origin = <paramref name="label"/>'s top-left corner, k frame
    /// pixels per unit) into <paramref name="r"/>: the outline (glyph grown by <paramref name="outlineGrow"/> units,
    /// at <paramref name="outlineAlpha"/>) first, then the fill.
    /// </summary>
    public void DrawLabel(SKPath labelPath, RectF label, float k, PixelRect r, SKColor fillColor, SKColor outlineColor, float outlineGrow, float outlineAlpha)
    {
        float[] fill = Coverage(labelPath, r, label, k, FillGrowPx / k);
        float[] line = Coverage(labelPath, r, label, k, outlineGrow);
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                int i = (y - r.Y) * r.Width + (x - r.X);
                float ao = line[i] * outlineAlpha, af = fill[i];
                if (ao <= 0 && af <= 0) continue;
                int p = (y * Width + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float bg = Pixels[p + c];
                    float oc = c == 0 ? outlineColor.Blue : c == 1 ? outlineColor.Green : outlineColor.Red;
                    float fc = c == 0 ? fillColor.Blue : c == 1 ? fillColor.Green : fillColor.Red;
                    float val = bg * (1 - ao) + oc * ao;
                    val = val * (1 - af) + fc * af;
                    Pixels[p + c] = (byte)Math.Clamp(MathF.Round(val), 0, 255);
                }
            }
    }

    private static float[] Coverage(SKPath labelPath, PixelRect r, RectF label, float k, float grow)
    {
        using var bmp = new SKBitmap(new SKImageInfo(r.Width, r.Height, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint
        {
            IsAntialias = true, Color = SKColors.White,
            Style = grow > 0 ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill,
            StrokeWidth = 2 * grow, StrokeJoin = SKStrokeJoin.Round,
        })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(label.X - r.X, label.Y - r.Y);
            canvas.Scale(k);
            canvas.DrawPath(labelPath, paint);
        }
        var a = new float[r.Width * r.Height];
        ReadOnlySpan<byte> px = bmp.GetPixelSpan();
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
                a[y * r.Width + x] = px[y * bmp.RowBytes + x] / 255f;
        return a;
    }

    /// <summary>UpgradeDetector.TitleEvidence on this canvas (the detector's own code path).</summary>
    public (float Green, float Covered) Evidence(float cx, float cy, float scale)
    {
        PixelRect region = Region(cx, cy, scale, 16);
        using IntegralImage img = IntegralImage.Build(Frame, region, Step);
        return UpgradeDetector.TitleEvidence(img, T, cx, cy, scale);
    }
}
