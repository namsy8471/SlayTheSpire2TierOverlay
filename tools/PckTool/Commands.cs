using Sts2Overlay.Resources.Pck;

namespace PckTool;

/// <summary>Console front-end for <see cref="PckExtractor"/> (the logic lives in src/Sts2Overlay.Resources).</summary>
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
        int count = PckExtractor.ExtractFiles(pck, pattern, outDir, (from, to) => Console.WriteLine($"{from} -> {to}"));
        Console.WriteLine($"Extracted {count} file(s) to {Path.GetFullPath(outDir)}");
        return count > 0 ? 0 : 2;
    }

    public static int ExtractImages(PckReader pck, string pattern, string outDir)
    {
        var result = PckExtractor.ExtractImages(pck, pattern, outDir);
        foreach (var skip in result.Skipped) Console.WriteLine("SKIP " + skip);

        Console.WriteLine($"Matched {result.Matched} texture(s), output: {Path.GetFullPath(outDir)}");
        foreach (var (payload, n) in result.Counts.OrderBy(kv => kv.Key))
        {
            string action = payload is CtexPayload.Webp or CtexPayload.Png ? "written" : "skipped";
            Console.WriteLine($"  {payload,-16} {n,6}  {action}");
        }
        return result.Matched > 0 ? 0 : 2;
    }
}
