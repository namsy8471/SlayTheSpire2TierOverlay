using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Shop;

/// <summary>
/// PLAN §7.3: cards ranked by (score − skip) / price, card removal as a candidate, best affordable bundle.
/// Unread prices are estimated from rarity (<see cref="ShopPricing"/>); gold is checked against the price colours.
/// </summary>
public sealed class ShopScorer(DataPack pack)
{
    /// <summary>Largest number of candidate items searched exhaustively for the recommended bundle.</summary>
    public const int MaxBundleCandidates = 12;

    public DataPack Pack { get; } = pack;

    public ShopEvaluation Evaluate(RunState state, DeckProfile profile, ShopOffer offer)
    {
        var gold = ShopPricing.EffectiveGold(state.Gold, offer.Cards, out var goldCorrected);
        var skip = new SkipScorer(Pack).Score(state, profile);
        var scorer = new CardScorer(Pack);
        var items = new List<ShopItemEvaluation>();

        foreach (var sale in offer.Cards)
        {
            var eval = scorer.Score(state, profile, sale.Card, OfferContext.Shop);
            var value = new ScoreBreakdown(eval.Score.Components.Append(
                new ScoreComponent(ComponentKeys.SkipRelative, ScoringLabels.SkipRelative, -skip.Total, Loc.T($"스킵 {skip.Total:0.#}점", $"Skip {skip.Total:0.#} pts"))));
            var price = sale.PriceKnown ? sale.Price : ShopPricing.Estimate(Pack, state.Character, sale.Card);
            items.Add(new ShopItemEvaluation
            {
                Kind = ShopItemKind.Card,
                Card = sale.Card,
                Price = price,
                Value = value,
                CardEvaluation = eval,
                // The label colour is the game's own verdict; trust it over our gold arithmetic.
                Affordable = sale.PriceKnown && sale.AffordableOnScreen is bool onScreen ? onScreen : price <= gold,
                PriceEstimated = !sale.PriceKnown,
                OnSale = sale.OnSale,
            });
        }

        var removal = RemovalItem(state, offer, gold);
        if (removal is not null) items.Add(removal);

        var sorted = items.OrderByDescending(i => i.ValuePerGold).ToArray();
        var bundle = BestBundle(sorted, gold);
        return new ShopEvaluation
        {
            Items = sorted,
            RecommendedPurchase = bundle,
            Gold = gold,
            GoldCorrectedByScreen = goldCorrected,
            GoldLeft = Math.Max(0, gold - bundle.Sum(i => i.Price)),
            Skip = skip,
        };
    }

    /// <summary>75 + 25 × removals so far (ascension ≥ 6: 100 + 50 ×). [코드 확인] SPIKE §5.</summary>
    public static int RemovalPrice(RunState state, Weights weights)
    {
        var s = weights.Shop;
        var high = state.Ascension >= s.HighAscension;
        var count = Math.Max(0, state.CardRemovalsBought);
        return high
            ? s.RemovalBaseHighAscension + s.RemovalStepHighAscension * count
            : s.RemovalBase + s.RemovalStep * count;
    }

    /// <summary>
    /// Value of a relic on sale: its rarity value (<see cref="Events.EventWeights.RelicTier"/>, the same scale event relics
    /// use), times <see cref="RelicStats.ValueMultiplier"/> when <c>relics.json</c> has a stable buy rate for it
    /// (rate ÷ the shop median, clamped to 0.5–1.5; purchases are gold-limited, so the rate is only read relative to the median).
    /// The shop screen reader does not read relics yet, so <see cref="Evaluate"/> has no relic item; this is the value it will use.
    /// </summary>
    /// <param name="relicId"><c>RELIC.X</c>.</param>
    /// <param name="rarity"><c>common</c>/<c>uncommon</c>/<c>rare</c>/<c>shop</c>; null = <see cref="Events.EventWeights.RelicDefault"/>.</param>
    public double RelicValue(string relicId, string? rarity)
        => Pack.Weights.Events.RelicValue(rarity, relicId, Pack.RelicStats);

