using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Sts2Overlay.App.Integration.CombatAdvice;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// Starting, reading, restarting and killing the combat advisor helper, with the FAKE helper from tests/FakeCombatAdvisor
/// (a script player with no game logic, copied next to the tests as FakeCombatAdvisor.exe).
/// </summary>
public sealed class AdvisorProcessHostTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly IReadOnlyList<TimeSpan> FastBackoff = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];
    private static readonly IReadOnlyList<string> LiveArgs = ["live", "--save", @"C:\made-up\saves\current_run.save", "--log", @"C:\made-up\logs\godot.log"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sts2-fake-advisor-" + Guid.NewGuid().ToString("N"));
    private readonly BlockingCollection<string> _lines = [];
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<AdvisorProcessHost> _hosts = [];

    public AdvisorProcessHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var h in _hosts) h.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FakeExe => Path.Combine(AppContext.BaseDirectory, "FakeCombatAdvisor.exe");

    /// <summary>A host for the fake helper playing <paramref name="script"/>. Normal priority: on a busy PC a BelowNormal
    /// process can wait many seconds for CPU, which would only make these tests slow and flaky.</summary>
    private AdvisorProcessHost Host(params string[] script) => Host(ProcessPriorityClass.Normal, script);

    private AdvisorProcessHost Host(ProcessPriorityClass priority, params string[] script)
    {
        var path = Path.Combine(_dir, $"script{_hosts.Count}.txt");
        File.WriteAllLines(path, script);
        var host = new AdvisorProcessHost(new AdvisorCommand(FakeExe, ["--script", path], "test"), _log.Enqueue, FastBackoff, priority);
        host.LineReceived += _lines.Add;
        _hosts.Add(host);
        return host;
    }

    private string NextLine()
    {
        Assert.True(_lines.TryTake(out var line, Wait), "no line from the fake helper in time; log:\n" + string.Join("\n", _log));
        return line!;
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < Wait, "timed out waiting for: " + what);
            Thread.Sleep(20);
        }
    }

    private static bool Gone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true; // no such process
        }
    }

    [Fact]
    public void The_helper_runs_below_normal_priority_by_default()
    {
        var host = Host(AdvisorProcessHost.DefaultPriority, "hang");
        host.Run(LiveArgs);

        using var p = Process.GetProcessById(host.ProcessId!.Value);
        Assert.Equal(ProcessPriorityClass.BelowNormal, p.PriorityClass); // set right after the start, no need to wait for output
    }

    [Fact]
    public void Starts_the_helper_with_the_leading_and_live_arguments_and_delivers_its_lines()
    {
        Assert.True(File.Exists(FakeExe), "the fake helper was not copied next to the tests");
        var host = Host("args", """print {"v":1,"state":"idle"}""", "print 강타 → 턱벌레", "hang");
        host.Run(LiveArgs);

        using (var doc = JsonDocument.Parse(NextLine()))
        {
            var args = doc.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray();
            Assert.Equal(["--script", Path.Combine(_dir, "script0.txt"), .. LiveArgs], args);
        }
        Assert.Equal("""{"v":1,"state":"idle"}""", NextLine());
        Assert.Equal("강타 → 턱벌레", NextLine()); // UTF-8 both ways
        Assert.Equal(1, host.StartCount);
    }

    [Fact]
    public void Restarts_the_helper_after_it_exits()
    {
        var exits = new BlockingCollection<int>();
        var host = Host("print hello", "exit 3");
        host.Exited += exits.Add;
        host.Run(LiveArgs);

        Assert.Equal("hello", NextLine());
        Assert.True(exits.TryTake(out var code, Wait));
        Assert.Equal(3, code);
        Assert.Equal("hello", NextLine()); // started again after the backoff
        WaitFor(() => host.StartCount >= 2, "second start");
        Assert.Contains(_log, l => l.Contains("exited with code 3", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_arguments_keep_the_running_helper_and_new_ones_restart_it()
    {
        var host = Host("print up", "hang");
        host.Run(LiveArgs);
        Assert.Equal("up", NextLine());
        var first = host.ProcessId!.Value;

        host.Run([.. LiveArgs]);
        Assert.Equal(first, host.ProcessId);
        Assert.Equal(1, host.StartCount);

        host.Run(["live", "--save", @"C:\made-up\other\current_run.save", "--log", @"C:\made-up\logs\godot.log"]);
        Assert.Equal("up", NextLine());
        Assert.NotEqual(first, host.ProcessId);
        WaitFor(() => Gone(first), "old helper killed");
    }

    [Fact]
    public void Dispose_kills_the_helper()
    {
        var host = Host("print up", "hang");
        host.Run(LiveArgs);
        Assert.Equal("up", NextLine());
        var pid = host.ProcessId!.Value;
        Assert.False(Gone(pid));

        host.Dispose();

        WaitFor(() => Gone(pid), "helper killed on dispose");
        Assert.Null(host.ProcessId);
        host.Run(LiveArgs); // disposed: nothing starts
        Assert.Null(host.ProcessId);
    }

    [Fact]
    public void Stop_kills_the_helper_and_does_not_restart_it()
    {
        var host = Host("print up", "hang");
        host.Run(LiveArgs);
        Assert.Equal("up", NextLine());
        var pid = host.ProcessId!.Value;

        host.Stop();

        WaitFor(() => Gone(pid), "helper killed on stop");
        Thread.Sleep(500); // longer than the backoff: a restart would have printed again
        Assert.False(_lines.TryTake(out _));
        Assert.Equal(1, host.StartCount);
        Assert.Null(host.Arguments);
    }

    [Fact]
    public void Stderr_goes_to_the_log_and_is_rate_limited()
    {
        var script = Enumerable.Range(1, AdvisorProcessHost.StderrLinesPerMinute + 5).Select(i => $"stderr noise {i}").Append("print done").Append("hang").ToArray();
        var host = Host(script);
        host.Run(LiveArgs);
        Assert.Equal("done", NextLine());

        WaitFor(() => _log.Any(l => l.Contains($"noise {AdvisorProcessHost.StderrLinesPerMinute}", StringComparison.Ordinal)), "stderr lines in the log");
        Thread.Sleep(200);
        Assert.Equal(AdvisorProcessHost.StderrLinesPerMinute, _log.Count(l => l.StartsWith("combat advisor stderr: noise", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_missing_exe_is_retried_with_backoff_without_throwing()
    {
        var host = new AdvisorProcessHost(new AdvisorCommand(Path.Combine(_dir, "nothing-here.exe"), [], "test"), _log.Enqueue, FastBackoff);
        _hosts.Add(host);
        host.Run(LiveArgs);

        WaitFor(() => _log.Count(l => l.Contains("could not start", StringComparison.Ordinal)) >= 2, "two failed starts");
        Assert.Equal(0, host.StartCount);
    }

    [Fact]
    public void Line_budget_drops_lines_over_the_limit_and_reports_them_in_the_next_window()
    {
        var budget = new LineBudget(2, TimeSpan.FromMinutes(1));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(["a"], budget.Next("a", t0));
        Assert.Equal(["b"], budget.Next("b", t0.AddSeconds(1)));
        Assert.Empty(budget.Next("c", t0.AddSeconds(2)));
        Assert.Empty(budget.Next("d", t0.AddSeconds(3)));
        Assert.Equal(["(2 more lines dropped)", "e"], budget.Next("e", t0.AddMinutes(1)));
    }
}
