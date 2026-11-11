using System.IO;

namespace Sts2Overlay.App.Infrastructure;

/// <summary>How the running exe found its data (see <see cref="AppLayout.Resolve"/>).</summary>
public enum AppLayoutKind
{
    /// <summary>Inside a repo checkout (dotnet run, bin/..., or dist\Sts2Overlay.exe): everything under the repo's data/.</summary>
    Checkout,

    /// <summary>Installed/published: data packs + catalog published next to the exe, generated files in %LOCALAPPDATA%.</summary>
    Installed,

    /// <summary>Neither: no data packs anywhere (the app still starts and says so); generated files in %LOCALAPPDATA%.</summary>
    Fallback,
}

/// <summary>
/// Where the read-only data (<c>data/packs</c>, <c>data/catalog</c>) and the generated local data (extracted card art,
/// localization, catalog, manifest.json) live for one exe location. Pure: the file system is reached only through the
/// injected existence check, so every case is unit-tested (tests/Sts2Overlay.App.Tests/AppLayoutTests.cs).
/// </summary>
/// <param name="ContentRoot">The folder that holds <c>data/packs</c> and <c>data/catalog</c> (repo root or exe folder).</param>
/// <param name="LocalDataDir">Generated, user-PC-only data (never shipped, never committed).</param>
/// <param name="Kind">Which rule matched.</param>
public sealed record AppLayout(string ContentRoot, string LocalDataDir, AppLayoutKind Kind)
{
    /// <summary>Files that only a source checkout has (a worktree's <c>.git</c> is a file, hence "exists", not "directory exists").</summary>
    private static readonly string[] CheckoutMarkers = ["SlayTheSpire2TierOverlay.sln", ".git"];

    /// <summary>The repo root when running from a checkout, else null.</summary>
    public string? RepoRoot => Kind == AppLayoutKind.Checkout ? ContentRoot : null;

    /// <summary>
    /// Resolves the layout for an exe in <paramref name="exeDir"/>, first match wins:
    /// <list type="number">
    /// <item>an ancestor (or the exe folder) with <c>data/packs</c> AND a checkout marker (.sln or .git) → <see cref="AppLayoutKind.Checkout"/>,
    /// exactly as before (repo <c>data/local</c>). Checked first so dist\Sts2Overlay.exe keeps the repo data even if
    /// published data files ever end up next to it;</item>
    /// <item><c>data/packs</c> next to the exe → <see cref="AppLayoutKind.Installed"/>: read-only data from the exe folder,
    /// generated data in <c>%LOCALAPPDATA%\Sts2Overlay\local</c> (Velopack replaces the install folder on every update);</item>
    /// <item>an ancestor with <c>data/packs</c> but no marker (a copied/unzipped repo) → <see cref="AppLayoutKind.Checkout"/>, the old rule;</item>
    /// <item>otherwise <see cref="AppLayoutKind.Fallback"/>: the exe folder, generated data in %LOCALAPPDATA%.</item>
    /// </list>
    /// </summary>
    /// <param name="exeDir">Folder of the running exe (<see cref="AppContext.BaseDirectory"/>).</param>
    /// <param name="localAppData">%LOCALAPPDATA%.</param>
    /// <param name="exists">True when a file or directory exists at the path.</param>
    public static AppLayout Resolve(string exeDir, string localAppData, Func<string, bool> exists)
    {
        var exe = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDir));
        var userLocal = Path.Combine(localAppData, AppPaths.DataFolderName, "local");
        bool HasPacks(string dir) => exists(Path.Combine(dir, "data", "packs"));

        foreach (var dir in SelfAndAncestors(exe))
        {
            if (HasPacks(dir) && CheckoutMarkers.Any(m => exists(Path.Combine(dir, m))))
            {
                return Checkout(dir);
            }
        }
        if (HasPacks(exe)) return new AppLayout(exe, userLocal, AppLayoutKind.Installed);
        foreach (var dir in SelfAndAncestors(exe).Skip(1))
        {
            if (HasPacks(dir)) return Checkout(dir);
        }
        return new AppLayout(exe, userLocal, AppLayoutKind.Fallback);

        static AppLayout Checkout(string root) => new(root, Path.Combine(root, "data", "local"), AppLayoutKind.Checkout);
    }

    private static IEnumerable<string> SelfAndAncestors(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            yield return Path.TrimEndingDirectorySeparator(d.FullName);
        }
    }
}

