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
/// An engine component whose learned weight is negative would change sign under its engine label ("자원 과잉 +19"), so its
/// value is shown inside the learned preference instead (<c>ranker_value</c>; Skip: <c>skip_act</c>); totals do not change.
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
            var flipped = new Flipped();
            var adjustLeft = adjust[i];
            foreach (var c in e.Score.Components)
            {
                var value = c.Value;
                if (c.Key == ComponentKeys.Base && adjustLeft != 0)
                {
                    value -= adjustLeft;
                    adjustLeft = 0;
                }

                Weigh(comps, flipped, c, value, model.RelativeWeight(c.Key));
            }

            AddLearned(comps, parts[i + 1], unit);
            flipped.FoldInto(comps, PolicyKeys.CardValue);
            var score = new ScoreBreakdown(comps);
            var known = model.Knows(options[i].Card);
            // The model having seen the card in the old game version does not make the pack's data reasons go away: a card
            // without a tier entry, or one the pack marks as changed by a patch, stays an estimate at the pack's confidence.
            var dataEstimate = !e.IsRated || pack.FindTier(state.Character, options[i].Card)?.ChangedIn is not null;
            // A co-op-only card is never in the solo training data: no learned value, so say so instead of inventing one.
            var coopOnly = !known && pack.FindCatalog(options[i].Card)?.MultiplayerOnly == true;
            rescored.Add(e with
            {
                Score = score,
                Chips = score.TopReasons(3).Select(c => new ReasonChip(c.Key, c.Label, c.Value)).ToArray(),
                Confidence = known && !dataEstimate ? Math.Max(e.Confidence, 1.0) : e.Confidence,
                IsEstimate = dataEstimate || coopOnly || (!known && e.IsEstimate),
                KnownToModel = known,
                CoopOnlyNoSoloData = coopOnly,
            });
        }

        var skipComps = new List<ScoreComponent>();
        var skipFlipped = new Flipped();
        foreach (var c in skip.Components) Weigh(skipComps, skipFlipped, c, c.Value, model.RelativeWeight(c.Key));
        AddLearned(skipComps, parts[0], unit);
        skipFlipped.FoldInto(skipComps, PolicyKeys.SkipAct);
        return (rescored, new ScoreBreakdown(skipComps));
    }

    /// <summary>
    /// Adds an engine component at its learned weight. When the weight is negative the value changes sign (the model learned
    /// that top players treat an engine penalty such as "자원 과잉" as a plus, or a bonus as a minus): showing it under the
    /// engine's label would read "penalty +19", so it is set aside in <paramref name="flipped"/> and later shown as part of
    /// the learned preference. The total stays the same: the value moves, it is not dropped.
    /// </summary>
    private static void Weigh(List<ScoreComponent> comps, Flipped flipped, ScoreComponent c, double value, double weight)
    {
        var weighted = value * weight;
        if (weight < 0 && Math.Abs(weighted) >= 1e-9)
        {
            flipped.Add(c.Label, weighted);
            return;
        }

        comps.Add(c with { Value = weighted });
    }

    /// <summary>Engine components whose sign the learned weight flipped: their summed value and their labels (for the detail line).</summary>
    private sealed class Flipped
    {
        private readonly List<string> _labels = [];
        private double _value;

        public void Add(string label, double value)
        {
            _value += value;
            if (!_labels.Contains(label, StringComparer.Ordinal)) _labels.Add(label);
        }

        /// <summary>Adds the set-aside value to the learned component <paramref name="key"/> (created if the model gave it nothing).</summary>
        public void FoldInto(List<ScoreComponent> comps, string key)
        {
            if (_labels.Count == 0) return;
            var detail = Loc.T($"랭커 기준으로 다시 잰 요소: {string.Join(", ", _labels)}", $"Re-weighed by top players' picks: {string.Join(", ", _labels)}");
            var i = comps.FindIndex(c => c.Key == key);
            if (i >= 0)
            {
                var c = comps[i];
                comps[i] = c with { Value = c.Value + _value, Detail = c.Detail is null ? detail : c.Detail + " · " + detail };
            }
            else
            {
                comps.Add(new ScoreComponent(key, Label(key)!, _value, detail));
            }
        }
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
