using System.Globalization;
using System.Text.Json;
using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Replay.Validation;

/// <summary>
/// One tunable scoring weight with a small grid of candidate values. <see cref="CurrentIndex"/> is the value the
/// pack uses today; the search is regularized toward it.
/// </summary>
public sealed record WeightKnob(string Name, IReadOnlyList<double> Values, int CurrentIndex, Action<Weights, double> Apply)
{
    public double Current => Values[CurrentIndex];

    /// <summary>A knob whose grid is the current value times each of <paramref name="factors"/> (which must contain 1).</summary>
    public static WeightKnob Scaled(string name, double current, IReadOnlyList<double> factors, Action<Weights, double> apply)
    {
        ArgumentNullException.ThrowIfNull(factors);
        var index = factors.ToList().IndexOf(1.0);
        if (index < 0) throw new ArgumentException("Factors must contain 1 (the current value).", nameof(factors));
        return new WeightKnob(name, factors.Select(f => current * f).ToArray(), index, apply);
    }
}

/// <summary>
/// Coarse, regularized coordinate search over scoring weights, fitted on a given set of decisions only
/// (so it can run inside every cross-validation fold).
/// </summary>
/// <remarks>
/// Objective on the fitting decisions: mean(top-1 hit + exact hit) − <see cref="Lambda"/> × (grid steps away from the
/// current values, summed over knobs). The penalty is the regularization: a knob moves only when the move buys
/// more agreement than it costs, so noise in a few decisions cannot drag weights to the edge of the grid.
/// Per-decision results are cached per candidate, so folds that visit the same candidate do not re-run the engine.
/// </remarks>
public sealed class WeightSearch
{
    private readonly Dictionary<string, AgreementMetrics[]> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<LabeledDecision, int> _index = new(ReferenceEqualityComparer.Instance);
    private readonly IReadOnlyList<LabeledDecision> _universe;

    /// <param name="basePack">Pack whose tiers/archetypes stay fixed; only <see cref="DataPack.Weights"/> varies.</param>
    /// <param name="knobs">The weights to search.</param>
    /// <param name="universe">Every decision any fold may fit or test on (results are cached over this list).</param>
    /// <param name="lambda">Penalty per grid step away from the current value.</param>
    public WeightSearch(DataPack basePack, IReadOnlyList<WeightKnob> knobs, IReadOnlyList<LabeledDecision> universe, double lambda)
    {
        ArgumentNullException.ThrowIfNull(basePack);
        ArgumentNullException.ThrowIfNull(knobs);
        ArgumentNullException.ThrowIfNull(universe);
        BasePack = basePack;
        Knobs = knobs;
        Lambda = lambda;
        _universe = universe;
        for (var i = 0; i < universe.Count; i++) _index[universe[i]] = i;
    }

    public DataPack BasePack { get; }

    public IReadOnlyList<WeightKnob> Knobs { get; }

    public double Lambda { get; }

    /// <summary>Grid position of the current weights.</summary>
    public int[] Current => Knobs.Select(k => k.CurrentIndex).ToArray();

    /// <summary>Number of distinct candidates evaluated so far (each costs one engine pass over the universe).</summary>
    public int EvaluatedCandidates => _cache.Count;

    /// <summary>Coordinate descent from the current weights, at most <paramref name="passes"/> sweeps. Uses <paramref name="train"/> only.</summary>
    public int[] Fit(IReadOnlyList<LabeledDecision> train, int passes = 3)
    {
        ArgumentNullException.ThrowIfNull(train);
        var best = Current;
        var bestScore = Objective(best, train);
        for (var pass = 0; pass < passes; pass++)
        {
            var changed = false;
            for (var k = 0; k < Knobs.Count; k++)
            {
                // Try values nearest the current one first so ties keep the smaller move.
                foreach (var v in Enumerable.Range(0, Knobs[k].Values.Count).OrderBy(v => Math.Abs(v - Knobs[k].CurrentIndex)))
                {
                    if (v == best[k]) continue;
                    var candidate = (int[])best.Clone();
                    candidate[k] = v;
                    var score = Objective(candidate, train);
                    if (score > bestScore + 1e-12)
                    {
                        best = candidate;
                        bestScore = score;
                        changed = true;
                    }
                }
            }

            if (!changed) break;
        }

        return best;
    }

    /// <summary>Regularized objective of a candidate on some decisions.</summary>
    public double Objective(int[] candidate, IReadOnlyList<LabeledDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (decisions.Count == 0) return -Penalty(candidate);
        var m = Evaluate(candidate, decisions);
        return (m.Top1Hits + m.ExactHits) / decisions.Count - Penalty(candidate);
    }

    public double Penalty(int[] candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Lambda * candidate.Select((v, k) => Math.Abs(v - Knobs[k].CurrentIndex)).Sum();
    }

