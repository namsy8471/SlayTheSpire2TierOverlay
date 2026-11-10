using System.Windows.Media;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.ViewModels;

// View models for the build forecast block (빌드 tab) and the end-of-run review (리뷰 tab).
// Immutable records like the other sections: the presenter builds a new one and the controller swaps it in.

/// <summary>One archetype line: "냉기 구체 · 3막 끝 62% · 다음 보스 전 18%".</summary>
public sealed record ForecastRowVm
{
    public required string Name { get; init; }
    public Brush Color { get; init; } = Brushes.SteelBlue;

    /// <summary>Big number on the right, e.g. "62%" (or "완성").</summary>
    public string EndText { get; init; } = "";

    /// <summary>Probability bar width in DIPs (0..356).</summary>
    public double BarWidth { get; init; }

    /// <summary>"다음 보스 전 18% · 주 빌드가 될 확률 40% · 핵심 카드 전부 9%".</summary>
    public string DetailText { get; init; } = "";

    public bool IsCompleteNow { get; init; }
}

public sealed record ForecastSectionVm
{
    public static ForecastSectionVm Empty { get; } = new() { HasContent = false };

    public bool HasContent { get; init; } = true;
    private readonly string? _title;

    /// <summary>Set by the presenter; the default follows the current language.</summary>
    public string Title
    {
        get => _title ?? Loc.T("빌드 완성 확률", "Build completion odds");
        init => _title = value;
    }
    public IReadOnlyList<ForecastRowVm> Rows { get; init; } = [];

    /// <summary>Assumptions and cost: remaining rewards, policy, simulation count.</summary>
    public string Note { get; init; } = "";
}

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
