using System.Globalization;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.Core.Scoring;

/// <summary>Entry point used by the App: one call per screen.</summary>
public sealed class RecommendationEngine(DataPack pack)
{
    public DataPack Pack { get; } = pack;

    public DeckProfile Analyze(RunState state) => DeckAnalyzer.Analyze(state, Pack);

    /// <summary>Card reward (or event card choice): ranks cards, adds Skip as a candidate, sets confidence.</summary>
    public OfferEvaluation EvaluateOffer(RunState state, IReadOnlyList<CardRef> cards, OfferContext context = OfferContext.CardReward)
        => EvaluateOffer(state, Analyze(state), cards, context);

    /// <summary>Same as <see cref="EvaluateOffer(RunState, IReadOnlyList{CardRef}, OfferContext)"/> with a precomputed profile.</summary>
    public OfferEvaluation EvaluateOffer(RunState state, DeckProfile profile, IReadOnlyList<CardRef> cards, OfferContext context = OfferContext.CardReward)
    {
        var w = Pack.Weights;
        var scorer = new CardScorer(Pack);
        var evals = cards.Select(c => scorer.Score(state, profile, c, context)).ToList();
        var skip = new SkipScorer(Pack).Score(state, profile);

        // Rank: total desc, ties by offer order (OrderBy is stable).
        var order = Enumerable.Range(0, evals.Count).OrderByDescending(i => evals[i].Total).ThenBy(i => i).ToArray();
        var ranks = new int[evals.Count];
        for (var k = 0; k < order.Length; k++) ranks[order[k]] = k + 1;

        // Low-confidence cards cannot beat skip (PLAN §7.2).
        var eligible = order.Where(i => evals[i].Confidence >= w.Skip.LowConfidence).ToList();
        int? best = eligible.Count > 0 && evals[eligible[0]].Total >= skip.Total + w.Skip.Margin ? eligible[0] : null;
        // ...unless no confident card beats skip and a low-confidence one beats it by a full tier step (skip.lowConfidenceStep):
        // even rated one tier lower it would still win, so the uncertainty cannot make skipping right.
        var lowConfidencePick = false;
        if (best is null)
        {
            var step = Math.Max(0.0, w.Skip.LowConfidenceStep);
            var sure = order.Where(i => evals[i].Total >= skip.Total + w.Skip.Margin + step).Cast<int?>().FirstOrDefault();
            if (sure is int s)
            {
                best = s;
                lowConfidencePick = true;
            }
        }

        // Winner vs runner-up, Skip counting as a candidate.
        ScoreBreakdown winner;
        ScoreBreakdown? runnerUp;
        bool winnerIsSkip, runnerIsSkip;
        if (best is int b)
        {
            winner = evals[b].Score;
            winnerIsSkip = false;
            var nextCard = eligible.Where(i => i != b).Select(i => evals[i].Score).FirstOrDefault();
            if (nextCard is not null && nextCard.Total > skip.Total)
            {
                runnerUp = nextCard;
                runnerIsSkip = false;
            }
            else
            {
                runnerUp = skip;
                runnerIsSkip = true;
            }
        }
        else
        {
            winner = skip;
            winnerIsSkip = true;
            runnerUp = eligible.Count > 0 ? evals[eligible[0]].Score : null;
            runnerIsSkip = false;
        }

        RecommendationConfidence confidence;
        string? flipHint = null;
        if (best is int bi && (evals[bi].IsEstimate || lowConfidencePick))
        {
            confidence = RecommendationConfidence.Estimate;
        }
        else if (best is null && evals.Any(e => e.Confidence < w.Skip.LowConfidence && e.Total >= skip.Total + w.Skip.Margin))
        {
            // Skip only wins because a higher-scoring card's rating is too uncertain to trust.
            confidence = RecommendationConfidence.Estimate;
        }
        else if (runnerUp is null)
        {
            confidence = RecommendationConfidence.Sure;
        }
        else
        {
            var gap = winner.Total - runnerUp.Total;
            if (gap >= w.ConfidenceSureGap) confidence = RecommendationConfidence.Sure;
            else if (gap < w.ConfidenceCloseGap)
            {
                confidence = RecommendationConfidence.Close;
                flipHint = FlipHint(winner, winnerIsSkip, runnerUp, runnerIsSkip);
            }
            else confidence = RecommendationConfidence.Normal;
        }

        var results = evals.Select((e, i) => e with { Rank = ranks[i], IsBest = best == i }).ToArray();
        return new OfferEvaluation
        {
            Cards = results,
            Skip = skip,
            SkipRecommended = best is null,
            Best = best is int bb ? evals[bb].Card : null,
            Confidence = confidence,
            FlipHint = flipHint,
            Profile = profile,
        };
    }

