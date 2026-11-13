using System.Text.Json;

namespace Sts2Overlay.Vision.Relics;

/// <summary>Relic id + rarity from <c>data/catalog/relics.&lt;version&gt;.json</c> (names-free, tools/CatalogBuilder --relics).</summary>
public sealed record CatalogRelic(string Id, string Rarity);

/// <summary>Reads the committed relic catalog. Only <c>id</c> and <c>rarity</c> are used here.</summary>
public static class RelicCatalog
{
    /// <summary>Rarities the merchant sells (MerchantInventory.PopulateRelicEntries: two RollRarity picks + one Shop relic).</summary>
    public static readonly IReadOnlySet<string> ShopRarities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Common", "Uncommon", "Rare", "Shop" };

    public static IReadOnlyList<CatalogRelic> Load(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using JsonDocument doc = JsonDocument.Parse(fs);
        var list = new List<CatalogRelic>();
        foreach (JsonElement r in doc.RootElement.GetProperty("relics").EnumerateArray())
            if (r.TryGetProperty("id", out JsonElement id) && r.TryGetProperty("rarity", out JsonElement rar) && id.GetString() is { } i && rar.GetString() is { } ra)
                list.Add(new CatalogRelic(i, ra));
        return list;
    }

    /// <summary>File base name of a relic's icon: <c>RelicModel.IconBaseName</c> = the id entry in lower case.</summary>
    public static string IconName(string relicId) =>
        (relicId.StartsWith("RELIC.", StringComparison.Ordinal) ? relicId[6..] : relicId).ToLowerInvariant();
}

/// <summary>One relic icon as a matching template.</summary>
/// <param name="Index">Position in <see cref="RelicIconDatabase.Entries"/>.</param>
/// <param name="RelicId"><c>RELIC.X</c>.</param>
/// <param name="Rarity">Catalog rarity (Common, Uncommon, Rare, Shop).</param>
public sealed record RelicEntry(int Index, string RelicId, string Rarity);

/// <summary>
/// The relic icons the merchant can show, as masked colour templates on two grids (coarse for the search, fine for the
/// decision). Built from the icons Sts2Overlay.Resources' RelicIconExtractor cut out of the game's
/// relic atlas (<c>data/local/pck/relic_icons/&lt;name&gt;.png</c>, never committed).
///
/// <para><b>Why masked.</b> An icon is an irregular shape on a transparent canvas, drawn over the shop rug and a
/// dark outline. Only grid cells the icon covers completely (alpha ≥ <see cref="MaskAlpha"/>) look the same on every
/// screen, so a template is compared only there: its colours are stored zero-mean / unit-length over its own mask
/// (the NCC numerator is then one dot product) together with the 0/1 mask (for the query's mean and spread over
/// the same cells, see <see cref="RelicTemplates.Score"/>).</para>
/// </summary>
public sealed class RelicIconDatabase
{
    /// <summary>Cells whose icon coverage is at least this are compared; the anti-aliased border is not.</summary>
    public const float MaskAlpha = 0.92f;

    public const int CoarseGrid = 12, FineGrid = 24;

    public IReadOnlyList<RelicEntry> Entries { get; }
    internal RelicTemplates Coarse { get; }
    internal RelicTemplates Fine { get; }

    private RelicIconDatabase(IReadOnlyList<RelicEntry> entries, RelicTemplates coarse, RelicTemplates fine)
    {
        Entries = entries;
        Coarse = coarse;
        Fine = fine;
    }

    public int Count => Entries.Count;

    /// <summary>
    /// Loads every shop-sellable relic of <paramref name="catalog"/> that has an icon in <paramref name="iconDir"/>.
    /// Icons are straight-alpha RGBA, all drawn into the same square on screen (the game's 85x85 atlas sprites).
    /// </summary>
    public static RelicIconDatabase Build(string iconDir, IReadOnlyList<CatalogRelic> catalog, Action<string>? log = null)
    {
        var icons = new List<(string Id, string Rarity, RgbaImage Image)>();
        var outlines = new List<RgbaImage?>();
        foreach (CatalogRelic r in catalog.Where(r => RelicCatalog.ShopRarities.Contains(r.Rarity)).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            string file = Path.Combine(iconDir, RelicCatalog.IconName(r.Id) + ".png");
            if (!File.Exists(file))
            {
                log?.Invoke($"no icon for {r.Id}");
                continue;
            }
            icons.Add((r.Id, r.Rarity, RgbaImage.Load(file)));
            // NRelic draws the relic's outline sprite behind a small icon (black, 50 % alpha): optional, it only
            // sharpens the whole-square (shape) view.
            string outline = Path.Combine(iconDir, OutlineDir, RelicCatalog.IconName(r.Id) + ".png");
            outlines.Add(File.Exists(outline) ? RgbaImage.Load(outline) : null);
        }
        return FromImages(icons, outlines);
    }