    /// <summary>
    /// Estimated merchant price of a card when the screen price cannot be read (<see cref="ShopPrices.Estimated"/>).
    /// <paramref name="rarity"/> and <paramref name="colorless"/> default to the catalog / tier data (<see cref="DataPack.CardRarity"/>,
    /// <see cref="DataPack.IsColorless"/>). Null when the rarity is unknown or not sold.
    /// </summary>
    public int? EstimatedPrice(string character, string cardId, string? rarity = null, bool? colorless = null, bool onSale = false)
        => ShopPrices.Estimated(Pack.Weights.Shop, rarity ?? Pack.CardRarity(character, cardId), colorless ?? Pack.IsColorless(cardId), onSale);

    /// <summary>
    /// The card the removal service should take: a removable curse first (Eternal ones such as Ascender's Bane or Greed
    /// cannot be removed), else a basic Strike, else a basic Defend.
    /// Strikes go before Defends so that removal never deepens a block shortage. Null when nothing is worth removing.
    /// </summary>
    public string? RemovalTarget(RunState state, out bool isCurse)
    {
        var curse = state.Deck.Select(d => d.Id)
            .FirstOrDefault(id => DeckAnalyzer.IsCurse(Pack, state.Character, id) && !Pack.IsEternal(id));
        if (curse is not null)
        {
            isCurse = true;
            return curse;
        }
        isCurse = false;
        var strike = state.Deck.Select(d => d.Id).FirstOrDefault(CardIdHeuristics.IsBasicStrike);
        var defend = state.Deck.Select(d => d.Id).FirstOrDefault(CardIdHeuristics.IsBasicDefend);
        return strike ?? defend;
    }

    private ShopItemEvaluation? RemovalItem(RunState state, ShopOffer offer, int gold)
    {
        var target = RemovalTarget(state, out var isCurse);
        if (target is null) return null;
        var w = Pack.Weights.Shop;
        var price = offer.RemovalPrice ?? RemovalPrice(state, Pack.Weights);
        var value = isCurse ? w.CurseRemovalValue : w.BasicRemovalValue;
        var component = new ScoreComponent(ComponentKeys.RemovalValue,
            isCurse ? ScoringLabels.CurseRemoval : ScoringLabels.BasicRemoval, value, target);
        return new ShopItemEvaluation
        {
            Kind = ShopItemKind.CardRemoval,
            Price = price,
            Value = new ScoreBreakdown([component]),
            RemovalTarget = target,
            Affordable = price <= gold,
        };
    }

    /// <summary>Exhaustive search over positive-value affordable items: max Σ value within gold, ties → less gold spent.</summary>
    private static IReadOnlyList<ShopItemEvaluation> BestBundle(IReadOnlyList<ShopItemEvaluation> sortedItems, int gold)
    {
        var candidates = sortedItems
            .Where(i => i.Affordable && i.Value.Total > 0 && i.Price >= 0 && double.IsFinite(i.Value.Total))
            .Take(MaxBundleCandidates)
            .ToArray();
        var bestMask = 0;
        var bestValue = 0.0;
        var bestCost = 0;
        for (var mask = 1; mask < 1 << candidates.Length; mask++)
        {
            long cost = 0;
            var value = 0.0;
            for (var j = 0; j < candidates.Length; j++)
            {
                if ((mask & (1 << j)) == 0) continue;
                cost += candidates[j].Price;
                value += candidates[j].Value.Total;
            }
            if (cost > gold) continue;
            if (value > bestValue + 1e-9 || (Math.Abs(value - bestValue) <= 1e-9 && cost < bestCost))
            {
                bestMask = mask;
                bestValue = value;
                bestCost = (int)cost;
            }
        }
        return candidates.Where((_, j) => (bestMask & (1 << j)) != 0).ToArray();
    }
}
