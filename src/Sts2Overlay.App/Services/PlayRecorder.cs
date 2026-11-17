using System.Globalization;
using System.IO;
using Path = System.IO.Path;

namespace Sts2Overlay.App.Services;

/// <summary>
/// "플레이 기록 보관" (settings.json <c>playRecord</c>, off by default): keeps a local copy of the game's run saves and
/// godot.log while the overlay runs, so a fight or a screen that went wrong can be replayed later (the combat helper's
/// <c>live --replay --saves &lt;dir&gt; --logs &lt;file&gt;</c> reads this layout directly).
/// <code>
///  &lt;dir&gt;\save_yyyyMMdd_HHmmss_fff.json         current_run.save, one copy per change (name = its write time, UTC)
///  &lt;dir&gt;\mp_save_yyyyMMdd_HHmmss_fff.json      current_run_mp.save (co-op), the same way
///  &lt;dir&gt;\sessions\godot_session_yyyyMMdd_HHmmss.log   godot.log, one file per game session, refreshed as it grows
/// </code>
/// Read-only on the game's files; nothing leaves the PC. The layout is the one <c>datawatch.ps1</c> writes, so a folder
/// can switch from that script to the overlay. Both writing at once would split a session into two mirrors, so the
/// recorder pauses while another program's session mirror in the folder changed in the last
/// <see cref="ForeignWriterWindow"/>.
/// <para>Not thread-safe: one caller (a timer) at a time.</para>
/// </summary>
public sealed class PlayRecorder
{
    public const string SessionsFolder = "sessions";

    /// <summary>A session mirror we did not write, changed this recently: another recorder is running.</summary>
    public static readonly TimeSpan ForeignWriterWindow = TimeSpan.FromSeconds(15);

    /// <summary>A save written this recently may still be in progress: copied on a later step.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(300);

    private const string StampFormat = "yyyyMMdd_HHmmss_fff";
    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, DateTime> _copied = new(StringComparer.OrdinalIgnoreCase);
    private string? _session;
    private string? _head;
    private long _lastLength = -1;
    private DateTime _lastWrite;

    /// <param name="dir">Where the records go (created when needed).</param>
    /// <param name="maxBytes">Size cap of the records; the oldest are deleted beyond it. 0 = no cap.</param>
    /// <param name="utcNow">Clock, for tests.</param>
    public PlayRecorder(string dir, long maxBytes, Func<DateTime>? utcNow = null)
    {
        _dir = dir;
        _maxBytes = Math.Max(0, maxBytes);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string RecordDir => _dir;

    /// <summary>The session mirror this recorder writes now (file name), null before the first log copy.</summary>
    public string? CurrentSession => _session;

    /// <summary>
    /// One pass: copies a save that changed since the last pass, refreshes the log mirror, then trims to the cap.
    /// Missing files are skipped. Returns what it did, for the app log (only when something happened).
    /// </summary>
    public PlayRecordStep Step(string? soloSave, string? coopSave, string? log)
    {
        if (ForeignMirror() is { } other) return new PlayRecordStep(0, false, 0, other);
        System.IO.Directory.CreateDirectory(Path.Combine(_dir, SessionsFolder));
        int saves = (CopySave(soloSave, "save_") ? 1 : 0) + (CopySave(coopSave, "mp_save_") ? 1 : 0);
        bool logCopied = MirrorLog(log);
        int pruned = saves > 0 || logCopied ? Prune() : 0;
        return new PlayRecordStep(saves, logCopied, pruned, null);
    }

    private bool CopySave(string? path, string prefix)
    {
        if (path is null || !File.Exists(path)) return false;
        DateTime written = File.GetLastWriteTimeUtc(path);
        if (_copied.TryGetValue(path, out var last) && last == written) return false;
        if (_utcNow() - written < SettleTime) return false; // the game may still be writing it
        string target = Path.Combine(_dir, prefix + written.ToString(StampFormat, CultureInfo.InvariantCulture) + ".json");
        if (!File.Exists(target)) CopyShared(path, target);
        _copied[path] = written;
        return true;
    }

    private bool MirrorLog(string? log)
    {
        if (log is null || !File.Exists(log)) return false;
        var info = new FileInfo(log);
        if (info.Length == _lastLength && info.LastWriteTimeUtc == _lastWrite) return false;
        if (ReadHead(log) is not { } head) return false;
        // A new game session truncates the file and starts it with a new header.
        if (_session is null || info.Length < _lastLength || head != _head)
        {
            _session = "godot_session_" + _utcNow().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".log";
            _head = head;
        }
        CopyShared(log, Path.Combine(_dir, SessionsFolder, _session));
        _lastLength = info.Length;
        _lastWrite = info.LastWriteTimeUtc;
        return true;
    }

    /// <summary>Name of a session mirror another program is writing right now, or null.</summary>
    private string? ForeignMirror()
    {
        var sessions = Path.Combine(_dir, SessionsFolder);
        if (!System.IO.Directory.Exists(sessions)) return null;
        var now = _utcNow();
        return new DirectoryInfo(sessions).EnumerateFiles("godot_session_*")
            .Where(f => !string.Equals(f.Name, _session, StringComparison.OrdinalIgnoreCase))
            .Where(f => now - f.LastWriteTimeUtc < ForeignWriterWindow)
            .Select(f => f.Name)
            .FirstOrDefault();
    }

    /// <summary>Deletes the oldest records until the folder is under the cap. Our current session file is kept.</summary>
    private int Prune()
    {
        if (_maxBytes == 0) return 0;
        var files = new DirectoryInfo(_dir).EnumerateFiles("save_*.json")
            .Concat(new DirectoryInfo(_dir).EnumerateFiles("mp_save_*.json"))
            .Concat(new DirectoryInfo(Path.Combine(_dir, SessionsFolder)).EnumerateFiles("godot_session_*.log"))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        long total = files.Sum(f => f.Length);
        int deleted = 0;
        foreach (var f in files)
        {
            if (total <= _maxBytes) break;
            if (string.Equals(f.Name, _session, StringComparison.OrdinalIgnoreCase)) continue;
            long length = f.Length; // read before Delete: a deleted FileInfo forgets its size
            try
            {
                f.Delete();
                total -= length;
                deleted++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    /// <summary>Copies while the game keeps the file open (it shares read/write/delete); writes a .tmp first.</summary>
    private static void CopyShared(string from, string to)
    {
        string tmp = to + ".tmp";
        using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var dst = File.Create(tmp))
            src.CopyTo(dst);
        File.Move(tmp, to, overwrite: true);
    }

    private static string? ReadHead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[128];
            int n = fs.Read(buf, 0, buf.Length);
            return Convert.ToHexString(buf, 0, n);
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <param name="Saves">Save copies made.</param>
/// <param name="LogCopied">The log mirror was refreshed.</param>
/// <param name="Pruned">Old records deleted for the size cap.</param>
/// <param name="PausedFor">Another program's session mirror that is being written (nothing done), or null.</param>
public sealed record PlayRecordStep(int Saves, bool LogCopied, int Pruned, string? PausedFor);