/// <summary>Where the app reads and writes files.</summary>
public static class AppPaths
{
    /// <summary>
    /// The data version (pack folder <c>data/packs/&lt;v&gt;</c> + catalogs <c>data/catalog/*.&lt;v&gt;.json</c>) chosen for the
    /// installed game by <see cref="Sts2Overlay.Resources.DataVersionSelector"/>. Set once at startup
    /// (<see cref="SelectDataVersion"/>); a game update while the overlay runs takes effect at the next start.
    /// </summary>
    public static string DataVersion { get; private set; } = "v0.107.1";

    /// <summary>Default data pack, relative to the content root (the repo root, or the exe folder when installed).</summary>
    public static string DefaultPackDir => "data/packs/" + DataVersion;

    /// <summary>
    /// Picks <see cref="DataVersion"/> from the pack folders under the content root and the game version
    /// (release_info.json; null when unknown). The one place the app matches data to the game.
    /// </summary>
    public static Sts2Overlay.Resources.DataVersionChoice SelectDataVersion(string? gameVersion)
    {
        var packs = Path.Combine(ContentRoot, "data", "packs");
        var names = Directory.Exists(packs) ? Directory.GetDirectories(packs).Select(Path.GetFileName).OfType<string>() : [];
        var choice = Sts2Overlay.Resources.DataVersionSelector.Select(names, gameVersion);
        if (choice.Version is { } v) DataVersion = v;
        return choice;
    }

    /// <summary>Our folder under %LOCALAPPDATA%.</summary>
    public const string DataFolderName = "Sts2Overlay";

    /// <summary><c>%LOCALAPPDATA%\Sts2Overlay</c>: settings, log, debug captures (and, when installed, <c>local</c>). Never committed.</summary>
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);

    public static string LogFile => Path.Combine(DataRoot, "app.log");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string CapturesDir => Path.Combine(DataRoot, "captures");

    /// <summary>Play-time decision log, one <c>&lt;runkey&gt;.jsonl</c> per run (<see cref="Services.DecisionLogStore"/>).</summary>
    public static string DecisionsDir => Path.Combine(DataRoot, "decisions");

    /// <summary>Resolved once per process from the exe location (see <see cref="AppLayout.Resolve"/>).</summary>
    public static AppLayout Layout { get; } = AppLayout.Resolve(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        p => Directory.Exists(p) || File.Exists(p));

    /// <summary>The folder that holds <c>data/packs</c> and <c>data/catalog</c>: the repo root, or the exe folder when installed.</summary>
    public static string ContentRoot => Layout.ContentRoot;

    /// <summary>The committed names-free catalog of <see cref="DataVersion"/> (<c>data/catalog/cards.&lt;v&gt;.json</c>), shipped with installed builds too.</summary>
    public static string CommittedCatalogFile => Path.Combine(ContentRoot, "data", "catalog", $"cards.{DataVersion}.json");

    /// <summary>
    /// Resolves the pack directory: an absolute path is used as-is, a relative one is taken from the content root
    /// (repo root, or the exe folder when installed). Null means <see cref="DefaultPackDir"/>.
    /// </summary>
    public static string ResolvePackDir(string? configured)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultPackDir : configured;
        if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
        return Path.GetFullPath(Path.Combine(ContentRoot, value));
    }

    /// <summary>
    /// Generated local data: extracted card art, localization, catalog, manifest.json. In a checkout it is the repo's
    /// <c>data/local</c> (gitignored); installed it is <c>%LOCALAPPDATA%\Sts2Overlay\local</c>, which survives updates.
    /// Generated automatically on first start by <see cref="Services.LocalResourceService"/>.
    /// </summary>
    public static string LocalDataDir => Layout.LocalDataDir;

    /// <summary>
    /// Locally generated card catalog with Korean names (<c>&lt;local&gt;/catalog.&lt;version&gt;.json</c>, gitignored),
    /// used only for display names. Returns null when none exists; the UI then shows prettified ids.
    /// </summary>
    public static string? FindLocalCatalog(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.IsPathRooted(configured) ? configured : Path.Combine(ContentRoot, configured);
            return File.Exists(full) ? Path.GetFullPath(full) : null;
        }
        // The catalog the last automatic preparation wrote (matches the installed game), else the newest by name.
        var local = LocalDataDir;
        if (new Sts2Overlay.Resources.LocalLayout(local).ManifestCatalog() is { } prepared) return prepared;
        if (!Directory.Exists(local)) return null;
        return Directory.GetFiles(local, "catalog.*.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
    }
}
