using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Sts2Overlay.App.Infrastructure;

/// <summary>
/// Tiny thread-safe file logger: <c>%LOCALAPPDATA%\Sts2Overlay\app.log</c>.
/// The capture worker, WinEvent callbacks and the UI thread all write here, hence the lock.
/// The previous log is kept as <c>app.log.1</c> when it grows past 5 MB (checked on every write).
/// Users attach this file to bug reports, so every line is <see cref="Redact">redacted</see> first (Steam ID, Windows
/// user folder, e-mail), and an error repeated back to back is written once with a count (<see cref="RepeatThrottle"/>).
/// </summary>
public static partial class AppLog
{
    internal const long MaxBytes = 5 * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static readonly RepeatThrottle ErrorRepeats = new(TimeSpan.FromMinutes(1));
    private static readonly string? UserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
    private static string? _path;
    private static long _size;

    public static string? FilePath => _path;

    /// <summary>The exe's informational version ("0.1.0+abc123"), for the start line.</summary>
    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    public static void Init(string path)
    {
        lock (Gate)
        {
            _path = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                _size = info.Exists ? info.Length : 0;
                if (_size > MaxBytes) Rotate();
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
        message = Redact(message, UserProfile);
        var now = DateTime.Now;
        lock (Gate)
        {
            // Errors only: the capture worker can fail with the same stack trace several times a second for as long as
            // the cause lasts. Other lines between two equal errors do not break the repetition.
            IReadOnlyList<string> messages = level == "ERROR" ? ErrorRepeats.Next(message, now) : [message];
            foreach (var m in messages)
            {
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] {m}");
                Debug.WriteLine(line);
                Append(line + Environment.NewLine);
            }
        }
    }

    /// <summary>Appends (caller holds <see cref="Gate"/>), rotating first when the file would pass <see cref="MaxBytes"/>.</summary>
    private static void Append(string text)
    {
        if (_path is null) return;
        try
        {
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (_size + bytes > MaxBytes) Rotate();
            File.AppendAllText(_path, text);
            _size += bytes;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>app.log → app.log.1 (replacing the older one). Caller holds <see cref="Gate"/>.</summary>
    private static void Rotate()
    {
        if (_path is null) return;
        try
        {
            if (File.Exists(_path)) File.Move(_path, _path + ".1", overwrite: true);
            _size = 0;
        }
        catch (IOException)
        {
            // Another process has it open (a second overlay, an editor): try again on a later write.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- privacy -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Removes what identifies the user from a log line: the Windows user folder (<paramref name="userProfile"/>, any
    /// case, either slash; other <c>X:\Users\name</c> folders such as 8.3 short names too), SteamID64s (<c>7656</c> + 13
    /// digits: the game's save folder and co-op ids) and e-mail addresses.
    /// </summary>
    internal static string Redact(string text, string? userProfile)
    {
        if (!string.IsNullOrEmpty(userProfile) && userProfile.Length > 3)
        {
            // C:\Users\Name → C:[\\/]+Users[\\/]+Name (doubled backslashes appear in JSON / escaped text). The lookahead
            // keeps C:\Users\Sam from matching inside C:\Users\Samantha.
            var parts = userProfile.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
            text = Regex.Replace(text, string.Join(@"[\\/]+", parts) + @"(?=[\\/""'\s,;:)\]]|$)", "%USERPROFILE%",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        text = OtherUserFolder().Replace(text, "${prefix}<user>");
        text = SteamId64().Replace(text, "<steamid>");
        return Email().Replace(text, "<email>");
    }

    [GeneratedRegex(@"(?<prefix>\b[A-Za-z]:[\\/]+Users[\\/]+)(?!(?:Public|Default|All Users)(?:[\\/]|\b))[^\\/\s""'<>|:*?]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherUserFolder();

    [GeneratedRegex(@"(?<!\d)7656\d{13}(?!\d)")]
    private static partial Regex SteamId64();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();
}

/// <summary>
/// Identical consecutive messages: the first is written, the repeats are counted, and at most once per window a
/// "… repeated N more times" line says how many were dropped (also when a different message ends the streak).
/// Not thread-safe (<see cref="AppLog"/> calls it under its lock); pure, so it is unit-tested.
/// </summary>
internal sealed class RepeatThrottle(TimeSpan window)
{
    private string? _last;
    private DateTime _lastWritten;
    private int _dropped;

    /// <summary>The lines to write for <paramref name="message"/> at <paramref name="now"/>: none, one or two.</summary>
    public IReadOnlyList<string> Next(string message, DateTime now)
    {
        if (message == _last)
        {
            _dropped++;
            if (now - _lastWritten < window) return [];
            var summary = Summary(_dropped, message);
            _dropped = 0;
            _lastWritten = now;
            return [summary];
        }
        var lines = new List<string>(2);
        if (_dropped > 0) lines.Add(Summary(_dropped, _last!));
        lines.Add(message);
        _last = message;
        _lastWritten = now;
        _dropped = 0;
        return lines;
    }

    private static string Summary(int count, string message)
    {
        var firstLine = message.AsSpan();
        var nl = firstLine.IndexOfAny('\r', '\n');
        if (nl >= 0) firstLine = firstLine[..nl];
        if (firstLine.Length > 160) firstLine = firstLine[..160];
        return string.Create(CultureInfo.InvariantCulture, $"… repeated {count} more times: {firstLine}");
    }
}
