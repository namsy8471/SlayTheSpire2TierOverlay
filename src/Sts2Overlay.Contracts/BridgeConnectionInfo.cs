using System.Security.Cryptography;
using System.Text.Json;

namespace Sts2Overlay.Contracts;

/// <summary>
/// Contents of <c>%LOCALAPPDATA%\Sts2Overlay\bridge.json</c>, written by the mod when its
/// WebSocket server starts and read by the app to find and authenticate to it.
/// </summary>
public sealed record BridgeConnectionInfo
{
    /// <summary>Loopback TCP port the server listens on.</summary>
    public int Port { get; set; }

    /// <summary>Random per-start secret; the client sends it in <see cref="BridgeTransport.TokenHeader"/>.</summary>
    public string Token { get; set; } = "";

    /// <summary>Game process id, used to detect a stale file left by a crashed game.</summary>
    public int Pid { get; set; }

    /// <summary>Protocol version of the server.</summary>
    public int Protocol { get; set; } = ProtocolVersion.Current;

    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>Reads and writes <see cref="BridgeConnectionInfo"/> files. All methods take an explicit path.</summary>
public static class BridgeConnectionFile
{
    /// <summary>File name inside the <c>Sts2Overlay</c> folder.</summary>
    public const string FileName = "bridge.json";

    /// <summary>The standard location: <c>%LOCALAPPDATA%\Sts2Overlay\bridge.json</c>.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sts2Overlay",
            FileName);

    /// <summary>Creates a 256-bit URL-safe random token.</summary>
    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Writes the file atomically (temp file + rename), creating the directory if needed,
    /// so a concurrent reader never sees a half-written file.
    /// </summary>
    public static void Write(string path, BridgeConnectionInfo info)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(info);

        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(info, BridgeJsonContext.Default.BridgeConnectionInfo));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads the file; returns null if it is missing, unreadable or malformed.</summary>
    public static BridgeConnectionInfo? TryRead(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            var info = JsonSerializer.Deserialize(bytes, BridgeJsonContext.Default.BridgeConnectionInfo);
            return info is null || info.Port is <= 0 or > 65535 || string.IsNullOrEmpty(info.Token) ? null : info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Deletes the file if it exists (the mod calls this on shutdown). Never throws for I/O errors.</summary>
    public static void TryDelete(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
