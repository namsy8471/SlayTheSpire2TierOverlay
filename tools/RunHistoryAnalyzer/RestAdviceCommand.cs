using System.Globalization;
using System.Text;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Replay.Validation;
using Sts2Overlay.Core.Rest;

namespace Sts2Overlay.Tools.RunHistoryAnalyzer;

/// <summary>
/// <c>rest-advice</c>: replays every recorded rest-site visit through <see cref="RestSiteAdvisor"/> and reports how often
/// it agrees with the player (heal vs smith, and which card was upgraded). Agreement is a proxy, not a target.
/// </summary>
internal static class RestAdviceCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Run(IReadOnlyList<(string Path, RunHistory Run)> files, DataPack pack, long? user, bool allPlayers)
    {
        var me = user ?? files.Select(f => ReplayStreams.SteamIdFromPath(f.Path)).FirstOrDefault(id => id is not null) ?? LocalPlayer.FixtureSelfId;
        var streams = ReplayStreams.Build(files, allPlayers, me);
        var decisions = RestSiteBacktest.Run(streams, pack);
        var s = RestSiteBacktest.Summarize(decisions, pack.Weights.Path);

        var sb = new StringBuilder();
        sb.AppendLine("# 휴식처 조언 (휴식 vs 강화) 리플레이");
        sb.AppendLine();
        sb.AppendLine("> 일치율은 대리 지표입니다. 플레이어의 선택이 정답은 아닙니다. 남은 길은 실제로 걸은 방(기록에 지도가 없음)입니다.");
        sb.AppendLine();
        sb.AppendLine(Inv, $"- 런 {streams.Select(x => x.GameKey).Distinct().Count()}판, 플레이어 묶음 {streams.Count}개{(allPlayers ? " (협동 동료 포함)" : "")}, 휴식처 방문 {s.Visits}회.");
        sb.AppendLine("- 기록된 선택 종류: " + string.Join(", ", s.ChoiceKinds.Select(kv => $"{kv.Key} {kv.Value}")));
        sb.AppendLine();

        sb.AppendLine("## 1. 휴식 vs 강화");
        sb.AppendLine();
        sb.AppendLine("| 지표 | 값 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(Inv, $"| 비교 대상 (HEAL/SMITH 중 하나만) | {s.Decided} |");
        sb.AppendLine(Inv, $"| 조언 일치 | {s.Agreed}/{s.Decided} ({Rate(s.Agreed, s.Decided)}) |");
        sb.AppendLine(Inv, $"| 확신일 때 일치 | {s.SureAgreed}/{s.SureDecided} ({Rate(s.SureAgreed, s.SureDecided)}) |");
        sb.AppendLine(Inv, $"| 박빙일 때 일치 | {s.Agreed - s.SureAgreed}/{s.Decided - s.SureDecided} ({Rate(s.Agreed - s.SureAgreed, s.Decided - s.SureDecided)}) |");
        sb.AppendLine(Inv, $"| 휴식 비율 (플레이어 / 조언) | {Rate(s.PlayerHealed, s.Decided)} / {Rate(s.AdvisorHealed, s.Decided)} |");
        sb.AppendLine(Inv, $"| 참고: HP {Pct(pack.Weights.Path.RestHealBelowHp ?? 0.5)} 미만이면 휴식 (같은 기록으로 맞춘 기준, 표본 내) | {s.ThresholdAgreed}/{s.Decided} ({Rate(s.ThresholdAgreed, s.Decided)}) |");
        sb.AppendLine();

        sb.AppendLine("HP 구간별 (플레이어 휴식 / 조언 휴식 / 일치):");
        sb.AppendLine();
        sb.AppendLine("| HP | 방문 | 플레이어 휴식 | 조언 휴식 | 일치 |");
        sb.AppendLine("|---|---|---|---|---|");
        var decided = decisions.Where(d => d.PlayerAction is not null).ToArray();
        foreach (var (lo, hi) in new[] { (0.0, 0.3), (0.3, 0.5), (0.5, 0.7), (0.7, 0.9), (0.9, 1.01) })
        {
            var b = decided.Where(d => d.Hp >= lo && d.Hp < hi).ToArray();
            if (b.Length == 0) continue;
            sb.AppendLine(Inv, $"| {lo * 100:0}–{Math.Min(100, hi * 100):0}% | {b.Length} | {b.Count(d => d.PlayerAction == RestAction.Heal)} | {b.Count(d => d.Advice.Action == RestAction.Heal)} | {Rate(b.Count(d => d.Agrees), b.Length)} |");
        }
        sb.AppendLine();

        // Mean heal-score components per HP band: shows which part of the simulation drives the heal side.
        var keys = decided.SelectMany(d => d.Advice.HealScore?.Components ?? []).Select(c => (c.Key, c.Label)).Distinct().ToArray();
        if (keys.Length > 0)
        {
            sb.AppendLine("휴식 점수 구성 (HP 구간별 평균, 강화 점수와 비교):");
            sb.AppendLine();
            sb.AppendLine("| HP | " + string.Join(" | ", keys.Select(k => k.Label)) + " | 휴식 합계 | 강화 |");
            sb.AppendLine("|---|" + string.Concat(keys.Select(_ => "---|")) + "---|---|");
            foreach (var (lo, hi) in new[] { (0.0, 0.3), (0.3, 0.5), (0.5, 0.7), (0.7, 0.9), (0.9, 1.01) })
            {
                var b = decided.Where(d => d.Hp >= lo && d.Hp < hi && d.Advice.HealScore is not null).ToArray();
                if (b.Length == 0) continue;
                var cells = string.Join(" | ", keys.Select(k => b.Average(d => d.Advice.HealScore!.ValueOf(k.Key)).ToString("+0.0;-0.0;0", Inv)));
                sb.AppendLine(Inv, $"| {lo * 100:0}–{Math.Min(100, hi * 100):0}% | {cells} | {b.Average(d => d.Advice.HealScore!.Total):0.0} | {b.Average(d => d.Advice.SmithScore!.Total):0.0} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 2. 강화한 카드");
        sb.AppendLine();
        sb.AppendLine("| 지표 | 1위 | 3위 안 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine(Inv, $"| 조언 (카드 강화 가치 + 빌드 역할 + 역할 부족) | {s.Top1:0.#}/{s.SmithWithCard} ({Rate(s.Top1, s.SmithWithCard)}) | {s.Top3:0.#}/{s.SmithWithCard} ({Rate(s.Top3, s.SmithWithCard)}) |");
        sb.AppendLine(Inv, $"| 카드 강화 가치만 (upgradeValue, 없으면 upgradedDelta) | {s.DeltaOnlyTop1:0.#}/{s.SmithWithCard} ({Rate(s.DeltaOnlyTop1, s.SmithWithCard)}) | {s.DeltaOnlyTop3:0.#}/{s.SmithWithCard} ({Rate(s.DeltaOnlyTop3, s.SmithWithCard)}) |");
        sb.AppendLine(Inv, $"| 무작위 기대값 | {s.RandomTop1:0.0} ({Rate(s.RandomTop1, s.SmithWithCard)}) | {s.RandomTop3:0.0} ({Rate(s.RandomTop3, s.SmithWithCard)}) |");
        sb.AppendLine();
        // Sensitivity: how much weight the role-need part gets relative to the pack's upgradedDelta.
        var smithDecisions = decisions.Where(d => d.UpgradeRank is not null).ToArray();
        if (smithDecisions.Length > 0)
        {
            sb.AppendLine("빌드·역할 부족 비중 민감도 (점수 = 카드 강화 가치 + 배수 × 나머지 부분, 현재 배수 1):");
            sb.AppendLine();
            sb.AppendLine("| 나머지 배수 | 1위 | 3위 안 |");
            sb.AppendLine("|---|---|---|");
            foreach (var f in new[] { 0.0, 0.01, 0.25, 0.5, 1.0, 2.0 })
            {
                double Value(Sts2Overlay.Core.Scoring.UpgradeCandidate c)
                {
                    var delta = c.Score.ValueOf(Sts2Overlay.Core.Scoring.UpgradeComponentKeys.Upgrade);
                    return delta + f * (c.Total - delta);
                }
                var ranks = smithDecisions.Select(d => RestSiteBacktest.RankOf(d.Advice, d.UpgradedCard, Value)!.Value).ToArray();
                var t1 = ranks.Sum(r => r.HitWithin(1));
                var t3 = ranks.Sum(r => r.HitWithin(3));
                sb.AppendLine(Inv, $"| {f:0.##} | {Rate(t1, ranks.Length)} | {Rate(t3, ranks.Length)} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine(Inv, $"- 강화 후보 평균 {s.MeanCandidates:0.0}장. 동점은 무작위로 순서를 정했을 때의 기대값으로 셉니다(동점 2장이 1위를 나누면 각 0.5). 기록의 카드를 후보에서 못 찾은 방문 {s.SmithCardMissing}회 (덱 복원 차이).");
        sb.AppendLine();

        sb.AppendLine("## 3. 눈에 띄는 불일치");
        sb.AppendLine();
        sb.AppendLine("확신인데 플레이어와 다른 휴식/강화 (차이가 큰 순):");
        sb.AppendLine();
        sb.AppendLine("| 런 | 막·층 | HP | 플레이어 | 조언 | 휴식−강화 점수 | 이유 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var d in decided.Where(d => !d.Agrees && d.Advice.Confidence == RestConfidence.Sure)
                     .OrderByDescending(d => Math.Abs(Margin(d))).Take(12))
        {
            sb.AppendLine(Inv, $"| {d.RunKey} | {d.Act}막 {d.Floor}층 | {Pct(d.Hp)} | {Name(d.PlayerAction!.Value)} | {Name(d.Advice.Action)} | {Margin(d):+0.0;-0.0} | {d.Advice.Reason} |");
        }
        sb.AppendLine();
        sb.AppendLine("강화한 카드가 조언 3위 밖 (순위가 낮은 순):");
        sb.AppendLine();
        sb.AppendLine("| 런 | 막·층 | 강화한 카드 | 조언 순위 | 조언 1위 (이유) |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var d in decisions.Where(d => d.UpgradeRank is { } r && r.Best > 3).OrderByDescending(d => d.UpgradeRank!.Value.Best).Take(12))
        {
            var best = d.Advice.BestUpgrade!;
            sb.AppendLine(Inv, $"| {d.RunKey} | {d.Act}막 {d.Floor}층 | {d.UpgradedCard} | {d.UpgradeRank!.Value.Best}/{d.Candidates} | {best.Card.Id} ({string.Join(", ", best.Chips.Select(c => $"{c.Label}"))}) |");
        }
        return sb.ToString();
    }

    private static double Margin(RestDecision d) => (d.Advice.HealScore?.Total ?? 0) - (d.Advice.SmithScore?.Total ?? 0);

    private static string Name(RestAction a) => a == RestAction.Heal ? "휴식" : "강화";

    private static string Rate(double n, double total) => total <= 0 ? "-" : (n / total).ToString("P0", Inv);

    private static string Pct(double ratio) => (ratio * 100).ToString("0", Inv) + "%";
}
