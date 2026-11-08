using System.Globalization;
using System.Text;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Replay;

/// <summary>Korean Markdown report of replayed runs, used to tune weights against real decisions.</summary>
public static class ReplayReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string VerdictLabel(ReplayVerdict v) => v switch
    {
        ReplayVerdict.Agreed => "추천과 같음",
        ReplayVerdict.AgreedSkip => "스킵 (추천과 같음)",
        ReplayVerdict.PickedOther => "다른 카드 선택",
        ReplayVerdict.PickedButSkipRecommended => "스킵이 나았음",
        ReplayVerdict.SkippedButCardRecommended => "추천 카드를 두고 스킵",
        _ => "참고",
    };

    public static string ToMarkdown(IReadOnlyList<RunReplay> runs, bool includeDecisions = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 런 기록 리플레이 리포트");
        sb.AppendLine();

        var comparable = runs.Sum(r => r.ComparableCount);
        var agreed = runs.Sum(r => r.AgreementCount);
        sb.AppendLine(Inv, $"- 런 {runs.Count}개, 비교 가능한 카드 선택 {comparable}회, 추천과 일치 {agreed}회 ({Pct(comparable == 0 ? 0 : (double)agreed / comparable)})");
        var verdicts = runs.SelectMany(r => r.Decisions).GroupBy(d => d.Verdict).OrderBy(g => g.Key);
        sb.AppendLine("- 판정 분포: " + string.Join(", ", verdicts.Select(g => $"{VerdictLabel(g.Key)} {g.Count()}")));
        sb.AppendLine();

        sb.AppendLine("| 런 | 캐릭터 | 승천 | 결과 | 비교 | 일치율 | 평균 후회 | 덱 복원 경고 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in runs)
        {
            var result = r.Run.Win ? "승리" : r.Run.WasAbandoned ? "포기" : $"패배 ({r.Run.Nodes.Count}층)";
            sb.AppendLine(Inv, $"| {r.Run.StartTime} | {Short(r.Player.Character)} | {r.Run.Ascension} | {result} | {r.ComparableCount} | {(r.AgreementRate is { } a ? Pct(a) : "-")} | {r.MeanRegret:0.0} | {r.TimelineWarnings.Count} |");
        }

        if (!includeDecisions) return sb.ToString();

        foreach (var r in runs)
        {
            sb.AppendLine();
            sb.AppendLine(Inv, $"## {r.Run.StartTime} · {Short(r.Player.Character)} · 승천 {r.Run.Ascension}");
            sb.AppendLine();
            sb.AppendLine("| 층 | 상황 | 제시 카드 (점수) | 스킵 | 추천 | 실제 | 판정 |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var d in r.Decisions.Where(d => d.Offer is not null))
            {
                var o = d.Offer!;
                var cards = string.Join(", ", o.Cards.Select(c => $"{Short(c.Card.ToString())} {c.Total:0}"));
                var rec = o.SkipRecommended ? "스킵" : o.Best is { } b ? Short(b.ToString()) : "-";
                var actual = d.Decision.Skipped ? "스킵" : string.Join("+", d.Decision.Picked.Select(p => Short(p.ToString())));
                sb.AppendLine(Inv, $"| {d.Decision.Node.Floor} | {ContextLabel(d.Decision.Context)} | {cards} | {o.Skip.Total:0} | {rec} | {actual} | {VerdictLabel(d.Verdict)} |");
            }
        }

        return sb.ToString();
    }

    public static string ContextLabel(OfferContext c) => c switch
    {
        OfferContext.CardReward => "일반 보상",
        OfferContext.EliteReward => "엘리트",
        OfferContext.BossReward => "보스",
        OfferContext.Event => "이벤트",
        OfferContext.Shop => "상점",
        _ => c.ToString(),
    };

    private static string Pct(double v) => (v * 100).ToString("0.#", Inv) + "%";

    private static string Short(string id)
    {
        var dot = id.IndexOf('.');
        return dot >= 0 ? id[(dot + 1)..] : id;
    }
}
