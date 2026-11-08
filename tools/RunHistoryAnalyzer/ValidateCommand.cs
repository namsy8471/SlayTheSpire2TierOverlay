using System.Globalization;
using System.Text;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Replay.Validation;

namespace Sts2Overlay.Tools.RunHistoryAnalyzer;

/// <summary>Settings of the <c>validate</c> command (see <see cref="ValidateCommand"/>).</summary>
internal sealed record ValidateOptions
{
    public required DataPack Pack { get; init; }
    public string PackName { get; init; } = "팩";
    public DataPack? ComparePack { get; init; }
    public string ComparePackName { get; init; } = "비교 팩";

    /// <summary>Base pack for cross-validating the calibrator. Must not contain tuning learned from the test runs.</summary>
    public DataPack? CvPack { get; init; }

    public string CvPackName { get; init; } = "팩";
    public long? User { get; init; }

    /// <summary>The user's runs started at or before this unix time were used to tune <see cref="Pack"/> by hand.</summary>
    public long? TunedUntil { get; init; }

    public bool AllPlayers { get; init; }
    public int K { get; init; } = 5;
    public int? ChronoTestGames { get; init; }
    public TierShiftOptions Calibration { get; init; } = new();
}

/// <summary>
/// <c>validate</c>: holdout validation of a data pack against recorded picks.
/// 1) fixed pack on the tuning runs vs runs never used for tuning (the honest holdout),
/// 2) fold-aware cross-validation of the automated tier calibrator (chronological, leave-one-game-out, k-fold).
/// Writes Korean Markdown with numbers and internal ids only.
/// </summary>
internal static class ValidateCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Run(IReadOnlyList<(string Path, RunHistory Run)> files, ValidateOptions o)
    {
        var user = o.User ?? files.Select(f => ReplayStreams.SteamIdFromPath(f.Path)).FirstOrDefault(id => id is not null) ?? LocalPlayer.FixtureSelfId;
        var streams = ReplayStreams.Build(files, o.AllPlayers, user);

        var sb = new StringBuilder();
        sb.AppendLine("# 홀드아웃 검증 리포트");
        sb.AppendLine();
        sb.AppendLine("> **일치율은 대리 지표입니다.** 사용자의 선택은 정답(최선의 플레이)이 아닙니다. 같은 결정 위에서 추천기끼리 비교할 때만 의미가 있고, 100%가 목표가 아닙니다.");
        sb.AppendLine();

        // ---------- 1. data ----------
        bool IsUser(ReplayStream s) => s.PersonId == user;
        bool IsTuning(ReplayStream s) => IsUser(s) && o.TunedUntil is { } t && s.StartTime <= t;
        var cv = new CrossValidation(o.CvPack ?? o.Pack, o.Calibration);

        sb.AppendLine("## 1. 데이터");
        sb.AppendLine();
        sb.AppendLine(Inv, $"- 파일 {files.Count}개 → 결정 묶음(런×플레이어) {streams.Count}개, 게임 {Folds.GamesByTime(streams).Count}판 (같은 협동 게임이 두 프로필에 기록된 중복은 하나로 합침)");
        sb.AppendLine(Inv, $"- 비교 가능한 결정: 카드 1장을 고르거나 스킵한 보상·이벤트 선택 (상점·여러 장 선택 제외). 협동 런은 각 플레이어의 결정을 따로 셉니다.");
        sb.AppendLine(o.TunedUntil is { } tu
            ? $"- 튜닝 집합: 사용자 런 중 시작 시각 ≤ {tu} (팩을 손으로 보정할 때 리플레이한 런). 나머지는 모두 **튜닝에 쓰지 않은 런**입니다."
            : "- 튜닝 집합 지정 없음 (`--tuned-until`).");
        sb.AppendLine();
        sb.AppendLine("| 런 시작(unix) | 플레이어 | 캐릭터 | 버전 | 집합 | 결정 | 선택 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in streams)
        {
            var ds = cv.DecisionsOf(s);
            sb.AppendLine(Inv, $"| {s.StartTime} | {(IsUser(s) ? "나" : "동료")} | {Short(s.Character)} | {s.Run.BuildId} | {(IsTuning(s) ? "튜닝" : "홀드아웃")} | {ds.Count} | {ds.Count(d => !d.Skipped)} |");
        }

        sb.AppendLine();

        // ---------- 2. fixed pack ----------
        var sets = new List<(string Name, ReplayStream[] Streams)>();
        if (o.TunedUntil is not null)
        {
            sets.Add(("튜닝에 쓴 런 (표본 내)", streams.Where(IsTuning).ToArray()));
            sets.Add(("홀드아웃: 나, 튜닝 이후 새 런", streams.Where(s => IsUser(s) && !IsTuning(s)).ToArray()));
        }
        else
        {
            sets.Add(("나", streams.Where(IsUser).ToArray()));
        }

        if (streams.Any(s => !IsUser(s)))
        {
            sets.Add(("홀드아웃: 동료 (튜닝에 안 씀)", streams.Where(s => !IsUser(s)).ToArray()));
        }

        var holdout = streams.Where(s => !IsTuning(s)).ToArray();
        if (o.TunedUntil is not null) sets.Add(("홀드아웃 전체", holdout));

        var tuningDecisions = cv.DecisionsOf(streams.Where(IsTuning));
        var refSkipRate = SkipRate(tuningDecisions.Count > 0 ? tuningDecisions : cv.DecisionsOf(streams));

        var engine = new CachedRecommender(new EngineRecommender(o.Pack, o.PackName));
        var compare = o.ComparePack is null ? null : new CachedRecommender(new EngineRecommender(o.ComparePack, o.ComparePackName));
        var tierOnly = new CachedRecommender(new TierOnlyRecommender(o.Pack));

        sb.AppendLine("## 2. 고정 팩 평가 (학습 없음)");
        sb.AppendLine();
        sb.AppendLine(Inv, $"- 무작위 기준선: 제시 카드 순서를 균등 무작위로, 스킵은 확률 {Pct(refSkipRate)}(튜닝 집합의 스킵 비율)로 추천했을 때의 기댓값.");
        sb.AppendLine("- 티어만 기준선: 덱을 보지 않고 배지 티어 기본값(+강화 보너스)만으로 순위. 최고값이 그 막의 스킵 기준(`skip.t0`)보다 낮으면 스킵.");
        sb.AppendLine("- 1위/2위 일치: 카드를 고른 결정 중 고른 카드가 추천 순위 1위(2위 안)인 비율. 스킵 일치: 스킵 추천 여부 = 실제 스킵 여부. 완전 일치: 추천(카드 또는 스킵)과 실제 행동이 같음.");
        sb.AppendLine("- 동점은 동전 던지기로 셉니다(2장 동점 1위면 0.5회). `.run` 파일은 고른 카드를 항상 첫 번째로 적기 때문에, 제시 순서로 동점을 깨면 고른 카드가 공짜로 1위가 됩니다(특히 동점이 많은 티어만 기준선).");
        sb.AppendLine();

        foreach (var (name, set) in sets)
        {
            var ds = cv.DecisionsOf(set);
            if (ds.Count == 0) continue;
            sb.AppendLine(Inv, $"### {name}: 런 {set.Length}개, 결정 {ds.Count}회 (선택 {ds.Count(d => !d.Skipped)}회)");
            sb.AppendLine();
            MetricsHeader(sb, "추천기");
            MetricsRow(sb, engine.Name, AgreementMetrics.Evaluate(ds, engine));
            if (compare is not null) MetricsRow(sb, compare.Name, AgreementMetrics.Evaluate(ds, compare));
            MetricsRow(sb, tierOnly.Name, AgreementMetrics.Evaluate(ds, tierOnly));
            MetricsRow(sb, "무작위", AgreementMetrics.EvaluateRandom(ds, refSkipRate));
            sb.AppendLine();
        }

        foreach (var (name, set) in sets.Where(s => s.Name.StartsWith("튜닝", StringComparison.Ordinal) || s.Name == "홀드아웃 전체" || s.Name == "나"))
        {
            var ds = cv.DecisionsOf(set);
            if (ds.Count == 0) continue;
            sb.AppendLine(Inv, $"### {name}: 캐릭터별·막별 1위 일치");
            sb.AppendLine();
            Breakdown(sb, "캐릭터", ds, d => Short(d.Decision.StateBefore.Character), engine, compare, tierOnly, refSkipRate);
            Breakdown(sb, "막", ds, d => $"{d.Act}막", engine, compare, tierOnly, refSkipRate);
        }

        if (compare is not null)
        {
            sb.AppendLine(Inv, $"### {engine.Name} − {compare.Name}: 게임별 차이");
            sb.AppendLine();
            sb.AppendLine("한 게임에서만 좋아지는 변경은 그 게임에 맞춘 것일 가능성이 큽니다 (과적합). 여러 게임에서 고르게 좋아져야 남길 가치가 있습니다.");
            sb.AppendLine();
            sb.AppendLine("| 집합 | 1위 일치 차이(회) | 완전 일치 차이(회) | 좋아진 게임 | 나빠진 게임 | 가장 큰 한 게임의 몫 |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var (name, set) in sets)
            {
                var diff = PackDiff.PerGame(set, cv.DecisionsOf, engine, compare);
                if (diff.Count == 0) continue;
                var total = diff.Sum(g => g.Score);
                var largest = diff.Select(g => g.Score).DefaultIfEmpty(0).MaxBy(Math.Abs);
                sb.AppendLine(Inv, $"| {name} | {diff.Sum(g => g.Top1):+0.0;-0.0;0} | {diff.Sum(g => g.Exact):+0.0;-0.0;0} | {diff.Count(g => g.Score > 1e-9)} | {diff.Count(g => g.Score < -1e-9)} | {(Math.Abs(total) < 1e-9 ? "-" : $"{largest:+0.0;-0.0;0} / {total:+0.0;-0.0;0}")} |");
            }

            sb.AppendLine();
        }

        // ---------- 3. cross-validation ----------
        var games = Folds.GamesByTime(streams);
        sb.AppendLine("## 3. 자동 보정의 교차 검증 (fold-aware)");
        sb.AppendLine();
        sb.AppendLine(Inv, $"- 기준 팩: {o.CvPackName}. 자동 보정(`TierShiftCalibrator`)은 **각 fold의 학습 런만** 보고 카드 티어를 한 글자씩 옮깁니다 (순 증거 ≥ {o.Calibration.MinNet}, 서로 다른 게임 ≥ {o.Calibration.MinGames}, 최고 {o.Calibration.MaxTier}). 시험 런은 보정에 전혀 쓰이지 않습니다.");
        sb.AppendLine("- 학습(표본 내)과 시험(표본 밖)의 차이가 과적합의 크기입니다. 시험 점수가 기준 팩보다 오르지 않으면 그 보정은 버려야 합니다.");
        sb.AppendLine();

        var results = new List<CrossValidationResult>();
        var chronoTest = o.ChronoTestGames ?? Math.Max(1, (int)Math.Round(games.Count * 0.3));
        if (games.Count >= 3)
        {
            var fold = Folds.Chronological(streams, Math.Min(chronoTest, games.Count - 1));
            results.Add(cv.Run(fold.Name, [fold], evaluateTrain: true));
            results.Add(cv.Run($"{Math.Min(o.K, games.Count)}-fold (게임 단위, 시간순 번갈아 배정)", Folds.KFold(streams, Math.Min(o.K, games.Count)), evaluateTrain: true));
            results.Add(cv.Run($"게임 하나씩 빼기 (LOGO, {games.Count} fold)", Folds.LeaveOneGameOut(streams)));
        }

        // Train exactly on what the hand tuning saw, test on games it never saw (strict: no shared game at all).
        var tuningStreams = streams.Where(IsTuning).ToArray();
        var tuningGames = tuningStreams.Select(s => s.GameKey).ToHashSet(StringComparer.Ordinal);
        var unseen = streams.Where(s => !tuningGames.Contains(s.GameKey)).ToArray();
        if (tuningStreams.Length > 0 && unseen.Length > 0)
        {
            var fold = new Fold($"튜닝 집합으로 학습 → 튜닝 때 없던 게임 {Folds.GamesByTime(unseen).Count}판으로 시험", tuningStreams, unseen);
            results.Add(cv.Run(fold.Name, [fold], evaluateTrain: true));
        }

        foreach (var r in results)
        {
            sb.AppendLine(Inv, $"### {r.Name}");
            sb.AppendLine();
            MetricsHeader(sb, "추천기 · 평가 집합");
            if (r.BaseTrain is { } bt) MetricsRow(sb, "기준 팩 · 학습(표본 내)", bt);
            if (r.CalibratedTrain is { } ct) MetricsRow(sb, "기준+자동 보정 · 학습(표본 내)", ct);
            MetricsRow(sb, "**기준 팩 · 시험**", r.BaseTest);
            MetricsRow(sb, "**기준+자동 보정 · 시험**", r.CalibratedTest);
            MetricsRow(sb, "티어만 · 시험", r.TierOnlyTest);
            MetricsRow(sb, "무작위 · 시험", r.RandomTest);
            sb.AppendLine();
            var stable = r.ShiftStability;
            if (stable.Count > 0)
            {
                sb.AppendLine(string.Create(Inv, $"보정이 제안한 이동 (fold {r.Folds.Count}개 중 제안된 횟수): ")
                    + string.Join(", ", stable.Take(60).Select(s => $"{Short(s.CardId)} {(s.Steps > 0 ? "↑" : "↓")} {s.Folds}")));
                sb.AppendLine();
            }
        }

        // Shifts fitted on everything (what the calibrator would propose now) — for review only, not applied.
        var all = cv.DecisionsOf(streams);
        var allShifts = cv.Calibrator.Fit(all);
        sb.AppendLine("### 전체 런으로 맞춘 자동 보정 제안 (참고용, 자동 적용 안 함)");
        sb.AppendLine();
        if (allShifts.Count == 0)
        {
            sb.AppendLine("- 제안 없음");
        }
        else
        {
            sb.AppendLine("| 카드 | 팩 | 이동 | 과소평가 증거 | 과대평가 증거 | 게임 수 |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var s in allShifts)
            {
                sb.AppendLine(Inv, $"| {Short(s.CardId)} | {Short(s.PackKey)} | {(s.Steps > 0 ? "한 단계 위" : "한 단계 아래")} | {s.Up} | {s.Down} | {s.Games} |");
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static void Breakdown(StringBuilder sb, string label, IReadOnlyList<LabeledDecision> ds, Func<LabeledDecision, string> key,
        IRecommender engine, IRecommender? compare, IRecommender tierOnly, double skipRate)
    {
        sb.AppendLine(compare is null
            ? $"| {label} | 결정 | 선택 | 엔진 1위 | 엔진 2위 안 | 엔진 평균 순위 | 엔진 스킵 일치 | 티어만 1위 | 무작위 1위 |"
            : $"| {label} | 결정 | 선택 | 엔진 1위 | 엔진 2위 안 | 엔진 평균 순위 | 엔진 스킵 일치 | {compare.Name} 1위 | 티어만 1위 | 무작위 1위 |");
        sb.AppendLine(compare is null ? "|---|---|---|---|---|---|---|---|---|" : "|---|---|---|---|---|---|---|---|---|---|");
        foreach (var g in ds.GroupBy(key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var e = AgreementMetrics.Evaluate(g, engine);
            var t = AgreementMetrics.Evaluate(g, tierOnly);
            var r = AgreementMetrics.EvaluateRandom(g, skipRate);
            var c = compare is null ? null : AgreementMetrics.Evaluate(g, compare);
            sb.AppendLine(string.Create(Inv, $"| {g.Key} | {e.Decisions} | {e.Picks} | {Pct(e.Top1)} | {Pct(e.Top2)} | {e.MeanRank:0.00} | {Pct(e.SkipAgreement)} |")
                + (c is null ? "" : $" {Pct(c.Top1)} |") + $" {Pct(t.Top1)} | {Pct(r.Top1)} |");
        }

        sb.AppendLine();
    }

    private static void MetricsHeader(StringBuilder sb, string first)
    {
        sb.AppendLine(Inv, $"| {first} | 결정 | 선택 | 1위 일치 | 2위 안 | 평균 순위 | 스킵 일치 | 완전 일치 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
    }

    private static void MetricsRow(StringBuilder sb, string name, AgreementMetrics m)
        => sb.AppendLine(Inv, $"| {name} | {m.Decisions} | {m.Picks} | {Pct(m.Top1)} | {Pct(m.Top2)} | {m.MeanRank:0.00} | {Pct(m.SkipAgreement)} | {Pct(m.Exact)} |");

    private static double SkipRate(IReadOnlyList<LabeledDecision> ds) => ds.Count == 0 ? 0 : ds.Count(d => d.Skipped) / (double)ds.Count;

    private static string Pct(double v) => (v * 100).ToString("0.0", Inv) + "%";

    private static string Short(string id)
    {
        var dot = id.IndexOf('.');
        return dot >= 0 ? id[(dot + 1)..] : id;
    }
}

/// <summary>Per-game difference between two recommenders (hits of A minus hits of B).</summary>
internal static class PackDiff
{
    public static IReadOnlyList<(string Game, double Top1, double Exact, double Score)> PerGame(
        IReadOnlyList<ReplayStream> streams, Func<ReplayStream, IReadOnlyList<LabeledDecision>> decisionsOf, IRecommender a, IRecommender b)
    {
        return Folds.GamesByTime(streams)
            .Select(g =>
            {
                var ds = g.SelectMany(decisionsOf).ToArray();
                var ma = AgreementMetrics.Evaluate(ds, a);
                var mb = AgreementMetrics.Evaluate(ds, b);
                var top1 = ma.Top1Hits - mb.Top1Hits;
                var exact = ma.ExactHits - mb.ExactHits;
                return (g[0].GameKey, top1, exact, top1 + exact);
            })
            .Where(x => Math.Abs(x.Item2) > 1e-9 || Math.Abs(x.Item3) > 1e-9)
            .ToArray();
    }
}
