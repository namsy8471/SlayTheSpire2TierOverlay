using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>
/// PLAN §7.2: skip = T0[act] + λ_size × max(0, deck − ideal) + λ_commit × [max core-gated commit ≥ threshold]
/// (<see cref="CardScorer.GatedCommit"/>) − basicPickBias[act] × min(1, basicShare / basicShareFull)
/// (a card replaces a starter Strike/Defend draw, so while the deck is mostly starter cards almost any card beats skipping).
/// One formula for the live overlay, the shop ("스킵 대비") and the post-run review.
/// </summary>
public sealed class SkipScorer(DataPack pack)
{
    public DataPack Pack { get; } = pack;

    public ScoreBreakdown Score(RunState state, DeckProfile profile)
    {
        var w = Pack.Weights;
        var t0 = Weights.ForAct(w.Skip.T0, state.Act, 0.0);
        var ideal = Weights.ForAct(w.IdealDeckSize, state.Act, int.MaxValue);
        var over = Math.Max(0.0, (double)profile.DeckSize - ideal);
        var parts = new List<ScoreComponent>
        {
            new(ComponentKeys.SkipBase, ScoringLabels.SkipBase, t0, Loc.T($"{state.Act}막 기준", $"Act {state.Act} baseline")),
        };

        var size = w.Skip.LambdaSize * over;
        if (size != 0)
        {
            parts.Add(new ScoreComponent(ComponentKeys.SkipDeckSize, ScoringLabels.SkipDeckSize, size, Loc.T($"덱 {profile.DeckSize}장 / 이상 {ideal}장", $"Deck {profile.DeckSize} / ideal {ideal}")));
        }

        // 코어 먼저: the build-completion term uses the core-gated commit (CardScorer.GatedCommit), so a build without its core
        // (commit made of synergy cards only) does not make skipping look better.
        var top = profile.Archetypes.MaxBy(s => CardScorer.GatedCommit(s, profile, Pack, state.Act));
        var maxCommit = top is null ? 0.0 : CardScorer.GatedCommit(top, profile, Pack, state.Act);
        if (top is not null && maxCommit >= w.CommitThreshold && w.Skip.LambdaCommit != 0)
        {
            parts.Add(new ScoreComponent(ComponentKeys.SkipCommit, ScoringLabels.SkipCommit, w.Skip.LambdaCommit,
                Loc.T($"{top.Archetype.DisplayName} {maxCommit * 100:0}% (코어 반영)", $"{top.Archetype.DisplayName} {maxCommit * 100:0}% (core-adjusted)")));
        }

        // 초반엔 어떤 카드든 타격·수비보다 낫다: lower skip by the act's bias while the deck is starter cards.
        var bias = Math.Max(0.0, Finite(Weights.ForAct(w.Skip.BasicPickBias, state.Act, 0.0)));
        var full = w.Skip.BasicShareFull > 0 && double.IsFinite(w.Skip.BasicShareFull) ? w.Skip.BasicShareFull : 1.0;
        var basics = -bias * Math.Min(1.0, profile.BasicShare / full);
        if (basics < 0)
        {
            parts.Add(new ScoreComponent(ComponentKeys.SkipBasics, ScoringLabels.SkipBasics, basics,
                Loc.T($"기본 카드 {profile.BasicCards}/{profile.DeckSize}장: 어떤 카드든 타격·수비보다 나음",
                    $"Basics {profile.BasicCards}/{profile.DeckSize}: any card beats Strike/Defend")));
        }

        return new ScoreBreakdown(parts);
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0.0;
}
