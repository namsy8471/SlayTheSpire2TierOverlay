using System.Globalization;
using System.Windows.Media;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.ViewModels;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Replay;

namespace Sts2Overlay.App.Controllers;

/// <summary>Turns review results from Core into view models in the current language. Pure formatting, no state.</summary>
internal static class ForecastReviewPresenter
{
    private const double BarMax = 356;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ReviewSectionVm Review(RunReview review, CardInfoProvider cards, bool showBanner)
    {
        var run = review.Run;
        var result = run.Win ? Loc.T("승리", "Victory")
            : run.WasAbandoned ? Loc.T($"포기 ({review.FloorReached}층)", $"Abandoned (Floor {review.FloorReached})")
            : Loc.T($"패배 ({review.FloorReached}층)", $"Defeat (Floor {review.FloorReached})");
        var when = DateTimeOffset.FromUnixTimeSeconds(run.StartTime).ToLocalTime().ToString("MM-dd HH:mm", Inv);
        var asc = run.Ascension > 0 ? Loc.T($" · 승천 {run.Ascension}", $" · Ascension {run.Ascension}") : "";
        var coop = run.IsMultiplayer ? Loc.T(" · 협동", " · Multiplayer") : "";

        var rate = review.AgreementRate;
        var agreement = review.Decisions == 0
            ? Loc.T("비교할 카드 선택이 없습니다", "No card picks to compare")
            : AgreementLine(review);
        if (review.ShopVisits > 0) agreement += Loc.T($" · 상점 {review.ShopVisits}번은 제외", $" · {review.ShopVisits} Shop visits left out");

        var build = review.Build;
        string buildText;
        if (build.Primary is not { } p || p.Commit <= 0)
        {
            buildText = Loc.T("뚜렷한 빌드 없이 끝났습니다", "Ended without a clear build");
        }
        else
        {
            var name = p.Archetype.DisplayName + (build.HybridWith is { } h ? Loc.T($" + {h.Archetype.DisplayName} (하이브리드)", $" + {h.Archetype.DisplayName} (hybrid)") : "");
            buildText = Loc.T(
                $"{(p.IsCommitted ? "완성된 빌드" : "가장 가까웠던 빌드")}: {name} · 몰입 {Pct(p.Commit)} · 완성도 {p.CompletionPercent:0}%",
                $"{(p.IsCommitted ? "Completed build" : "Closest build")}: {name} · commit {Pct(p.Commit)} · completion {p.CompletionPercent:0}%");
            if (build.CommittedAtFloor is { } f) buildText += Loc.T($" · {f}층에 방향 확정", $" · locked in on Floor {f}");
            buildText += Loc.T($" · 덱 {build.DeckSize}장", $" · deck {build.DeckSize} cards");
        }

        var disagreements = review.BiggestDisagreements.Select(d => new ReviewDisagreementVm
        {
            Header = Loc.T($"{d.Floor}층 · {ContextLabel(d.Context)}", $"Floor {d.Floor} · {ContextLabel(d.Context)}")
                     + (d.Recomputed ? Loc.T(" · 재계산", " · recomputed") : ""),
            OfferedText = Loc.T("제시: ", "Offered: ") + string.Join(", ", d.Offered.Select(cards.Name)),
            ChoiceText = (d.Recomputed
                    ? Loc.T($"추천 {Choice(d.EnginePick, cards)} ({Fmt(d.EngineScore)}) · 내 선택 {Choice(d.UserPick, cards)} ({Fmt(d.UserScore)})",
                            $"Pick {Choice(d.EnginePick, cards)} ({Fmt(d.EngineScore)}) · You {Choice(d.UserPick, cards)} ({Fmt(d.UserScore)})")
                    : Loc.T($"당시 추천 {Choice(d.EnginePick, cards)} ({Fmt(d.EngineScore)}) · 내 선택 {Choice(d.UserPick, cards)} ({Fmt(d.UserScore)})",
                            $"Advice then {Choice(d.EnginePick, cards)} ({Fmt(d.EngineScore)}) · You {Choice(d.UserPick, cards)} ({Fmt(d.UserScore)})"))
                 + (!d.Recomputed && d.CurrentDiffers
                    ? Loc.T($" · 지금 버전은 {Choice(d.CurrentPick, cards)} 추천", $" · current version picks {Choice(d.CurrentPick, cards)}")
                    : ""),
            GapText = Loc.T($"−{Fmt(d.Gap)}점", $"−{Fmt(d.Gap)} pts"),
        }).ToArray();

        var banner = review.Decisions == 0
            ? Loc.T("런 리뷰가 준비됐습니다", "Run review is ready")
            : Loc.T($"런 리뷰: 추천 일치 {Pct(rate ?? 0)} · 큰 차이 {disagreements.Length}번",
                    $"Run review: {Pct(rate ?? 0)} matched picks · {disagreements.Length} big gaps");

        return new ReviewSectionVm
        {
            Title = $"{CharacterName(review.Player.Character)} · {result}{asc}{coop} · {when}",
            AgreementText = agreement,
            AgreementBarWidth = (rate ?? 0) * BarMax,
            BuildText = buildText,
            BuildColor = ParseColor(build.Primary?.Archetype.Color),
            Disagreements = disagreements,
            Note = disagreements.Length == 0 && review.Decisions > 0
                ? Loc.T("모든 카드 선택이 추천 1순위와 같았습니다.", "Every card pick matched the top pick.")
                : review.StoredJudged > 0
                ? Loc.T("점수 차 = 추천 점수 − 내 선택 점수 (기록된 결정은 플레이 당시 점수, '재계산'은 지금 데이터 팩 기준) [추정]",
                        "Gap = advice score − your pick score (play-time scores for logged picks, the current data pack for 'recomputed') [est.]")
                : Loc.T("점수 차 = 추천 1순위 점수 − 내 선택 점수 (지금 데이터 팩 기준으로 다시 계산) [추정]",
                        "Gap = top pick score − your pick score (recomputed with the current data pack) [est.]"),
            ShowBanner = showBanner,
            BannerText = banner,
        };
    }

