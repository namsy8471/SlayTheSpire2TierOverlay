using System.Globalization;
using System.Text.RegularExpressions;

namespace Sts2Overlay.Resources.Pck;

/// <summary>
/// Cuts the small relic icons (what the merchant, the top bar and reward screens draw) out of the game's relic atlas.
///
/// <para>In the pack, <c>RelicModel.PackedIconPath</c> = <c>images/atlases/relic_atlas.sprites/&lt;id lower&gt;.tres</c>
/// is an AtlasTexture: a <c>region</c> of <c>images/atlases/relic_atlas.png</c> (imported only as a BC7 VRAM texture,
/// decoded with <see cref="Bc7Decoder"/>) plus a <c>margin</c> that restores the transparent border the atlas packer
/// trimmed. The icon is written at its original size (region + margin), as the game lays it out.</para>
/// </summary>
public static partial class RelicIconExtractor
{
    public const string AtlasImport = "images/atlases/relic_atlas.png.import";
    public const string SpritesPrefix = "images/atlases/relic_atlas.sprites/";
    /// <summary>The silhouettes NRelic draws behind a small icon (black at 50 % alpha, same rect as the icon).</summary>
    public const string OutlineAtlasImport = "images/atlases/relic_outline_atlas.png.import";
    public const string OutlineSpritesPrefix = "images/atlases/relic_outline_atlas.sprites/";
    /// <summary>Sub-folder of the icon folder that holds the outlines.</summary>
    public const string OutlineDir = "outline";

    /// <summary>
    /// Writes <c>&lt;outDir&gt;/&lt;name&gt;.png</c> for every sprite of the relic atlas and, when the pack has it,
    /// <c>&lt;outDir&gt;/outline/&lt;name&gt;.png</c> for the outline atlas; returns how many icons.
    /// </summary>
    public static int Extract(PckReader pck, string outDir, CancellationToken ct = default)
    {
        int written = ExtractAtlas(pck, AtlasImport, SpritesPrefix, "relic_atlas.png", outDir, ct);
        if (pck.Entries.Any(e => e.Path == OutlineAtlasImport))
            ExtractAtlas(pck, OutlineAtlasImport, OutlineSpritesPrefix, "relic_outline_atlas.png", Path.Combine(outDir, OutlineDir), ct);
        return written;
    }

    private static int ExtractAtlas(PckReader pck, string importPath, string spritesPrefix, string atlasFile, string outDir, CancellationToken ct)
    {
        PckEntry import = pck.Entries.FirstOrDefault(e => e.Path == importPath)
            ?? throw new InvalidDataException($"{importPath} not found in the pack");
        string importText = System.Text.Encoding.UTF8.GetString(pck.ReadAllBytes(import));
        Match pathMatch = ImportPathRegex().Match(importText);
        if (!pathMatch.Success) throw new InvalidDataException($"no texture path in {importPath}");
        string ctexPath = pathMatch.Groups[1].Value;
        PckEntry ctexEntry = pck.Entries.FirstOrDefault(e => e.Path == ctexPath)
            ?? throw new InvalidDataException($"{ctexPath} not found in the pack");
        var ctex = new CtexImage(pck.ReadAllBytes(ctexEntry));
        if (!ctex.CanDecodeRgba) throw new InvalidDataException($"{atlasFile} has an unsupported format {ctex.PixelFormat}");
        byte[] atlas = ctex.DecodeRgba();

        Directory.CreateDirectory(outDir);
        int written = 0;
        foreach (PckEntry e in pck.Entries.Where(e => e.Path.StartsWith(spritesPrefix, StringComparison.Ordinal) && e.Path.EndsWith(".tres", StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            string tres = System.Text.Encoding.UTF8.GetString(pck.ReadAllBytes(e));
            if (!tres.Contains(atlasFile, StringComparison.Ordinal)) continue;
            if (ParseRect(tres, "region") is not { } region) continue;
            var margin = ParseRect(tres, "margin") ?? (0, 0, 0, 0);
            byte[]? icon = Cut(atlas, ctex.Width, ctex.Height, region, margin, out int w, out int h);
            if (icon == null) continue;
            string name = Path.GetFileNameWithoutExtension(e.Path);
            File.WriteAllBytes(Path.Combine(outDir, name + ".png"), PngWriter.Encode(icon, w, h));
            written++;
        }
        return written;
    }

    /// <summary>The sprite at its original size: the region placed at the margin offset in a transparent canvas.</summary>
    internal static byte[]? Cut(byte[] atlas, int atlasW, int atlasH, (int X, int Y, int W, int H) region, (int X, int Y, int W, int H) margin, out int w, out int h)
    {
        w = region.W + margin.W;
        h = region.H + margin.H;
        if (region.W <= 0 || region.H <= 0 || region.X < 0 || region.Y < 0 || region.X + region.W > atlasW || region.Y + region.H > atlasH) return null;
        var icon = new byte[w * h * 4];
        for (int y = 0; y < region.H; y++)
            atlas.AsSpan(((region.Y + y) * atlasW + region.X) * 4, region.W * 4)
                .CopyTo(icon.AsSpan(((margin.Y + y) * w + margin.X) * 4));
        return icon;
    }

    internal static (int X, int Y, int W, int H)? ParseRect(string tres, string key)
    {
        Match m = Regex.Match(tres, key + @"\s*=\s*Rect2\(([-\d.]+),\s*([-\d.]+),\s*([-\d.]+),\s*([-\d.]+)\)");
        if (!m.Success) return null;
        int P(int g) => (int)Math.Round(double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture));
        return (P(1), P(2), P(3), P(4));
    }

    [GeneratedRegex("path(?:\\.bptc|\\.s3tc)?=\"res://([^\"]+)\"")]
    private static partial Regex ImportPathRegex();
}
