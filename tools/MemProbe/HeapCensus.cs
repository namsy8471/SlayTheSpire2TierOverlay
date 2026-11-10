using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Diagnostics.Runtime;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Events;
using Sts2Overlay.Vision.Rest;

namespace MemProbe;

/// <summary>
/// <c>MemProbe heap [--frames 60] [--upscale 2] [--idle 0,30] [--min 65536] [--threads 1]</c>: what the card recogniser
/// keeps alive between frames. Recognises saved captures (4K = FHD x2) the way the capture worker does, then, after a
/// full GC (and again after each idle wait), snapshots this very process with ClrMD and lists every array of at least
/// --min bytes with the type of the object that references it: an ArrayPool thread-local slot shows up as
/// <c>SharedArrayPoolThreadLocalArray[]</c>, a per-core pool partition as <c>Object[]</c>/<c>Array[]</c>.
/// --threads n recognises from n threads in turn (the app uses one capture thread; tools and tests may use more).
/// </summary>
internal static class HeapCensus
{
    public static int Run(string local, Dictionary<string, string> opts)
    {
        int frames = int.Parse(opts.GetValueOrDefault("frames") ?? "60", CultureInfo.InvariantCulture);
        int k = int.Parse(opts.GetValueOrDefault("upscale") ?? "2", CultureInfo.InvariantCulture);
        long min = long.Parse(opts.GetValueOrDefault("min") ?? "65536", CultureInfo.InvariantCulture);
        int threads = int.Parse(opts.GetValueOrDefault("threads") ?? "1", CultureInfo.InvariantCulture);
        int[] idle = (opts.GetValueOrDefault("idle") ?? "0").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();

        using var loaded = LoadedRecognizer.Create(local, cold: false);
        CardRecognizer rec = loaded.Recognizer.Loaded!;
        var pick = Captures.Spread(Captures.List(Path.Combine(local, "test-captures")).Where(c => c.Width == 1920).ToList(), frames);
        var buffer = new ReusedFrameBuffer();
        Console.WriteLine($"heap census: {pick.Count} frames, upscale {k}, {threads} recognising thread(s), arrays >= {min:N0} B");

        var report = new MemReport(Console.Out);
        report.Header();
        report.Row("recogniser loaded");
        Census("recogniser loaded", min);

        var kinds = new Dictionary<ScreenKind, int>();
        var times = new List<double>();
        for (int i = 0; i < pick.Count; i++)
        {
            buffer.Decode(pick[i], k);
            var vf = new ScreenFrame(buffer.Buffer, buffer.Width, buffer.Height, buffer.Width * 4);
            ScreenRecognition r = default!;
            void Work()
            {
                var t0 = Stopwatch.GetTimestamp();
                r = rec.Recognize(vf);
                RestSiteDetector.Detect(vf);
                CardRemovalProbe.Read(vf);
                EventButtonDetector.Detect(vf);
                AncientButtonDetector.Detect(vf);
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            if (threads <= 1) Work();
            else
            {
                var th = new Thread(Work) { IsBackground = true };
                th.Start();
                th.Join();
            }
            kinds[r.Kind] = kinds.GetValueOrDefault(r.Kind) + 1;
            if ((i + 1) % 20 == 0 || i + 1 == pick.Count) Console.WriteLine($"   recognise {i + 1}/{pick.Count}");
        }
        times.Sort();
        Console.WriteLine("   kinds: " + string.Join(", ", kinds.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}")));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   median {times[times.Count / 2]:F1} ms per frame (all per-frame vision stages)"));
        GC.KeepAlive(buffer);

        // Idle points are read WITHOUT an induced GC in between (the app idles without one); the table row at the end
        // then shows what a full GC leaves.
        int waited = 0;
        foreach (int s in idle.Order())
        {
            if (s > waited) Thread.Sleep((s - waited) * 1000);
            waited = Math.Max(waited, s);
            Census($"idle {waited} s", min);
        }
        report.Row($"after recognising + idle {waited} s");
        Census($"idle {waited} s, after full GC", min);
        return 0;
    }

