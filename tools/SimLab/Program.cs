using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SimTrial;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;

// Usage: SimFuzz <fights.jsonl> <out.csv> [--act1cap 3000] [--totalcap 8000] [--seeds 5]
//                [--threads N] [--wallcap-sec 20] [--coverage-only] [--report coverage.txt]
// Bigger gen0 budget (1 GB): beam-search states then die young instead of being promoted, so
// gen2 GCs drop 19 -> 2 on the dev boss sample. The GC reads this only at process start and
// ignores the runtimeconfig keys, so relaunch once with the env var set. The runtime caps the
// value, so going above 1 GB changes nothing. Set DOTNET_GCgen0size=0 to keep the default.
if (Environment.GetEnvironmentVariable("DOTNET_GCgen0size") is null && Environment.ProcessPath is { } exe)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
    foreach (var a in args) psi.ArgumentList.Add(a);
    psi.Environment["DOTNET_GCgen0size"] = "40000000"; // hex: 0x40000000 = 1 GB
    using var child = Process.Start(psi)!;
    child.WaitForExit();
    Environment.Exit(child.ExitCode);
}

string fightsPath = args.Length > 0 ? args[0] : "fights.jsonl";
string outCsv = args.Length > 1 ? args[1] : "sim_fights.csv";
int act1Cap = IntArg("--act1cap", 3000);
int totalCap = IntArg("--totalcap", 8000);
int seeds = IntArg("--seeds", 5);
int threads = IntArg("--threads", Environment.ProcessorCount);
int wallCapSec = IntArg("--wallcap-sec", 20);
bool coverageOnly = args.Contains("--coverage-only");
string reportPath = StrArg("--report", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outCsv))!, "coverage.txt"));

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var total = Stopwatch.StartNew();
long firstChance = 0;
var fcTypes = new ConcurrentDictionary<string, int>();
AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
{
    Interlocked.Increment(ref firstChance);
    fcTypes.AddOrUpdate(e.Exception.GetType().Name + ": " + e.Exception.Message[..Math.Min(80, e.Exception.Message.Length)], 1, (_, v) => v + 1);
};
ModelDb.Init(ContentRegistry.AllTypes);
if (args.Length > 0 && args[0] == "api")
{
    SimApi.Cli(args[1..]);
    return;
}

List<FightRow> rows = Mapper.ReadFights(fightsPath).ToList();
Console.WriteLine($"read {rows.Count} fights");

// ---------- coverage over all rows and the v0.111 subset ----------
var specs = rows.Select(Mapper.Map).ToList();
var report = new StringBuilder();
void Coverage(string label, IList<FightSpec> set)
{
    double Pct(Func<FightSpec, bool> f) => set.Count == 0 ? 0 : 100.0 * set.Count(f) / set.Count;
    report.AppendLine($"== coverage: {label} (n={set.Count}) ==");
    report.AppendLine($"encounter mapped : {Pct(s => s.EncounterMapped):F1}%");
    report.AppendLine($"character mapped : {Pct(s => s.CharacterMapped):F1}%");
    report.AppendLine($"all cards mapped : {Pct(s => s.AllCardsMapped):F1}%");
    report.AppendLine($"all relics mapped: {Pct(s => s.AllRelicsMapped):F1}%");
    report.AppendLine($"fully mapped     : {Pct(s => s.FullyMapped):F1}%");
    report.AppendLine($"simulatable (relics may be skipped): {Pct(s => s.Simulatable):F1}%");
    var counts = new Dictionary<string, int>();
    foreach (FightSpec s in set)
        foreach (string u in s.Unmapped.Distinct())
            counts[u] = counts.GetValueOrDefault(u) + 1;
    report.AppendLine("top unmapped items (fights containing):");
    foreach (var kv in counts.OrderByDescending(kv => kv.Value).Take(30))
        report.AppendLine($"  {kv.Value,6}  {kv.Key}");
    report.AppendLine();
}
Coverage("all builds", specs);
var v111 = specs.Where(s => s.Row.build_id.StartsWith("v0.111", StringComparison.Ordinal)).ToList();
Coverage("v0.111", v111);
string encFilter = StrArg("--encounter", "");
if (encFilter != "") v111 = v111.Where(s => s.Row.encounter == encFilter).ToList();
Console.Write(report.ToString());
File.WriteAllText(reportPath, report.ToString());
if (coverageOnly) return;

// ---------- sample ----------
var rng = new Random(12345);
List<FightSpec> act1 = v111.Where(s => s.Row.act == 1).OrderBy(_ => rng.Next()).Take(act1Cap).ToList();
List<FightSpec> later = v111.Where(s => s.Row.act is 2 or 3).OrderBy(_ => rng.Next())
    .Take(Math.Max(0, totalCap - act1.Count)).ToList();
List<FightSpec> sample = act1.Concat(later).ToList();
string shard = StrArg("--shard", "");
if (shard != "")
{
    int si = int.Parse(shard.Split('/')[0]), sn = int.Parse(shard.Split('/')[1]);
    sample = sample.Where((_, idx) => idx % sn == si).ToList();
}
Console.WriteLine($"sample: act1={act1.Count} act2-3={later.Count}");

