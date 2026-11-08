using System.Text.Json;

namespace Sts2Overlay.Resources;

/// <summary>
/// Where the generated files live inside the local data folder (<c>data/local/</c>, gitignored).
/// Paths match the ones the README's manual commands always used, so older checkouts keep working.
/// </summary>
public sealed record LocalLayout(string LocalDir)
{
    public string ManifestPath => Path.Combine(LocalDir, "manifest.json");
    public string PckDir => Path.Combine(LocalDir, "pck");
    public string CardArtDir => Path.Combine(PckDir, "card_art");
    public string LocalizationDir => Path.Combine(PckDir, "localization");
    /// <summary>Small relic icons cut out of the relic atlas (<see cref="Pck.RelicIconExtractor"/>): <c>&lt;name&gt;.png</c>.</summary>
    public string RelicIconDir => Path.Combine(PckDir, "relic_icons");
    public string CatalogPath(string versionLabel) => Path.Combine(LocalDir, $"catalog.{versionLabel}.json");
    internal string StagingDir => Path.Combine(LocalDir, ".prepare-staging");
    internal string LockPath => Path.Combine(LocalDir, ".prepare.lock");

    /// <summary>The catalog the manifest points to, if both exist.</summary>
    public string? ManifestCatalog()
    {
        var m = ResourceManifest.TryLoad(ManifestPath);
        if (m?.CatalogFile is not { Length: > 0 } name) return null;
        var path = Path.Combine(LocalDir, name);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>
/// <c>data/local/manifest.json</c>: what was generated, from which game files, and when. Written LAST by
/// <see cref="ResourcePreparer"/>, so a crash mid-way leaves no manifest (or the old one) and the next start rebuilds.
/// </summary>
public sealed class ResourceManifest
{
    public int Schema { get; set; } = 1;

    /// <summary><see cref="ResourcePreparer.FormatVersion"/> at build time: bumping it in code forces a rebuild everywhere.</summary>
    public int PrepareFormat { get; set; }

    public string? GameVersion { get; set; }
    public string? GameCommit { get; set; }
    public string? GameDir { get; set; }
    public InstallFingerprint? Fingerprint { get; set; }

    public DateTime PreparedAtUtc { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>Catalog file name relative to the local folder, e.g. <c>catalog.v0.107.1.json</c>.</summary>
    public string? CatalogFile { get; set; }

    public int CatalogCards { get; set; }
    public int CardArtFiles { get; set; }
    public int LocalizationFiles { get; set; }

    /// <summary>Relic icons written (0 when the pack has no relic atlas, e.g. a test pack).</summary>
    public int RelicIconFiles { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ResourceManifest? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ResourceManifest>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // a broken manifest is the same as none: rebuild
        }
    }

    /// <summary>Atomic save: write a temp file, then swap it in (a crash never leaves half a manifest).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
