namespace Sts2Overlay.Core.Replay.Validation;

/// <summary>
/// How often a recommender agrees with the recorded choices. Agreement is a <b>proxy</b>: the player's pick is
/// not the best play, so 100% is not the goal; the numbers are only useful to compare recommenders on the same decisions.
/// Built by summing (micro average), so folds can be added together with <see cref="Add"/>.
/// </summary>
public sealed record AgreementMetrics
{
    /// <summary>Comparable decisions (picked one card or skipped).</summary>
    public int Decisions { get; init; }

    /// <summary>Decisions where a card was picked.</summary>
    public int Picks { get; init; }

    /// <summary>Picks where the picked card was the recommender's rank 1 among the offered cards (Skip ignored). Ties count fractionally.</summary>
    public double Top1Hits { get; init; }

    /// <summary>Picks where the picked card was rank 1 or 2.</summary>
    public double Top2Hits { get; init; }

    /// <summary>Sum of the picked card's rank over all picks.</summary>
    public double RankSum { get; init; }

    /// <summary>Decisions where "recommend Skip" equals "player skipped".</summary>
    public double SkipHits { get; init; }

    /// <summary>Decisions where the full recommendation (that card, or Skip) equals what the player did.</summary>
    public double ExactHits { get; init; }

    /// <summary>Decisions where the player skipped.</summary>
    public int Skips { get; init; }

    public double Top1 => Picks == 0 ? 0 : Top1Hits / Picks;
    public double Top2 => Picks == 0 ? 0 : Top2Hits / Picks;
    public double MeanRank => Picks == 0 ? 0 : RankSum / Picks;
    public double SkipAgreement => Decisions == 0 ? 0 : SkipHits / Decisions;
    public double Exact => Decisions == 0 ? 0 : ExactHits / Decisions;
    public double SkipRate => Decisions == 0 ? 0 : (double)Skips / Decisions;

    public static AgreementMetrics Empty { get; } = new();

    public AgreementMetrics Add(AgreementMetrics other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new AgreementMetrics
        {
            Decisions = Decisions + other.Decisions,
            Picks = Picks + other.Picks,
            Top1Hits = Top1Hits + other.Top1Hits,
            Top2Hits = Top2Hits + other.Top2Hits,
            RankSum = RankSum + other.RankSum,
            SkipHits = SkipHits + other.SkipHits,
            ExactHits = ExactHits + other.ExactHits,
            Skips = Skips + other.Skips,
        };
    }

    /// <summary>Scores one decision against one prediction.</summary>
    public static AgreementMetrics Of(LabeledDecision decision, Prediction prediction)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(prediction);
        if (prediction.Scores.Count != decision.OfferedCount)
        {
            throw new ArgumentException("Prediction does not match the offer size.", nameof(prediction));
        }

        if (decision.PickedIndex is not int picked)
        {
            var skipHit = prediction.SkipRecommended ? 1 : 0;
            return new AgreementMetrics { Decisions = 1, Skips = 1, SkipHits = skipHit, ExactHits = skipHit };
        }

        // A recommendation tied with the pick gets a fair share (1/tied), not full credit.
        var exact = prediction.Recommended is int r && prediction.Scores[r] == prediction.Scores[picked]
            ? 1.0 / prediction.Standing(picked).Tied
            : 0.0;
        return new AgreementMetrics
        {
            Decisions = 1,
            Picks = 1,
            Top1Hits = prediction.TopK(picked, 1),
            Top2Hits = prediction.TopK(picked, 2),
            RankSum = prediction.ExpectedRank(picked),
            SkipHits = prediction.SkipRecommended ? 0 : 1,
            ExactHits = exact,
        };
    }

    /// <summary>
    /// Expected values of a uniform random recommender that ranks the offered cards in random order and recommends
    /// Skip with probability <paramref name="skipProbability"/> (use the training folds' skip rate, never the test's).
    /// </summary>
    public static AgreementMetrics UniformRandom(LabeledDecision decision, double skipProbability)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var p = Math.Clamp(skipProbability, 0, 1);
        var n = decision.OfferedCount;
        if (decision.Skipped)
        {
            return new AgreementMetrics { Decisions = 1, Skips = 1, SkipHits = p, ExactHits = p };
        }

        return new AgreementMetrics
        {
            Decisions = 1,
            Picks = 1,
            Top1Hits = 1.0 / n,
            Top2Hits = Math.Min(2, n) / (double)n,
            RankSum = (n + 1) / 2.0,
            SkipHits = 1 - p,
            ExactHits = (1 - p) / n,
        };
    }

    /// <summary>Sums <see cref="Of"/> over many decisions.</summary>
    public static AgreementMetrics Evaluate(IEnumerable<LabeledDecision> decisions, IRecommender recommender)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(recommender);
        return decisions.Aggregate(Empty, (acc, d) => acc.Add(Of(d, recommender.Predict(d))));
    }

    /// <summary>Sums <see cref="UniformRandom"/> over many decisions.</summary>
    public static AgreementMetrics EvaluateRandom(IEnumerable<LabeledDecision> decisions, double skipProbability)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        return decisions.Aggregate(Empty, (acc, d) => acc.Add(UniformRandom(d, skipProbability)));
    }
}
