using System.IO;
using System.Text;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.Core.Replay;

namespace Sts2Overlay.App.Services;

/// <summary>
/// The play-time decision log on disk: <c>%LOCALAPPDATA%\Sts2Overlay\decisions\&lt;runkey&gt;.jsonl</c>, one JSON line per
/// card screen (<see cref="DecisionRecord"/>). Appends run on a background task, one after another, so the UI thread
/// never waits for the disk. Only the newest <see cref="KeepRuns"/> run files are kept. Local only: nothing is uploaded,
/// and deleting the folder is safe (the review then recomputes every decision).
/// </summary>
public sealed class DecisionLogStore : IDisposable
{
    public const int KeepRuns = 300;
    public const string Extension = ".jsonl";

    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <param name="dir">The folder of the run files (created on the first write).</param>
    /// <param name="keepRuns">How many run files survive a prune.</param>
    public DecisionLogStore(string dir, int keepRuns = KeepRuns)
    {
        Dir = dir;
        Keep = keepRuns;
    }

    public string Dir { get; }
    public int Keep { get; }

    public string PathFor(string runKey) => Path.Combine(Dir, runKey + Extension);

    /// <summary>Queues one record (written in order, off the calling thread).</summary>
    public void Append(DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Run.Length == 0) return;
        var line = DecisionLog.Serialize(record);
        var path = PathFor(record.Run);
        lock (_gate) _tail = _tail.ContinueWith(_ => AppendLine(path, line), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Waits for the queued writes (exit, tests).</summary>
    public bool Flush(TimeSpan timeout)
    {
        Task tail;
        lock (_gate) tail = _tail;
        try
        {
            return tail.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    /// <summary>
    /// Appends one line. A file whose last line was cut short (the app crashed mid-write) first gets the newline it is
    /// missing, so the new record starts a line of its own and only the broken one is skipped when reading.
    /// </summary>
    private void AppendLine(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var created = !File.Exists(path);
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                var prefix = "";
                if (fs.Length > 0)
                {
                    fs.Seek(-1, SeekOrigin.End);
                    if (fs.ReadByte() != '\n') prefix = "\n";
                }
                fs.Seek(0, SeekOrigin.End);
                var bytes = Encoding.UTF8.GetBytes(prefix + line + "\n");
                fs.Write(bytes, 0, bytes.Length);
            }
            if (created) Prune(Dir, Keep);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("decision log: write failed", ex);
        }
    }

    /// <summary>The records of one run (empty when there is no file). Unreadable lines are skipped.</summary>
    public IReadOnlyList<DecisionRecord> Read(string? runKey)
    {
        if (runKey is null) return [];
        var path = PathFor(runKey);
        try
        {
            if (!File.Exists(path)) return [];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } l) lines.Add(l);
            return DecisionLog.Parse(lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("decision log: read failed", ex);
            return [];
        }
    }

    /// <summary>Deletes all but the <paramref name="keep"/> most recently written run files. Returns how many were deleted.</summary>
    public static int Prune(string dir, int keep = KeepRuns)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            var old = new DirectoryInfo(dir).GetFiles("*" + Extension)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(Math.Max(0, keep))
                .ToArray();
            var deleted = 0;
            foreach (var f in old)
            {
                try
                {
                    f.Delete();
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or read-only: try again next time.
                }
            }
            return deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("decision log: prune failed", ex);
            return 0;
        }
    }

    /// <summary>Prunes on a worker thread (app start).</summary>
    public void PruneInBackground() => _ = Task.Run(() => Prune(Dir, Keep));

    public void Dispose() => Flush(TimeSpan.FromSeconds(2));
}
