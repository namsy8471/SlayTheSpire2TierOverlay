using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Replay;

/// <summary>
/// "What would the overlay have said?" — replays every recorded card decision of a run through the
/// <see cref="RecommendationEngine"/> using the reconstructed deck at that moment (PLAN §7.7, DECK_COMPASS §5.6).
/// Live advice and post-run analysis use the exact same engine and skip formula.
/// </summary>
public sealed class ReplayAnalyzer(DataPack pack)
{
    private readonly RecommendationEngine _engine = new(pack);

    public DataPack Pack { get; } = pack;

    /// <summary>Replays the run for <paramref name="playerId"/> (default: <see cref="LocalPlayer.Resolve"/>).</summary>
    public RunReplay Replay(RunHistory run, long? playerId = null)
    {
        var player = playerId is { } id
            ? run.FindPlayer(id) ?? throw new ArgumentException($"Player {id} not in run.", nameof(playerId))
            : LocalPlayer.Resolve(run);

        var timeline = DeckTimeline.Build(run, player.Id);
        var decisions = DecisionExtractor.Extract(run, player.Id);
        var replays = decisions.Select(ReplayOne).ToArray();

        return new RunReplay
        {
            Run = run,
            Player = player,
            Decisions = replays,
            TimelineWarnings = timeline.Warnings,
        };
    }

    public DecisionReplay ReplayOne(CardDecision decision)
    {
        if (decision.Context == OfferContext.Shop)
        {
            var shop = _engine.EvaluateShop(
                decision.StateBefore,
                new ShopOffer(decision.Offered.Select(c => new ShopCardOffer(c, 0)).ToArray()));
            return new DecisionReplay { Decision = decision, Shop = shop, Verdict = ReplayVerdict.Informational };
        }

        var offer = _engine.EvaluateOffer(decision.StateBefore, decision.Offered, decision.Context);

        if (decision.Picked.Count > 1)
        {
            return new DecisionReplay { Decision = decision, Offer = offer, Verdict = ReplayVerdict.Informational, Recommended = offer.Best };
        }

        var bestValue = Math.Max(offer.Skip.Total, offer.Cards.Select(c => c.Total).DefaultIfEmpty(double.MinValue).Max());

        if (decision.Picked.Count == 0)
        {
            return new DecisionReplay
            {
                Decision = decision,
                Offer = offer,
                Recommended = offer.Best,
                Verdict = offer.SkipRecommended ? ReplayVerdict.AgreedSkip : ReplayVerdict.SkippedButCardRecommended,
                Regret = Math.Max(0, bestValue - offer.Skip.Total),
            };
        }

        var picked = decision.Picked[0];
        var pickedEval = FindEvaluation(offer, picked);
        var verdict = offer.SkipRecommended
            ? ReplayVerdict.PickedButSkipRecommended
            : offer.Best is { } best && SameCard(best, picked) ? ReplayVerdict.Agreed : ReplayVerdict.PickedOther;

        return new DecisionReplay
        {
            Decision = decision,
            Offer = offer,
            Recommended = offer.Best,
            Verdict = verdict,
            PickedRank = pickedEval?.Rank,
            Regret = pickedEval is null ? 0 : Math.Max(0, bestValue - pickedEval.Total),
        };
    }

    private static CardEvaluation? FindEvaluation(OfferEvaluation offer, CardRef picked)
        => offer.Cards.FirstOrDefault(c => c.Card == picked)
           ?? offer.Cards.FirstOrDefault(c => c.Card.Id == picked.Id);

    private static bool SameCard(CardRef a, CardRef b) => a.Id == b.Id && a.Upgrade == b.Upgrade;
}
