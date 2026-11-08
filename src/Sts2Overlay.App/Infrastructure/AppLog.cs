using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Sts2Overlay.App.Infrastructure;

/// <summary>
/// Tiny thread-safe file logger: <c>%LOCALAPPDATA%\Sts2Overlay\app.log</c>.
/// The capture worker, WinEvent callbacks and the UI thread all write here, hence the lock.
/// The previous log is kept as <c>app.log.1</c> when it grows past 5 MB.
/// </summary>
public static class AppLog
{
    private static readonly Lock Gate = new();
    private static string? _path;

    public static string? FilePath => _path;

    public static void Init(string path)
    {
        lock (Gate)
        {
            _path = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 5 * 1024 * 1024)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] {message}");
        Debug.WriteLine(line);
        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
