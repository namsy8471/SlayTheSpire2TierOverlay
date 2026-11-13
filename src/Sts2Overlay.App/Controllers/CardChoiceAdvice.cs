using Sts2Overlay.App.Integration;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Controllers;

/// <summary>Do not turn an unobserved Skip button into an unconditional action recommendation.</summary>
public static class CardChoiceAdvice
{
    public static bool? SkipAvailability(RecognitionResult result) =>
        result.ScreenKind == ScreenKind.Reward ? true : result.CanSkip;

    public static string? ConditionNote(bool? canSkip, bool optionalWouldSkip) => canSkip is not null ? null
        : optionalWouldSkip
            ? Loc.T("카드를 골라야 한다면 위 추천을 따르세요. 건너뛰기가 가능하면 스킵을 추천합니다.",
                "If a card is required, use the pick above. If skipping is available, prefer Skip.")
            : Loc.T("이 화면에서 건너뛰기가 가능한지는 확인되지 않았습니다.",
                "Whether this screen allows skipping has not been confirmed.");
}
