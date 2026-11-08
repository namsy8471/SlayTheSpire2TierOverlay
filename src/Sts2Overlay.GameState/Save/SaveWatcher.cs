using System.Collections.Concurrent;
using System.Text.Json;

namespace Sts2Overlay.GameState.Save;

/// <summary>A run save was (re)written and parsed.</summary>
/// <param name="Path">Full path of the save file.</param>
/// <param name="Save">Parsed content.</param>
/// <param name="FileWriteUtc">File's last-write time: the moment the game committed this save.</param>
/// <param name="FileBytes">File size; equals N in the log's <c>Wrote N bytes to path=...</c> line for this save.</param>
public sealed record SaveSnapshot(string Path, ParsedSave Save, DateTime FileWriteUtc, long FileBytes);

/// <summary>
/// Watches the saves folder for <c>current_run.save</c> / <c>current_run_mp.save</c>.
/// The game writes <c>X.tmp</c>, keeps the old file as <c>X.backup</c>, then renames <c>.tmp → X</c>,
/// so a plain "Changed" filter is not enough: we listen to every event, map it to the target
/// file name, and debounce (one burst of FS events → one read).
/// </summary>
public sealed class SaveWatcher : IDisposable
{
    private readonly string _directory;
    private readonly Func<ulong?> _localNetId;
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, Timer> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastText = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _fsw;
    private volatile bool _disposed;

    public event EventHandler<SaveSnapshot>? SaveChanged;

    /// <summary>A run save disappeared (run ended or abandoned). Argument: full path.</summary>
    public event EventHandler<string>? SaveRemoved;

    /// <summary>Parsing failed even after retries (argument: path, exception). Mostly for diagnostics.</summary>
    public event EventHandler<(string Path, Exception Error)>? SaveError;

    /// <param name="directory">The <c>profileN\saves</c> folder.</param>
    /// <param name="localNetId">Our co-op net id, asked at parse time (it may be learned from the log later).</param>
    /// <param name="debounce">Quiet time after the last FS event before reading. Default 250 ms.</param>
    public SaveWatcher(string directory, Func<ulong?> localNetId, TimeSpan? debounce = null)
    {
        _directory = directory;
        _localNetId = localNetId;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Reads any existing run saves once, then starts watching.</summary>
    public void Start()
    {
        foreach (string name in RunSaveParser.RunSaveFileNames)
            ReadNow(Path.Combine(_directory, name));

        _fsw = new FileSystemWatcher(_directory)
        {
            Filter = "*", // the .tmp and .backup siblings matter too
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };
        _fsw.Changed += (_, e) => Touch(e.Name);
        _fsw.Created += (_, e) => Touch(e.Name);
        _fsw.Deleted += (_, e) => Touch(e.Name);
        _fsw.Renamed += (_, e) => { Touch(e.OldName); Touch(e.Name); };
        // The internal buffer can overflow during bursts; then events were lost, so just re-read both.
        _fsw.Error += (_, _) => { foreach (string n in RunSaveParser.RunSaveFileNames) Touch(n); };
        _fsw.EnableRaisingEvents = true;
    }

    /// <summary>"current_run.save.tmp" / ".backup" → "current_run.save"; anything else → null.</summary>
    internal static string? TargetName(string? fileName)
    {
        if (fileName == null) return null;
        foreach (string target in RunSaveParser.RunSaveFileNames)
            if (fileName.Equals(target, StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(target + ".tmp", StringComparison.OrdinalIgnoreCase))
                return target;
        return null; // ".backup" is the previous version: never interesting on its own
    }

    private void Touch(string? fileName)
    {
        if (_disposed || TargetName(fileName) is not { } target) return;
        string path = Path.Combine(_directory, target);
        // Debounce: every event pushes the timer back; it fires once the burst is over.
        Timer timer = _timers.GetOrAdd(path, p => new Timer(_ => ReadNow(p), null, Timeout.Infinite, Timeout.Infinite));
        timer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void ReadNow(string path)
    {
        if (_disposed) return;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                byte[]? bytes = IO.SharedFile.ReadAllBytes(path);
                if (bytes == null)
                {
                    if (_lastText.TryRemove(path, out _)) SaveRemoved?.Invoke(this, path);
                    return;
                }
                string text = IO.SharedFile.Decode(bytes);
                if (_lastText.TryGetValue(path, out string? prev) && prev == text) return; // nothing new

                ParsedSave save = RunSaveParser.Parse(text, _localNetId());
                _lastText[path] = text;
                SaveChanged?.Invoke(this, new SaveSnapshot(path, save, File.GetLastWriteTimeUtc(path), bytes.Length));
                return;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Most likely we caught the file between truncate and write. Wait and retry.
                if (attempt == 3) { SaveError?.Invoke(this, (path, ex)); return; }
                Thread.Sleep(150 * attempt);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _fsw?.Dispose();
        foreach (Timer t in _timers.Values) t.Dispose();
        _timers.Clear();
    }
}
