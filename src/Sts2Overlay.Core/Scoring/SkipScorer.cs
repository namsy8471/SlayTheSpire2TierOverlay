using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>PLAN §7.2: skip = T0[act] + λ_size × max(0, deck − ideal) + λ_commit × [max commit ≥ threshold].</summary>
public sealed class SkipScorer(DataPack pack)
{
    public DataPack Pack { get; } = pack;

    public ScoreBreakdown Score(RunState state, DeckProfile profile)
        => throw new NotImplementedException();
}
