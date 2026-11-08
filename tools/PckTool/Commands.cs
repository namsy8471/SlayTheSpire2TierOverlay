using System.Text;
using System.Text.RegularExpressions;

namespace PckTool;

public static class Commands
{
    public static int List(PckReader pck, string? filter)
    {
        var glob = filter is null ? null : new Glob(filter);
        int count = 0;
        foreach (var entry in pck.Entries)
        {
            if (glob is not null && !glob.IsMatch(entry.Path)) continue;
            Console.WriteLine($"{entry.Size,12:N0}  {entry.Path}{(entry.Encrypted ? "  [encrypted]" : "")}");
            count++;
        }
        Console.WriteLine($"{count} file(s)");
        return 0;
    }

    public static int Extract(PckReader pck, string pattern, string outDir)
    {
        var glob = new Glob(pattern);
        int count = 0;
        foreach (var entry in pck.Entries.Where(e => glob.IsMatch(e.Path)))
        {
            string outPath = OutputPath(outDir, entry.Path, glob.LiteralDirectoryPrefix);
            pck.ExtractTo(entry, outPath);
            Console.WriteLine($"{entry.Path} -> {outPath}");
            count++;
        }
        Console.WriteLine($"Extracted {count} file(s) to {Path.GetFullPath(outDir)}");
        return count > 0 ? 0 : 2;
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
    public static int ExtractImages(PckReader pck, string pattern, string outDir)
    {
        var glob = new Glob(pattern);
        var byPath = pck.Entries.ToDictionary(e => e.Path);

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
                    Console.WriteLine($"SKIP {logicalPath}: no .ctex found in its .import file");
            }
            else if (entry.Path.EndsWith(".ctex", StringComparison.Ordinal) && glob.IsMatch(entry.Path))
            {
                jobs.Add((entry.Path, entry));
            }
        }

        var counts = new Dictionary<CtexPayload, int>();
        foreach (var (logicalPath, ctexEntry) in jobs)
        {
            var image = new CtexImage(pck.ReadAllBytes(ctexEntry));
            counts[image.Payload] = counts.GetValueOrDefault(image.Payload) + 1;

            if (image.EmbeddedFile is null)
            {
                Console.WriteLine($"SKIP {logicalPath}: {image.Payload} {image.PixelFormat} {image.Width}x{image.Height}");
                continue;
            }

            // strike_defect.png -> strike_defect.webp (the extension must match the real content)
            string outPath = Path.ChangeExtension(OutputPath(outDir, logicalPath, glob.LiteralDirectoryPrefix), image.FileExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, image.EmbeddedFile);
        }

        Console.WriteLine($"Matched {jobs.Count} texture(s), output: {Path.GetFullPath(outDir)}");
        foreach (var (payload, n) in counts.OrderBy(kv => kv.Key))
        {
            string action = payload is CtexPayload.Webp or CtexPayload.Png ? "written" : "skipped";
            Console.WriteLine($"  {payload,-16} {n,6}  {action}");
        }
        return jobs.Count > 0 ? 0 : 2;
    }

    /// <summary>Finds the first  path="res://....ctex"  or  path.xxx="res://....ctex"  line of a .import file.</summary>
    private static string? FindCtexPath(string importFileText)
    {
        var match = Regex.Match(importFileText, @"^path(\.\w+)?=""res://(?<p>[^""]+\.ctex)""", RegexOptions.Multiline);
        return match.Success ? match.Groups["p"].Value : null;
    }

    /// <summary>"localization/kor/cards.json" with prefix "localization/" -> "&lt;outDir&gt;\kor\cards.json".</summary>
    private static string OutputPath(string outDir, string packPath, string prefixToStrip)
    {
        string relative = packPath.StartsWith(prefixToStrip, StringComparison.OrdinalIgnoreCase)
            ? packPath[prefixToStrip.Length..]
            : packPath;
        return Path.Combine(outDir, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
