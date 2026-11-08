namespace Sts2Overlay.GameState.IO;

/// <summary>
/// Reads files the game may be writing at the same moment. We never take a lock that could make
/// the game's own write/rename fail: the game must always win.
/// </summary>
public static class SharedFile
{
    /// <summary>
    /// Opens with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>: "other processes may
    /// keep writing, renaming or deleting this file while we hold it". Without Delete, the game's
    /// <c>.tmp → current_run.save</c> rename would fail while we are reading.
    /// </summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024);

    /// <summary>
    /// Reads the whole file as UTF-8, retrying on <see cref="IOException"/> (a sharing violation while the
    /// game holds an exclusive handle for a few ms). Returns null if the file does not exist.
    /// </summary>
    public static string? ReadAllText(string path, int attempts = 6, int delayMs = 50) =>
        ReadAllBytes(path, attempts, delayMs) is { } bytes ? Decode(bytes) : null;

    /// <summary>UTF-8 decode, skipping a BOM if present.</summary>
    public static string Decode(byte[] bytes) =>
        bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])
            ? System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : System.Text.Encoding.UTF8.GetString(bytes);

    /// <summary>Like <see cref="ReadAllText"/> but returns the raw bytes (their count = file size).</summary>
    public static byte[]? ReadAllBytes(string path, int attempts = 6, int delayMs = 50)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                using FileStream fs = OpenShared(path);
                using var ms = new MemoryStream((int)Math.Min(fs.Length, int.MaxValue));
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) when (i < attempts)
            {
                Thread.Sleep(delayMs * i); // back off a little more each time
            }
        }
    }
}
