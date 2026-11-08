using MegaCrit.Sts2.Core.Logging;

namespace Sts2Overlay.Bridge;

/// <summary>
/// Writes to both the game's log (godot.log) and our own file so spike results are easy to find.
/// Our file: %LOCALAPPDATA%\Sts2Overlay\bridge.log
/// </summary>
internal static class BridgeLog
{
    private static readonly object Gate = new();

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay");

    private static readonly string FilePath = Path.Combine(Dir, "bridge.log");

    public static void Info(string message)
    {
        string line = $"[Sts2Overlay] {message}";
        try { Log.Info(line); } catch { /* game logger not ready: ignore */ }
        Append(line);
    }

    public static void Error(string message, Exception? ex = null)
    {
        string line = $"[Sts2Overlay] ERROR {message}{(ex is null ? "" : "\n" + ex)}";
        try { Log.Error(line); } catch { }
        Append(line);
    }

    private static void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the game.
        }
    }
}
