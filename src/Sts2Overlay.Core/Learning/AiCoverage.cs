using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Learning;

/// <summary>
/// Which runs the simulator's learned run layers (<see cref="RunBaselinePolicy"/>) apply to. The simulator has no
/// two-player mode yet, so a co-op run gets the layers learned on solo runs of the same character and ascension, and its
/// screens say so ("1인 기준", <see cref="SoloBasisNote"/>) until a co-op version is measured.
/// </summary>
public static class AiCoverage
{
    /// <summary>Standard runs at A0 to <see cref="RunBaselinePolicy.MaxAscension"/>, solo and co-op alike.</summary>
    public static bool Covers(RunState state) =>
        state.IsStandardRun && state.Ascension is >= 0 and <= RunBaselinePolicy.MaxAscension;

    /// <summary>The standing line on a co-op run's recommendations.</summary>
    public static string SoloBasisNote =>
        Loc.T("1인 기준 AI 추천입니다 (협동은 아직 학습 전)", "Solo-trained AI advice (co-op not learned yet)");
}
