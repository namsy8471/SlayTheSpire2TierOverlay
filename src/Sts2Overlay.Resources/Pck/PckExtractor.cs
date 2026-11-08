using System.Text;
using System.Text.RegularExpressions;

namespace Sts2Overlay.Resources.Pck;

/// <summary>Result of <see cref="PckExtractor.ExtractImages"/>.</summary>
/// <param name="Matched">Textures whose logical path matched the glob.</param>
/// <param name="Written">Image files written (embedded PNG/WebP).</param>
/// <param name="Counts">How many textures of each payload kind were seen.</param>
/// <param name="Skipped">Logical paths that could not be converted, with the reason.</param>
public sealed record ImageExtractionResult(int Matched, int Written, IReadOnlyDictionary<CtexPayload, int> Counts, IReadOnlyList<string> Skipped);

/// <summary>
/// Copies files out of a <see cref="PckReader"/> (shared by tools/PckTool and the in-process
/// <see cref="ResourcePreparer"/>). Writes only below the given output folder; the pack is never modified.
/// </summary>
public static partial class PckExtractor
{
    /// <summary>Copies matching files as-is. Returns the number of files written.</summary>
    public static int ExtractFiles(PckReader pck, string pattern, string outDir, Action<string, string>? onFile = null, CancellationToken ct = default)
    {
        var glob = new Glob(pattern);
        int count = 0;
        foreach (var entry in pck.Entries.Where(e => glob.IsMatch(e.Path)))
        {
            ct.ThrowIfCancellationRequested();
            string outPath = OutputPath(outDir, entry.Path, glob.LiteralDirectoryPrefix);
            pck.ExtractTo(entry, outPath);
            onFile?.Invoke(entry.Path, outPath);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Converts imported textures to image files.
    ///
    /// The glob is matched against the ORIGINAL resource paths ("images/packed/card_portraits/defect/strike_defect.png"),
    /// because those carry readable names. In an exported game each such image exists only as a small text file
    /// "&lt;name&gt;.png.import" that points to the real data, e.g.
    ///     path="res://.godot/imported/strike_defect.png-37908890b0aaa1767ab7ff5857e1b43b.ctex"
    /// (VRAM-compressed textures use keys like path.bptc= / path.s3tc= instead of path=).
    /// A glob that matches .ctex paths directly also works; then the .ctex file name is used.
    /// </summary>
    /// <param name="progress">Reports (done, total) after each texture.</param>
    public static ImageExtractionResult ExtractImages(
        PckReader pck, string pattern, string outDir, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var glob = new Glob(pattern);
        var byPath = pck.Entries.ToDictionary(e => e.Path);
        var skipped = new List<string>();

        // (logical name used for the output file, the .ctex entry holding the pixels)
        var jobs = new List<(string LogicalPath, PckEntry Ctex)>();
        foreach (var entry in pck.Entries)
        {
            if (entry.Path.EndsWith(".import", StringComparison.Ordinal))
            {
                string logicalPath = entry.Path[..^".import".Length];
                if (!glob.IsMatch(logicalPath)) continue;

                string? ctexPath = FindCtexPath(Encoding.UTF8.GetString(pck.ReadAllBytes(entry)));
                if (ctexPath is not null && byPath.TryGetValue(ctexPath, out var ctex))
                    jobs.Add((logicalPath, ctex));
                else
                    skipped.Add($"{logicalPath}: no .ctex found in its .import file");
            }
            else if (entry.Path.EndsWith(".ctex", StringComparison.Ordinal) && glob.IsMatch(entry.Path))
            {
                jobs.Add((entry.Path, entry));
            }
        }

        var counts = new Dictionary<CtexPayload, int>();
        int written = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (logicalPath, ctexEntry) = jobs[i];
            var image = new CtexImage(pck.ReadAllBytes(ctexEntry));
            counts[image.Payload] = counts.GetValueOrDefault(image.Payload) + 1;

            if (image.EmbeddedFile is null)
            {
                skipped.Add($"{logicalPath}: {image.Payload} {image.PixelFormat} {image.Width}x{image.Height}");
            }
            else
            {
                // strike_defect.png -> strike_defect.webp (the extension must match the real content)
                string outPath = Path.ChangeExtension(OutputPath(outDir, logicalPath, glob.LiteralDirectoryPrefix), image.FileExtension);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, image.EmbeddedFile);
                written++;
            }
            progress?.Report((i + 1, jobs.Count));
        }

        return new ImageExtractionResult(jobs.Count, written, counts, skipped);
    }

    [GeneratedRegex(@"^path(\.\w+)?=""res://(?<p>[^""]+\.ctex)""", RegexOptions.Multiline)]
    private static partial Regex CtexLine();

    /// <summary>Finds the first  path="res://....ctex"  or  path.xxx="res://....ctex"  line of a .import file.</summary>
    internal static string? FindCtexPath(string importFileText)
    {
        var match = CtexLine().Match(importFileText);
        return match.Success ? match.Groups["p"].Value : null;
    }

    /// <summary>"localization/kor/cards.json" with prefix "localization/" -> "&lt;outDir&gt;\kor\cards.json".</summary>
    public static string OutputPath(string outDir, string packPath, string prefixToStrip)
    {
        string relative = packPath.StartsWith(prefixToStrip, StringComparison.OrdinalIgnoreCase)
            ? packPath[prefixToStrip.Length..]
            : packPath;
        string root = Path.GetFullPath(outDir);
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        // A pack path with ".." must never write outside the output folder.
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Pack path escapes the output folder: {packPath}");
        return full;
    }
}
