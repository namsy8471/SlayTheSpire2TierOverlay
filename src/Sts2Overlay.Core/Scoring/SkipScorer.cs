using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>
/// PLAN §7.2: skip = T0[act] + λ_size × max(0, deck − ideal) + λ_commit × [max core-gated commit ≥ threshold]
/// (<see cref="CardScorer.GatedCommit"/>).
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
            new(ComponentKeys.SkipBase, ScoringLabels.SkipBase, t0, $"{state.Act}막 기준"),
        };

        var size = w.Skip.LambdaSize * over;
        if (size != 0)
        {
            parts.Add(new ScoreComponent(ComponentKeys.SkipDeckSize, ScoringLabels.SkipDeckSize, size, $"덱 {profile.DeckSize}장 / 이상 {ideal}장"));
        }

        // 코어 먼저: the build-completion term uses the core-gated commit (CardScorer.GatedCommit), so a build without its core
        // (commit made of synergy cards only) does not make skipping look better.
        var top = profile.Archetypes.MaxBy(s => CardScorer.GatedCommit(s, profile, Pack, state.Act));
        var maxCommit = top is null ? 0.0 : CardScorer.GatedCommit(top, profile, Pack, state.Act);
        if (top is not null && maxCommit >= w.CommitThreshold && w.Skip.LambdaCommit != 0)
        {
            parts.Add(new ScoreComponent(ComponentKeys.SkipCommit, ScoringLabels.SkipCommit, w.Skip.LambdaCommit,
                $"{top.Archetype.DisplayName} {maxCommit * 100:0}% (코어 반영)"));
        }

        return new ScoreBreakdown(parts);
    }
}
