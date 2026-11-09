using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.Core.Simulation;

/// <summary>Follows the overlay: the card the <see cref="RecommendationEngine"/> marks best (or skip), and the shop bundle it recommends.</summary>
public sealed class EnginePolicy(DataPack pack) : ISimPolicy
{
    private readonly RecommendationEngine _engine = new(pack);

    public string Name => "engine";

    public CardRef? PickReward(RunState state, IReadOnlyList<CardRef> offer, OfferContext context)
        => _engine.EvaluateOffer(state, offer, context).Best;

    public ShopChoice Shop(RunState state, ShopOffer offer)
    {
        var eval = _engine.EvaluateShop(state, offer);
        return new ShopChoice(
            eval.RecommendedPurchase.Where(i => i.Kind == ShopItemKind.Card && i.Card is not null).Select(i => i.Card!.Value).ToArray(),
            eval.RecommendedPurchase.Any(i => i.Kind == ShopItemKind.CardRemoval));
    }
}

/// <summary>
/// Baseline that ignores the deck: the highest base tier (act tier, upgrade delta) wins, skip when it is below the engine's skip
/// score. Shops: the best-tier card above skip if affordable, and a removal when gold allows. Shows what "standalone strength only"
/// would build.
/// </summary>
public sealed class TierOnlyPolicy(DataPack pack) : ISimPolicy
{
    public string Name => "tier-only";

    public CardRef? PickReward(RunState state, IReadOnlyList<CardRef> offer, OfferContext context)
    {
        var skip = new SkipScorer(pack).Score(state, DeckAnalyzer.AnalyzeDeck(state, pack)).Total;
        var best = offer.Select(c => (Card: c, Value: Value(state, c))).OrderByDescending(x => x.Value).FirstOrDefault();
        return best.Card.Id is not null && best.Value >= skip ? best.Card : null;
    }

    public ShopChoice Shop(RunState state, ShopOffer offer)
    {
        var skip = new SkipScorer(pack).Score(state, DeckAnalyzer.AnalyzeDeck(state, pack)).Total;
        var best = offer.Cards.Where(c => c.Price <= state.Gold).OrderByDescending(c => Value(state, c.Card)).FirstOrDefault();
        var cards = best is not null && Value(state, best.Card) >= skip ? new[] { best.Card } : [];
        var left = state.Gold - (best is not null && cards.Length > 0 ? best.Price : 0);
        return new ShopChoice(cards, left >= ShopScorer.RemovalPrice(state, pack.Weights));
    }

    private double Value(RunState state, CardRef card)
    {
        var w = pack.Weights;
        var entry = pack.FindTier(state.Character, pack.Canonical(card.Id));
        var tier = entry is null ? w.UnknownTier
            : entry.ActTier is not null && entry.ActTier.TryGetValue(state.Act, out var t) ? t : entry.Tier;
        var value = w.TierValue.GetValueOrDefault(tier);
        if (entry is not null && card.Upgrade > 0) value += entry.UpgradedDelta;
        return value;
    }
}
