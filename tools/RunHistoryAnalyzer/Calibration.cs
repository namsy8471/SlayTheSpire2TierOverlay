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
/// that is not complete yet with the live schedule, and compares with the archetype's status in the run's final deck.
/// </summary>
/// <remarks>
/// Everything fitted from history is fitted <b>leave-one-run-out</b>: the event-card rate, the per-act death rates and
/// the isotonic calibration curves used for run r come only from the other runs, so the scores are out-of-sample.
/// <list type="bullet">
/// <item><b>Conditional</b> ("살아남는다면"): only runs that reached the act 3 boss; forecast = EndOfRun.</item>
/// <item><b>Unconditional</b> ("사망 포함"): all runs; forecast = EndOfRunAll (simulated deaths).</item>
/// </list>
/// "Before" is the previous model: no event cards, nobody dies, no calibration curve.
/// </remarks>
internal static class Calibration
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly double[] BinEdges = [0, 0.05, 0.15, 0.30, 0.50, 0.70, 1.0001];

    private sealed record Sample(int Run, double Completion, double CondBefore, double AllBefore, double CondRaw, double AllRaw, bool Outcome, bool Survived)
    {
        public double CondCal { get; set; }
        public double AllCal { get; set; }
        public double CondPlatt { get; set; }
        public double AllPlatt { get; set; }
        public double CondPlattC { get; set; }
        public double AllPlattC { get; set; }
    }

    public static string Run(IReadOnlyList<(RunHistory Run, string File, long? Player)> runs, DataPack pack, ForecastCardPool pool, ForecastOptions options, bool emit)
    {
        var raw = new ArchetypeForecaster(pack, pool, cacheCapacity: 0, calibration: ForecastCalibration.Identity);
        var before = options with { EventCardChance = 0, ActDeathChance = null };
        var samples = new List<Sample>();
        var timings = new List<double>();
        var skipped = 0;
        var fitted = new List<FittedRates>();

        for (var r = 0; r < runs.Count; r++)
        {
            var (run, _, player) = runs[r];
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

            // Rates from every OTHER run (leave-one-run-out).
            var held = r;
            var rates = ForecastFit.Measure(runs.Where((_, i) => i != held).Select(x => (x.Run, x.Player)), pool);
            fitted.Add(rates);
            var after = options.Apply(rates);

            var final = new RunState
            {
                Character = me.Character,
                Act = run.Nodes[^1].Act,
                Floor = run.Nodes.Count,
                Deck = me.Deck,
                Relics = me.Relics.Select(x => x.Id).ToArray(),
                IsMultiplayer = run.IsMultiplayer,
            };
            var outcome = ArchetypeDetector.Detect(final, pack).ToDictionary(s => s.Archetype.Id, s => s.IsCommitted, StringComparer.Ordinal);
            var survived = run.Win || (run.Nodes[^1].Act >= 3 && DecisionExtractor.ContextOf(run.Nodes[^1]) == OfferContext.BossReward);

            foreach (var (index, _) in Checkpoints(run))
            {
                var state = timeline.StateBefore(index) with { IsMultiplayer = run.IsMultiplayer };
                var old = raw.Forecast(new ForecastRequest { State = state }, before).Archetypes.ToDictionary(f => f.Archetype.Id, StringComparer.Ordinal);
                var now = raw.Forecast(new ForecastRequest { State = state }, after);
                timings.Add(now.ElapsedMs);
                foreach (var f in now.Archetypes.Where(f => !f.IsCompleteNow))
                {
                    var o = old[f.Archetype.Id];
                    samples.Add(new Sample(r, f.CurrentCompletionPercent / 100.0, o.EndOfRun.PComplete, o.EndOfRunAll.PComplete, f.EndOfRun.PComplete, f.EndOfRunAll.PComplete,
                        outcome.GetValueOrDefault(f.Archetype.Id), survived));
                }
            }
        }

        // Leave-one-run-out isotonic curves: run r is mapped by a curve that never saw run r.
        foreach (var g in samples.GroupBy(s => s.Run))
        {
            var others = samples.Where(s => s.Run != g.Key).ToList();
            var cond = IsotonicMap.Fit(others.Where(s => s.Survived).Select(s => (s.CondRaw, s.Outcome)));
            var all = IsotonicMap.Fit(others.Select(s => (s.AllRaw, s.Outcome)));
            var condP = PlattMap.Fit(others.Where(s => s.Survived).Select(s => (s.CondRaw, s.Completion, s.Outcome)), useCompletion: false);
            var allP = PlattMap.Fit(others.Select(s => (s.AllRaw, s.Completion, s.Outcome)), useCompletion: false);
            var condC = PlattMap.Fit(others.Where(s => s.Survived).Select(s => (s.CondRaw, s.Completion, s.Outcome)));
            var allC = PlattMap.Fit(others.Select(s => (s.AllRaw, s.Completion, s.Outcome)));
            foreach (var s in g)
            {
                s.CondCal = cond.Apply(s.CondRaw, s.Completion);
                s.AllCal = all.Apply(s.AllRaw, s.Completion);
                s.CondPlatt = condP.Apply(s.CondRaw, s.Completion);
                s.AllPlatt = allP.Apply(s.AllRaw, s.Completion);
                s.CondPlattC = condC.Apply(s.CondRaw, s.Completion);
                s.AllPlattC = allC.Apply(s.AllRaw, s.Completion);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# 빌드 완성 확률 보정 (calibration, leave-one-run-out)");
        sb.AppendLine();
        sb.AppendLine(Inv, $"- 런 {runs.Count}개 (건너뜀 {skipped}), 시뮬레이션 {options.Simulations}회/예측, 정책 {options.Policy}, 선택 잡음 {options.PickNoise:0.#}");
        sb.AppendLine(Inv, $"- 예측 1회 시간 (새 모델): 평균 {timings.DefaultIfEmpty(0).Average():0.0} ms, 최대 {timings.DefaultIfEmpty(0).Max():0.0} ms");
        if (fitted.Count > 0)
        {
            sb.AppendLine(Inv, $"- LORO로 맞춘 이벤트 카드 비율: {fitted.Min(f => f.EventCardChance):0.000}–{fitted.Max(f => f.EventCardChance):0.000} / \"?\" 이벤트, " +
                               $"막별 종료율: {string.Join(" / ", Enumerable.Range(0, 3).Select(k => $"{fitted.Average(f => f.ActDeathChance[k]):P0}"))}");
        }
        sb.AppendLine("- 대상: 체크포인트(1막 6층, 2막 시작, 3막 시작)에서 아직 완성(몰입 ≥ 기준)이 아닌 아키타입. 결과 = 마지막 덱에서 완성 여부.");
        sb.AppendLine("- 이벤트 카드 비율·막별 사망률·보정 곡선은 모두 해당 런을 뺀 나머지 런으로 맞춘 값입니다 (표본 밖 평가).");
        sb.AppendLine("- 표본은 서로 독립이 아닙니다 (같은 런의 여러 아키타입·체크포인트). 대략적인 경향만 보세요.");

        var survivors = samples.Where(s => s.Survived).ToList();
        sb.AppendLine();
        sb.AppendLine("## 조건부: 3막 보스까지 간 런만 (\"살아남는다면\")");
        Section(sb, "이전 모델 (이벤트 카드 없음, 보정 없음)", survivors, s => s.CondBefore);
        Section(sb, "새 모델, 보정 전 (이벤트 카드 포함)", survivors, s => s.CondRaw);
        Section(sb, "새 모델 + 등위(isotonic) 보정", survivors, s => s.CondCal);
        Section(sb, "새 모델 + Platt 보정 (raw만)", survivors, s => s.CondPlatt);
        Section(sb, "새 모델 + Platt 보정 (raw + 현재 완성도) ← 패널에 표시되는 값", survivors, s => s.CondPlattC);
        sb.AppendLine();
        sb.AppendLine("## 무조건부: 모든 런 (\"사망 포함\")");
        Section(sb, "이전 모델 (사망 없음, 보정 없음)", samples, s => s.AllBefore);
        Section(sb, "새 모델, 보정 전 (막별 사망률 시뮬레이션)", samples, s => s.AllRaw);
        Section(sb, "새 모델 + 등위(isotonic) 보정", samples, s => s.AllCal);
        Section(sb, "새 모델 + Platt 보정 (raw만)", samples, s => s.AllPlatt);
        Section(sb, "새 모델 + Platt 보정 (raw + 현재 완성도) ← 패널에 표시되는 값", samples, s => s.AllPlattC);

        if (emit)
        {
            var rates = ForecastFit.Measure(runs.Select(x => (x.Run, x.Player)), pool);
            sb.AppendLine();
            sb.AppendLine("## 기본값으로 붙여 넣을 값 (모든 런으로 적합)");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(Inv, $"// event visits {rates.EventVisits}, event cards {rates.EventCards}, reached {string.Join("/", rates.Reached)}");
            sb.AppendLine(Inv, $"public const double DefaultEventCardChance = {rates.EventCardChance:0.###};");
            sb.AppendLine(Inv, $"public static readonly IReadOnlyList<double> DefaultActDeathChance = [{string.Join(", ", rates.ActDeathChance.Select(h => h.ToString("0.###", Inv)))}];");
            sb.AppendLine("IfSurvive: " + PlattMap.Fit(survivors.Select(s => (s.CondRaw, s.Completion, s.Outcome))).ToCSharp() + ",");
            sb.AppendLine("All: " + PlattMap.Fit(samples.Select(s => (s.AllRaw, s.Completion, s.Outcome))).ToCSharp() + ");");
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, List<Sample> samples, Func<Sample, double> p)
    {
        sb.AppendLine();
        sb.AppendLine("### " + title);
        sb.AppendLine();
        if (samples.Count == 0)
        {
            sb.AppendLine("표본 없음");
            return;
        }

        var baseRate = samples.Average(s => s.Outcome ? 1.0 : 0.0);
        var brier = samples.Average(s => Sq(p(s) - (s.Outcome ? 1 : 0)));
        var brierBase = samples.Average(s => Sq(baseRate - (s.Outcome ? 1 : 0)));
        sb.AppendLine(Inv, $"- 표본 {samples.Count}개, 실제 완성 {samples.Count(s => s.Outcome)}개 (기저율 {baseRate:P1}), 평균 예측 {samples.Average(p):P1}");
        var (lo90, hi90) = BootstrapSkill(samples, p);
        sb.AppendLine(Inv, $"- Brier {brier:0.000} (기저율만 쓰면 {brierBase:0.000}) → **skill {(brierBase > 0 ? 1 - brier / brierBase : 0):+0.00;-0.00}** (런 단위 부트스트랩 90% 구간 {lo90:+0.00;-0.00} ~ {hi90:+0.00;-0.00})");
        sb.AppendLine();
        sb.AppendLine("| 예측 구간 | 표본 | 평균 예측 | 실제 완성률 |");
        sb.AppendLine("|---|---|---|---|");
        for (var i = 0; i < BinEdges.Length - 1; i++)
        {
            var lo = BinEdges[i];
            var hi = BinEdges[i + 1];
            var bin = samples.Where(s => p(s) >= lo && p(s) < hi).ToList();
            if (bin.Count == 0) continue;
            sb.AppendLine(Inv, $"| {lo * 100:0}–{Math.Min(100, hi * 100):0}% | {bin.Count} | {bin.Average(p):P1} | {bin.Average(s => s.Outcome ? 1.0 : 0.0):P1} |");
        }
    }

    private static double Sq(double x) => x * x;

    /// <summary>
    /// Skill's 5th and 95th percentile when whole runs are resampled with replacement (samples of one run move
    /// together, because they aren't independent). Fixed seed so reports are reproducible.
    /// </summary>
    private static (double Lo, double Hi) BootstrapSkill(List<Sample> samples, Func<Sample, double> p, int rounds = 1000)
    {
        var byRun = samples.GroupBy(s => s.Run).Select(g => g.Select(s => (P: p(s), Y: s.Outcome ? 1.0 : 0.0)).ToArray()).ToArray();
        var rng = new Random(20261108);
        var skills = new List<double>(rounds);
        for (var b = 0; b < rounds; b++)
        {
            double sumY = 0, sumSq = 0, sumYY = 0;
            var n = 0;
            for (var k = 0; k < byRun.Length; k++)
            {
                foreach (var (pp, y) in byRun[rng.Next(byRun.Length)])
                {
                    sumY += y;
                    sumYY += y * y;
                    sumSq += Sq(pp - y);
                    n++;
                }
            }
            if (n == 0) continue;
            var mean = sumY / n;
            var brierBase = sumYY / n - mean * mean; // = mean of (mean - y)^2
            if (brierBase > 0) skills.Add(1 - sumSq / n / brierBase);
        }
        if (skills.Count == 0) return (0, 0);
        skills.Sort();
        return (skills[(int)(0.05 * (skills.Count - 1))], skills[(int)(0.95 * (skills.Count - 1))]);
    }

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
}