    /// <summary>Snapshots this process and prints the big arrays grouped by (type, length, holder type).</summary>
    private static void Census(string label, long min)
    {
        // Process numbers first: ClrMD's own work below allocates (it becomes garbage for the next census).
        var p = Process.GetCurrentProcess();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"   --- raw (no GC) '{label}': private {p.PrivateMemorySize64 / 1048576.0:F1} MB, managed {GC.GetTotalMemory(false) / 1048576.0:F1} MB, " +
            $"gen0/1/2 so far {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}"));
        var pooled = PoolCensus.Collect();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"   --- ArrayPool.Shared holds {pooled.Count} arrays, {pooled.Sum(x => (double)x.Bytes) / 1048576.0:F2} MB (>= {min:N0} B: {pooled.Where(x => x.Bytes >= min).Sum(x => (double)x.Bytes) / 1048576.0:F2} MB)"));
        foreach (var g in pooled.Where(x => x.Bytes >= min).GroupBy(x => (x.Element, x.Length, x.Where)).OrderByDescending(g => g.Sum(x => x.Bytes)))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"   {g.Sum(x => (double)x.Bytes) / 1048576.0,8:F2} MB  {g.Count(),3} x {g.Key.Element}[{g.Key.Length:N0}]  pool {g.Key.Where}"));
        using DataTarget dt = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        using ClrRuntime rt = dt.ClrVersions[0].CreateRuntime();
        ClrHeap heap = rt.Heap;

        // Live objects: marked from the roots (no GC is induced, so the heap still holds this run's garbage, and a
        // gen2 GC would let ArrayPool.Shared trim itself, which is not what the app sees between frames).
        var live = new HashSet<ulong>();
        var stack = new Stack<ClrObject>();
        foreach (ClrRoot root in heap.EnumerateRoots())
            if (root.Object.IsValid && live.Add(root.Object.Address)) stack.Push(root.Object);
        while (stack.Count > 0)
        {
            ClrObject o = stack.Pop();
            foreach (ClrObject r in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
                if (r.IsValid && live.Add(r.Address)) stack.Push(r);
        }

        var big = new Dictionary<ulong, (string Type, ulong Size, int Length, string Seg)>();
        double garbageMb = 0, liveLoh = 0, deadLoh = 0;
        var deadLarge = new List<(string Type, int Length, ulong Size)>();
        foreach (ClrObject o in heap.EnumerateObjects())
        {
            if (!o.IsValid || o.Type is null) continue;
            bool isLive = live.Contains(o.Address);
            var kind = heap.GetSegmentByAddress(o.Address)?.Kind;
            if (kind == GCSegmentKind.Large)
            {
                if (isLive) liveLoh += o.Size;
                else deadLoh += o.Size;
            }
            if (!isLive)
            {
                garbageMb += o.Size / 1048576.0;
                if (kind == GCSegmentKind.Large && o.Type.IsArray) deadLarge.Add((o.Type.Name ?? "?", o.AsArray().Length, o.Size));
                continue;
            }
            if (!o.Type.IsArray || o.Size < (ulong)min) continue;
            big[o.Address] = (o.Type.Name ?? "?", o.Size, o.AsArray().Length, kind?.ToString() ?? "?");
        }
        // Holders: every live object that references one of the big arrays (one pass over the live heap), then roots.
        var holders = new Dictionary<ulong, List<string>>();
        foreach (ulong a in live)
        {
            ClrObject o = heap.GetObject(a);
            if (!o.IsValid || o.Type is null) continue;
            foreach (ClrObject r in o.EnumerateReferences(carefully: false, considerDependantHandles: false))
                if (big.ContainsKey(r.Address))
                {
                    if (!holders.TryGetValue(r.Address, out var l)) holders[r.Address] = l = [];
                    if (l.Count < 3) l.Add(o.Type.Name ?? "?");
                }
        }
        foreach (ClrRoot root in heap.EnumerateRoots())
            if (big.ContainsKey(root.Object.Address))
            {
                if (!holders.TryGetValue(root.Object.Address, out var l)) holders[root.Object.Address] = l = [];
                if (l.Count < 3) l.Add("root:" + root.RootKind);
            }

        var load = GC.GetGCMemoryInfo();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"   --- census '{label}': {big.Count} live arrays >= {min:N0} B, {big.Values.Sum(v => (double)v.Size) / 1048576.0:F1} MB; " +
            $"LOH live {liveLoh / 1048576.0:F1} MB, LOH dead {deadLoh / 1048576.0:F1} MB, all dead {garbageMb:F1} MB; " +
            $"machine load {load.MemoryLoadBytes >> 20}/{load.HighMemoryLoadThresholdBytes >> 20} MB (high-load threshold)"));
        var groups = big.Select(kv => (kv.Value.Type, kv.Value.Length, kv.Value.Seg, kv.Value.Size,
                Holder: holders.TryGetValue(kv.Key, out var l) ? string.Join(" + ", l.Distinct()) : "(unreferenced)"))
            .GroupBy(x => (x.Type, x.Length, x.Seg, x.Holder))
            .Select(g => (g.Key, Count: g.Count(), Mb: g.Sum(x => (double)x.Size) / 1048576.0))
            .OrderByDescending(g => g.Mb);
        foreach (var (key, count, mb) in groups)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"   {mb,8:F2} MB  {count,3} x {key.Type}[{key.Length:N0}]  {key.Seg,-12} held by {key.Holder}"));
        // Unmarked large arrays: ArrayPool thread-local slots (listed above, not seen as roots by ClrMD) and garbage
        // waiting for a gen2 GC (LOH churn).
        Console.WriteLine("   unmarked LOH arrays (pool thread-local slots + garbage), top 10:");
        foreach (var g in deadLarge.GroupBy(x => (x.Type, x.Length)).OrderByDescending(g => g.Sum(x => (double)x.Size)).Take(10))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"   {g.Sum(x => (double)x.Size) / 1048576.0,8:F2} MB  {g.Count(),3} x {g.Key.Type}[{g.Key.Length:N0}]"));
    }
}
