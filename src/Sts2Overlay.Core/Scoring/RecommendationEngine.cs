using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.Core.Scoring;

/// <summary>Entry point used by the App: one call per screen.</summary>
public sealed class RecommendationEngine(DataPack pack)
{
    public DataPack Pack { get; } = pack;

    public DeckProfile Analyze(RunState state) => throw new NotImplementedException();

    /// <summary>Card reward (or event card choice): ranks cards, adds Skip as a candidate, sets confidence.</summary>
    public OfferEvaluation EvaluateOffer(RunState state, IReadOnlyList<CardRef> cards, OfferContext context = OfferContext.CardReward)
        => throw new NotImplementedException();

    public ShopEvaluation EvaluateShop(RunState state, ShopOffer offer) => throw new NotImplementedException();

    /// <summary>Build collection tab: every archetype of the character with commit, completion and probability.</summary>
    public ArchetypeReport Archetypes(RunState state, int remainingRewards) => throw new NotImplementedException();
}
