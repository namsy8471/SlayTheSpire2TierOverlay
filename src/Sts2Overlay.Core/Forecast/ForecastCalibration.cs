using System.Globalization;
using System.Text;

namespace Sts2Overlay.Core.Forecast;

/// <summary>Maps a raw simulated probability to "how often forecasts like this came true" in the user's runs.</summary>
public interface IProbabilityMap
{
    /// <summary>Calibrated probability, kept inside [1%, 99%].</summary>
    /// <param name="p">Raw simulated probability.</param>
    /// <param name="completion">How far the build is now, 0–1 (<see cref="ArchetypeForecast.CurrentCompletionPercent"/> / 100).</param>
    double Apply(double p, double completion);

    /// <summary>A C# expression that rebuilds this map, for pasting fitted values into <see cref="ForecastCalibration.Default"/>.</summary>
    string ToCSharp();
}

/// <summary>Shared clamp: a handful of runs can't prove 0% or 100%.</summary>
public static class ProbabilityClamp
{
    public const double Floor = 0.01, Ceiling = 0.99;

    public static double Apply(double p) => double.IsNaN(p) ? p : Math.Clamp(p, Floor, Ceiling);
}

/// <summary>
/// Platt scaling with one extra input: <c>calibrated = σ(A·logit(p) + B + C·completion)</c>.
/// Three numbers, so it can't overfit a small history the way a step curve can. A &lt; 1 pulls an over-confident
/// simulation toward the middle; C &gt; 0 says "a build that is already far along gets finished more often than the
/// simulated picks alone suggest" (the player steers toward it).
/// </summary>
/// <param name="a">Slope on logit(p).</param>
/// <param name="b">Intercept.</param>
/// <param name="c">Slope on the current completion (0–1). 0 = plain Platt scaling.</param>
public sealed class PlattMap(double a, double b, double c = 0) : IProbabilityMap
{
    /// <summary>Raw probabilities are clamped to this before logit, so a simulated 0% or 100% stays finite.</summary>
    public const double Eps = 0.005;

    public double A { get; } = a;
    public double B { get; } = b;
    public double C { get; } = c;

    public static PlattMap Identity { get; } = new(1, 0);

    public double Apply(double p, double completion) => ProbabilityClamp.Apply(Sigmoid(A * Logit(p) + B + C * completion));

    public string ToCSharp() => string.Create(CultureInfo.InvariantCulture, $"new PlattMap({A:0.####}, {B:0.####}, {C:0.####})");

    internal static double Logit(double p)
    {
        var q = Math.Clamp(p, Eps, 1 - Eps);
        return Math.Log(q / (1 - q));
    }

    internal static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));

    /// <summary>
    /// Logistic regression of the outcome on (logit p, 1, completion) by Newton's method, with a small ridge toward
    /// (A, B, C) = (1, 0, 0) so that perfectly separable data can't send a slope to infinity.
    /// </summary>
    /// <param name="samples">(raw probability, completion 0–1, came true).</param>
    /// <param name="useCompletion">False fits plain two-number Platt scaling (C = 0).</param>
    /// <param name="ridge">Penalty weight.</param>
    public static PlattMap Fit(IEnumerable<(double P, double Completion, bool Outcome)> samples, bool useCompletion = true, double ridge = 1e-2)
    {
        var data = samples.Where(s => double.IsFinite(s.P))
            .Select(s => (X: new[] { Logit(s.P), 1.0, useCompletion ? s.Completion : 0.0 }, Y: s.Outcome ? 1.0 : 0.0))
            .ToArray();
        if (data.Length == 0) return Identity;

        double[] w = [1, 0, 0];
        double[] prior = [1, 0, 0];
        for (var iter = 0; iter < 50; iter++)
        {
            // Gradient g and Hessian h of the penalised negative log-likelihood.
            var g = new double[3];
            var h = new double[3, 3];
            for (var i = 0; i < 3; i++)
            {
                g[i] = ridge * (w[i] - prior[i]);
                h[i, i] = ridge;
            }
            foreach (var (x, y) in data)
            {
                var q = Sigmoid(w[0] * x[0] + w[1] * x[1] + w[2] * x[2]);
                var wt = q * (1 - q);
                for (var i = 0; i < 3; i++)
                {
                    g[i] += (q - y) * x[i];
                    for (var j = 0; j < 3; j++) h[i, j] += wt * x[i] * x[j];
                }
            }
            if (!Solve3(h, g, out var step)) break;
            for (var i = 0; i < 3; i++) w[i] -= step[i];
            if (Math.Abs(step[0]) + Math.Abs(step[1]) + Math.Abs(step[2]) < 1e-9) break;
        }
        return new PlattMap(w[0], w[1], w[2]);
    }

    /// <summary>Solves h·x = g for a 3×3 system (Gaussian elimination with partial pivoting).</summary>
    private static bool Solve3(double[,] h, double[] g, out double[] x)
    {
        var m = new double[3, 4];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++) m[i, j] = h[i, j];
            m[i, 3] = g[i];
        }
        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 3; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            }
            if (Math.Abs(m[pivot, col]) < 1e-12)
            {
                x = [];
                return false;
            }
            for (var j = 0; j < 4; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            for (var r = 0; r < 3; r++)
            {
                if (r == col) continue;
                var f = m[r, col] / m[col, col];
                for (var j = col; j < 4; j++) m[r, j] -= f * m[col, j];
            }
        }
        x = [m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2]];
        return true;
    }
}

