using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// usage: allocrep <trace.nettrace>  -> bytes by type, and by (type, first sim/engine frame)
string etlx = TraceLog.CreateFromEventPipeDataFile(args[0]);
using var log = new TraceLog(etlx);
var byType = new Dictionary<string, double>();
var bySite = new Dictionary<string, double>();
double total = 0;
var gcReasons = new Dictionary<string, int>();
var lohBytes = 0.0;
foreach (TraceEvent ev in log.Events)
{
    if (ev is GCStartTraceData g)
    {
        string k = $"gen{g.Depth} {g.Reason} {g.Type}";
        gcReasons[k] = gcReasons.GetValueOrDefault(k) + 1;
        continue;
    }
    if (ev is not GCAllocationTickTraceData a) continue;
    if (a.AllocationKind == GCAllocationKind.Large) lohBytes += a.AllocationAmount64;
    double bytes = a.AllocationAmount64;
    total += bytes;
    string type = a.TypeName ?? "?";
    byType[type] = byType.GetValueOrDefault(type) + bytes;
    string site = "?";
    var frames = new List<string>();
    for (var f = ev.CallStack(); f != null && frames.Count < 40; f = f.Caller)
        frames.Add(f.CodeAddress.FullMethodName ?? "");
    foreach (string m in frames)
        if (m.Contains("Sts2Sim") || m.Contains("SimTrial")) { site = m; break; }
    string key = type + "  @  " + site;
    bySite[key] = bySite.GetValueOrDefault(key) + bytes;
}
Console.WriteLine($"total sampled {total / 1e9:F2} GB, LOH {lohBytes / 1e9:F3} GB");
foreach (var kv in gcReasons.OrderByDescending(k => k.Value)) Console.WriteLine($"  GC {kv.Value,5}  {kv.Key}");
Console.WriteLine("== by type");
foreach (var kv in byType.OrderByDescending(k => k.Value).Take(25)) Console.WriteLine($"{100 * kv.Value / total,5:F1}%  {kv.Key}");
Console.WriteLine("== by type @ first sim/engine frame");
foreach (var kv in bySite.OrderByDescending(k => k.Value).Take(45)) Console.WriteLine($"{100 * kv.Value / total,5:F1}%  {kv.Key[..Math.Min(170, kv.Key.Length)]}");
