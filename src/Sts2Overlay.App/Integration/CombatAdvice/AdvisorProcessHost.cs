using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Sts2Overlay.App.Interop;

namespace Sts2Overlay.App.Integration.CombatAdvice;

/// <summary>
/// Runs the combat advisor helper as a child process and hands its stdout lines to <see cref="LineReceived"/>.
/// <list type="bullet">
/// <item>No window, stdout/stderr redirected (UTF-8), stdin an open pipe nobody writes to (EOF = the overlay is gone),
/// BelowNormal priority so the game keeps the CPU.</item>
/// <item>stderr is written to the overlay's log, at most <see cref="StderrLinesPerMinute"/> lines a minute.</item>
/// <item>When the helper exits by itself it is started again after 2 s, 5 s, 15 s, then every 60 s
/// (<see cref="DefaultBackoff"/>); a run that lasted <see cref="StableRun"/> resets the steps.</item>
/// <item>It is killed on <see cref="Stop"/>, <see cref="Dispose"/> and AppDomain.ProcessExit, and it sits in a
/// kill-on-close job object (<see cref="KillOnCloseJob"/>), so even a crashed overlay leaves no helper behind.</item>
/// </list>
/// Thread-safe. Events are raised on worker threads.
/// </summary>
public sealed class AdvisorProcessHost : IDisposable
{
    public static readonly IReadOnlyList<TimeSpan> DefaultBackoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    /// <summary>A helper that ran this long before exiting starts the backoff from the first step again.</summary>
    public static readonly TimeSpan StableRun = TimeSpan.FromSeconds(60);

    public const int StderrLinesPerMinute = 20;

    public const ProcessPriorityClass DefaultPriority = ProcessPriorityClass.BelowNormal;

    private readonly AdvisorCommand _command;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly Action<string> _log;
    private readonly ProcessPriorityClass _priority;
    private readonly Lock _gate = new();
    private readonly LineBudget _stderrBudget = new(StderrLinesPerMinute, TimeSpan.FromMinutes(1));
    private IReadOnlyList<string>? _args;
    private Process? _process;
    private long _generation; // bumped by every start/stop: callbacks of an older process are ignored
    private Timer? _restartTimer;
    private int _failures;
    private DateTime _startedUtc;
    private bool _disposed;
    private bool _jobWarned;

