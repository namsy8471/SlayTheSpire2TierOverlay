using System.Text;
using Sts2Overlay.GameState.IO;

namespace Sts2Overlay.GameState.Log;

/// <summary>
/// "tail -f" for godot.log by polling. We re-open the file on every poll instead of keeping a handle:
/// when the game starts it renames godot.log → godotYYYY-MM-DDTHH.MM.SS.log and creates a new godot.log;
/// a kept handle would silently follow the renamed old file forever.
/// </summary>
public sealed class LogTailer : IDisposable
{
    private const int MaxChunkBytes = 8 * 1024 * 1024;
    private readonly string _path;
    private readonly bool _startAtEnd;
    private readonly object _gate = new();
    private readonly List<byte> _partial = []; // bytes of a line whose '\n' has not been written yet
    private long _position;
    private bool _firstPoll = true;
    private Timer? _timer;
    private TimeSpan _interval;

    /// <summary>Complete new lines, in order (raised from a timer thread when started with <see cref="Start"/>).</summary>
    public event EventHandler<IReadOnlyList<string>>? LinesRead;

    /// <summary>The file was replaced (game relaunch). Raised before the new file's lines.</summary>
    public event EventHandler? Rotated;

    /// <summary>
    /// A poll or a <see cref="LinesRead"/> handler threw something other than a transient IO error (a log format the
    /// handlers do not expect). The lines of that read are skipped and tailing goes on with the next ones.
    /// </summary>
    public event EventHandler<Exception>? Error;

    /// <param name="path">Full path of godot.log.</param>
    /// <param name="startAtEnd">True: skip what is already there. False (default): replay the whole current log,
    /// which is how the tracker rebuilds the in-progress run when the overlay starts mid-run.</param>
    public LogTailer(string path, bool startAtEnd = false)
    {
        _path = path;
        _startAtEnd = startAtEnd;
    }

    /// <summary>Byte offset of the next unread byte.</summary>
    public long Position { get { lock (_gate) return _position; } }

    /// <summary>Starts polling every <paramref name="interval"/> (300 ms is a good default; the game flushes ~1/s).</summary>
    public void Start(TimeSpan interval)
    {
        _interval = interval;
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void Tick()
    {
        try
        {
            IReadOnlyList<string> lines = Poll();
            if (lines.Count > 0) LinesRead?.Invoke(this, lines);
        }
        catch (IOException) { /* transient: try again next tick */ }
        catch (UnauthorizedAccessException) { }
        catch (Exception ex)
        {
            // Anything else would end the process (timer thread), and a restart replays the same log into the same
            // throw. The position is already past these lines, so they are skipped; report and keep tailing.
            try { Error?.Invoke(this, ex); } catch (Exception) { /* a failing reporter must not stop the tailer */ }
        }
        finally
        {
            // One-shot timer re-armed after each tick → ticks never overlap even if a handler is slow.
            try { _timer?.Change(_interval, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Reads everything appended since the last call and returns the complete lines.</summary>
    public IReadOnlyList<string> Poll()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            using FileStream fs = SharedFile.OpenShared(_path);
            // Use the stream length, not FileInfo.Length: Explorer/FileInfo can show godot.log as 0 bytes
            // while the game holds it open (metadata is only refreshed on close).
            long length = fs.Length;

            if (_firstPoll)
            {
                _firstPoll = false;
                if (_startAtEnd) { _position = length; return []; }
            }

            // Rotation check: a new godot.log starts small, so it is shorter than where we were.
            // (Creation time is NOT reliable: NTFS "file tunneling" gives a file re-created under the same
            // name within ~15 s the old file's creation time, which is exactly what the game's rename does.)
            if (length < _position)
            {
                _position = 0;
                _partial.Clear();
                Rotated?.Invoke(this, EventArgs.Empty);
            }
            if (length == _position) return [];

            fs.Seek(_position, SeekOrigin.Begin);
            // Cap one poll's read so replaying a huge co-op Debug log (20+ MB) happens in slices.
            byte[] buffer = new byte[Math.Min(length - _position, MaxChunkBytes)];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            _position += read;
            return SplitLines(buffer.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Splits on '\n' at the BYTE level and decodes each complete line as UTF-8. Decoding per line (not per
    /// chunk) matters: a chunk can end in the middle of a multi-byte Korean character.
    /// </summary>
    private List<string> SplitLines(ReadOnlySpan<byte> chunk)
    {
        var lines = new List<string>();
        while (true)
        {
            int nl = chunk.IndexOf((byte)'\n');
            if (nl < 0) break;
            if (_partial.Count > 0)
            {
                _partial.AddRange(chunk[..nl]);
                lines.Add(Decode(_partial.ToArray()));
                _partial.Clear();
            }
            else
            {
                lines.Add(Decode(chunk[..nl]));
            }
            chunk = chunk[(nl + 1)..];
        }
        _partial.AddRange(chunk); // unfinished last line: keep for the next poll
        return lines;
    }

    private static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes).TrimEnd('\r');

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
