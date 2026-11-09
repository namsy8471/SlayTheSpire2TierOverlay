using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Learning;

/// <summary>
/// Re-scores a reward screen with the learned model while keeping the engine's explanations. Every engine component is
/// multiplied by the weight top players actually give it (relative to the tier base, so the numbers stay in engine
/// points), the fitted <c>baseAdjust</c> comes out of <c>base</c>, and the model's own parts (card value, deck fit,
/// upgraded, copies, skip tendency) are added as components. The new total is the model's utility in base points, so
/// ranking by total is ranking by the model, and <see cref="ScoreBreakdown.Total"/> still equals the sum of its parts.
/// </summary>
public static class LearnedRescore
{
    public static (List<CardEvaluation> Cards, ScoreBreakdown Skip) Apply(
        CardPolicyModel model, DataPack pack, RunState state, DeckProfile profile, IReadOnlyList<CardEvaluation> cards, ScoreBreakdown skip)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(skip);

        var options = new List<PolicyOption>(cards.Count);
        var adjust = new double[cards.Count];
        for (var i = 0; i < cards.Count; i++)
        {
            var e = cards[i];
            var id = pack.Canonical(e.Card.Id);
            adjust[i] = pack.FindTier(state.Character, id)?.BaseAdjust ?? 0.0;
            options.Add(new PolicyOption(id, e.Card.Upgrade, profile.Copies.GetValueOrDefault(id), Sum(e.Score), adjust[i]));
        }

        var deck = state.Deck.Select(c => pack.Canonical(c.Card.Id)).ToArray();
        var parts = model.Parts(state.Act, deck, options, Sum(skip));
        var unit = model.PointUtility;

        var rescored = new List<CardEvaluation>(cards.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            var e = cards[i];
            var comps = new List<ScoreComponent>();
            var adjustLeft = adjust[i];
            foreach (var c in e.Score.Components)
            {
                var value = c.Value;
                if (c.Key == ComponentKeys.Base && adjustLeft != 0)
                {
                    value -= adjustLeft;
                    adjustLeft = 0;
                }

                comps.Add(c with { Value = value * model.RelativeWeight(c.Key) });
            }

            AddLearned(comps, parts[i + 1], unit);
            var score = new ScoreBreakdown(comps);
            var known = model.Knows(options[i].Card);
            rescored.Add(e with
            {
                Score = score,
                Chips = score.TopReasons(3).Select(c => new ReasonChip(c.Key, c.Label, c.Value)).ToArray(),
                Confidence = known ? Math.Max(e.Confidence, 1.0) : e.Confidence,
                IsEstimate = !known && e.IsEstimate,
            });
        }

        var skipComps = skip.Components.Select(c => c with { Value = c.Value * model.RelativeWeight(c.Key) }).ToList();
        AddLearned(skipComps, parts[0], unit);
        return (rescored, new ScoreBreakdown(skipComps));
    }

    private static void AddLearned(List<ScoreComponent> comps, IReadOnlyList<(string Key, double Utility)> parts, double unit)
    {
        foreach (var (key, utility) in parts)
        {
            if (Label(key) is not { } label || Math.Abs(utility) < 1e-9)
            {
                continue; // engine components are already in the list
            }

            comps.Add(new ScoreComponent(key, label, utility / unit));
        }
    }

    private static string? Label(string key) => key switch
    {
        PolicyKeys.CardValue => Loc.T("상위 랭커 선호", "Top players' preference"),
        PolicyKeys.DeckFit => Loc.T("덱 궁합", "Deck fit"),
        PolicyKeys.Upgraded => Loc.T("강화됨", "Upgraded"),
        PolicyKeys.Copies => Loc.T("이미 보유", "Already owned"),
        PolicyKeys.SkipAct => Loc.T("막별 스킵 경향", "Skip tendency this act"),
        _ => null,
    };

    private static Dictionary<string, double> Sum(ScoreBreakdown score)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var c in score.Components)
        {
            d[c.Key] = d.GetValueOrDefault(c.Key) + c.Value;
        }

        return d;
    }
}
