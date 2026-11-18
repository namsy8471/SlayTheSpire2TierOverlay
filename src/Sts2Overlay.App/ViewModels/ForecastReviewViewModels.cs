using System.Windows.Media;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.ViewModels;

// View models for the end-of-run review (리뷰 tab).
// Immutable records like the other sections: the presenter builds a new one and the controller swaps it in.

/// <summary>One of the biggest disagreements between the player and the engine.</summary>
public sealed record ReviewDisagreementVm
{
    /// <summary>"12층 · 엘리트", plus " · 재계산" when judged by the current version (no play-time record).</summary>
    public required string Header { get; init; }

    /// <summary>"제시: A, B, C".</summary>
    public string OfferedText { get; init; } = "";

    /// <summary>"추천 A (72) · 내 선택 B (51)".</summary>
    public string ChoiceText { get; init; } = "";

    /// <summary>"−21점".</summary>
    public string GapText { get; init; } = "";
}

public sealed record ReviewSectionVm
{
    public static ReviewSectionVm Empty { get; } = new() { HasContent = false };

    public bool HasContent { get; init; } = true;

    /// <summary>"디펙트 · 패배 (23층) · 11-08 21:40".</summary>
    public string Title { get; init; } = "";

    /// <summary>"22개 결정 중 15개 일치 (68%) · 당시 추천 기준 18개, 현재 버전 재계산 4개".</summary>
    public string AgreementText { get; init; } = "";

    /// <summary>Agreement bar width in DIPs (0..356).</summary>
    public double AgreementBarWidth { get; init; }

    /// <summary>"완성된 빌드: 냉기 구체 (몰입 85% · 완성도 60%) · 14층에 방향 확정".</summary>
    public string BuildText { get; init; } = "";

    public Brush BuildColor { get; init; } = Brushes.SteelBlue;
    public IReadOnlyList<ReviewDisagreementVm> Disagreements { get; init; } = [];
    public bool HasDisagreements => Disagreements.Count > 0;
    public string? Note { get; init; }

    /// <summary>A run just ended and the user has not looked at / dismissed its review yet: show the banner.</summary>
    public bool ShowBanner { get; init; }

    /// <summary>One line for the banner: "런 리뷰: 추천 일치 61% · 큰 차이 3번".</summary>
    public string BannerText { get; init; } = "";
}