    /// <summary>Sub-folder of the icon folder with the outline sprites (same names, same size as the icons).</summary>
    public const string OutlineDir = "outline";

    /// <summary>Builds from images in memory (tests use synthetic icons). <paramref name="outlines"/>: per icon, or null.</summary>
    public static RelicIconDatabase FromImages(IReadOnlyList<(string Id, string Rarity, RgbaImage Image)> icons, IReadOnlyList<RgbaImage?>? outlines = null)
    {
        var entries = icons.Select((x, i) => new RelicEntry(i, x.Id, x.Rarity)).ToList();
        var images = icons.Select(x => x.Image).ToList();
        return new RelicIconDatabase(entries, RelicTemplates.Build(images, outlines, CoarseGrid), RelicTemplates.Build(images, outlines, FineGrid));
    }

    public static RelicIconDatabase BuildPotions(string iconDir)
    {
        var icons = new List<(string Id, string Rarity, RgbaImage Image)>();
        var outlines = new List<RgbaImage?>();
        foreach (var file in Directory.EnumerateFiles(iconDir, "*.png").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            icons.Add(("POTION." + name.ToUpperInvariant(), "", RgbaImage.Load(file)));
            string outline = Path.Combine(iconDir, OutlineDir, Path.GetFileName(file));
            outlines.Add(File.Exists(outline) ? RgbaImage.Load(outline) : null);
        }
        return FromImages(icons, outlines);
    }
}

/// <summary>A straight-alpha RGBA8 image (row-major).</summary>
public sealed record RgbaImage(byte[] Rgba, int Width, int Height)
{
    public static RgbaImage Load(string path)
    {
        using SkiaSharp.SKBitmap bmp = SkiaSharp.SKBitmap.Decode(path)
            ?? throw new InvalidDataException($"cannot decode {path}");
        var info = new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using var conv = new SkiaSharp.SKBitmap(info);
        if (!bmp.CopyTo(conv, SkiaSharp.SKColorType.Rgba8888)) throw new InvalidDataException($"cannot convert {path}");
        return new RgbaImage(conv.GetPixelSpan().ToArray(), bmp.Width, bmp.Height);
    }
}

/// <summary>
/// All templates on one g x g grid, stored back to back so a scan is a sequence of contiguous dot products.
/// Per template: <c>T</c> (colour, zero-mean and unit-length over the mask, 0 elsewhere) and <c>M</c> (1 on masked
/// values), both g*g*3 long (interleaved R,G,B), and the masked value count.
/// </summary>
internal sealed class RelicTemplates
{
    /// <summary>
    /// The shop rug's colour (ScreenClassifier's rug probes, ~ RGB 75, 110, 98): what shows through the transparent part
    /// of an icon's square, for the whole-square ("silhouette") templates.
    /// </summary>
    public static readonly (float R, float G, float B) RugColour = (75, 110, 98);

    public int Grid { get; }
    public int Length { get; }
    private readonly float[] _t;
    private readonly float[] _m;
    private readonly int[] _n;
    private readonly float[] _full;

    private RelicTemplates(int grid, float[] t, float[] m, int[] n, float[] full)
    {
        Grid = grid;
        Length = grid * grid * 3;
        _t = t;
        _m = m;
        _n = n;
        _full = full;
    }

    public int Count => _n.Length;

    /// <summary>
    /// Whole-square template: the icon composited over <see cref="RugColour"/> darkened by the relic's outline
    /// (relic.tscn: Outline self_modulate black at alpha 0.5, behind the icon), zero-mean and unit-length. Unlike the
    /// masked template it also compares the SHAPE (where the icon is not), which separates two relics of the same
    /// colour (a red toolbox vs a red mask) that the interior colours alone barely tell apart.
    /// </summary>
    public ReadOnlySpan<float> Full(int i) => _full.AsSpan(i * Length, Length);

    /// <summary>NCC of the whole-square template with a query already normalised by <see cref="Ncc.Normalise"/>.</summary>
    public float ScoreFull(int i, ReadOnlySpan<float> normalisedQuery) => Ncc.Dot(Full(i), normalisedQuery);

    public ReadOnlySpan<float> Template(int i) => _t.AsSpan(i * Length, Length);
    public ReadOnlySpan<float> Mask(int i) => _m.AsSpan(i * Length, Length);
    public int MaskCount(int i) => _n[i];

    /// <summary>Outline darkening: the Outline TextureRect's self_modulate alpha (black).</summary>
    public const double OutlineAlpha = 0.5;