    public ShopEvaluation EvaluateShop(RunState state, ShopOffer offer)
        => new ShopScorer(Pack).Evaluate(state, Analyze(state), offer);

    /// <summary>Build collection tab: every archetype of the character with commit, completion and probability.</summary>
    public ArchetypeReport Archetypes(RunState state, int remainingRewards)
    {
        var profile = DeckAnalyzer.AnalyzeDeck(state, Pack);
        return ArchetypeDetector.Report(state, Pack, remainingRewards, profile.RoleSupply, profile.Provides);
    }

    /// <summary>
    /// One line (current overlay language) naming the component that separates the top two candidates most, phrased as
    /// "what would have to change for the order to flip".
    /// </summary>
    internal static string FlipHint(ScoreBreakdown first, bool firstIsSkip, ScoreBreakdown second, bool secondIsSkip)
    {
        if (firstIsSkip || secondIsSkip)
        {
            var card = firstIsSkip ? second : first;
            var reason = card.TopReasons(1).FirstOrDefault();
            return reason is null
                ? Loc.T("스킵과 점수가 거의 같습니다. 덱 크기가 조금만 달라도 순위가 바뀝니다.",
                    "Nearly tied with Skip. A slightly different deck size would flip the order.")
                : Loc.T($"스킵과 점수가 거의 같습니다. '{reason.Label}' 요소가 조금만 달라도 순위가 바뀝니다.",
                    $"Nearly tied with Skip. A small change in '{reason.Label}' would flip the order.");
        }

        var keys = first.Components.Concat(second.Components).Select(c => c.Key).Distinct(StringComparer.Ordinal).ToList();
        var key = keys.OrderByDescending(k => Math.Abs(first.ValueOf(k) - second.ValueOf(k))).First();
        var diff = first.ValueOf(key) - second.ValueOf(key);
        if (Math.Abs(diff) < 0.05)
        {
            return Loc.T("두 카드의 점수 구성이 같습니다. 덱 상황이 조금만 달라져도 순위가 바뀝니다.",
                "Both cards score alike. A small change in the deck would flip the order.");
        }
        var leaderIsFirst = diff > 0;
        var leader = leaderIsFirst ? first : second;
        var component = leader.Components.Where(c => c.Key == key).OrderByDescending(c => Math.Abs(c.Value)).FirstOrDefault()
            ?? (leaderIsFirst ? second : first).Components.Where(c => c.Key == key).OrderByDescending(c => Math.Abs(c.Value)).First();

        switch (key)
        {
            // The need component carries its dominant role key (ScoreComponent.Role); never parse it back from the label text.
            case ComponentKeys.Need when component.Role is { } role:
                var roleName = ScoringLabels.Role(role);
                return leaderIsFirst
                    ? Loc.T($"덱에 {roleName} 카드가 더 있으면 순위가 바뀝니다.", $"More {roleName} cards in the deck would flip the order.")
                    : Loc.T($"덱의 {roleName} 공급이 조금만 더 부족하면 순위가 바뀝니다.", $"Slightly less {roleName} in the deck would flip the order.");
            case ComponentKeys.CorePriority or ComponentKeys.CoreMissing:
                return Loc.T("빌드의 코어 카드를 이미 갖고 있었다면 순위가 바뀝니다.", "Already owning the build's core card would flip the order.");
            case ComponentKeys.Build or ComponentKeys.Combo when diff > 0:
                return Loc.T("빌드 몰입도가 더 낮았다면 순위가 바뀝니다.", "A lower build commitment would flip the order.");
            case ComponentKeys.Build or ComponentKeys.Combo:
                return Loc.T("빌드 몰입도가 조금만 더 높아지면 순위가 바뀝니다.", "A slightly higher build commitment would flip the order.");
            case ComponentKeys.Base when diff > 0:
                return Loc.T("기본 티어 차이로 앞섭니다. 2위의 덱 시너지가 조금만 커지면 순위가 바뀝니다.",
                    "Ahead on base tier. Slightly more deck synergy for #2 would flip the order.");
            case ComponentKeys.Base:
                return Loc.T("2위가 기본 티어는 더 높습니다. 덱 상황이 조금만 달라져도 순위가 바뀝니다.",
                    "#2 has the higher base tier. A small change in the deck would flip the order.");
            default:
                var d = Math.Abs(diff).ToString("0.#", CultureInfo.InvariantCulture);
                return Loc.T($"'{component.Label}' 차이({d}점)로 순위가 갈렸습니다. 이 요소가 조금만 달라도 순위가 바뀝니다.",
                    $"'{component.Label}' ({d} pts) decided the order. A small change in it would flip the order.");
        }
    }
}
