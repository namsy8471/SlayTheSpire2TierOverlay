using System.Globalization;
using System.Windows.Media;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;

namespace Sts2Overlay.App.Controllers;

/// <summary>Turns forecast and review results from Core into Korean view models. Pure formatting, no state.</summary>
internal static class ForecastReviewPresenter
{
    private const double BarMax = 356;
    private const int MaxRows = 4;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ForecastSectionVm Forecast(ForecastResult? result)
    {
        if (result is null || result.Archetypes.Count == 0) return ForecastSectionVm.Empty;

        // Most likely first; archetypes with no chance at all are noise unless nothing else is left.
        var shown = result.Archetypes.Where(a => a.IsCompleteNow || a.EndOfRun.PComplete >= 0.005 || a.EndOfRun.PPrimary >= 0.05).Take(MaxRows).ToList();
        if (shown.Count == 0) shown = result.Archetypes.Take(2).ToList();

        var rows = shown.Select(a => new ForecastRowVm
        {
            Name = a.Archetype.DisplayName,
            Color = ParseColor(a.Archetype.Color),
            IsCompleteNow = a.IsCompleteNow,
            EndText = a.IsCompleteNow ? "완성" : Pct(a.CalibratedIfSurvive),
            BarWidth = (a.IsCompleteNow ? 1 : a.CalibratedIfSurvive) * BarMax,
            DetailText = a.IsCompleteNow
                ? $"이미 방향 확정 (몰입 {Pct(a.CurrentCommit)}) · 3막 끝 완성도 평균 {a.EndOfRun.MeanCompletionPercent:0}%"
                : $"사망 포함 {Pct(a.CalibratedAll)} · 보정 전 {Pct(a.EndOfRun.PComplete)} · 지금 몰입 {Pct(a.CurrentCommit)} · 완성도 {a.CurrentCompletionPercent:0}%",
        }).ToArray();

        var e = result.ExpectedUntilEnd;
        var source = result.UsedMap ? "이번 막은 지도 기준" : "이번 막은 보통 길이 기준";
        var note = result.PoolSize == 0
            ? "카드 풀 데이터가 없어 지금 덱 기준으로만 표시합니다."
            : $"큰 숫자 = 3막 보스까지 살아남는다면, 지난 런 기록으로 보정한 값 · 남은 카드 기회 약 {e.Total:0}번 (일반 {e.Normal:0.#} · 엘리트 {e.Elite:0.#} · 보스 {e.Boss:0} · 상점 {e.Shop:0.#}), {source} · 시뮬레이션 {result.Simulations}회 [추정]";
        return new ForecastSectionVm { Title = "빌드 완성 확률 (살아남는다면 · 사망 포함)", Rows = rows, Note = note };
    }

    /// <summary>
    /// Puts the forecast line under each matching archetype of the 빌드 list (it replaced the old "핵심 카드가 나올
    /// 확률" line, which could say 80% while the build block said 10%). Archetypes without a forecast get no line.
    /// </summary>
    public static BuildSectionVm WithForecast(BuildSectionVm build, ForecastResult? result)
    {
        if (!build.HasContent || build.Archetypes.Count == 0) return build;
        var byName = new Dictionary<string, ArchetypeForecast>(StringComparer.Ordinal);
        foreach (var f in result?.Archetypes ?? []) byName.TryAdd(f.Archetype.DisplayName, f);

        var changed = false;
        var items = build.Archetypes.Select(a =>
        {
            var text = byName.TryGetValue(a.Name, out var f) ? ForecastLine(f) : null;
            if (text == a.ProbabilityText) return a;
            changed = true;
            return a with { ProbabilityText = text };
        }).ToArray();
        return changed ? build with { Archetypes = items } : build;
    }

    internal static string? ForecastLine(ArchetypeForecast f) => f.IsCompleteNow
        ? null
        : $"3막 끝 완성 확률: 살아남는다면 {Pct(f.CalibratedIfSurvive)} · 사망 포함 {Pct(f.CalibratedAll)} [추정]";