    public static RelicTemplates Build(IReadOnlyList<RgbaImage> images, IReadOnlyList<RgbaImage?>? outlines, int g)
    {
        int len = g * g * 3;
        var t = new float[images.Count * len];
        var m = new float[images.Count * len];
        var n = new int[images.Count];
        var full = new float[images.Count * len];
        for (int k = 0; k < images.Count; k++)
        {
            Span<float> tk = t.AsSpan(k * len, len), mk = m.AsSpan(k * len, len), fk = full.AsSpan(k * len, len);
            RgbaImage img = images[k];
            RgbaImage? outline = outlines != null && k < outlines.Count && outlines[k] is { } ol && ol.Width == img.Width && ol.Height == img.Height ? ol : null;
            // The icon is drawn with KEEP_ASPECT_CENTERED into a square: a non-square sprite is centred.
            int side = Math.Max(img.Width, img.Height);
            float ox = (side - img.Width) / 2f, oy = (side - img.Height) / 2f;
            for (int cy = 0; cy < g; cy++)
                for (int cx = 0; cx < g; cx++)
                {
                    // Cell bounds in sprite pixels; every pixel weighted by its overlap with the cell.
                    double x0 = cx * side / (double)g - ox, x1 = (cx + 1) * side / (double)g - ox;
                    double y0 = cy * side / (double)g - oy, y1 = (cy + 1) * side / (double)g - oy;
                    double area = 0, alpha = 0, r = 0, gr = 0, b = 0, back = 0;
                    for (int py = (int)Math.Floor(y0); py < Math.Ceiling(y1); py++)
                        for (int px = (int)Math.Floor(x0); px < Math.Ceiling(x1); px++)
                        {
                            double w = (Math.Min(px + 1, x1) - Math.Max(px, x0)) * (Math.Min(py + 1, y1) - Math.Max(py, y0));
                            if (w <= 0) continue;
                            area += w;
                            if (px < 0 || py < 0 || px >= img.Width || py >= img.Height)
                            {
                                back += w;
                                continue;
                            }
                            int p = (py * img.Width + px) * 4;
                            double a = img.Rgba[p + 3] / 255.0;
                            // what shows through: the rug, darkened where the outline sprite is
                            double shade = outline != null ? 1 - OutlineAlpha * outline.Rgba[p + 3] / 255.0 : 1;
                            back += w * (1 - a) * shade;
                            alpha += w * a;
                            r += w * a * img.Rgba[p];
                            gr += w * a * img.Rgba[p + 1];
                            b += w * a * img.Rgba[p + 2];
                        }
                    int o = (cy * g + cx) * 3;
                    if (area > 0)
                    {
                        // straight alpha over the (shaded) rug: a·icon + (1 − a)·shade·rug, integrated over the cell
                        fk[o] = (float)((r + back * RugColour.R) / area);
                        fk[o + 1] = (float)((gr + back * RugColour.G) / area);
                        fk[o + 2] = (float)((b + back * RugColour.B) / area);
                    }
                    if (area <= 0 || alpha / area < RelicIconDatabase.MaskAlpha) continue;
                    tk[o] = (float)(r / alpha);
                    tk[o + 1] = (float)(gr / alpha);
                    tk[o + 2] = (float)(b / alpha);
                    mk[o] = mk[o + 1] = mk[o + 2] = 1;
                    n[k] += 3;
                }
            // zero mean and unit length over the mask
            double sum = 0, sq = 0;
            for (int i = 0; i < len; i++) sum += tk[i];
            double mean = n[k] > 0 ? sum / n[k] : 0;
            for (int i = 0; i < len; i++)
                if (mk[i] > 0)
                {
                    tk[i] = (float)(tk[i] - mean);
                    sq += tk[i] * (double)tk[i];
                }
            float inv = sq > 1e-9 ? (float)(1 / Math.Sqrt(sq)) : 0;
            for (int i = 0; i < len; i++) tk[i] *= inv;
            Ncc.Normalise(fk);
        }
        return new RelicTemplates(g, t, m, n, full);
    }

    /// <summary>
    /// Masked NCC of template <paramref name="i"/> with the raw query <paramref name="q"/> (cell colours 0..255,
    /// interleaved RGB) whose squares are <paramref name="q2"/>: <c>Σ T·q / sqrt(Σ_M q² − (Σ_M q)² / n)</c>.
    /// T is zero-mean over the mask, so the query's own mean over the mask drops out of the numerator.
    /// A flat query (no contrast under the mask) scores 0.
    /// </summary>
    public float Score(int i, ReadOnlySpan<float> q, ReadOnlySpan<float> q2)
    {
        int n = _n[i];
        if (n == 0) return 0;
        ReadOnlySpan<float> t = Template(i), m = Mask(i);
        float num = Ncc.Dot(t, q);
        float sum = Ncc.Dot(m, q), sumSq = Ncc.Dot(m, q2);
        float var = sumSq - sum * sum / n;
        // Ncc.MinContrast is per value after normalisation; here: at least ~3 levels of spread per masked value.
        if (var <= 9f * n) return 0;
        return num / MathF.Sqrt(var);
    }
}
