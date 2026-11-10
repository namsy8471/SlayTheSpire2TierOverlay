using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sts2Overlay.Resources;

/// <summary>
/// <c>&lt;install&gt;/release_info.json</c>, written by the game's build pipeline and read by the game itself
/// (decompiled <c>ReleaseInfoManager</c>; godot.log prints "Found release_info.json" and Sentry's <c>release=v0.107.1</c> from it).
/// <code>{"commit":"59260271","version":"v0.107.1","date":"2026-06-18T15:43:56-07:00","branch":"v0.107.1","main_assembly_hash":-1555940892}</code>
/// </summary>
public sealed record ReleaseInfo(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("commit")] string? Commit,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("main_assembly_hash")] int? MainAssemblyHash)
{
    public static ReleaseInfo? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ReleaseInfo>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The game's own "main assembly hash" (decompiled <c>AssemblyHasher.GetMainAssemblyHash</c>):
    /// the first 4 bytes of SHA-1(sts2.dll) read as a little-endian int. Matching it against
    /// <see cref="MainAssemblyHash"/> proves release_info.json belongs to the sts2.dll that is installed.
    /// </summary>
    public static int ComputeMainAssemblyHash(string dllPath)
    {
        using var fs = OpenShared(dllPath);
        byte[] sha1 = SHA1.HashData(fs);
        return BinaryPrimitives.ReadInt32LittleEndian(sha1);
    }

    /// <summary>Read-only, and tolerant of the game holding the file open.</summary>
    internal static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}

/// <summary>A Slay the Spire 2 install folder (read-only).</summary>
public sealed partial record GameInstall(string Directory)
{
    public const string SteamFolderName = "Slay the Spire 2";

    public string PckPath => Path.Combine(Directory, "SlayTheSpire2.pck");
    public string DataDir => Path.Combine(Directory, "data_sts2_windows_x86_64");
    public string DllPath => Path.Combine(DataDir, "sts2.dll");
    public string ReleaseInfoPath => Path.Combine(Directory, "release_info.json");

    /// <summary>True when the two files we read exist.</summary>
    public bool IsValid => File.Exists(PckPath) && File.Exists(DllPath);

    /// <summary>
    /// Finds the install: <paramref name="configured"/> (settings / command line) → <c>STS2_GAME_DIR</c> → every Steam library
    /// listed in <c>steamapps/libraryfolders.vdf</c> of the Steam folder from the registry and the usual Program Files ones
    /// (<see cref="DefaultSteamRoots"/>). Returns null when nothing valid is found.
    /// </summary>
    public static GameInstall? Locate(string? configured = null, IEnumerable<string>? steamRoots = null)
    {
        foreach (var dir in Candidates(configured, steamRoots ?? DefaultSteamRoots()))
        {
            var install = new GameInstall(dir);
            if (install.IsValid) return install;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string? configured, IEnumerable<string> steamRoots)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        var env = Environment.GetEnvironmentVariable("STS2_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        foreach (var root in steamRoots)
        {
            yield return Path.Combine(root, "steamapps", "common", SteamFolderName);
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            string text;
            try
            {
                if (!File.Exists(vdf)) continue;
                text = File.ReadAllText(vdf);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var lib in ParseLibraryFolders(text))
                yield return Path.Combine(lib, "steamapps", "common", SteamFolderName);
        }
    }

    /// <summary>Where Steam records its own folder (key, value): the Steam client's per-user key, then the installer's.</summary>
    internal static readonly (string Key, string Value)[] SteamRegistryValues =
    [
        (@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
        (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
    ];

    /// <summary>
    /// Steam folders to search, without duplicates: the ones the registry names (Steam installed anywhere, e.g. D:\Steam),
    /// then the usual Program Files folders. <paramref name="readRegistry"/> (key, value) → string is injectable for tests.
    /// </summary>
    internal static IEnumerable<string> DefaultSteamRoots(Func<string, string, string?>? readRegistry = null)
    {
        readRegistry ??= ReadRegistryString;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = SteamRegistryValues.Select(r => readRegistry(r.Key, r.Value))
            .Concat(new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
                .Select(Environment.GetFolderPath)
                .Where(pf => !string.IsNullOrEmpty(pf))
                .Select(pf => Path.Combine(pf, "Steam")));
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim())); // SteamPath uses '/' ("c:/program files (x86)/steam")
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (seen.Add(full)) yield return full;
        }
    }

    private static string? ReadRegistryString(string key, string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Microsoft.Win32.Registry.GetValue(key, value, null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("^\\s*\"path\"\\s*\"(?<p>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Multiline)]
    private static partial Regex VdfPath();

    /// <summary>
    /// Library roots from Steam's <c>libraryfolders.vdf</c> (KeyValues text: <c>"path"  "D:\\SteamLibrary"</c>, backslashes doubled).
    /// </summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdfText)
        => VdfPath().Matches(vdfText).Select(m => m.Groups["p"].Value.Replace(@"\\", @"\", StringComparison.Ordinal)).ToList();
}

/// <summary>What we know about the installed game: where, which version, and a fingerprint of its files.</summary>
/// <param name="Version">From release_info.json; null if that file is missing or has no parsable version.</param>
/// <param name="AssemblyHashMatches">release_info's main_assembly_hash vs. the installed sts2.dll; null when unknown.</param>
public sealed record InstalledGame(
    GameInstall Install,
    ReleaseInfo? Release,
    GameVersion? Version,
    bool? AssemblyHashMatches,
    InstallFingerprint Fingerprint)
{
    /// <summary>Label used in file names and the UI: "v0.107.1", or "unknown-&lt;sha&gt;" when no version is known.</summary>
    public string VersionLabel => Version?.Text ?? "unknown-" + Fingerprint.DllSha256[..8];

    /// <summary>Reads release_info.json and fingerprints the install (≈30 ms: SHA-256 + SHA-1 of the 9 MB sts2.dll).</summary>
    public static InstalledGame Detect(GameInstall install)
    {
        var release = ReleaseInfo.TryLoad(install.ReleaseInfoPath);
        GameVersion.TryParse(release?.Version, out var version);
        bool? hashOk = release?.MainAssemblyHash is { } expected
            ? ReleaseInfo.ComputeMainAssemblyHash(install.DllPath) == expected
            : null;
        return new InstalledGame(install, release, version, hashOk, InstallFingerprint.Compute(install, release));
    }
}
