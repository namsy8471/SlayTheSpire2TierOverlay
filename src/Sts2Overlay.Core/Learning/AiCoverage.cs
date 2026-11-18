using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Learning;

/// <summary>
/// Which runs the simulator's whole-run AI was measured on. The owner's rule (2026-11): the overlay recommends only what the
/// AI recommends, so outside these runs (co-op, custom or daily runs, ascensions the AI has not been measured at) the
/// reward, shop, event, rest and path screens show no recommendation, only <see cref="Note"/>. The combat helper is separate
/// and keeps its own rules.
/// </summary>
public static class AiCoverage
{
    /// <summary>Solo standard runs at the measured ascensions: Ironclad's adopted policy A0 and A10; the other characters'
    /// A0, A8 and A10.</summary>
    public static bool Covers(RunState state) =>
        state.IsStandardRun && !state.IsMultiplayer
        && (state.Character == RunBaselinePolicy.Ironclad ? state.Ascension is 0 or 10 : state.Ascension is 0 or 8 or 10);

    /// <summary>True when the run is known (a character was read) and the AI was not measured on it. An unknown run is not
    /// judged here: its screens behave as before.</summary>
    public static bool Excludes(RunState? state) =>
        state is { Character: { } c } && c.StartsWith("CHARACTER.", StringComparison.Ordinal) && c != "CHARACTER.UNKNOWN" && !Covers(state);

    /// <summary>One line for a screen that shows nothing because the AI has not learned this kind of run yet.</summary>
    public static string Note(RunState state) =>
        state.IsMultiplayer
            ? Loc.T("AI 학습 전: 협동 판은 아직 AI가 배우지 않아 추천을 보여 주지 않습니다", "Not learned yet: the AI has not learned co-op runs, so no recommendation is shown")
            : !state.IsStandardRun
                ? Loc.T("AI 학습 전: 이 모드는 아직 AI가 배우지 않아 추천을 보여 주지 않습니다", "Not learned yet: the AI has not learned this mode, so no recommendation is shown")
                : Loc.T($"AI 학습 전: 승천 {state.Ascension}은 아직 AI가 배우지 않아 추천을 보여 주지 않습니다",
                    $"Not learned yet: the AI has not learned Ascension {state.Ascension}, so no recommendation is shown");
}
