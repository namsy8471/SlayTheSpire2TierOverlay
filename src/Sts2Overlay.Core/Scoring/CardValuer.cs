using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>
/// The one way to value cards against skipping, shared by the reward screen
/// (<see cref="RecommendationEngine.EvaluateOffer(RunState, DeckProfile, IReadOnlyList{CardRef}, OfferContext)"/>), the shop
/// (<see cref="Shop.ShopScorer"/>) and an event's "gain this card" effect (<see cref="Events.EventScorer"/>), so the same card gets
/// the same value on every screen. The engine scores each card (<see cref="CardScorer"/>) and Skip (<see cref="SkipScorer"/>);
/// with <paramref name="useLearnedModel"/> and a model in the pack, <see cref="LearnedRescore.Apply"/> turns both into the
/// learned model's utilities (in tier-base points, <see cref="CardPolicyModel.PointUtility"/>).
/// </summary>
/// <remarks>
/// A card's value does not depend on which other cards are offered with it: <see cref="CardScorer.Score"/> sees one card, and
/// <see cref="CardPolicyModel.Parts"/> computes each option from that option, the act, the deck and the skip components only
/// (the softmax over a screen is applied in training, not here). So valuing one card on its own, as the shop and events do,
/// gives exactly the total it would have on a reward screen with the same deck. The offer context only reaches the engine
/// (the boss-reward rule in <see cref="CardScorer"/>); the model has no context input.
/// </remarks>
public sealed class CardValuer(DataPack pack, bool useLearnedModel)
{
    public DataPack Pack { get; } = pack;

    /// <summary>False: the hand-tuned engine alone (<c>cardModel: engine</c>).</summary>
    public bool UseLearnedModel { get; } = useLearnedModel;

    /// <summary>True when values come from the learned model (switch on and a model in the pack).</summary>
    public bool LearnedModelActive => UseLearnedModel && Pack.CardPolicy is not null;

    /// <summary>
    /// Per-card evaluations in offer order and the Skip score. No cards: Skip is the engine's (there is nothing for the model
    /// to compare it with).
    /// </summary>
    public (List<CardEvaluation> Cards, ScoreBreakdown Skip) Evaluate(RunState state, DeckProfile profile, IReadOnlyList<CardRef> cards, OfferContext context)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var scorer = new CardScorer(Pack);
        var evals = cards.Select(c => scorer.Score(state, profile, c, context)).ToList();
        var skip = new SkipScorer(Pack).Score(state, profile);
        if (UseLearnedModel && Pack.CardPolicy is { } policy && evals.Count > 0)
        {
            (evals, skip) = LearnedRescore.Apply(policy, Pack, state, profile, evals, skip);
        }

        return (evals, skip);
    }
}
