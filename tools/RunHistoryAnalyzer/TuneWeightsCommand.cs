using System.Globalization;
using System.Text;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Replay.Validation;

namespace Sts2Overlay.Tools.RunHistoryAnalyzer;

/// <summary>
/// <c>tune-weights</c>: fold-aware search of the card-scoring weights (<see cref="ScoringKnobs"/>).
/// The search is run inside every leave-one-game-out fold and once on the tuning set (tested on the untouched runs);
/// a new weight set is recommended only when both beat the current weights and the tier-only baseline.
/// </summary>
internal static class TuneWeightsCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Run(IReadOnlyList<(string Path, RunHistory Run)> files, DataPack pack, long? user, long? tunedUntil, bool allPlayers, IReadOnlyList<double> lambdas, bool noZero = false)
    {
        var me = user ?? files.Select(f => ReplayStreams.SteamIdFromPath(f.Path)).FirstOrDefault(id => id is not null) ?? LocalPlayer.FixtureSelfId;
        var streams = ReplayStreams.Build(files, allPlayers, me);
        bool IsTuning(ReplayStream s) => s.PersonId == me && tunedUntil is { } t && s.StartTime <= t;

        var cv = new CrossValidation(pack);
        var universe = cv.DecisionsOf(streams);
        var tierOnly = new CachedRecommender(new TierOnlyRecommender(pack));
        var games = Folds.LeaveOneGameOut(streams);
        var tuning = cv.DecisionsOf(streams.Where(IsTuning));
        var holdout = cv.DecisionsOf(streams.Where(s => !IsTuning(s)));

        var sb = new StringBuilder();
        sb.AppendLine("# 점수 가중치 탐색 (fold-aware)");
        sb.AppendLine();
        sb.AppendLine("> 일치율은 대리 지표입니다. 사용자의 선택은 정답이 아닙니다. 목표 = 평균(1위 일치 + 완전 일치) − λ × (현재 값에서 벗어난 격자 칸 수).");
        sb.AppendLine();
        sb.AppendLine(Inv, $"- 결정 묶음 {streams.Count}개, 게임 {games.Count}판, 결정 {universe.Count}회. 튜닝 집합 {tuning.Count}회, 홀드아웃 {holdout.Count}회.");
        sb.AppendLine("- 탐색 손잡이(격자 5칸, 가운데가 현재 값): " + string.Join(", ", ScoringKnobs.Default(pack.Weights, noZero ? ScoringKnobs.NoZeroFactors : null).Select(k => $"{k.Name} [{string.Join("/", k.Values.Select(v => v.ToString("0.##", Inv)))}]")));
        sb.AppendLine();

        var verdicts = new List<string>();
        foreach (var lambda in lambdas)
        {
            var search = new WeightSearch(pack, ScoringKnobs.Default(pack.Weights, noZero ? ScoringKnobs.NoZeroFactors : null), universe, lambda);
            var current = search.Current;

            // ---- leave-one-game-out: search on the other games, score the left-out game ----
            AgreementMetrics logoCur = AgreementMetrics.Empty, logoNew = AgreementMetrics.Empty, logoTier = AgreementMetrics.Empty;
            var moves = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var fold in games)
            {
                var train = cv.DecisionsOf(fold.Train);
                var test = cv.DecisionsOf(fold.Test);
                var fit = search.Fit(train);
                logoCur = logoCur.Add(search.Evaluate(current, test));
                logoNew = logoNew.Add(search.Evaluate(fit, test));
                logoTier = logoTier.Add(AgreementMetrics.Evaluate(test, tierOnly));
                foreach (var (k, i) in search.Knobs.Select((k, i) => (k, i)).Where(x => fit[x.i] != x.k.CurrentIndex))
                {
                    var label = string.Create(Inv, $"{k.Name}→{k.Values[fit[i]]:0.##}");
                    moves[label] = moves.GetValueOrDefault(label) + 1;
                }
            }

            // ---- tuning set → untouched runs ----
            var fitTuning = tuning.Count > 0 ? search.Fit(tuning) : current;
            var hoCur = search.Evaluate(current, holdout);
            var hoNew = search.Evaluate(fitTuning, holdout);
            var hoTier = AgreementMetrics.Evaluate(holdout, tierOnly);

            sb.AppendLine(Inv, $"## λ = {lambda:0.####}");
            sb.AppendLine();
            sb.AppendLine("| 평가 | 가중치 | 결정 | 1위 일치 | 2위 안 | 평균 순위 | 스킵 일치 | 완전 일치 | 목표(1위+완전) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            Row(sb, $"LOGO {games.Count} fold 시험", "현재", logoCur);
            Row(sb, $"LOGO {games.Count} fold 시험", "fold마다 탐색", logoNew);
            Row(sb, $"LOGO {games.Count} fold 시험", "티어만", logoTier);
            if (tuning.Count > 0)
            {
                Row(sb, "튜닝 집합 (표본 내)", "현재", search.Evaluate(current, tuning));
                Row(sb, "튜닝 집합 (표본 내)", "튜닝 집합으로 탐색", search.Evaluate(fitTuning, tuning));
                Row(sb, "홀드아웃", "현재", hoCur);
                Row(sb, "홀드아웃", "튜닝 집합으로 탐색", hoNew);
                Row(sb, "홀드아웃", "티어만", hoTier);
            }

            sb.AppendLine();
            sb.AppendLine(Inv, $"- 튜닝 집합으로 찾은 가중치: {search.Describe(fitTuning)}");
            sb.AppendLine("- LOGO fold에서 자주 고른 이동: " + (moves.Count == 0 ? "없음" : string.Join(", ", moves.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} ({kv.Value}/{games.Count})"))));
            var full = search.Fit(universe);
            sb.AppendLine(Inv, $"- 전체 결정으로 찾은 가중치(참고): {search.Describe(full)}");

            var accept = Primary(logoNew) > Primary(logoCur) && Primary(logoNew) > Primary(logoTier)
                         && Primary(hoNew) > Primary(hoCur) && Primary(hoNew) > Primary(hoTier);
            var verdict = string.Create(Inv, $"λ={lambda:0.####}: LOGO {Primary(logoCur):0.000}→{Primary(logoNew):0.000} (티어만 {Primary(logoTier):0.000}), 홀드아웃 {Primary(hoCur):0.000}→{Primary(hoNew):0.000} (티어만 {Primary(hoTier):0.000}) ⇒ {(accept ? "채택 가능" : "기각")}");
            verdicts.Add(verdict);
            sb.AppendLine(Inv, $"- 판정: {verdict}");
            sb.AppendLine(Inv, $"- 엔진을 돌린 후보 수: {search.EvaluatedCandidates}");
            sb.AppendLine();
            sb.AppendLine("`weights.json` 덮어쓰기 (튜닝 집합으로 찾은 값):");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(OverrideJson(search, fitTuning));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## 요약");
        sb.AppendLine();
        foreach (var v in verdicts) sb.AppendLine("- " + v);
        return sb.ToString();
    }

    /// <summary>The acceptance score: top-1 hits + exact hits per decision.</summary>
    private static double Primary(AgreementMetrics m) => m.Decisions == 0 ? 0 : (m.Top1Hits + m.ExactHits) / m.Decisions;

    private static void Row(StringBuilder sb, string set, string weights, AgreementMetrics m)
        => sb.AppendLine(Inv, $"| {set} | {weights} | {m.Decisions} | {Pct(m.Top1)} | {Pct(m.Top2)} | {m.MeanRank:0.00} | {Pct(m.SkipAgreement)} | {Pct(m.Exact)} | {Primary(m):0.000} |");

    /// <summary>Partial weights JSON with only the keys the candidate changes.</summary>
    private static string OverrideJson(WeightSearch search, int[] candidate)
    {
        var w = search.Build(candidate);
        var lines = new List<string>();
        var skip = new List<string>();
        foreach (var (k, i) in search.Knobs.Select((k, i) => (k, i)).Where(x => candidate[x.i] != x.k.CurrentIndex))
        {
            switch (k.Name)
            {
                case "wNeed": lines.Add(Kv("wNeed", w.WNeed)); break;
                case "lambdaDilution": lines.Add(Kv("lambdaDilution", w.LambdaDilution)); break;
                case "rhoDuplicate": lines.Add(Kv("rhoDuplicate", w.RhoDuplicate)); break;
                case "offBuildPenalty": lines.Add(Kv("offBuildPenalty", w.OffBuildPenalty)); break;
                case "wArch": lines.Add(Kv("wArch", w.WArch)); break;
                case "requiresPenalty": lines.Add(Kv("requiresPenalty", w.RequiresPenalty)); break;
                case "skip.lambdaSize": skip.Add(Kv("lambdaSize", w.Skip.LambdaSize)); break;
                case "skip.lambdaCommit": skip.Add(Kv("lambdaCommit", w.Skip.LambdaCommit)); break;
                case var n when n.StartsWith("skip.t0", StringComparison.Ordinal):
                    skip.Add("\"t0\": {" + string.Join(", ", w.Skip.T0.OrderBy(kv => kv.Key).Select(kv => $"\"{kv.Key}\": {Num(kv.Value)}")) + "}");
                    break;
                case var n when n.StartsWith("tierSpread", StringComparison.Ordinal):
                    lines.Add("\"tierValue\": {" + string.Join(", ", w.TierValue.OrderBy(kv => kv.Key).Select(kv => $"\"{kv.Key}\": {Num(kv.Value)}")) + "}");
                    break;
            }
        }

        if (skip.Count > 0) lines.Add("\"skip\": {" + string.Join(", ", skip) + "}");
        return "{\n" + string.Join(",\n", lines.Select(l => "  " + l)) + "\n}";

        static string Kv(string key, double v) => $"\"{key}\": {Num(v)}";
    }

    private static string Num(double v) => v.ToString("0.###", Inv);

    private static string Pct(double v) => (v * 100).ToString("0.0", Inv) + "%";
}
