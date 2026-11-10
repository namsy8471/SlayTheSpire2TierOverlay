using System.Globalization;

namespace Sts2Overlay.GameState;

/// <summary>
/// Where the game keeps its live data:
/// <c>%APPDATA%\SlayTheSpire2\steam\&lt;SteamID&gt;\profile&lt;N&gt;\saves</c> and <c>%APPDATA%\SlayTheSpire2\logs\godot.log</c>.
/// </summary>
/// <param name="SavesDirectory">Folder holding <c>current_run.save</c> / <c>current_run_mp.save</c>, or null if not found.</param>
/// <param name="LogFile">Path of <c>godot.log</c> (may not exist yet), or null.</param>
/// <param name="SteamId">The Steam ID from the folder name. In co-op this is our <c>net_id</c>.</param>
public sealed record GamePaths(string? SavesDirectory, string? LogFile, ulong? SteamId)
{
    public const string LogFileName = "godot.log";

    /// <summary>
    /// The data root (<c>%APPDATA%\SlayTheSpire2</c>) the saves folder was discovered under, so the tracker can look
    /// again later (folder created on the game's first run, another profile picked in the game). Null when the
    /// saves folder was given explicitly: then it is never changed.
    /// </summary>
    public string? Root { get; init; }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2");

    /// <summary>
    /// Auto-discovers the paths. Overrides win; otherwise the most recently written
    /// <c>steam/&lt;id&gt;/profile&lt;N&gt;/saves</c> folder is picked (a PC can have several Steam accounts).
    /// </summary>
    public static GamePaths Discover(string? root = null, string? savesOverride = null, string? logOverride = null)
    {
        root ??= DefaultRoot;
        string? saves = savesOverride ?? FindSavesDirectory(root);
        string log = logOverride ?? Path.Combine(root, "logs", LogFileName);
        return new GamePaths(saves, log, saves != null ? SteamIdFromPath(saves) : null) { Root = savesOverride is null ? root : null };
    }

    /// <summary><c>root\steam\&lt;steamId&gt;\&lt;profile&gt;\saves</c>: the folder the log's profile line names.</summary>
    public static string SavesDirectoryFor(string root, ulong steamId, string profile) =>
        Path.Combine(root, "steam", steamId.ToString(CultureInfo.InvariantCulture), profile, "saves");

    /// <summary>Most recently written <c>steam/&lt;digits&gt;/profile*/saves</c> under <paramref name="root"/>.</summary>
    public static string? FindSavesDirectory(string root)
    {
        string steamDir = Path.Combine(root, "steam");
        if (!Directory.Exists(steamDir)) return null;

        var candidates =
            from account in Directory.EnumerateDirectories(steamDir)
            where ulong.TryParse(Path.GetFileName(account), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            from profile in Directory.EnumerateDirectories(account, "profile*") // skips "modded/" (archived mod profile)
            let saves = Path.Combine(profile, "saves")
            where Directory.Exists(saves)
            select (saves, written: LastWrite(saves));

        return candidates.OrderByDescending(c => c.written).Select(c => c.saves).FirstOrDefault();
    }

    /// <summary>Newest write time among the folder and the files directly in it.</summary>
    private static DateTime LastWrite(string dir)
    {
        DateTime newest = Directory.GetLastWriteTimeUtc(dir);
        foreach (string f in Directory.EnumerateFiles(dir))
        {
            DateTime t = File.GetLastWriteTimeUtc(f);
            if (t > newest) newest = t;
        }
        return newest;
    }

    /// <summary>Finds the all-digit folder above <c>profileN</c> in a saves path, e.g. <c>...\steam\7656...\profile1\saves</c>.</summary>
    public static ulong? SteamIdFromPath(string savesDir)
    {
        foreach (string part in Path.GetFullPath(savesDir).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Reverse())
        {
            // Steam IDs are 17 digits; "profile1" etc. are not all digits so they are skipped.
            if (part.Length >= 10 && ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                return id;
        }
        return null;
    }
}
