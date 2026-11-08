namespace Sts2Overlay.GameState.Tests;

/// <summary>Locates test assets regardless of the runner's working directory.</summary>
internal static class TestPaths
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SlayTheSpire2TierOverlay.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    });

    public static string RepoRoot => Root.Value;

    /// <summary>Anonymised log excerpts (Steam IDs → 76561190000000001 = user, ...002 = friend).</summary>
    public static string Fixture(string name) => Path.Combine(RepoRoot, "tests", "Sts2Overlay.GameState.Tests", "Fixtures", name);

    public static string[] FixtureLines(string name) => File.ReadAllLines(Fixture(name));

    /// <summary>Shared anonymised run-history files (see tests/fixtures/README.md).</summary>
    public static string RunsDir => Path.Combine(RepoRoot, "tests", "fixtures", "runs");

    public const ulong Me = 76561190000000001;
    public const ulong Friend = 76561190000000002;

    /// <summary>The user's real game folder (read-only tests skip themselves when it is missing).</summary>
    public static string RealRoot => GamePaths.DefaultRoot;
}
