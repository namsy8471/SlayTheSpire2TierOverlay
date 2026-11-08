namespace Sts2Overlay.App.Integration;

/// <summary>
/// What the 경로 (path) section shows. The lead fills it from Core's PathAdvisor and passes it to
/// <see cref="Controllers.AppController.UpdatePath"/>; the App only displays it.
/// </summary>
/// <param name="Options">Top paths, best first (e.g. 안전 / 균형 / 공격).</param>
/// <param name="NextStep">One line for the next node, e.g. "다음: 왼쪽 휴식 (2,5)".</param>
/// <param name="Note">Optional footnote, e.g. "추정: 덱 강도 0.62 기준".</param>
public sealed record PathView(IReadOnlyList<PathOptionView> Options, string? NextStep, string? Note = null);

/// <param name="Label">Short name, e.g. "균형".</param>
/// <param name="Summary">One-line summary, e.g. "엘리트 2 · 휴식 2 · 상점 1 · 보스 도착 HP 62%".</param>
/// <param name="ReasonChips">Short reason chips, e.g. "휴식 전 엘리트 없음", "위험: 엘리트 연속".</param>
/// <param name="Score">Optional score shown on the right.</param>
/// <param name="IsRecommended">Highlights the option.</param>
public sealed record PathOptionView(
    string Label,
    string Summary,
    IReadOnlyList<string> ReasonChips,
    double? Score = null,
    bool IsRecommended = false);
