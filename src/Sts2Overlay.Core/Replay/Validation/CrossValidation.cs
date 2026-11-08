using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Replay.Validation;

/// <summary>Result of one fold. "Calibrated" = base pack + <see cref="TierShiftCalibrator"/> fitted on this fold's train part only.</summary>
public sealed record FoldResult
{
    public required Fold Fold { get; init; }
    public required IReadOnlyList<TierShift> Shifts { get; init; }
    public required AgreementMetrics BaseTest { get; init; }
    public required AgreementMetrics CalibratedTest { get; init; }
    public required AgreementMetrics TierOnlyTest { get; init; }
    public required AgreementMetrics RandomTest { get; init; }

    /// <summary>In-sample scores on the train part (null when not computed). The gap to the test scores shows overfitting.</summary>
    public AgreementMetrics? BaseTrain { get; init; }

    public AgreementMetrics? CalibratedTrain { get; init; }
}

/// <summary>All folds of one split scheme, with test metrics summed over folds (every decision is tested once for LOGO/k-fold).</summary>
public sealed record CrossValidationResult(string Name, IReadOnlyList<FoldResult> Folds)
{
    public AgreementMetrics BaseTest => Sum(f => f.BaseTest);
    public AgreementMetrics CalibratedTest => Sum(f => f.CalibratedTest);
    public AgreementMetrics TierOnlyTest => Sum(f => f.TierOnlyTest);
    public AgreementMetrics RandomTest => Sum(f => f.RandomTest);
    public AgreementMetrics? BaseTrain => Folds.All(f => f.BaseTrain is not null) ? Sum(f => f.BaseTrain!) : null;
    public AgreementMetrics? CalibratedTrain => Folds.All(f => f.CalibratedTrain is not null) ? Sum(f => f.CalibratedTrain!) : null;

    /// <summary>How many folds proposed each shift (a shift chosen in most folds is stable, one chosen once is noise).</summary>
    public IReadOnlyList<(string PackKey, string CardId, int Steps, int Folds)> ShiftStability =>
        Folds.SelectMany(f => f.Shifts)
            .GroupBy(s => (s.PackKey, s.CardId, s.Steps))
            .Select(g => (g.Key.PackKey, g.Key.CardId, g.Key.Steps, g.Count()))
            .OrderByDescending(x => x.Item4).ThenBy(x => x.CardId, StringComparer.Ordinal)
            .ToArray();

    private AgreementMetrics Sum(Func<FoldResult, AgreementMetrics> pick)
        => Folds.Aggregate(AgreementMetrics.Empty, (acc, f) => acc.Add(pick(f)));
}

/// <summary>
/// Runs fold-aware validation: for every fold the calibrator is fitted on the train streams only and both the base
/// and the calibrated pack are scored on the test streams. Baselines (tier only, uniform random) are scored on the same test decisions.
/// </summary>
public sealed class CrossValidation
{
    private readonly Dictionary<ReplayStream, IReadOnlyList<LabeledDecision>> _decisions = new(ReferenceEqualityComparer.Instance);
    private readonly CachedRecommender _base;
    private readonly CachedRecommender _tierOnly;
    private readonly TierShiftCalibrator _calibrator;

    public CrossValidation(DataPack basePack, TierShiftOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(basePack);
        BasePack = basePack;
        _base = new CachedRecommender(new EngineRecommender(basePack));
        _tierOnly = new CachedRecommender(new TierOnlyRecommender(basePack));
        _calibrator = new TierShiftCalibrator(basePack, options, _base);
    }

    public DataPack BasePack { get; }

    public TierShiftCalibrator Calibrator => _calibrator;

    /// <summary>Comparable decisions of a stream (cached).</summary>
    public IReadOnlyList<LabeledDecision> DecisionsOf(ReplayStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!_decisions.TryGetValue(stream, out var list))
        {
            _decisions[stream] = list = LabeledDecision.Extract(stream);
        }

        return list;
    }

    public IReadOnlyList<LabeledDecision> DecisionsOf(IEnumerable<ReplayStream> streams) => streams.SelectMany(DecisionsOf).ToArray();

    public CrossValidationResult Run(string name, IReadOnlyList<Fold> folds, bool evaluateTrain = false)
    {
        ArgumentNullException.ThrowIfNull(folds);
        return new CrossValidationResult(name, folds.Select(f => RunFold(f, evaluateTrain)).ToArray());
    }

    public FoldResult RunFold(Fold fold, bool evaluateTrain = false)
    {
        ArgumentNullException.ThrowIfNull(fold);
        var trainKeys = fold.Train.Select(s => s.GameKey).ToHashSet(StringComparer.Ordinal);
        if (fold.Test.Any(s => trainKeys.Contains(s.GameKey)))
        {
            throw new ArgumentException($"Fold '{fold.Name}' shares a game between train and test.", nameof(fold));
        }

        var train = DecisionsOf(fold.Train);
        var test = DecisionsOf(fold.Test);
        var shifts = _calibrator.Fit(train);
        var calibrated = shifts.Count == 0 ? _base : (IRecommender)new EngineRecommender(_calibrator.Apply(shifts));
        var trainSkipRate = train.Count == 0 ? 0 : train.Count(d => d.Skipped) / (double)train.Count;

        return new FoldResult
        {
            Fold = fold,
            Shifts = shifts,
            BaseTest = AgreementMetrics.Evaluate(test, _base),
            CalibratedTest = AgreementMetrics.Evaluate(test, calibrated),
            TierOnlyTest = AgreementMetrics.Evaluate(test, _tierOnly),
            RandomTest = AgreementMetrics.EvaluateRandom(test, trainSkipRate),
            BaseTrain = evaluateTrain ? AgreementMetrics.Evaluate(train, _base) : null,
            CalibratedTrain = evaluateTrain ? AgreementMetrics.Evaluate(train, calibrated) : null,
        };
    }
}
