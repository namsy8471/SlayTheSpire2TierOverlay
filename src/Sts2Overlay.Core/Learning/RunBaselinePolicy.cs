using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Learning;

public sealed record BaselineCandidate(string Type, string? Id = null, int Upgrades = 0, int? Price = null, bool Legal = true);

/// <summary>An event button as the simulator's adjustment reads it: its option id (an Ancient's relic option: the relic's
/// class name), the relic it offers (an Ancient's relic option only, id without <c>RELIC.</c>) and whether it can be taken.</summary>
public sealed record EventCandidate(string Id, string? Relic, bool Legal);

/// <summary>Learned adjustments for the full-run policy's rest, shop and map choices.</summary>
public sealed class RunBaselinePolicy(IReadOnlyDictionary<string, double> weights)
{
    internal const string Ironclad = "CHARACTER.IRONCLAD";

    /// <summary>Highest ascension the learned policies run at.</summary>
    internal const int MaxAscension = 10;

    /// <summary>The runs the AI covers (<see cref="AiCoverage.Covers"/>: standard runs at A0 to A10, co-op with the solo
    /// policy) with HP and gold read. Measured on whole runs at A0 and A10 (Ironclad's adopted policy) and at A0, A8 and
    /// A10 (the other characters' adjustments, learned at A8); the ascensions between use the same files (11-18: an A1 run
    /// otherwise fell back to the old rules). Which characters have a policy is the pack's <see cref="DataPack.ForRun"/>.</summary>
    internal static bool Supports(RunState state) =>
        AiCoverage.Covers(state) && state.VitalsKnown && state.SaveSchemaVersion is null or 20 or 21;

    public static RunBaselinePolicy? For(DataPack pack, RunState state) =>
        Supports(state) ? pack.ForRun(state).RunBaseline : null;

    internal static RunBaselinePolicy Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int n) || n != 1)
            throw new InvalidDataException("Unknown run adjustment version.");
        if (!root.TryGetProperty("weights", out var entries)) throw new InvalidDataException("Missing run adjustment weights.");
        var values = entries.Deserialize<Dictionary<string, double>>();
        if (values is null || values.Count == 0 || values.Values.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("Invalid run adjustment weights.");
        return new(values);
    }

    /// <summary>Returns the best legal index, or -1 if no action is legal. Exact ties favor the rule, then offer order.</summary>
    public int Pick(string kind, RunState state, IReadOnlyList<BaselineCandidate> options, int rule, int second = -1, bool? hasCore = null)
    {
        var hp = (long)state.Hp * 10;
        var max = (long)state.MaxHp;
        var h = max <= 0 || hp < 3 * max ? 0 : hp < 5 * max ? 1 : hp < 7 * max ? 2 : hp < 9 * max ? 3 : 4;
        var best = -1;
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < options.Count; i++)
        {
            var c = options[i];
            if (!c.Legal) continue;
            var key = $"{kind}:{c.Type}";
            var id = c.Id;
            if (id is not null && (id.StartsWith("CARD.", StringComparison.Ordinal) || id.StartsWith("RELIC.", StringComparison.Ordinal)
                || id.StartsWith("POTION.", StringComparison.Ordinal))) id = id[(id.IndexOf('.') + 1)..];
            double score = i == rule ? Weight($"{kind}:rule") : i == second ? Weight($"{kind}:rank2") : 0;
            score += Weight(key);
            switch (kind)
            {
                case "rest":
                    score += Weight($"{key}|h{h}");
                    score += Weight($"{key}|a{state.Act}");
                    score += Weight($"{key}|h{h}|a{state.Act}");
                    if (c.Type == "smith" && id is not null) score += Weight($"{key}|{id}");
                    if (c.Type == "smith" && c.Upgrades > 0) score += Weight($"{key}|up");
                    break;
                case "path":
                    score += Weight($"{key}|a{state.Act}");
                    score += Weight($"{key}|h{h}");
                    score += Weight($"{key}|a{state.Act}|h{h}");
                    break;
                case "shop":
                    score += Weight($"{key}|a{state.Act}");
                    if (id is not null) score += Weight($"{key}|{id}");
                    score += Weight($"{key}|h{h}");
                    if (c.Price is int price)
                    {
                        var left = state.Gold - price;
                        score += Weight($"{key}|g{(left < 50 ? 0 : left < 150 ? 1 : 2)}");
                    }
                    if (hasCore.HasValue) score += Weight($"{key}|c{(hasCore.Value ? 1 : 0)}");
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
            if (score > bestScore || (score == bestScore && i == rule))
            {
                best = i;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>True when the file has event weights (the simulator's <c>--adjust-kinds event</c>).</summary>
    public bool HasEventWeights => _hasEvent ??= weights.Keys.Any(k => k.StartsWith("event:", StringComparison.Ordinal));
    private bool? _hasEvent;

    /// <summary>
    /// The simulator's event adjustment (sts2-agent <c>runs/adjust.rs</c>, kind <c>event</c>): each legal button's total over
    /// <c>event:rule</c> (the rule's pick), <c>event:E|O</c>, <c>event:E|O|hH</c>, <c>event:E|O|aA</c>, <c>event:E|O|gG</c> (gold at
    /// the question), <c>event:E|O|C</c> (character) and for a relic option <c>event:relic|R</c>, <c>event:relic|R|C</c>. The
    /// highest total wins; an exact tie goes to the rule, then to the lower index. Returns <paramref name="rule"/> when it is
    /// not a legal index.
    /// </summary>
    public int PickEvent(RunState state, string eventId, IReadOnlyList<EventCandidate> options, int rule)
    {
        if (rule < 0 || rule >= options.Count || !options[rule].Legal) return rule;
        var e = eventId.StartsWith("EVENT.", StringComparison.Ordinal) ? eventId["EVENT.".Length..] : eventId;
        var h = HpBucket(state);
        var g = state.Gold < 50 ? 0 : state.Gold < 150 ? 1 : 2;
        var c = (state.Character ?? "").Replace("CHARACTER.", "", StringComparison.Ordinal).ToLowerInvariant();
        double Total(int i)
        {
            var o = options[i];
            var t = i == rule ? Weight("event:rule") : 0;
            t += Weight($"event:{e}|{o.Id}") + Weight($"event:{e}|{o.Id}|h{h}") + Weight($"event:{e}|{o.Id}|a{state.Act}")
                + Weight($"event:{e}|{o.Id}|g{g}") + Weight($"event:{e}|{o.Id}|{c}");
            if (o.Relic is not null) t += Weight($"event:relic|{o.Relic}") + Weight($"event:relic|{o.Relic}|{c}");
            return t;
        }
        var best = rule;
        var bestTotal = Total(rule);
        for (var i = 0; i < options.Count; i++)
        {
            if (!options[i].Legal) continue;
            var t = Total(i);
            if (t > bestTotal) { best = i; bestTotal = t; }
        }
        return best;
    }

    private static int HpBucket(RunState state)
    {
        var hp = (long)state.Hp * 10;
        var max = (long)state.MaxHp;
        return max <= 0 || hp < 3 * max ? 0 : hp < 5 * max ? 1 : hp < 7 * max ? 2 : hp < 9 * max ? 3 : 4;
    }

    private double Weight(string key) => weights.GetValueOrDefault(key);
}
