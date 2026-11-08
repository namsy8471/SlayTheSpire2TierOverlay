namespace Sts2Overlay.Core.Tests;

/// <summary>Locates repository test assets regardless of the test runner's working directory.</summary>
internal static class TestPaths
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SlayTheSpire2TierOverlay.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    });

    public static string RepoRoot => Root.Value;

    public static string RunsDir => Path.Combine(RepoRoot, "tests", "fixtures", "runs");

    public static IReadOnlyList<string> AllRuns => Directory.GetFiles(RunsDir, "*.run").OrderBy(p => p, StringComparer.Ordinal).ToArray();

    public static string Run(string fileName) => Path.Combine(RunsDir, fileName);

    public static IEnumerable<object[]> RunFileNames => AllRuns.Select(p => new object[] { Path.GetFileName(p) });
}