    public static ReviewSectionVm Review(RunReview review, CardInfoProvider cards, bool showBanner)
    {
        var run = review.Run;
        var result = run.Win ? "승리" : run.WasAbandoned ? $"포기 ({review.FloorReached}층)" : $"패배 ({review.FloorReached}층)";
        var when = DateTimeOffset.FromUnixTimeSeconds(run.StartTime).ToLocalTime().ToString("MM-dd HH:mm", Inv);
        var asc = run.Ascension > 0 ? $" · 승천 {run.Ascension}" : "";
        var coop = run.IsMultiplayer ? " · 협동" : "";

        var rate = review.AgreementRate;
        var agreement = review.Decisions == 0
            ? "비교할 카드 선택이 없습니다"
            : $"카드 선택 {review.Decisions}번 · 추천 1순위와 같은 선택 {review.Agreements}번 ({Pct(rate ?? 0)})";
        if (review.ShopVisits > 0) agreement += $" · 상점 {review.ShopVisits}번은 제외";

        var build = review.Build;
        string buildText;
        if (build.Primary is not { } p || p.Commit <= 0)
        {
            buildText = "뚜렷한 빌드 없이 끝났습니다";
        }
        else
        {
            var name = p.Archetype.DisplayName + (build.HybridWith is { } h ? $" + {h.Archetype.DisplayName} (하이브리드)" : "");
            buildText = $"{(p.IsCommitted ? "완성된 빌드" : "가장 가까웠던 빌드")}: {name} · 몰입 {Pct(p.Commit)} · 완성도 {p.CompletionPercent:0}%";
            if (build.CommittedAtFloor is { } f) buildText += $" · {f}층에 방향 확정";
            buildText += $" · 덱 {build.DeckSize}장";
        }

        var disagreements = review.BiggestDisagreements.Select(d => new ReviewDisagreementVm
        {
            Header = $"{d.Floor}층 · {ContextLabel(d.Context)}",
            OfferedText = "제시: " + string.Join(", ", d.Offered.Select(cards.Name)),
            ChoiceText = $"추천 {Choice(d.EnginePick, cards)} ({Fmt(d.EngineScore)}) · 내 선택 {Choice(d.UserPick, cards)} ({Fmt(d.UserScore)})",
            GapText = $"−{Fmt(d.Gap)}점",
        }).ToArray();

        var banner = review.Decisions == 0
            ? "런 리뷰가 준비됐습니다"
            : $"런 리뷰: 추천 일치 {Pct(rate ?? 0)} · 큰 차이 {disagreements.Length}번";

        return new ReviewSectionVm
        {
            Title = $"{CharacterName(review.Player.Character)} · {result}{asc}{coop} · {when}",
            AgreementText = agreement,
            AgreementBarWidth = (rate ?? 0) * BarMax,
            BuildText = buildText,
            BuildColor = ParseColor(build.Primary?.Archetype.Color),
            Disagreements = disagreements,
            Note = disagreements.Length == 0 && review.Decisions > 0
                ? "모든 카드 선택이 추천 1순위와 같았습니다."
                : "점수 차 = 추천 1순위 점수 − 내 선택 점수 (지금 데이터 팩 기준으로 다시 계산) [추정]",
            ShowBanner = showBanner,
            BannerText = banner,
        };
    }

    private static string Choice(CardRef? card, CardInfoProvider cards) => card is { } c ? cards.Name(c) : "스킵";

    private static string ContextLabel(OfferContext c) => c switch
    {
        OfferContext.EliteReward => "엘리트",
        OfferContext.BossReward => "보스",
        OfferContext.Event => "이벤트",
        OfferContext.Shop => "상점",
        _ => "일반 전투",
    };

    internal static string CharacterName(string id) => id switch
    {
        "CHARACTER.IRONCLAD" => "아이언클래드",
        "CHARACTER.SILENT" => "사일런트",
        "CHARACTER.DEFECT" => "디펙트",
        "CHARACTER.REGENT" => "리젠트",
        "CHARACTER.NECROBINDER" => "네크로바인더",
        _ => id.Replace("CHARACTER.", "", StringComparison.Ordinal),
    };

    /// <summary>"62%", with "&lt;1%" / "&gt;99%" so a rounded 0% or 100% never claims certainty it doesn't have.</summary>
    internal static string Pct(double p) => p switch
    {
        <= 0 => "0%",
        < 0.01 => "<1%",
        >= 1 => "100%",
        > 0.99 => ">99%",
        _ => (p * 100).ToString("0", Inv) + "%",
    };

    private static string Fmt(double v) => Math.Round(v).ToString("0", Inv);

    private static Brush ParseColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }
        catch (FormatException)
        {
        }
        return Brushes.SteelBlue;
    }
}
