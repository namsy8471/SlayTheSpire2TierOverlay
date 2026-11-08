using System.Globalization;
using System.Text;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Tools.RunHistoryAnalyzer;

/// <summary>
/// "Does a forecast of X% come true X% of the time?" Replays each run to a few checkpoints, forecasts every archetype
/// that is not complete yet, and compares with the archetype's status in the run's final deck.
/// </summary>
/// <remarks>
/// Two modes per checkpoint:
/// <list type="bullet">
/// <item><b>A (real future)</b>: the forecaster gets the run's actual remaining rewards (from the node types that followed),
/// so it only has to predict which cards show up and which the player takes. Tests the card model.</item>
/// <item><b>B (live-like)</b>: the schedule the overlay would use live (typical acts to the end of act 3). The outcome is
/// still the final deck, so early deaths count as "did not complete". Tests what the panel actually shows.</item>
/// </list>
/// </remarks>
internal static class Calibration
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly double[] BinEdges = [0, 0.05, 0.15, 0.30, 0.50, 0.70, 1.0001];

    private sealed record Sample(string Run, string Checkpoint, string Archetype, double P, bool Outcome, bool ReachedFinalBoss);

    public static string Run(IReadOnlyList<(RunHistory Run, string File, long? Player)> runs, DataPack pack, ForecastCardPool pool, ForecastOptions options)
    {
        var forecaster = new ArchetypeForecaster(pack, pool, cacheCapacity: 0);
        var a = new List<Sample>();
        var b = new List<Sample>();
        var timings = new List<double>();
        var skipped = 0;

        foreach (var (run, file, player) in runs)
        {
            var me = LocalPlayer.Resolve(run, player);
            if (!pack.ArchetypesFor(me.Character).Any() || run.Nodes.Count < 3)
            {
                skipped++;
                continue;
            }

            DeckTimeline timeline;
            try
            {
                timeline = DeckTimeline.Build(run, me.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                skipped++;
                continue;
            }

            var final = new RunState
            {
                Character = me.Character,
                Act = run.Nodes[^1].Act,
                Floor = run.Nodes.Count,
                Deck = me.Deck,
                Relics = me.Relics.Select(r => r.Id).ToArray(),
                IsMultiplayer = run.IsMultiplayer,
            };
            var outcome = ArchetypeDetector.Detect(final, pack).ToDictionary(s => s.Archetype.Id, s => s.IsCommitted, StringComparer.Ordinal);

            foreach (var (index, label) in Checkpoints(run))
            {
                var state = timeline.StateBefore(index) with { IsMultiplayer = run.IsMultiplayer };
                var real = forecaster.Forecast(new ForecastRequest { State = state, FixedSchedule = RealFuture(run, index) }, options);
                var live = forecaster.Forecast(new ForecastRequest { State = state }, options);
                timings.Add(live.ElapsedMs);
                var name = Path.GetFileNameWithoutExtension(file);
                var reached = run.Win || (run.Nodes[^1].Act >= 3 && DecisionExtractor.ContextOf(run.Nodes[^1]) == OfferContext.BossReward);
                foreach (var f in real.Archetypes.Where(f => !f.IsCompleteNow))
                {
                    a.Add(new Sample(name, label, f.Archetype.Id, f.EndOfRun.PComplete, outcome.GetValueOrDefault(f.Archetype.Id), reached));
                }
                foreach (var f in live.Archetypes.Where(f => !f.IsCompleteNow))
                {
                    b.Add(new Sample(name, label, f.Archetype.Id, f.EndOfRun.PComplete, outcome.GetValueOrDefault(f.Archetype.Id), reached));
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# 빌드 완성 확률 보정 (calibration)");
        sb.AppendLine();
        sb.AppendLine(Inv, $"- 런 {runs.Count}개 (건너뜀 {skipped}), 시뮬레이션 {options.Simulations}회/예측, 정책 {options.Policy}, 선택 잡음 {options.PickNoise:0.#}");
        sb.AppendLine(Inv, $"- 예측 1회 시간: 평균 {timings.DefaultIfEmpty(0).Average():0.0} ms, 최대 {timings.DefaultIfEmpty(0).Max():0.0} ms");
        sb.AppendLine("- 대상: 체크포인트(1막 6층, 2막 시작, 3막 시작)에서 아직 완성(몰입 ≥ 기준)이 아닌 아키타입. 결과 = 마지막 덱에서 완성 여부.");
        sb.AppendLine("- 표본은 서로 독립이 아닙니다 (같은 런의 여러 아키타입·체크포인트). 대략적인 경향만 보세요.");
        Section(sb, "A. 실제 남은 보상 일정 (카드 모델만 검증)", a);
        Section(sb, "B. 라이브와 같은 일정 (3막 끝까지 전형적 맵, 사망 포함)", b);
        Section(sb, "B'. 라이브와 같은 일정, 3막 보스까지 간 런만 (패널 문구 \"살아남는다면\"과 같은 조건)", b.Where(x => x.ReachedFinalBoss).ToList());
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, List<Sample> samples)
    {
        sb.AppendLine();
        sb.AppendLine("## " + title);
        sb.AppendLine();
        if (samples.Count == 0)
        {
            sb.AppendLine("표본 없음");
            return;
        }

        var baseRate = samples.Average(s => s.Outcome ? 1.0 : 0.0);
        var brier = samples.Average(s => Sq(s.P - (s.Outcome ? 1 : 0)));
        var brierBase = samples.Average(s => Sq(baseRate - (s.Outcome ? 1 : 0)));
        sb.AppendLine(Inv, $"- 표본 {samples.Count}개, 실제 완성 {samples.Count(s => s.Outcome)}개 (기저율 {baseRate:P1}), 평균 예측 {samples.Average(s => s.P):P1}");
        sb.AppendLine(Inv, $"- Brier {brier:0.000} (기저율만 쓰면 {brierBase:0.000}, skill {(brierBase > 0 ? 1 - brier / brierBase : 0):+0.00;-0.00})");
        sb.AppendLine();
        sb.AppendLine("| 예측 구간 | 표본 | 평균 예측 | 실제 완성률 |");
        sb.AppendLine("|---|---|---|---|");
        for (var i = 0; i < BinEdges.Length - 1; i++)
        {
            var lo = BinEdges[i];
            var hi = BinEdges[i + 1];
            var bin = samples.Where(s => s.P >= lo && s.P < hi).ToList();
            if (bin.Count == 0) continue;
            sb.AppendLine(Inv, $"| {lo * 100:0}–{Math.Min(100, hi * 100):0}% | {bin.Count} | {bin.Average(s => s.P):P1} | {bin.Average(s => s.Outcome ? 1.0 : 0.0):P1} |");
        }

        sb.AppendLine();
        sb.AppendLine("| 체크포인트 | 표본 | 평균 예측 | 실제 완성률 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var g in samples.GroupBy(s => s.Checkpoint).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(Inv, $"| {g.Key} | {g.Count()} | {g.Average(s => s.P):P1} | {g.Average(s => s.Outcome ? 1.0 : 0.0):P1} |");
        }
    }

    private static double Sq(double x) => x * x;

    /// <summary>Act 1 floor 6, and the first node of acts 2 and 3 (when the run got there).</summary>
    internal static IEnumerable<(int Index, string Label)> Checkpoints(RunHistory run)
    {
        var early = -1;
        for (var i = 0; i < run.Nodes.Count; i++)
        {
            if (run.Nodes[i].Act == 1 && run.Nodes[i].IndexInAct >= 5)
            {
                early = i;
                break;
            }
        }
        if (early >= 0) yield return (early, "1막 6층");
        for (var act = 2; act <= 3; act++)
        {
            var first = -1;
            for (var i = 0; i < run.Nodes.Count; i++)
            {
                if (run.Nodes[i].Act == act)
                {
                    first = i;
                    break;
                }
            }
            if (first >= 0 && first < run.Nodes.Count - 1) yield return (first, $"{act}막 시작");
        }
    }

    /// <summary>
    /// The rewards the run really had from node <paramref name="from"/> on: monster/elite/boss rooms and shops.
    /// The last node of a lost or abandoned run gave nothing (the player died or quit there).
    /// </summary>
    internal static IReadOnlyList<RewardEvent> RealFuture(RunHistory run, int from)
    {
        var events = new List<RewardEvent>();
        for (var j = from; j < run.Nodes.Count; j++)
        {
            var node = run.Nodes[j];
            var survived = j < run.Nodes.Count - 1 || run.Win;
            switch (DecisionExtractor.ContextOf(node))
            {
                case OfferContext.CardReward when survived: events.Add(new RewardEvent(RewardKind.Normal, node.Act)); break;
                case OfferContext.EliteReward when survived: events.Add(new RewardEvent(RewardKind.Elite, node.Act)); break;
                case OfferContext.BossReward:
                    events.Add(new RewardEvent(node.Act >= 3 || !survived ? RewardKind.FinalBoss : RewardKind.Boss, node.Act));
                    break;
                case OfferContext.Shop: events.Add(new RewardEvent(RewardKind.Shop, node.Act)); break;
            }
        }
        return events;
    }
}
