using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimTrial;

/// <summary>One fight to simulate (one JSON line in <c>SimFuzz api</c> mode).</summary>
public sealed class FightRequest
{
    public string id { get; set; } = "";
    public string character { get; set; } = "";          // CHARACTER.IRONCLAD
    public List<string> deck { get; set; } = new();       // CARD.X, CARD.X+1, CARD.X+1#ENCHANTMENT.Y:3
    public List<string> relics { get; set; } = new();     // RELIC.X
    public int hp { get; set; }
    public int max_hp { get; set; }
    public string encounter { get; set; } = "";          // ENCOUNTER.X
    public int act { get; set; } = 1;                     // 1..3
    public List<string>? monsters { get; set; }           // optional: force this monster composition
    public List<string>? potions { get; set; }            // optional: potions in the belt (used by the AI)
    public Dictionary<string, int>? relic_state { get; set; } // optional: GIRYA lifts, PUMPKIN_CANDLE, tea sets
    public int seeds { get; set; } = 0;                   // 0 = the command line's --seeds
    public string seed_base { get; set; } = "";          // RNG seed prefix (default: id)
}

public sealed record FightOutcome(bool won, bool died, int damage, int turns);

public sealed class FightSummary
{
    public string id { get; set; } = "";
    public int runs { get; set; }
    public double win_rate { get; set; }
    public double damage_mean { get; set; }
    public double damage_sd { get; set; }
    public double turns_mean { get; set; }
    public List<FightOutcome> outcomes { get; set; } = new();
    public List<string> notes { get; set; } = new();
    public string? error { get; set; }
}

/// <summary>
/// Programmatic entry point for other offline tools (e.g. a value model trained on simulated fights):
/// (character, deck, relics, HP, encounter, seed) -> damage taken / won / turns, played by
/// <see cref="BeamPolicy"/>. Damage = HP lost in the fight (all current HP when the player dies).
/// Timeouts (30 player turns or the wall-time cap) are left out of the outcomes and noted.
/// Requires <c>ModelDb.Init(ContentRegistry.AllTypes)</c> once per process.
/// </summary>
public static class SimApi
{
    public static FightSummary Simulate(FightRequest req, int seeds, TimeSpan? wallCap = null)
    {
        var sum = new FightSummary { id = req.id };
        var row = new FightRow
        {
            run_id = string.IsNullOrEmpty(req.seed_base) ? req.id : req.seed_base,
            character = req.character,
            build_id = "v0.111",
            ascension = 10,
            act = Math.Clamp(req.act, 1, 3),
            encounter = req.encounter,
            monsters = req.monsters ?? new List<string>(),
            hp_before = req.hp,
            max_hp_before = req.max_hp,
            deck = req.deck,
            relics = req.relics,
            potions = req.potions,
            relic_state = req.relic_state,
        };
        FightSpec spec = Mapper.Map(row);
        sum.notes.AddRange(spec.Unmapped);
        if (!spec.Simulatable)
        {
            sum.error = "unmapped";
            return sum;
        }
        int n = req.seeds > 0 ? req.seeds : seeds;
        int timeouts = 0;
        for (int k = 0; k < n; k++)
        {
            try
            {
                SimOutcome o = CombatRunner.RunOne(spec, k, new BeamPolicy(row.act), wallCap ?? TimeSpan.FromSeconds(30));
                if (o.MonsterNote is not null && req.monsters is not null && k == 0) sum.notes.Add(o.MonsterNote);
                if (o.TimedOut) { timeouts++; continue; }
                sum.outcomes.Add(new FightOutcome(o.Won, o.Died, o.Damage, o.Turns));
            }
            catch (Exception e)
            {
                sum.error = e.GetType().Name + ": " + e.Message;
            }
        }
        if (timeouts > 0) sum.notes.Add($"timeouts={timeouts}");
        sum.runs = sum.outcomes.Count;
        if (sum.runs > 0)
        {
            sum.win_rate = sum.outcomes.Count(o => o.won) / (double)sum.runs;
            sum.damage_mean = sum.outcomes.Average(o => o.damage);
            sum.turns_mean = sum.outcomes.Average(o => o.turns);
            double m = sum.damage_mean;
            sum.damage_sd = sum.runs > 1 ? Math.Sqrt(sum.outcomes.Sum(o => (o.damage - m) * (o.damage - m)) / (sum.runs - 1)) : 0;
        }
        return sum;
    }

    /// <summary>
    /// <c>SimFuzz api &lt;requests.jsonl&gt; &lt;results.jsonl&gt; [--seeds 5] [--threads N] [--set name=value ...]</c>:
    /// one <see cref="FightRequest"/> per input line, one <see cref="FightSummary"/> per output line (same order).
    /// Potions in a request are always given to the player.
    /// </summary>
    public static void Cli(string[] args)
    {
        string inPath = args[0], outPath = args[1];
        int seeds = Int("--seeds", 5), threads = Int("--threads", Environment.ProcessorCount);
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--set") Tun.Set(args[i + 1]);
        CombatRunner.GivePotions = true;
        var reqs = File.ReadLines(inPath).Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<FightRequest>(l)!).ToList();
        var results = new FightSummary[reqs.Count];
        var sw = Stopwatch.StartNew();
        Parallel.For(0, reqs.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i => results[i] = Simulate(reqs[i], seeds));
        var opts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        File.WriteAllLines(outPath, results.Select(r => JsonSerializer.Serialize(r, opts)));
        int combats = results.Sum(r => r.runs);
        Console.WriteLine($"fights={reqs.Count} combats={combats} wall={sw.Elapsed.TotalSeconds:F1}s combats/s={combats / sw.Elapsed.TotalSeconds:F2}");

        int Int(string name, int def)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : def;
        }
    }
}
