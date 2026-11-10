using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.IO;

namespace MemProbe;

/// <summary>
/// Samples the runtime's GC AllocationTick events (one per ~100 KB allocated, carrying the type of the object that
/// crossed the mark), so a type that shows up in N ticks allocated roughly N x 100 KB. Small and large-object-heap
/// allocations are counted apart: big arrays go straight to the LOH, which only a gen2 collection cleans.
/// </summary>
internal sealed class AllocSampler : EventListener
{
    private const int AllocationTick = 10;
    private readonly ConcurrentDictionary<(string Type, bool Large), (int Ticks, long Bytes)> _byType = new();
    public bool Active { get; set; }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1); // GC keyword
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!Active || e.EventId != AllocationTick || e.Payload is null || e.PayloadNames is null) return;
        string type = "?";
        bool large = false;
        long amount = 0, size = 0;
        for (int i = 0; i < e.PayloadNames.Count; i++)
        {
            switch (e.PayloadNames[i])
            {
                case "TypeName": type = e.Payload[i] as string ?? "?"; break;
                case "AllocationKind": large = Convert.ToInt32(e.Payload[i], System.Globalization.CultureInfo.InvariantCulture) == 1; break;
                case "AllocationAmount64": amount = Convert.ToInt64(e.Payload[i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "ObjectSize": size = Convert.ToInt64(e.Payload[i], System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }
        // A large object's exact size names its allocation site (e.g. a pool-sized int[2^21] is an integral table).
        if (large) type += $" ({size:N0} B)";
        _byType.AddOrUpdate((type, large), (1, amount), (_, v) => (v.Ticks + 1, v.Bytes + amount));
    }

    public void Print(TextWriter o, int top = 15)
    {
        foreach (var ((type, large), (ticks, bytes)) in _byType.OrderByDescending(kv => kv.Value.Bytes).Take(top))
            o.WriteLine(System.FormattableString.Invariant($"   {bytes / 1048576.0,8:F1} MB  {ticks,6} ticks  {(large ? "LOH" : "SOH")}  {type}"));
        _byType.Clear();
    }
}
