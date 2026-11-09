using System.Globalization;
using System.Text;

namespace Sts2Overlay.Tools.DeckSim;

/// <summary>Markdown report of a <see cref="Summary"/> (Korean, dev only).</summary>
internal static class Report
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly (string Key, string Label, string Format)[] Columns =
    [
        ("Size", "덱", "0.0"),
        ("Commit", "주빌드 commit", "0.00"),
        ("Complete", "빌드 완성", "P0"),
        ("Core", "코어 확보", "P0"),
        ("Coherence", "일관성", "P0"),
        ("Generic", "범용", "P0"),
        ("Other", "다른 빌드", "P0"),
        ("Builds", "건드린 빌드", "0.0"),
        ("Draw", "드로우", "0.0"),
        ("OffDraw", "빌드 밖 드로우", "0.0"),
        ("MeanCost", "평균 코스트", "0.00"),
        ("Cost0", "0코", "P0"),
        ("Cost1", "1코", "P0"),
        ("Cost2", "2코", "P0"),
        ("Cost3", "3코+", "P0"),
        ("CostX", "X", "P0"),
        ("EnergyPerCard", "장당 에너지", "0.00"),
        ("Powers", "파워", "0.0"),
        ("BuildDupes", "빌드 카드 중복", "0.0"),
        ("OtherDupes", "그 밖 중복", "0.0"),
    ];

    public static void Write(StringBuilder sb, Summary s, int rankerRuns)
    {
        sb.AppendLine("# 덱 시뮬레이션 보고서");
        sb.AppendLine();
        sb.AppendLine($"- 시뮬레이션: 캐릭터·정책마다 {s.Simulations}판, 승천 {s.Ascension}, {(s.Multiplayer ? "협동 카드 풀" : "싱글 카드 풀")}");
        sb.AppendLine($"- 기준: OP.GG 상위 랭커 A10 솔로 승리 {rankerRuns}판 (빌드 근거 문서에 인용된 런)");
        sb.AppendLine("- engine = 오버레이 추천을 그대로 따름, tier = 덱을 무시하고 티어만 봄, ranker = 랭커 실제 덱");
        sb.AppendLine("- 일관성 = 선택한 카드(시작 덱·저주 제외) 중 주빌드 목록 + 테마 카드 비율. 범용 = 어느 빌드에도 없는 카드. 다른 빌드 = 주빌드가 아닌 빌드의 카드.");
        sb.AppendLine();

        foreach (var c in s.Rows.Select(r => r.Character).Distinct().Order(StringComparer.Ordinal))
        {
            sb.AppendLine($"## {Program.Short(c)}");
            sb.AppendLine();
            sb.Append("| 막 | 출처 | n |");
            foreach (var col in Columns) sb.Append(' ').Append(col.Label).Append(" |");
            sb.AppendLine();
            sb.Append("|---|---|---|");
            foreach (var _ in Columns) sb.Append("---|");
            sb.AppendLine();
            foreach (var row in s.Rows.Where(r => r.Character == c).OrderBy(r => r.Act).ThenBy(r => Order(r.Source)))
            {
                sb.Append($"| {row.Act} | {row.Source} | {row.N} |");
                foreach (var col in Columns)
                {
                    var v = row.Mean.GetValueOrDefault(col.Key);
                    sb.Append(' ').Append(v.ToString(col.Format, Inv)).Append(" |");
                }
                sb.AppendLine();
            }
            sb.AppendLine();

            var builds = s.Builds.Where(b => b.Character == c).OrderBy(b => Order(b.Source)).ToList();
            if (builds.Count > 0)
            {
                sb.AppendLine("최종 덱의 주빌드 분포 (괄호: 그 빌드가 주빌드일 때 완성 비율)");
                sb.AppendLine();
                var ids = builds.SelectMany(b => b.Share.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
                sb.Append("| 출처 |");
                foreach (var id in ids) sb.Append(' ').Append(id).Append(" |");
                sb.AppendLine();
                sb.Append("|---|");
                foreach (var _ in ids) sb.Append("---|");
                sb.AppendLine();
                foreach (var b in builds)
                {
                    sb.Append($"| {b.Source} |");
                    foreach (var id in ids)
                    {
                        sb.Append(b.Share.TryGetValue(id, out var share)
                            ? $" {share.ToString("P0", Inv)} ({b.CompleteGiven[id].ToString("P0", Inv)}) |"
                            : " - |");
                    }
                    sb.AppendLine();
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("## 카드 보상 스킵률 (막별)");
        sb.AppendLine();
        sb.AppendLine("| 캐릭터 | 출처 | 1막 | 2막 | 3막 |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in s.Skip.OrderBy(r => r.Character, StringComparer.Ordinal).ThenBy(r => Order(r.Source)))
        {
            sb.AppendLine($"| {Program.Short(r.Character)} | {r.Source} | {r.ByAct[0]:P0} | {r.ByAct[1]:P0} | {r.ByAct[2]:P0} |");
        }
        sb.AppendLine("| 전체 | ranker (docs/research/v2-stats/weights.md §2) | 14% | 38% | 52% |");
        foreach (var a in s.Agreement)
        {
            string By(int[] k, int i) => Pct(k[i], a.ScreensByAct[i]);
            sb.AppendLine($"| {Program.Short(a.Character)} | 랭커 런 다시보기: 랭커 / 엔진 | {By(a.RankerSkipsByAct, 0)} / {By(a.EngineSkipsByAct, 0)} | {By(a.RankerSkipsByAct, 1)} / {By(a.EngineSkipsByAct, 1)} | {By(a.RankerSkipsByAct, 2)} / {By(a.EngineSkipsByAct, 2)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 랭커 선택과 비교 (랭커 런의 카드 보상 화면을 엔진에 다시 넣음)");
        sb.AppendLine();
        sb.AppendLine("현재 빌드 = 그 화면 직전 랭커 덱의 주빌드(코어 확보, 코어 목록이 없으면 commit ≥ 0.4일 때만). 혼합 화면 = 그 빌드 목록 카드와 목록 밖 카드가 함께 나온 화면.");
        sb.AppendLine();
        sb.AppendLine("| 캐릭터 | 화면 | 1순위 일치 | 랭커 스킵 | 엔진 스킵 | 혼합 화면 | 랭커가 빌드 카드 | 엔진이 빌드 카드 | 랭커는 빌드, 엔진은 밖 | (그중 드로우) | (그중 스킵) | 랭커는 밖, 엔진은 빌드 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var a in s.Agreement)
        {
            sb.AppendLine($"| {Program.Short(a.Character)} | {a.Screens} | {Pct(a.Top1, a.Screens)} | {Pct(a.RankerSkips, a.Screens)} | {Pct(a.EngineSkips, a.Screens)} | {a.Mixed} | "
                          + $"{Pct(a.RankerTookBuild, a.Mixed)} | {Pct(a.EngineTookBuild, a.Mixed)} | {Pct(a.EngineLeftBuild, a.Mixed)} | {Pct(a.LeftForDraw, a.EngineLeftBuild)} | {Pct(a.LeftForSkip, a.EngineLeftBuild)} | {Pct(a.EngineOverBuild, a.Mixed)} |");
        }
        var t = s.Agreement;
        if (t.Count > 0)
        {
            int screens = t.Sum(a => a.Screens), mixed = t.Sum(a => a.Mixed), left = t.Sum(a => a.EngineLeftBuild);
            sb.AppendLine($"| 전체 | {screens} | {Pct(t.Sum(a => a.Top1), screens)} | {Pct(t.Sum(a => a.RankerSkips), screens)} | {Pct(t.Sum(a => a.EngineSkips), screens)} | {mixed} | "
                          + $"{Pct(t.Sum(a => a.RankerTookBuild), mixed)} | {Pct(t.Sum(a => a.EngineTookBuild), mixed)} | {Pct(left, mixed)} | {Pct(t.Sum(a => a.LeftForDraw), left)} | {Pct(t.Sum(a => a.LeftForSkip), left)} | {Pct(t.Sum(a => a.EngineOverBuild), mixed)} |");
            sb.AppendLine();
            sb.AppendLine("랭커가 카드를 고른 화면에서 그 카드가 엔진 순위(스킵 제외) 몇 위였나, 엔진이 스킵을 권한 비율");
            sb.AppendLine();
            sb.AppendLine("| 막 | 랭커가 고른 화면 | 엔진 1위 | 엔진 2위 | 엔진 3위 이하 | 엔진은 스킵 | 랭커가 스킵한 화면 | 엔진도 스킵 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            for (var act = 0; act < 3; act++)
            {
                int actScreens = t.Sum(a => a.ScreensByAct[act]), skips = t.Sum(a => a.RankerSkipsByAct[act]), picks = actScreens - skips;
                int first = t.Sum(a => a.RankerCardFirst[act]), second = t.Sum(a => a.RankerCardSecond[act]);
                int bothSkip = t.Sum(a => a.EngineSkipsByAct[act]) - t.Sum(a => a.EngineSkipOnPick[act]);
                sb.AppendLine($"| {act + 1} | {picks} | {Pct(first, picks)} | {Pct(second, picks)} | {Pct(picks - first - second, picks)} | {Pct(t.Sum(a => a.EngineSkipOnPick[act]), picks)} | {skips} | {Pct(bothSkip, skips)} |");
            }
            sb.AppendLine();
            sb.AppendLine("코어 전(주빌드 commit ≥ 0.3인데 코어가 없음) 혼합 화면: 그 빌드의 코어 아닌 목록 카드를 누가 집었나");
            sb.AppendLine();
            sb.AppendLine("| 캐릭터 | 화면 | 랭커가 빌드 카드 | 엔진이 빌드 카드 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var a in t) sb.AppendLine($"| {Program.Short(a.Character)} | {a.LeanMixed} | {Pct(a.LeanRankerBuild, a.LeanMixed)} | {Pct(a.LeanEngineBuild, a.LeanMixed)} |");
            int lm = t.Sum(a => a.LeanMixed);
            sb.AppendLine($"| 전체 | {lm} | {Pct(t.Sum(a => a.LeanRankerBuild), lm)} | {Pct(t.Sum(a => a.LeanEngineBuild), lm)} |");
            sb.AppendLine();
            sb.AppendLine("랭커는 빌드 카드, 엔진은 다른 선택을 한 화면의 점수 구성 요소 차 (엔진 선택 − 랭커 선택, 평균). 양수가 엔진을 빌드 밖으로 끈 요소입니다.");
            sb.AppendLine();
            DiffTable(sb, t.Select(a => (a.Character, a.EngineLeftBuild, a.MeanDiff)).ToList());
            sb.AppendLine();
            sb.AppendLine($"반대로 랭커는 빌드 밖(또는 스킵), 엔진은 빌드 카드를 고른 화면 (랭커가 스킵한 화면 {Pct(t.Sum(a => a.OverRankerSkip), t.Sum(a => a.EngineOverBuild))}): 엔진 선택 − 랭커 선택. 양수가 엔진을 빌드 쪽으로 끈 요소입니다.");
            sb.AppendLine();
            DiffTable(sb, t.Select(a => (a.Character, a.EngineOverBuild, a.OverDiff)).ToList());
            if (s.AgreementExamples.Count > 0)
            {
                sb.AppendLine("예시");
                sb.AppendLine();
                foreach (var line in s.AgreementExamples) sb.AppendLine($"- {line}");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        if (s.ByBuild.Count > 0)
        {
            sb.AppendLine("## 빌드별 최종 덱 (3막 끝, 주빌드가 같은 덱끼리, 5판 이상)");
            sb.AppendLine();
            sb.Append("| 빌드 | 출처 | n |");
            foreach (var col in Columns) sb.Append(' ').Append(col.Label).Append(" |");
            sb.AppendLine();
            sb.Append("|---|---|---|");
            foreach (var _ in Columns) sb.Append("---|");
            sb.AppendLine();
            foreach (var row in s.ByBuild.OrderBy(r => r.Build, StringComparer.Ordinal).ThenBy(r => Order(r.Source)))
            {
                sb.Append($"| {row.Build} | {row.Source} | {row.N} |");
                foreach (var col in Columns) sb.Append(' ').Append(row.Mean.GetValueOrDefault(col.Key).ToString(col.Format, Inv)).Append(" |");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (s.Axes.Count > 0)
        {
            sb.AppendLine("## 빌드 축 포화 점검 (3막 끝 덱)");
            sb.AppendLine();
            sb.AppendLine("주빌드가 필요로 하는 축(needs.tag)의 생산 카드 수와 소비 카드 수. 포화 = 생산 > 비율 × max(1, 소비)라서 그 축 카드의 시너지가 깎이는 덱 비율, 계수 = 깎인 뒤 남는 시너지 비율 평균.");
            sb.AppendLine();
            sb.AppendLine("| 빌드 | 축 | 출처 | n | 생산 | 소비 | 포화 덱 | 계수 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var a in s.Axes.OrderBy(a => a.Build, StringComparer.Ordinal).ThenBy(a => a.Axis, StringComparer.Ordinal).ThenBy(a => Order(a.Source)))
            {
                sb.AppendLine($"| {a.Build} | {a.Axis} | {a.Source} | {a.Decks} | {a.Produced:0.0} | {a.Consumed:0.0} | {a.OverShare * 100:0} % | {a.MeanFactor:0.00} |");
            }
            sb.AppendLine();
        }

        if (s.Leaks.Count > 0)
        {
            sb.AppendLine("## 빌드 이탈 진단 (engine 시뮬레이션)");
            sb.AppendLine();
            sb.AppendLine("대상 화면 = 주빌드가 자리 잡은 덱(코어 확보, 코어 목록이 없으면 commit ≥ 0.4)에 주빌드 목록 카드가 나온 화면. 이탈 = 엔진이 빌드 밖 카드를 추천.");
            sb.AppendLine("구성 요소 차 = 이탈한 화면에서 (추천 카드 − 가장 높은 빌드 카드)의 평균. 양수가 이탈을 만든 요소입니다.");
            sb.AppendLine();
            var keys = s.Leaks.SelectMany(l => l.MeanDiff.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
            sb.Append("| 캐릭터 | 대상 화면 | 이탈 | 스킵 | 이탈 중 드로우 | 이탈 중 테마 | 코어 있는 빌드 이탈 | 코어 없는 빌드 이탈 |");
            foreach (var k in keys) sb.Append(' ').Append(k).Append(" |");
            sb.AppendLine();
            sb.Append("|---|---|---|---|---|---|---|---|");
            foreach (var _ in keys) sb.Append("---|");
            sb.AppendLine();
            foreach (var l in s.Leaks)
            {
                sb.Append($"| {Program.Short(l.Character)} | {l.Eligible} | {Pct(l.Leaked, l.Eligible)} | {Pct(l.Skipped, l.Eligible)} | {Pct(l.LeakedToDraw, l.Leaked)} | {Pct(l.LeakedToOnTheme, l.Leaked)} | {Pct(l.Leaked - l.LeakedCoreless, l.Eligible - l.EligibleCoreless)} ({l.Eligible - l.EligibleCoreless}) | {Pct(l.LeakedCoreless, l.EligibleCoreless)} ({l.EligibleCoreless}) |");
                foreach (var k in keys) sb.Append(' ').Append(l.MeanDiff.GetValueOrDefault(k).ToString("+0.0;-0.0;0", Inv)).Append(" |");
                sb.AppendLine();
            }
            sb.AppendLine();
            if (s.LeakExamples.Count > 0)
            {
                sb.AppendLine("예시");
                sb.AppendLine();
                foreach (var line in s.LeakExamples) sb.AppendLine($"- {line}");
                sb.AppendLine();
            }
        }
    }

    private static void DiffTable(StringBuilder sb, List<(string Character, int N, Dictionary<string, double> Diff)> rows)
    {
        var keys = rows.SelectMany(r => r.Diff.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
        sb.Append("| 캐릭터 | 화면 |");
        foreach (var k in keys) sb.Append(' ').Append(k).Append(" |");
        sb.AppendLine();
        sb.Append("|---|---|");
        foreach (var _ in keys) sb.Append("---|");
        sb.AppendLine();
        foreach (var r in rows)
        {
            sb.Append($"| {Program.Short(r.Character)} | {r.N} |");
            foreach (var k in keys) sb.Append(' ').Append(r.Diff.GetValueOrDefault(k).ToString("+0.0;-0.0;0", Inv)).Append(" |");
            sb.AppendLine();
        }
        var total = rows.Sum(r => r.N);
        if (total > 0)
        {
            sb.Append($"| 전체 | {total} |");
            foreach (var k in keys) sb.Append(' ').Append((rows.Sum(r => r.Diff.GetValueOrDefault(k) * r.N) / total).ToString("+0.0;-0.0;0", Inv)).Append(" |");
            sb.AppendLine();
        }
        sb.AppendLine();
    }

    private static string Pct(int a, int b) => b == 0 ? "-" : ((double)a / b).ToString("P0", Inv);

    private static int Order(string source) => source switch { "engine" => 0, "tier" => 1, "ranker" => 2, _ => 3 };
}