/// <summary>
/// A monotone map "raw simulated probability → how often that actually happened", fitted by isotonic regression
/// (pool-adjacent-violators). Applied with linear interpolation between the fitted points.
/// </summary>
/// <remarks>
/// Isotonic regression only assumes "a higher raw forecast never means a lower real chance", so it can fix any bent
/// reliability curve (here: low raw values that happen more often than they say, high ones that happen less).
/// </remarks>
public sealed class IsotonicMap : IProbabilityMap
{
    private readonly double[] _x;
    private readonly double[] _y;

    public IsotonicMap(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count) throw new ArgumentException("x and y differ in length.");
        _x = x.ToArray();
        _y = y.ToArray();
    }

    /// <summary>No change (besides the 1–99% clamp).</summary>
    public static IsotonicMap Identity { get; } = new([0.0, 1.0], [0.0, 1.0]);

    public IReadOnlyList<double> X => _x;
    public IReadOnlyList<double> Y => _y;

    /// <summary>Uses only <paramref name="p"/>; <paramref name="completion"/> is ignored.</summary>
    public double Apply(double p, double completion = 0)
    {
        if (_x.Length == 0 || double.IsNaN(p)) return ProbabilityClamp.Apply(p);
        double v;
        if (p <= _x[0]) v = _y[0];
        else if (p >= _x[^1]) v = _y[^1];
        else
        {
            var i = Array.BinarySearch(_x, p);
            if (i >= 0) v = _y[i];
            else
            {
                var hi = ~i;
                var lo = hi - 1;
                var t = (p - _x[lo]) / (_x[hi] - _x[lo]);
                v = _y[lo] + t * (_y[hi] - _y[lo]);
            }
        }
        return ProbabilityClamp.Apply(v);
    }

    /// <summary>
    /// Pool-adjacent-violators: sort by forecast, then merge neighbouring groups while an earlier group has a higher
    /// observed rate than a later one. Each final group becomes one point (mean forecast, observed rate).
    /// </summary>
    public static IsotonicMap Fit(IEnumerable<(double P, bool Outcome)> samples)
    {
        var sorted = samples.Where(s => double.IsFinite(s.P)).OrderBy(s => s.P).ToList();
        if (sorted.Count == 0) return Identity;

        // Stack of blocks: (sum of forecasts, sum of outcomes, count).
        var blocks = new List<(double SumP, double SumY, int N)>();
        foreach (var (p, outcome) in sorted)
        {
            blocks.Add((p, outcome ? 1 : 0, 1));
            while (blocks.Count >= 2)
            {
                var b = blocks[^1];
                var a = blocks[^2];
                if (a.SumY / a.N < b.SumY / b.N) break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (a.SumP + b.SumP, a.SumY + b.SumY, a.N + b.N);
            }
        }

        // Equal mean forecasts would break interpolation: keep the later (higher-rate) point.
        var x = new List<double>();
        var y = new List<double>();
        foreach (var (sumP, sumY, n) in blocks)
        {
            var mx = sumP / n;
            if (x.Count > 0 && mx <= x[^1] + 1e-12)
            {
                y[^1] = sumY / n;
                continue;
            }
            x.Add(mx);
            y.Add(sumY / n);
        }
        return new IsotonicMap(x, y);
    }

    /// <inheritdoc />
    public string ToCSharp()
    {
        static string Arr(double[] v) => "[" + string.Join(", ", v.Select(d => d.ToString("0.####", CultureInfo.InvariantCulture))) + "]";
        return new StringBuilder().Append("new IsotonicMap(").Append(Arr(_x)).Append(", ").Append(Arr(_y)).Append(')').ToString();
    }
}

/// <summary>The two calibration curves the forecaster applies.</summary>
/// <param name="IfSurvive">For "complete by the end of act 3 if the run gets there" (fitted on runs that reached the act 3 boss).</param>
/// <param name="All">For "complete when the run ends", deaths included (fitted on all runs).</param>
public sealed record ForecastCalibration(IProbabilityMap IfSurvive, IProbabilityMap All)
{
    public static ForecastCalibration Identity { get; } = new(IsotonicMap.Identity, IsotonicMap.Identity);

    /// <summary>
    /// Platt maps (raw + current completion) fitted on the user's 50 runs (both Steam folders, v0.107.1 pack, 1000 sims) with
    /// <c>RunHistoryAnalyzer calibrate --emit</c>. Refit when the pack or the model changes. [추정, 개인 기록 기반]
    /// </summary>
    public static ForecastCalibration Default { get; } = new(
        IfSurvive: new PlattMap(0.3923, -0.3802, 4.3322),
        All: new PlattMap(0.3583, -0.9475, 6.7843));
}
