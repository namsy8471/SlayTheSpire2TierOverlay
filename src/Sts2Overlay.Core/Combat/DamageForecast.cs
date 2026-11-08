namespace Sts2Overlay.Core.Combat;

/// <summary>
/// The outcome distribution of many simulated fights against a target that never acts: how much damage the deck deals in
/// its first turns (each sample = one shuffle played out greedily).
/// </summary>
public sealed class DamageForecast
{
    private readonly double[] _sorted;

    public DamageForecast(IReadOnlyList<double> totals, IReadOnlyList<double> meanPerTurn, double meanDoom, ForecastCoverage coverage)
    {
        _sorted = totals.Order().ToArray();
        Totals = totals;
        MeanPerTurn = meanPerTurn;
        MeanDoom = meanDoom;
        Coverage = coverage;
    }

    /// <summary>
    /// One value per simulated fight: the most HP the target could have had and still died
    /// (damage dealt, or damage + Doom at a Doom check if that is higher). Unsorted, in simulation order.
    /// </summary>
    public IReadOnlyList<double> Totals { get; }

    /// <summary>Average damage per player turn (index 0 = turn 1), including the poison that ticks on the enemy turn after it.</summary>
    public IReadOnlyList<double> MeanPerTurn { get; }

    /// <summary>Average Doom on the target at the last Doom check (it kills when Doom ≥ remaining HP).</summary>
    public double MeanDoom { get; }

    public ForecastCoverage Coverage { get; }

    public int Samples => _sorted.Length;

    public double Mean => _sorted.Length == 0 ? 0 : _sorted.Average();

    /// <summary>Share of simulated fights that killed a target with <paramref name="hp"/> HP.</summary>
    public double ProbAtLeast(double hp)
    {
        if (_sorted.Length == 0) return 0;
        // first index with total >= hp
        int lo = 0, hi = _sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_sorted[mid] >= hp) hi = mid; else lo = mid + 1;
        }
        return (double)(_sorted.Length - lo) / _sorted.Length;
    }

    /// <summary>The <paramref name="p"/>-quantile of <see cref="Totals"/> (0.5 = median).</summary>
    public double Quantile(double p)
    {
        if (_sorted.Length == 0) return 0;
        var i = (int)Math.Clamp(Math.Floor(p * (_sorted.Length - 1)), 0, _sorted.Length - 1);
        return _sorted[i];
    }

    /// <summary>
    /// Fights of several players against one target: sample i of every forecast is one joint fight, so totals and per-turn
    /// means add up (the players' shuffles are independent).
    /// </summary>
    public static DamageForecast Combine(IReadOnlyList<DamageForecast> parts)
    {
        if (parts.Count == 1) return parts[0];
        var n = parts.Min(p => p.Totals.Count);
        var totals = new double[n];
        foreach (var p in parts)
        {
            for (var i = 0; i < n; i++) totals[i] += p.Totals[i];
        }
        var turns = parts.Max(p => p.MeanPerTurn.Count);
        var perTurn = new double[turns];
        foreach (var p in parts)
        {
            for (var t = 0; t < p.MeanPerTurn.Count; t++) perTurn[t] += p.MeanPerTurn[t];
        }
        var coverage = ForecastCoverage.Merge(parts.Select(p => p.Coverage));
        return new DamageForecast(totals, perTurn, parts.Sum(p => p.MeanDoom), coverage);
    }
}

/// <summary>Which deck cards/relics the simulator could express exactly, approximately, or not at all.</summary>
public sealed record ForecastCoverage
{
    public int Cards { get; init; }
    public int FullCards { get; init; }
    public IReadOnlyList<string> ApproxCards { get; init; } = [];
    public IReadOnlyList<string> UnknownCards { get; init; } = [];
    public IReadOnlyList<string> ApproxRelics { get; init; } = [];

    public static ForecastCoverage Merge(IEnumerable<ForecastCoverage> parts)
    {
        var list = parts.ToList();
        return new ForecastCoverage
        {
            Cards = list.Sum(p => p.Cards),
            FullCards = list.Sum(p => p.FullCards),
            ApproxCards = list.SelectMany(p => p.ApproxCards).Distinct().ToArray(),
            UnknownCards = list.SelectMany(p => p.UnknownCards).Distinct().ToArray(),
            ApproxRelics = list.SelectMany(p => p.ApproxRelics).Distinct().ToArray(),
        };
    }
}
