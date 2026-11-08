using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Replay.Validation;

/// <summary>
/// A reward/event decision that can be compared 1:1 with a recommendation: zero or one card picked, not a shop.
/// <see cref="PickedIndex"/> is the position of the picked card in <see cref="CardDecision.Offered"/>, null when skipped.
/// </summary>
public sealed record LabeledDecision(ReplayStream Stream, CardDecision Decision, int? PickedIndex)
{
    public bool Skipped => PickedIndex is null;

    public int OfferedCount => Decision.Offered.Count;

    public int Act => Decision.StateBefore.Act;

    /// <summary>All comparable decisions of a stream (same filter as <see cref="ReplayAnalyzer"/>'s non-informational verdicts).</summary>
    public static IReadOnlyList<LabeledDecision> Extract(ReplayStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var result = new List<LabeledDecision>();
        foreach (var d in DecisionExtractor.Extract(stream.Run, stream.Player.Id))
        {
            if (d.Context == OfferContext.Shop || d.Picked.Count > 1 || d.Offered.Count == 0) continue;
            if (d.Picked.Count == 0)
            {
                result.Add(new LabeledDecision(stream, d, null));
                continue;
            }

            var index = IndexOf(d.Offered, d.Picked[0]);
            if (index >= 0) result.Add(new LabeledDecision(stream, d, index));
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<CardRef> offered, CardRef picked)
    {
        for (var i = 0; i < offered.Count; i++)
        {
            if (offered[i] == picked) return i;
        }

        for (var i = 0; i < offered.Count; i++)
        {
            if (offered[i].Id == picked.Id) return i;
        }

        return -1;
    }
}

/// <summary>
/// What a recommender says about one decision: a score per offered card (offer order) and the recommended card's
/// offer index (null = Skip). The recommended card is usually rank 1, but the engine never recommends a low-confidence
/// card, so it can be lower. Ranks are by score descending; ties count as random (see <see cref="Standing"/>).
/// </summary>
public sealed record Prediction(IReadOnlyList<double> Scores, int? Recommended)
{
    public bool SkipRecommended => Recommended is null;

    /// <summary>
    /// Where the card at <paramref name="index"/> stands: how many cards score strictly higher, and how many share its
    /// score (itself included). Evaluation must treat ties as a coin flip, never by offer order: in <c>.run</c> files the
    /// picked card is always listed first, so "ties by offer order" would hand every tie to the player's pick.
    /// </summary>
    public (int Above, int Tied) Standing(int index)
    {
        var mine = Scores[index];
        int above = 0, tied = 0;
        foreach (var s in Scores)
        {
            if (s > mine) above++;
            else if (s == mine) tied++;
        }

        return (above, tied);
    }

    /// <summary>Expected 1-based rank of the card when ties are broken at random.</summary>
    public double ExpectedRank(int index)
    {
        var (above, tied) = Standing(index);
        return above + (tied + 1) / 2.0;
    }

    /// <summary>Probability that the card lands in the top <paramref name="k"/> when ties are broken at random.</summary>
    public double TopK(int index, int k)
    {
        var (above, tied) = Standing(index);
        var slots = k - above;
        return slots <= 0 ? 0 : Math.Min(slots, tied) / (double)tied;
    }

    /// <summary>Offer index of the rank-1 card.</summary>
    public int TopIndex()
    {
        var best = 0;
        for (var i = 1; i < Scores.Count; i++)
        {
            if (Scores[i] > Scores[best]) best = i;
        }

        return best;
    }
}

/// <summary>Something that turns a recorded decision into a <see cref="Prediction"/>.</summary>
public interface IRecommender
{
    string Name { get; }

    Prediction Predict(LabeledDecision decision);
}

/// <summary>The real engine (deck-aware): the same scores the overlay shows.</summary>
public sealed class EngineRecommender(DataPack pack, string name = "엔진") : IRecommender
{
    private readonly RecommendationEngine _engine = new(pack);

    public string Name { get; } = name;

    public DataPack Pack { get; } = pack;

    public Prediction Predict(LabeledDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var d = decision.Decision;
        var offer = _engine.EvaluateOffer(d.StateBefore, d.Offered, d.Context);
        int? best = null;
        for (var i = 0; i < offer.Cards.Count; i++)
        {
            if (offer.Cards[i].IsBest) best = i;
        }

        return new Prediction(offer.Cards.Select(c => c.Total).ToArray(), best);
    }
}

/// <summary>
/// Baseline that only reads the badge tier: base value of the (act) tier plus the upgrade bonus, no deck context.
/// Recommends Skip when the best base value is below the act's skip base <c>skip.t0</c>.
/// </summary>
public sealed class TierOnlyRecommender(DataPack pack) : IRecommender
{
    public string Name => "티어만";

    public Prediction Predict(LabeledDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var d = decision.Decision;
        var w = pack.Weights;
        var act = d.StateBefore.Act;
        var scores = d.Offered.Select(c => BaseValue(pack, d.StateBefore.Character, act, c)).ToArray();
        var t0 = Weights.ForAct(w.Skip.T0, act, 0.0);
        var prediction = new Prediction(scores, null);
        return scores.Length == 0 || scores.Max() < t0 + w.Skip.Margin ? prediction : prediction with { Recommended = prediction.TopIndex() };
    }

    /// <summary>The engine's "base" component on its own.</summary>
    public static double BaseValue(DataPack pack, string character, int act, CardRef card)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var w = pack.Weights;
        var entry = pack.FindTier(character, card.Id);
        if (entry is null) return w.TierValue.GetValueOrDefault(w.UnknownTier);
        var tier = entry.ActTier is not null && entry.ActTier.TryGetValue(act, out var t) ? t : entry.Tier;
        var value = w.TierValue.GetValueOrDefault(tier);
        if (card.Upgrade > 0) value += entry.UpgradedDelta;
        return value;
    }
}
