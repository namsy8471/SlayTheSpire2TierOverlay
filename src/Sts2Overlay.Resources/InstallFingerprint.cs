using System.Security.Cryptography;

namespace Sts2Overlay.Resources;

/// <summary>
/// Identifies the exact game files our local resources were generated from. If ANY field changes (Steam update,
/// file verification, beta branch), the resources are rebuilt.
/// <list type="bullet">
/// <item><b>sts2.dll SHA-256</b>: the card data source. Hashing 9 MB takes ~25 ms, so we can afford it on every start.</item>
/// <item><b>pck size + last write time</b>: the art/text source. It is 1.9 GB, so hashing it (seconds) is too slow;
/// Steam rewrites the file on every update, which changes the time stamp.</item>
/// <item><b>version + commit</b> from release_info.json: makes the manifest readable for humans.</item>
/// </list>
/// A record compares all fields by value, so <c>a == b</c> means "same game files".
/// </summary>
public sealed record InstallFingerprint(
    string DllSha256,
    long DllSize,
    long PckSize,
    long PckWriteTicksUtc,
    string? Version,
    string? Commit)
{
    public static InstallFingerprint Compute(GameInstall install, ReleaseInfo? release)
    {
        string sha;
        long dllSize;
        using (var fs = ReleaseInfo.OpenShared(install.DllPath))
        {
            dllSize = fs.Length;
            sha = Convert.ToHexStringLower(SHA256.HashData(fs));
        }
        var pck = new FileInfo(install.PckPath);
        return new InstallFingerprint(sha, dllSize, pck.Length, pck.LastWriteTimeUtc.Ticks, release?.Version, release?.Commit);
    }

    /// <summary>Short form for logs.</summary>
    public override string ToString()
        => $"{Version ?? "?"} ({Commit ?? "?"}) dll {DllSha256[..Math.Min(12, DllSha256.Length)]}/{DllSize} pck {PckSize}@{new DateTime(PckWriteTicksUtc, DateTimeKind.Utc):yyyy-MM-dd HH:mm:ss}Z";
}
