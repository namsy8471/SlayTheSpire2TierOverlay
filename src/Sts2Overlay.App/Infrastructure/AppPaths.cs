using System.IO;

namespace Sts2Overlay.App.Infrastructure;

/// <summary>Where the app reads and writes files.</summary>
public static class AppPaths
{
    /// <summary>Default data pack, relative to the repo root (the real v0.107.1 pack replaces it later).</summary>
    public const string DefaultPackDir = "data/packs/sample";

    /// <summary><c>%LOCALAPPDATA%\Sts2Overlay</c>: settings, log, debug captures. Never committed.</summary>
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay");

    public static string LogFile => Path.Combine(DataRoot, "app.log");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string CapturesDir => Path.Combine(DataRoot, "captures");

    /// <summary>
    /// The repository root, found by walking up from the exe folder until a <c>data/packs</c> folder appears.
    /// During development the exe lives in <c>src/Sts2Overlay.App/bin/...</c>, so this finds the checkout.
    /// </summary>
    public static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "packs"))) return dir.FullName;
        }
        return null;
    }

    /// <summary>
    /// Resolves the pack directory: an absolute path is used as-is, a relative one is taken from the repo root
    /// (or the exe folder when there is no repo). Null means <see cref="DefaultPackDir"/>.
    /// </summary>
    public static string ResolvePackDir(string? configured)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultPackDir : configured;
        if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
        var root = FindRepoRoot() ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(root, value));
    }

    /// <summary>
    /// Locally generated card catalog with Korean names (<c>data/local/catalog.&lt;version&gt;.json</c>, gitignored),
    /// used only for display names. Returns null when none exists; the UI then shows prettified ids.
    /// </summary>
    public static string? FindLocalCatalog(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.IsPathRooted(configured) ? configured : Path.Combine(FindRepoRoot() ?? AppContext.BaseDirectory, configured);
            return File.Exists(full) ? Path.GetFullPath(full) : null;
        }
        var root = FindRepoRoot();
        if (root is null) return null;
        var local = Path.Combine(root, "data", "local");
        if (!Directory.Exists(local)) return null;
        return Directory.GetFiles(local, "catalog.*.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
    }
}