    /// <summary>Unregularized metrics of a candidate on some decisions (all must be in the universe).</summary>
    public AgreementMetrics Evaluate(int[] candidate, IEnumerable<LabeledDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var all = Results(candidate);
        return decisions.Aggregate(AgreementMetrics.Empty, (acc, d) => acc.Add(all[_index[d]]));
    }

    /// <summary>The weights of a candidate: a copy of the base weights with every knob applied.</summary>
    public Weights Build(int[] candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var w = Clone(BasePack.Weights);
        for (var k = 0; k < Knobs.Count; k++) Knobs[k].Apply(w, Knobs[k].Values[candidate[k]]);
        return w;
    }

    /// <summary>Human-readable "name=value" for knobs that differ from the current weights.</summary>
    public string Describe(int[] candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var moved = Knobs.Select((k, i) => (k, i)).Where(x => candidate[x.i] != x.k.CurrentIndex)
            .Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.k.Name} {x.k.Current:0.##}→{x.k.Values[candidate[x.i]]:0.##}"));
        var text = string.Join(", ", moved);
        return text.Length == 0 ? "(변경 없음)" : text;
    }

    /// <summary>Deep copy through the pack's own JSON options.</summary>
    public static Weights Clone(Weights w)
    {
        ArgumentNullException.ThrowIfNull(w);
        var json = JsonSerializer.Serialize(w, DataPackLoader.JsonOptions);
        return DataPackLoader.LoadWeights(json);
    }

    private AgreementMetrics[] Results(int[] candidate)
    {
        var key = string.Join(",", candidate);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var pack = new DataPack
        {
            Weights = Build(candidate),
            Tiers = BasePack.Tiers,
            Archetypes = BasePack.Archetypes,
            Combos = BasePack.Combos,
            Catalog = BasePack.Catalog,
            IdAliases = BasePack.IdAliases,
        };
        var engine = new EngineRecommender(pack);
        var results = new AgreementMetrics[_universe.Count];
        Parallel.For(0, _universe.Count, i => results[i] = AgreementMetrics.Of(_universe[i], engine.Predict(_universe[i])));
        _cache[key] = results;
        return results;
    }
}

/// <summary>The default knob set: the card-scoring weights the holdout pointed at (need, dilution, duplicates, build, skip).</summary>
public static class ScoringKnobs
{
    /// <summary>Multipliers of the current value tried for each scaled knob.</summary>
    public static IReadOnlyList<double> DefaultFactors { get; } = [0, 0.5, 1, 1.5, 2];

    /// <summary>Same grid without 0, for a search that may weaken a component but never switch it off.</summary>
    public static IReadOnlyList<double> NoZeroFactors { get; } = [0.5, 0.75, 1, 1.5, 2];

    public static IReadOnlyList<WeightKnob> Default(Weights current, IReadOnlyList<double>? factors = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var grid = factors ?? DefaultFactors;
        var t0 = current.Skip.T0.ToDictionary(kv => kv.Key, kv => kv.Value);
        var tierValue = current.TierValue.ToDictionary(kv => kv.Key, kv => kv.Value);
        const double mid = 50; // B; the spread knob scales distances from it

        return
        [
            WeightKnob.Scaled("wNeed", current.WNeed, grid, (w, v) => w.WNeed = v),
            WeightKnob.Scaled("lambdaDilution", current.LambdaDilution, grid, (w, v) => w.LambdaDilution = v),
            WeightKnob.Scaled("rhoDuplicate", current.RhoDuplicate, grid, (w, v) => w.RhoDuplicate = v),
            WeightKnob.Scaled("offBuildPenalty", current.OffBuildPenalty, grid, (w, v) => w.OffBuildPenalty = v),
            WeightKnob.Scaled("wArch", current.WArch, grid, (w, v) => w.WArch = v),
            WeightKnob.Scaled("requiresPenalty", current.RequiresPenalty, grid, (w, v) => w.RequiresPenalty = v),
            new WeightKnob("skip.t0 (모든 막 +)", [-10, -5, 0, 5, 10], 2, (w, v) =>
            {
                foreach (var (act, value) in t0) w.Skip.T0[act] = value + v;
            }),
            WeightKnob.Scaled("skip.lambdaSize", current.Skip.LambdaSize, grid, (w, v) => w.Skip.LambdaSize = v),
            WeightKnob.Scaled("skip.lambdaCommit", current.Skip.LambdaCommit, grid, (w, v) => w.Skip.LambdaCommit = v),
            new WeightKnob("tierSpread (B 기준 배율)", [0.5, 0.75, 1, 1.5, 2], 2, (w, v) =>
            {
                foreach (var (tier, value) in tierValue) w.TierValue[tier] = mid + (value - mid) * v;
            }),
        ];
    }
}
