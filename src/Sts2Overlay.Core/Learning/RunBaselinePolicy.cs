using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Learning;

public sealed record BaselineCandidate(string Type, string? Id = null, int Upgrades = 0, int? Price = null, bool Legal = true);

/// <summary>Learned adjustments for the full-run policy's rest, shop and map choices.</summary>
public sealed class RunBaselinePolicy(IReadOnlyDictionary<string, double> weights)
{
    internal static bool Supports(RunState state) => state.Character == "CHARACTER.IRONCLAD"
        && state.IsStandardRun && !state.IsMultiplayer && state.Ascension is 0 or 10 && state.VitalsKnown
        && state.SaveSchemaVersion is null or 20 or 21;

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

    private double Weight(string key) => weights.GetValueOrDefault(key);
}