var results = new ConcurrentDictionary<int, string>();
var policy = new LookaheadPolicy { Horizon = IntArg("--horizon", 2) };
LookaheadPolicy.EnemyHpWeight = double.Parse(StrArg("--enemy-w", "0.6"), CultureInfo.InvariantCulture);
string policyName = StrArg("--policy", "beam");
CombatRunner.GivePotions = args.Contains("--potions");
for (int ai = 0; ai < args.Length - 1; ai++) if (args[ai] == "--quality") Tun.Quality(args[ai + 1]); else if (args[ai] == "--set") Tun.Set(args[ai + 1]);
Func<FightSpec, ICombatPolicy> makePolicy = policyName switch
{
    "old" => _ => policy,
    "beam" => f => new BeamPolicy(f.Row.act),
    _ => throw new ArgumentException("unknown --policy " + policyName),
};
Console.WriteLine($"potions={CombatRunner.GivePotions} policy={policyName} " + (policyName == "beam" ? Tun.Describe() : $"horizon={policy.Horizon} enemy-w={LookaheadPolicy.EnemyHpWeight}"));
long simRuns = 0;
var simWatch = Stopwatch.StartNew();
var po = new ParallelOptions { MaxDegreeOfParallelism = threads };
using (var progress = new Progress(outCsv, sample.Count))
Parallel.For(0, sample.Count, po, i =>
{
    FightSpec f = sample[i];
    var fw = Stopwatch.StartNew();
    results[i] = SimulateFight(f, seeds, makePolicy, TimeSpan.FromSeconds(wallCapSec), ref simRuns);
    if (fw.Elapsed.TotalSeconds > 2)
        Console.Error.WriteLine($"  slow fight {fw.Elapsed.TotalSeconds:F1}s {f.Row.character} {f.Row.encounter} deck={f.Row.deck.Count} -> {results[i].Split(',').Skip(12).FirstOrDefault()} runs");
    progress.Tick();
});
double simSec = simWatch.Elapsed.TotalSeconds;

using (var w = new StreamWriter(outCsv, false, new UTF8Encoding(false)))
{
    w.WriteLine("run_id,node_index,build_id,act,room_type,encounter,character,real_damage,real_turns,real_died,mapped,unmapped_note,sim_runs,sim_win_rate,sim_damage_mean,sim_damage_sd,sim_turns_mean,error");
    for (int i = 0; i < sample.Count; i++) w.WriteLine(results[i]);
}
string summary = $"fights={sample.Count} seeds={seeds} threads={threads} sim_wall={simSec:F1}s " +
                 $"fights/s={sample.Count / simSec:F3} combats/s={simRuns / simSec:F2} total_wall={total.Elapsed.TotalSeconds:F1}s " +
                 $"rollout_failures={policy.RolloutFailures + BeamPolicy.Failures}";
summary += $" first_chance_exceptions={firstChance}";
Console.WriteLine(summary);
// Allocation / GC cost of the whole process (speed work: how much time the collector takes).
Console.WriteLine($"gc: allocated={GC.GetTotalAllocatedBytes() / 1e9:F2}GB pause={GC.GetTotalPauseDuration().TotalSeconds:F2}s " +
                  $"gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} server={System.Runtime.GCSettings.IsServerGC}");
foreach (var kv in fcTypes.OrderByDescending(k => k.Value).Take(8)) Console.WriteLine($"  exc {kv.Value}: {kv.Key}");
File.AppendAllText(reportPath, "== run ==\n" + summary + "\n");
if (BeamPolicy.Profile)
{
    Console.WriteLine(BeamPolicy.ProfileReport());
    foreach (var kv in SimpleSelector.Prompts.OrderByDescending(k => k.Value).Take(40)) Console.WriteLine($"  prompt {kv.Value,8} {kv.Key}");
}

static string SimulateFight(FightSpec f, int seeds, Func<FightSpec, ICombatPolicy> makePolicy, TimeSpan cap, ref long simRuns)
{
    FightRow r = f.Row;
    var notes = new List<string>(f.Unmapped);
    string error = "";
    var dmg = new List<double>();
    var turns = new List<double>();
    int wins = 0, runs = 0, timeouts = 0;
    if (!f.Simulatable)
    {
        error = "unmapped";
    }
    else
    {
        var errors = new List<string>();
        for (int k = 0; k < seeds; k++)
        {
            try
            {
                SimOutcome o = CombatRunner.RunOne(f, k, makePolicy(f), cap);
                Interlocked.Increment(ref simRuns);
                if (o.MonsterNote is not null && k == 0) notes.Add(o.MonsterNote);
                if (o.TimedOut) { timeouts++; continue; }
                runs++;
                if (o.Won) wins++;
                dmg.Add(o.Damage);
                turns.Add(o.Turns);
            }
            catch (Exception e)
            {
                string msg = e.GetType().Name + ": " + e.Message;
                if (!errors.Contains(msg)) errors.Add(msg);
            }
        }
        if (timeouts > 0) errors.Add($"timeouts={timeouts}");
        error = string.Join(" | ", errors);
    }
    double mean = dmg.Count > 0 ? dmg.Average() : double.NaN;
    double sd = dmg.Count > 1 ? Math.Sqrt(dmg.Sum(x => (x - mean) * (x - mean)) / (dmg.Count - 1)) : (dmg.Count == 1 ? 0 : double.NaN);
    string F(double x) => double.IsNaN(x) ? "" : x.ToString("0.###", CultureInfo.InvariantCulture);
    var cells = new[]
    {
        r.run_id, r.node_index.ToString(), r.build_id, r.act.ToString(), r.room_type, r.encounter, r.character,
        r.damage_taken.ToString(), r.turns.ToString(), r.died ? "1" : "0", f.FullyMapped ? "1" : "0",
        string.Join(";", notes), runs.ToString(), runs > 0 ? F((double)wins / runs) : "", F(mean), F(sd),
        turns.Count > 0 ? F(turns.Average()) : "", error,
    };
    return string.Join(",", cells.Select(Csv));
}

static string Csv(string s)
{
    if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
    return "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
}

int IntArg(string name, int def)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : def;
}

string StrArg(string name, string def)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}