    /// <summary>
    /// "22개 결정 중 15개 일치 (68%) · 당시 추천 기준 18개, 현재 버전 재계산 4개": says what the count is based on. Without
    /// a play-time decision log every decision was recomputed with the current version, and the line says so.
    /// </summary>
    internal static string AgreementLine(RunReview review)
    {
        var pct = Pct(review.AgreementRate ?? 0);
        var basis = review.HasDecisionLog
            ? Loc.T($"당시 추천 기준 {review.StoredJudged}개, 현재 버전 재계산 {review.Recomputed}개",
                    $"{review.StoredJudged} judged by the advice shown then, {review.Recomputed} recomputed with the current version")
            : Loc.T("현재 버전으로 다시 계산한 결과입니다 (플레이 당시 추천 기록 없음)",
                    "recomputed with the current version (no record of the advice shown during play)");
        return Loc.T($"{review.Decisions}개 결정 중 {review.Agreements}개 일치 ({pct}) · {basis}",
                     $"{review.Agreements} of {review.Decisions} agreed ({pct}) · {basis}");
    }

    private static string Choice(CardRef? card, CardInfoProvider cards) => card is { } c ? cards.Name(c) : Loc.T("스킵", "Skip");

    private static string ContextLabel(OfferContext c) => c switch
    {
        OfferContext.EliteReward => Loc.T("엘리트", "Elite"),
        OfferContext.BossReward => Loc.T("보스", "Boss"),
        OfferContext.Event => Loc.T("이벤트", "Event"),
        OfferContext.Shop => Loc.T("상점", "Shop"),
        _ => Loc.T("일반 전투", "Enemy"),
    };

    /// <summary>Character display name (game titles: "The Ironclad" …); an unknown id loses its prefix.</summary>
    internal static string CharacterName(string id) => id switch
    {
        "CHARACTER.IRONCLAD" => Loc.T("아이언클래드", "The Ironclad"),
        "CHARACTER.SILENT" => Loc.T("사일런트", "The Silent"),
        "CHARACTER.DEFECT" => Loc.T("디펙트", "The Defect"),
        "CHARACTER.REGENT" => Loc.T("리젠트", "The Regent"),
        "CHARACTER.FOOL" => Loc.T("풀", "Fool"),
        "CHARACTER.NECROBINDER" => Loc.T("네크로바인더", "The Necrobinder"),
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