    /// <param name="backoff">Restart delays (tests use short ones); the last one repeats.</param>
    /// <param name="log">Where diagnostics and the helper's stderr go (the overlay passes AppLog.Info).</param>
    /// <param name="priority">
    /// The helper's priority. BelowNormal for the overlay: the game wins every CPU contest. Tests that wait for output pass
    /// Normal, because on a busy PC a BelowNormal process can wait many seconds for its first time slice.
    /// </param>
    public AdvisorProcessHost(AdvisorCommand command, Action<string> log, IReadOnlyList<TimeSpan>? backoff = null,
        ProcessPriorityClass priority = DefaultPriority)
    {
        _command = command;
        _log = log;
        _priority = priority;
        _backoff = backoff is { Count: > 0 } ? backoff : DefaultBackoff;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>One stdout line of the current helper (worker thread).</summary>
    public event Action<string>? LineReceived;

    /// <summary>The helper exited by itself (worker thread); argument = exit code. Not raised for <see cref="Stop"/>.</summary>
    public event Action<int>? Exited;

    /// <summary>Times a helper process was started (first start and restarts).</summary>
    public int StartCount { get; private set; }

    /// <summary>The running helper's process id, or null.</summary>
    public int? ProcessId
    {
        get
        {
            lock (_gate) return _process is { } p ? SafePid(p) : null;
        }
    }

    /// <summary>The arguments of the current (or pending) run; null when stopped.</summary>
    public IReadOnlyList<string>? Arguments
    {
        get
        {
            lock (_gate) return _args;
        }
    }

    /// <summary>
    /// Keeps a helper running with <paramref name="args"/> (after the command's leading arguments; normally
    /// <see cref="AdvisorCommand.LiveArgs"/>): starts one, or restarts it when the arguments changed (another save folder).
    /// Same arguments: nothing happens, also while a restart is pending.
    /// </summary>
    public void Run(IReadOnlyList<string> args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_args is not null && _args.SequenceEqual(args, StringComparer.Ordinal)) return;
            KillLocked();
            _args = args.ToArray();
            _failures = 0;
            StartLocked();
        }
    }

    /// <summary>Kills the helper and does not restart it (until the next <see cref="Run"/>).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_args is null && _process is null) return;
            KillLocked();
            _args = null;
            _log("combat advisor: stopped");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            KillLocked();
            _args = null;
            _disposed = true;
        }
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    // ---- start / exit / restart (callers hold _gate) ------------------------------------------------------------

    private void StartLocked()
    {
        var generation = ++_generation;
        var psi = new ProcessStartInfo(_command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(_command.FileName) ?? "",
        };
        foreach (var a in _command.CommandArgs(_args!)) psi.ArgumentList.Add(a);
        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            process.Dispose();
            _log($"combat advisor: could not start {_command.FileName}: {ex.Message}");
            ScheduleRestartLocked(generation, exitCode: null);
            return;
        }
        if (KillOnCloseJob.Shared is { } job)
        {
            if (!job.TryAdd(process, out var error) && !_jobWarned)
            {
                _jobWarned = true;
                _log($"combat advisor: could not add the helper to the job object (error {error}); it is killed on exit only");
            }
        }
        else if (!_jobWarned)
        {
            _jobWarned = true;
            _log($"combat advisor: no job object (error {KillOnCloseJob.CreateError}); the helper is killed on exit only");
        }
        try
        {
            if (_priority != ProcessPriorityClass.Normal) process.PriorityClass = _priority;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // It may already have exited; the reader below sees that.
        }
        _process = process;
        _startedUtc = DateTime.UtcNow;
        StartCount++;
        _log(string.Create(CultureInfo.InvariantCulture, $"combat advisor: started pid {SafePid(process)} ({_command.Source}: {Path.GetFileName(_command.FileName)})"));
        var stdout = new Thread(() => ReadStdout(process, generation)) { IsBackground = true, Name = "advisor stdout" };
        var stderr = new Thread(() => DrainStderr(process)) { IsBackground = true, Name = "advisor stderr", Priority = ThreadPriority.BelowNormal };
        stdout.Start();
        stderr.Start();
    }

    private void ScheduleRestartLocked(long generation, int? exitCode)
    {
        var ran = DateTime.UtcNow - _startedUtc;
        if (exitCode is not null && ran >= StableRun) _failures = 0;
        var delay = _backoff[Math.Min(_failures, _backoff.Count - 1)];
        _failures++;
        _log(string.Create(CultureInfo.InvariantCulture,
            $"combat advisor: {(exitCode is { } c ? $"exited with code {c} after {ran.TotalSeconds:0.#} s" : "start failed")}; next try in {delay.TotalSeconds:0.#} s"));
        _restartTimer?.Dispose();
        _restartTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                if (_disposed || _args is null || generation != _generation) return; // stopped or restarted meanwhile
                StartLocked();
            }
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Kills the current helper and cancels a pending restart (caller holds _gate).</summary>
    private void KillLocked()
    {
        _generation++;
        _restartTimer?.Dispose();
        _restartTimer = null;
        if (_process is not { } p) return;
        _process = null;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Already gone.
        }
        // The stdout reader disposes the Process object when the pipe closes.
    }

    // ---- reader threads ------------------------------------------------------------------------------------------

    private void ReadStdout(Process process, long generation)
    {
        try
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                if (Interlocked.Read(ref _generation) != generation) continue; // killed: drain without delivering
                try
                {
                    LineReceived?.Invoke(line);
                }
                catch (Exception ex)
                {
                    _log($"combat advisor: line handler failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The pipe broke: treat it as an exit.
        }
        int exitCode;
        try
        {
            exitCode = process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }
        bool raise;
        lock (_gate)
        {
            raise = !_disposed && generation == _generation && _args is not null;
            if (raise)
            {
                _process = null;
                ScheduleRestartLocked(generation, exitCode);
            }
        }
        process.Dispose();
        if (raise) Exited?.Invoke(exitCode);
    }

    private void DrainStderr(Process process)
    {
        try
        {
            while (process.StandardError.ReadLine() is { } line)
            {
                foreach (var l in _stderrBudget.Next(line, DateTime.UtcNow)) _log("combat advisor stderr: " + l);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Process gone.
        }
    }

    private static int? SafePid(Process p)
    {
        try
        {
            return p.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// At most <c>max</c> lines per window; the dropped ones are counted and reported in one line when the next window opens.
/// Thread-safe (a restarted helper's stderr reader can overlap the old one's last lines). Pure, so it is unit-tested.
/// </summary>
internal sealed class LineBudget(int max, TimeSpan window)
{
    private readonly Lock _gate = new();
    private DateTime _windowStart = DateTime.MinValue;
    private int _used;
    private int _dropped;

    public IReadOnlyList<string> Next(string line, DateTime now)
    {
        lock (_gate)
        {
            var lines = new List<string>(2);
            if (now - _windowStart >= window)
            {
                if (_dropped > 0) lines.Add(string.Create(CultureInfo.InvariantCulture, $"({_dropped} more lines dropped)"));
                _windowStart = now;
                _used = 0;
                _dropped = 0;
            }
            if (_used < max)
            {
                _used++;
                lines.Add(line.Length <= 400 ? line : line[..400] + "…");
            }
            else
            {
                _dropped++;
            }
            return lines;
        }
    }
}
