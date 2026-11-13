using System.Text.Json;
using System.Text.Encodings.Web;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Learning;

public sealed record VisibleEventCandidate(string Id, string Content = "", bool Legal = true);
public sealed record ChoicePrediction(int Index, double Gain, double Uncertainty, bool Supported);

/// <summary>Frozen paired-screen model. Only player-visible state enters inference.</summary>
public sealed class PairedChoicePolicy
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string[] _features;
    private readonly double[] _coefficients;
    private readonly double[][] _covariance;
    private readonly Dictionary<string, int> _support;
    private readonly double _minimumGain, _uncertaintyZ;
    private readonly int _minimumSupport;
    public string Kind { get; }

    private PairedChoicePolicy(JsonElement root)
    {
        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unknown choice model version.");
        Kind = root.GetProperty("kind").GetString()!;
        if (Kind is not ("event" or "ancient")) throw new InvalidDataException("Unknown choice model kind.");
        _features = root.GetProperty("features").Deserialize<string[]>()!;
        _coefficients = root.GetProperty("coefficients").Deserialize<double[]>()!;
        _covariance = root.GetProperty("covariance").Deserialize<double[][]>()!;
        _support = root.GetProperty("action_support").Deserialize<Dictionary<string, int>>()!;
        _minimumGain = root.GetProperty("min_gain").GetDouble();
        _uncertaintyZ = root.GetProperty("uncertainty_z").GetDouble();
        _minimumSupport = root.GetProperty("min_support").GetInt32();
        if (_features.Length != _coefficients.Length || _covariance.Length != _features.Length
            || _covariance.Any(r => r.Length != _features.Length || r.Any(v => !double.IsFinite(v)))
            || _coefficients.Any(v => !double.IsFinite(v)) || !double.IsFinite(_minimumGain)
            || !double.IsFinite(_uncertaintyZ) || _minimumSupport < 0)
            throw new InvalidDataException("Invalid choice model dimensions or values.");
    }

    public static PairedChoicePolicy Load(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return new(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("Malformed choice model.", ex);
        }
    }

    private static string Action(string eventId, VisibleEventCandidate candidate) =>
        JsonSerializer.Serialize(new[] { eventId, "option", candidate.Id, candidate.Content }, Json);

    private Dictionary<string, double> Features(RunState run, string eventId, VisibleEventCandidate candidate)
    {
        var key = "action=" + Action(eventId, candidate);
        var ratio = Math.Min(1, Math.Max(0, run.Hp) / (double)Math.Max(1, run.MaxHp));
        var result = new Dictionary<string, double>
        {
            [key] = 1,
            [key + "|hp_fraction"] = ratio,
            [key + "|missing_hp_100"] = Math.Min(2, Math.Max(0, run.MaxHp - run.Hp) / 100.0),
            [key + "|gold_200"] = Math.Min(5, Math.Max(0, run.Gold) / 200.0),
            [key + "|deck_size_30"] = Math.Min(3, run.Deck.Count / 30.0),
            [key + "|upgraded_fraction"] = run.Deck.Count(c => c.Card.Upgrade > 0) / (double)Math.Max(1, run.Deck.Count),
            [key + "|floor_50"] = Math.Min(2, Math.Max(0, run.Floor) / 50.0),
            [key + "|act=" + run.Act] = 1,
            [key + "|hp=" + (ratio < .35 ? "low" : ratio < .7 ? "mid" : "high")] = 1,
            [key + "|deck=" + (run.Deck.Count < 18 ? "small" : run.Deck.Count < 28 ? "mid" : "large")] = 1,
        };
        if (!string.IsNullOrEmpty(run.ActBoss)) result[key + "|boss=" + run.ActBoss] = 1;
        if (Kind == "ancient" && candidate.Content.Length > 0)
        {
            var relic = candidate.Content.StartsWith("RELIC.", StringComparison.Ordinal) ? candidate.Content : "RELIC." + candidate.Content;
            result["public_relic=" + relic] = 1;
            foreach (var held in run.Relics) result["public_relic=" + relic + "|held=" + held] = 1;
        }
        return result;
    }

    public ChoicePrediction Predict(RunState run, string eventId, IReadOnlyList<VisibleEventCandidate> candidates, int baseline, int alternative)
    {
        if (alternative == baseline) return new(alternative, 0, 0, true);
        var original = Features(run, eventId, candidates[baseline]);
        var proposed = Features(run, eventId, candidates[alternative]);
        var active = _features.Select((name, index) => (Index: index, Value: proposed.GetValueOrDefault(name) - original.GetValueOrDefault(name)))
            .Where(x => x.Value != 0).ToArray();
        double gain = 0, variance = 0;
        foreach (var x in active)
        {
            gain += x.Value * _coefficients[x.Index];
            foreach (var y in active) variance += x.Value * _covariance[x.Index][y.Index] * y.Value;
        }
        var supported = Math.Min(_support.GetValueOrDefault(Action(eventId, candidates[baseline])),
            _support.GetValueOrDefault(Action(eventId, candidates[alternative]))) >= _minimumSupport;
        return new(alternative, gain, Math.Sqrt(Math.Max(0, variance)), supported);
    }

    public int Pick(RunState run, string eventId, IReadOnlyList<VisibleEventCandidate> candidates, int baseline, IReadOnlyList<int> alternatives)
    {
        var best = baseline;
        var threshold = _minimumGain;
        foreach (var index in alternatives)
        {
            if (index < 0 || index >= candidates.Count || !candidates[index].Legal) continue;
            var prediction = Predict(run, eventId, candidates, baseline, index);
            var lower = prediction.Gain - _uncertaintyZ * prediction.Uncertainty;
            if (prediction.Supported && lower > threshold) { best = index; threshold = lower; }
        }
        return best;
    }
}
