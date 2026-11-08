using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Replay;

// PathStats: measures the path advisor's numbers (HP loss, gold, heal, deaths, "?" outcomes) from .run history,
// fits PathWeights with shrinkage toward the previous guesses, and backtests old vs new weights.
//
//   dotnet run --project tools/PathStats -- <runs-dir|file.run>... --pack data/packs/v0.107.1 [--out-dir data/local/path]
//       [--map <map.json>]... [--player <steamId>] [--k 8]
//
// Writes <out-dir>/stats.md, stats.json, backtest.md, backtest.json. Run files live in
// %APPDATA%\SlayTheSpire2\steam\<steamid>\profile1\saves\history\ (co-op copies in several folders are de-duplicated).
// --map takes a saved map (test fixture format or a current_run.save); acts whose walked rooms fit it exactly one way
// are replayed on that real map.

return Cli.Run(args);

internal static class Cli
{
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine("PathStats <runs-dir|file.run>... --pack <dir> [--out-dir data/local/path] [--map <map.json>]... [--player <steamId>] [--k 8]");
            return args.Length == 0 ? 1 : 0;
        }

        var inputs = new List<string>();
        var maps = new List<string>();
        string? packDir = null;
        var outDir = Path.Combine("data", "local", "path");
        long? player = null;
        var k = 8.0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pack": packDir = Next(args, ref i); break;
                case "--out-dir": outDir = Next(args, ref i); break;
                case "--map": maps.Add(Next(args, ref i)); break;
                case "--player": player = long.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--k": k = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                default: inputs.Add(args[i]); break;
            }
        }
        if (packDir is null)
        {
            Console.Error.WriteLine("--pack is required (deck strength needs tiers/archetypes).");
            return 2;
        }

        var files = ExpandInputs(inputs);
        var all = files.Select(RunHistoryReader.ReadFile).ToArray();
        var runs = PathFloorExtractor.Distinct(all);
        if (runs.Count == 0)
        {
            Console.Error.WriteLine("No .run files found.");
            return 2;
        }
        Console.Error.WriteLine($"{all.Length} files, {runs.Count} distinct runs");

        var packNew = DataPackLoader.LoadDirectory(packDir);
        var packOld = DataPackLoader.LoadDirectory(packDir);
        packOld.Weights.Path = LegacyWeights(packOld.Weights.Path);

        // Measure with the legacy weights' strength factor (identical in both: StrengthDamageSlope is not refitted).
        var floors = runs.SelectMany(r => PathFloorExtractor.Extract(r, packOld)).ToArray();
        var report = PathStats.Compute(floors, packOld.Weights.Path, PathHpLoss.Legacy(), k);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "stats.json"), JsonSerializer.Serialize(report, JsonOut));
        File.WriteAllText(Path.Combine(outDir, "stats.md"), StatsMarkdown(report));

        // Backtest: current code defaults (packNew) vs the legacy guesses (packOld) vs the freshly fitted values.
        // Second fit that keeps the old deck-strength slope, to see whether the fitted slope helps or hurts.
        var reportKeepSlope = PathStats.Compute(floors, packOld.Weights.Path, PathHpLoss.Legacy(), k, packOld.Weights.Path.StrengthDamageSlope);
        var packFit = DataPackLoader.LoadDirectory(packDir);
        packFit.Weights.Path = report.Fitted!;
        var packKeep = DataPackLoader.LoadDirectory(packDir);
        packKeep.Weights.Path = reportKeepSlope.Fitted!;
        var sets = new[]
        {
            ("old (추정)", packOld), ("new (코드 기본값)", packNew),
            ($"fit (기울기 {report.Fitted!.StrengthDamageSlope:0.##})", packFit),
            ($"fit (기울기 {reportKeepSlope.Fitted!.StrengthDamageSlope:0.##} 유지)", packKeep),
        };
        var summaries = sets.Select(s => PathBacktest.Summarize(s.Item1,
            PathBacktest.Fights(floors, s.Item2.Weights.Path), PathBacktest.Acts(runs, s.Item2))).ToArray();

        var real = new List<(string Map, string Run, int Act, string Label, RealMapResult Result)>();
        foreach (var mapFile in maps)
        {
            var map = LoadMap(mapFile);
            foreach (var run in runs)
            {
                var me = LocalPlayer.Resolve(run, player);
                foreach (var (label, pack) in sets)
                {
                    var r = PathBacktest.RealMap(run, me.Id, map.Act - 1, map, pack);
                    if (r is not null) real.Add((Path.GetFileName(mapFile), run.Seed, map.Act, label, r));
                }
            }
        }
        File.WriteAllText(Path.Combine(outDir, "backtest.json"), JsonSerializer.Serialize(new { summaries, real = real.Select(r => new { r.Map, r.Run, r.Act, r.Label, r.Result }) }, JsonOut));
        File.WriteAllText(Path.Combine(outDir, "backtest.md"), BacktestMarkdown(summaries, real));
        Console.Error.WriteLine($"wrote {outDir}/stats.md, stats.json, backtest.md, backtest.json");
        return 0;
    }

    /// <summary>The previous guessed values, for the "old" side of the backtest.</summary>
    private static PathWeights LegacyWeights(PathWeights current)
    {
        var json = JsonSerializer.Serialize(current);
        var p = JsonSerializer.Deserialize<PathWeights>(json)!;
        p.HpLoss = PathHpLoss.Legacy();
        p.CoopHpLoss = null;
        p.StrengthDamageSlope = 1.0;
        p.RestHeal = 0.30;
        p.MonsterGold = 15;
        p.EliteGold = 30;
        p.UnknownGold = 8;
        p.TreasureGold = 0;
        return p;
    }

    private static string P(double v) => double.IsFinite(v) ? (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "-";
    private static string F(double v, string fmt = "0.000") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "-";

    private static string StatsMarkdown(PathStatsReport r)
    {
        var w = new StringBuilder();
        w.AppendLine("# 경로 가중치 측정 (런 기록)");
        w.AppendLine();
        w.AppendLine($"- 런 {r.Runs}개 (싱글 {r.SoloRuns}, 협동 {r.CoopRuns}), 플레이어-층 {r.PlayerFloors}행. 협동 런은 두 플레이어 모두 표본입니다.");
        w.AppendLine($"- 승천: {string.Join(", ", r.RunsByAscension.Select(kv => $"A{kv.Key} {kv.Value}"))} · 빌드: {string.Join(", ", r.RunsByBuild.Select(kv => $"{kv.Key} {kv.Value}"))}");
        w.AppendLine($"- 패배 위치(런 기준): {string.Join(", ", r.DeathsByRoom.Select(kv => $"{kv.Key} {kv.Value}"))}");
        w.AppendLine("- HP 값은 모두 최대 HP 대비 비율. `피해`는 `damage_taken`, `보정`은 덱 강도 계수(1 + slope×(0.5−강도))로 나눈 값(가중치 표가 담는 값).");
        w.AppendLine("- 사망한 방의 피해는 들어갈 때 HP에서 잘립니다(검열값) → 엘리트·보스 평균은 실제보다 약간 낮게 나옵니다.");
        w.AppendLine();

        w.AppendLine("## 방 종류 × 막: HP 손실");
        w.AppendLine();
        foreach (var segment in new[] { PathStats.All, PathStats.Solo, PathStats.Coop })
        {
            w.AppendLine($"### {segment}");
            w.AppendLine();
            w.AppendLine("| 방 | 막 | n | 피해 평균 ± SE | 보정 평균 | p50 | p80 | p90 | 최대 | 순 HP 변화 | 사망률 | 진입 HP | 덱 강도 |");
            w.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var s in r.Rooms.Where(x => x.Segment == segment && x.Room is "monster" or "elite" or "unknown" or "boss").OrderBy(x => RoomOrder(x.Room)).ThenBy(x => x.Act))
            {
                w.AppendLine($"| {s.Room} | {(s.Act == 0 ? "전체" : s.Act)} | {s.N} | {P(s.DamageMean)} ± {P(s.DamageSe)} | {P(s.DamageNormalizedMean)} | {P(s.DamageP50)} | {P(s.DamageP80)} | {P(s.DamageP90)} | {P(s.DamageMax)} | {P(s.NetHpMean)} | {P(s.DeathRate)} | {P(s.HpBeforeMean)} | {F(s.StrengthMean, "0.00")} |");
            }
            w.AppendLine();
        }

        w.AppendLine("## 승천 구간별 (전체 막)");
        w.AppendLine();
        w.AppendLine("| 구간 | 방 | n | 피해 평균 ± SE | p90 | 사망률 |");
        w.AppendLine("|---|---|---|---|---|---|");
        foreach (var s in r.Rooms.Where(x => x.Segment.StartsWith("asc", StringComparison.Ordinal) && x.Act == 0).OrderBy(x => x.Segment, StringComparer.Ordinal).ThenBy(x => RoomOrder(x.Room)))
        {
            w.AppendLine($"| {s.Segment} | {s.Room} | {s.N} | {P(s.DamageMean)} ± {P(s.DamageSe)} | {P(s.DamageP90)} | {P(s.DeathRate)} |");
        }
        w.AppendLine();

        w.AppendLine("## 협동 vs 싱글 (막별 평균으로 나눈 상대 피해)");
        w.AppendLine();
        w.AppendLine("| 방 | 싱글 n | 협동 n | 싱글 | 협동 | 협동/싱글 | z | 유의미 |");
        w.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var c in r.Coop)
        {
            w.AppendLine($"| {c.Room} | {c.SoloN} | {c.CoopN} | {F(c.SoloMean, "0.00")} | {F(c.CoopMean, "0.00")} | {F(c.Ratio, "0.00")} | {F(c.Z, "0.0")} | {(c.Material ? "예" : "아니오")} |");
        }
        w.AppendLine();
        w.AppendLine(r.CoopSeparate ? "→ 협동 전용 HP 표(`CoopHpLoss`)를 씁니다." : "→ 차이가 유의미하지 않아 하나의 표를 씁니다.");
        w.AppendLine();

        if (r.Strength is { } sf)
        {
            w.AppendLine("## 덱 강도와 피해");
            w.AppendLine();
            w.AppendLine($"전투 {sf.N}개: 덱 강도 평균 {F(sf.StrengthMean, "0.00")} (sd {F(sf.StrengthSd, "0.00")}). `피해 ÷ 그룹 평균 = a + b×(0.5−강도)` → b = {F(sf.Slope, "0.00")} ± {F(sf.SlopeSe, "0.00")} (코드 가정 `StrengthDamageSlope` = 1.0).");
            w.AppendLine();
        }

        w.AppendLine("## 골드·보상 (전체 막)");
        w.AppendLine();
        w.AppendLine("| 방 | n | 골드 ± SE | 물약 | 유물 | 카드 보상 | 카드 선택률 |");
        w.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in r.Rooms.Where(x => x.Segment == PathStats.All && x.Act == 0).OrderBy(x => RoomOrder(x.Room)))
        {
            w.AppendLine($"| {s.Room} | {s.N} | {F(s.GoldMean, "0.0")} ± {F(s.GoldSe, "0.0")} | {F(s.PotionsMean, "0.00")} | {F(s.RelicsMean, "0.00")} | {P(s.CardRewardRate)} | {P(s.CardPickRate)} |");
        }
        w.AppendLine();

        w.AppendLine("## ? 방이 된 것 (점 기준, 협동은 한 번만)");
        w.AppendLine();
        w.AppendLine("| 구간 | 막 | 결과 | n | 비율 | 피해 | 골드 |");
        w.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var u in r.Unknown.Where(u => u.Act == 0))
        {
            w.AppendLine($"| {u.Segment} | 전체 | {u.Resolved} | {u.N} | {P(u.Share)} | {P(u.DamageMean)} | {F(u.GoldMean, "0.0")} |");
        }
        w.AppendLine();

        w.AppendLine("## 휴식처");
        w.AppendLine();
        foreach (var rest in r.Rest)
        {
            w.AppendLine($"- **{rest.Segment}**: 방문 {rest.Visits}, 선택 {string.Join(", ", rest.Choices.Select(kv => $"{kv.Key} {kv.Value}"))}. " +
                         $"HEAL(최대 HP로 잘리지 않은 것) n={rest.HealUncappedN}, 평균 {P(rest.HealUncappedMean)}, 중앙값 {P(rest.HealUncappedMedian)} (범위 {P(rest.HealUncappedMin)}–{P(rest.HealUncappedMax)}). " +
                         $"진입 HP별 HEAL 비율: {string.Join(", ", rest.ByHp.Select(b => $"{b.Bucket} {b.Heals}/{b.Visits}"))}. " +
                         $"점수기 회복/강화 규칙과 실제 선택 일치: 현재 UpgradeValue {P(rest.CurrentAgreement)}, UpgradeValue={F(rest.UpgradeValueMatchingChoices, "0.0")}이면 {P(rest.MatchingAgreement)} (참고용, 미적용)");
        }
        w.AppendLine();

        w.AppendLine("## 이벤트 (자주 나온 순, ID만)");
        w.AppendLine();
        w.AppendLine("| 이벤트 | n | 피해 | 순 HP | 골드 순변화 | 최대 HP 순변화 | 카드 | 유물 |");
        w.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var e in r.Events.Take(30))
        {
            w.AppendLine($"| {e.EventId} | {e.N} | {P(e.DamageMean)} | {P(e.NetHpMean)} | {F(e.GoldNetMean, "0.0")} | {F(e.MaxHpNetMean, "0.0")} | {F(e.CardsMean, "0.00")} | {F(e.RelicsMean, "0.00")} |");
        }
        w.AppendLine();

        w.AppendLine($"## 맞춘 가중치 (축소 강도 k = {r.PriorStrength}: 사전값을 표본 k개로 취급)");
        w.AppendLine();
        w.AppendLine("| 가중치 | 사전(이전 추정) | 측정 | n | 적용 |");
        w.AppendLine("|---|---|---|---|---|");
        foreach (var f in r.Fits)
        {
            w.AppendLine($"| {f.Name} | {F(f.Prior)} | {F(f.Measured)} | {f.N} | {F(f.Fitted)} |");
        }
        return w.ToString();
    }

    private static int RoomOrder(string room) => Array.IndexOf(["monster", "elite", "unknown", "boss", "treasure", "shop", "rest_site", "ancient"], room);

    private static string BacktestMarkdown(IReadOnlyList<CalibrationSummary> sums, IReadOnlyList<(string Map, string Run, int Act, string Label, RealMapResult Result)> real)
    {
        var w = new StringBuilder();
        w.AppendLine("# 경로 가중치 백테스트");
        w.AppendLine();
        w.AppendLine("`.run`에는 지나간 길만 있고 맵이 없습니다. 그래서 기록 전체로는 **모델 자체**를 검사합니다:");
        w.AppendLine("1. 전투마다 (실제 진입 HP, 덱 강도)에서 모델의 사망 확률 → 실제 사망과 비교 (Brier, 예측 사망 수 합).");
        w.AppendLine("2. 막마다 실제로 걸은 길을 막 시작 상태에서 시뮬레이션 → 보스 도착 HP를 실제와 비교 (MAE), 보스 전 사망 확률(Brier).");
        w.AppendLine("맵이 따로 저장된 막(`--map`)만 \"추천 길 vs 걸은 길\"을 비교할 수 있습니다.");
        w.AppendLine();
        w.AppendLine("| 가중치 | 전투 | 사망 | 예측 사망 | 전투 Brier | 막 | 보스 도착 | 보스 HP MAE | 편향(예측−실제) | 기준선 MAE | 보스 전 사망 | 예측 | 막 Brier |");
        w.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in sums)
        {
            w.AppendLine($"| {s.Label} | {s.Fights} | {s.Deaths} | {F(s.PredictedDeaths, "0.0")} | {F(s.FightBrier, "0.0000")} | {s.Acts} | {s.ReachedBoss} | {P(s.BossHpMae)} | {P(s.BossHpBias)} | {P(s.BaselineBossHpMae)} | {s.DiedBeforeBoss} | {F(s.PredictedDiedBeforeBoss, "0.0")} | {F(s.ArrivalBrier, "0.0000")} |");
        }
        w.AppendLine();
        w.AppendLine("### 방 종류별 (전투 수 / 실제 사망 / 예측 사망 / 예상 피해 평균 / 실제 피해 평균)");
        w.AppendLine();
        w.AppendLine("| 가중치 | 방 | 전투 | 사망 | 예측 사망 | 예상 피해 | 실제 피해 |");
        w.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in sums)
        {
            foreach (var r in s.ByRoom)
            {
                w.AppendLine($"| {s.Label} | {r.Room} | {r.Fights} | {r.Deaths} | {F(r.PredictedDeaths, "0.0")} | {P(r.ExpectedLossMean)} | {P(r.ActualLossMean)} |");
            }
        }
        w.AppendLine();

        w.AppendLine("## 실제 맵 재생");
        w.AppendLine();
        if (real.Count == 0)
        {
            w.AppendLine("맞는 맵이 없습니다 (`--map`).");
            return w.ToString();
        }
        foreach (var g in real.GroupBy(r => (r.Map, r.Run, r.Act)))
        {
            var first = g.First().Result;
            w.AppendLine($"### {g.Key.Map}: 런 {g.Key.Run} {g.Key.Act}막 (경로 {first.TotalPaths}개, 시작 HP {P(first.StartHp)}, 보스 도착 HP {(first.ActualBossHp is { } b ? P(b) : "-")}, 막 생존 {(first.SurvivedAct ? "예" : "아니오")})");
            w.AppendLine();
            w.AppendLine($"- 걸은 길: {Types(first.TakenScored.Types)} (엘리트 {first.TakenScored.Elites}, 휴식 {first.TakenScored.Rests})");
            foreach (var (_, _, _, label, r) in g)
            {
                var top = r.AtStart.Top.FirstOrDefault();
                w.AppendLine($"- **{label}**: 추천 1위 {(top is null ? "-" : Types(top.Types))} (엘리트 {top?.Elites}, 휴식 {top?.Rests}, 예상 보스 HP {P(top?.ExpectedBossHp ?? double.NaN)}, 도착 확률 {P(top?.BossArrivalChance ?? double.NaN)}) · " +
                             $"걸은 길 점수 순위 {r.TakenRank}/{r.TotalPaths} (예상 보스 HP {P(r.TakenScored.ExpectedBossHp)}) · 갈림길 일치 {r.Agreed}/{r.RealChoices}");
            }
            w.AppendLine();
            w.AppendLine("| 단계 | HP | 선택지 | 걸은 곳 | " + string.Join(" | ", g.Select(x => x.Label)) + " |");
            w.AppendLine("|---|---|---|---|" + string.Concat(g.Select(_ => "---|")));
            for (var i = 0; i < first.Decisions.Count; i++)
            {
                var d = first.Decisions[i];
                if (d.Choices <= 1) continue;
                w.AppendLine($"| {d.Step} | {P(d.HpRatio)} | {d.Choices} | {d.TakenType} {d.Taken} | " +
                             string.Join(" | ", g.Select(x => x.Result.Decisions[i] is var e && e.Recommended == e.Taken ? "=" : $"{e.RecommendedType} {e.Recommended}")) + " |");
            }
            w.AppendLine();
        }
        return w.ToString();
    }

    private static string Types(IReadOnlyList<MapNodeType> types) => string.Join(" ", types.Select(t => t switch
    {
        MapNodeType.Monster => "M",
        MapNodeType.Elite => "E",
        MapNodeType.RestSite => "R",
        MapNodeType.Shop => "$",
        MapNodeType.Treasure => "T",
        MapNodeType.Boss => "B",
        MapNodeType.Ancient => "A",
        _ => "?",
    }));

    /// <summary>Reads a saved map: test fixture format (<c>saved_map</c> at the root) or a <c>current_run.save</c>.</summary>
    private static MapGraph LoadMap(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        int actIndex;
        JsonElement savedMap;
        string actId;
        if (root.TryGetProperty("saved_map", out savedMap))
        {
            actIndex = root.GetProperty("current_act_index").GetInt32();
            actId = root.TryGetProperty("act_id", out var a) ? a.GetString() ?? "" : "";
        }
        else
        {
            actIndex = root.GetProperty("current_act_index").GetInt32();
            var act = root.GetProperty("acts")[actIndex];
            savedMap = act.GetProperty("saved_map");
            actId = act.TryGetProperty("id", out var a) ? a.GetString() ?? "" : "";
        }

        var nodes = new Dictionary<MapCoord, MapNode>();
        MapCoord Add(JsonElement p)
        {
            var coord = Coord(p.GetProperty("coord"));
            var children = p.TryGetProperty("children", out var ch) ? ch.EnumerateArray().Select(Coord).ToArray() : [];
            nodes[coord] = new MapNode(coord, MapGraph.ParseType(p.GetProperty("type").GetString()), children);
            return coord;
        }
        var start = Add(savedMap.GetProperty("start"));
        foreach (var p in savedMap.GetProperty("points").EnumerateArray()) Add(p);
        var boss = Add(savedMap.GetProperty("boss"));
        return new MapGraph
        {
            ActId = actId,
            Act = actIndex + 1,
            Width = savedMap.GetProperty("width").GetInt32(),
            Height = savedMap.GetProperty("height").GetInt32(),
            Nodes = nodes,
            Start = start,
            Boss = boss,
        };

        static MapCoord Coord(JsonElement e) => new(e.GetProperty("col").GetInt32(), e.GetProperty("row").GetInt32());
    }

    private static List<string> ExpandInputs(IEnumerable<string> inputs)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input)) files.AddRange(Directory.GetFiles(input, "*.run").OrderBy(f => f, StringComparer.Ordinal));
            else if (File.Exists(input)) files.Add(input);
            else Console.Error.WriteLine($"Not found: {input}");
        }
        return files;
    }

    private static string Next(string[] args, ref int i)
        => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value after {args[i - 1]}");
}
