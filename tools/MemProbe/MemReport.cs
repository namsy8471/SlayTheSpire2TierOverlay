using System.IO;
using System.Diagnostics;
using System.Globalization;

namespace MemProbe;

/// <summary>
/// One line of the memory table. "raw" columns are read as the process stands after the step; the rest after a full
/// blocking GC (no LOH compaction, like the app), so live managed data and native memory can be told apart:
/// <c>native ≈ private − GC committed</c> (runtime, JIT code, native libraries, Skia/WIC/D3D allocations).
/// </summary>
internal sealed class MemReport
{
    private readonly TextWriter _out;
    private int _inducedGen2;
    private (int G0, int G1, int G2) _lastCounts;
    private long _lastAllocated;

    public MemReport(TextWriter output)
    {
        _out = output;
        _lastCounts = Counts();
        _lastAllocated = GC.GetTotalAllocatedBytes(precise: false);
    }

    private static (int G0, int G1, int G2) Counts() => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

    public void Header()
    {
        _out.WriteLine("step                              | raw: private    ws  managed | after full GC: private    ws  managed  heap commit  frag   gen2    LOH   POH  native | GCs in step g0/g1/g2 | allocated");
        _out.WriteLine(new string('-', 196));
    }

    /// <summary>Prints one row. GC counts and allocated bytes are the step's own (the harness' induced collections excluded).</summary>
    public void Row(string step)
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        long rawPrivate = p.PrivateMemorySize64, rawWs = p.WorkingSet64, rawManaged = GC.GetTotalMemory(false);
        var c = Counts();
        long allocated = GC.GetTotalAllocatedBytes(precise: false);
        var stepCounts = (c.G0 - _lastCounts.G0, c.G1 - _lastCounts.G1, c.G2 - _lastCounts.G2);
        long stepAllocated = allocated - _lastAllocated;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _inducedGen2 += 2;
        p.Refresh();
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        long Gen(int i) => info.GenerationInfo.Length > i ? info.GenerationInfo[i].SizeAfterBytes : 0;
        long privateAfter = p.PrivateMemorySize64;
        long native = privateAfter - info.TotalCommittedBytes;
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{step,-33} | {Mb(rawPrivate),12:F1} {Mb(rawWs),5:F0} {Mb(rawManaged),8:F1} | {Mb(privateAfter),22:F1} {Mb(p.WorkingSet64),5:F0} {Mb(GC.GetTotalMemory(false)),8:F1} {Mb(info.HeapSizeBytes),5:F0} {Mb(info.TotalCommittedBytes),6:F0} {Mb(info.FragmentedBytes),5:F1} {Mb(Gen(2)),6:F1} {Mb(Gen(3)),6:F1} {Mb(Gen(4)),5:F1} {Mb(native),7:F1} | {stepCounts.Item1,8}/{stepCounts.Item2}/{stepCounts.Item3,-8} | {Mb(stepAllocated),8:F0} MB"));
        _lastCounts = Counts();
        _lastAllocated = GC.GetTotalAllocatedBytes(precise: false);
    }

    public int InducedGen2 => _inducedGen2;
}
