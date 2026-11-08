using System.Globalization;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Replay;
using Sts2Overlay.Core.Replay.Validation;
using Sts2Overlay.Tools.RunHistoryAnalyzer;

// RunHistoryAnalyzer: replays .run history files through the recommendation engine and prints
// a Korean Markdown report (agreement with your picks) or personal statistics.
//
//   dotnet run --project tools/RunHistoryAnalyzer -- replay <runs-dir|file.run>... [--pack <dir>] [--player <steamId>] [--out <file.md>] [--summary]
//   dotnet run --project tools/RunHistoryAnalyzer -- stats  <runs-dir|file.run>... [--player <steamId>]
//   dotnet run --project tools/RunHistoryAnalyzer -- validate <runs-dir|file.run>... --pack <dir> [--compare-pack <dir>] [--cv-pack <dir>]
//        [--player <steamId>] [--tuned-until <unix>] [--all-players] [--k 5] [--chrono-test <games>] [--min-net 3] [--min-games 2] [--out <file.md>]
//   dotnet run --project tools/RunHistoryAnalyzer -- calibrate <runs-dir>... --pack <dir> [--catalog <cards.json>] [--sims N] [--noise σ] [--policy greedy|engine] [--emit]
//   dotnet run --project tools/RunHistoryAnalyzer -- tune-weights <runs-dir|file.run>... --pack <dir> [--player <steamId>] [--tuned-until <unix>] [--all-players] [--lambda 0.005]... [--no-zero] [--out <file.md>]
//
// Run files live in %APPDATA%\SlayTheSpire2\steam\<steamid>\profile1\saves\history\.

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0];
        var inputs = new List<string>();
        string? packDir = null, outFile = null, comparePackDir = null, cvPackDir = null;
        long? player = null, tunedUntil = null;
        string? catalogFile = null;
        var sims = 1000;
        var noise = 0.0;
        var emit = false;
        var policy = PickPolicy.Greedy;
        var summaryOnly = false;
        var allPlayers = false;
        int k = 5, minNet = 3, minGames = 2;
        int? chronoTest = null;
        var lambdas = new List<double>();
        var noZero = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pack": packDir = Next(args, ref i); break;
                case "--out": outFile = Next(args, ref i); break;
                case "--player": player = long.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--summary": summaryOnly = true; break;
                case "--compare-pack": comparePackDir = Next(args, ref i); break;
                case "--cv-pack": cvPackDir = Next(args, ref i); break;
                case "--tuned-until": tunedUntil = long.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--all-players": allPlayers = true; break;
                case "--k": k = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--chrono-test": chronoTest = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--min-net": minNet = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--min-games": minGames = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--emit": emit = true; break;
                case "--catalog": catalogFile = Next(args, ref i); break;
                case "--sims": sims = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--noise": noise = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--policy": policy = Enum.Parse<PickPolicy>(Next(args, ref i), ignoreCase: true); break;
                case "--no-zero": noZero = true; break;
                case "--lambda": lambdas.Add(double.Parse(Next(args, ref i), CultureInfo.InvariantCulture)); break;
                default: inputs.Add(args[i]); break;
            }
        }

        var files = ExpandInputs(inputs);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("No .run files found.");
            return 2;
        }

        var runs = files.Select(RunHistoryReader.ReadFile).ToArray();
        if (command == "tune-weights")
        {
            if (packDir is null)
            {
                Console.Error.WriteLine("tune-weights 에는 --pack 이 필요합니다.");
                return 1;
            }

            Write(TuneWeightsCommand.Run(files.Zip(runs).ToArray(), LoadPack(packDir), player, tunedUntil, allPlayers, lambdas.Count > 0 ? lambdas : [0.005], noZero), outFile);
            return 0;
        }

        if (command == "validate")
        {
            if (packDir is null)
            {
                Console.Error.WriteLine("validate 에는 --pack 이 필요합니다.");
                return 1;
            }

            var options = new ValidateOptions
            {
                Pack = LoadPack(packDir),
                PackName = Path.GetFileName(Path.TrimEndingDirectorySeparator(packDir)),
                ComparePack = comparePackDir is null ? null : LoadPack(comparePackDir),
                ComparePackName = comparePackDir is null ? "비교 팩" : Path.GetFileName(Path.TrimEndingDirectorySeparator(comparePackDir)),
                CvPack = cvPackDir is null ? null : LoadPack(cvPackDir),
                CvPackName = Path.GetFileName(Path.TrimEndingDirectorySeparator(cvPackDir ?? packDir)),
                User = player,
                TunedUntil = tunedUntil,
                AllPlayers = allPlayers,
                K = k,
                ChronoTestGames = chronoTest,
                Calibration = new TierShiftOptions { MinNet = minNet, MinGames = minGames },
            };
            Write(ValidateCommand.Run(files.Zip(runs).ToArray(), options), outFile);
            return 0;
        }

        switch (command)
        {
            case "replay":
            {
                var pack = LoadPack(packDir);
                var analyzer = new ReplayAnalyzer(pack);
                var replays = runs.Select(r => analyzer.Replay(r, player is { } p && r.FindPlayer(p) is not null ? p : null)).ToArray();
                Write(ReplayReport.ToMarkdown(replays, includeDecisions: !summaryOnly), outFile);
                return 0;
            }
            case "stats":
            {
                var stats = PersonalStats.Compute(runs, player);
                using var w = new StringWriter(CultureInfo.InvariantCulture);
                w.WriteLine($"# 개인 통계 (런 {stats.Runs}개, 협동 {stats.MultiplayerRuns}개)");
                w.WriteLine();
                w.WriteLine("| 캐릭터 | 런 | 승리 | 승률 | 평균 도달 층 |");
                w.WriteLine("|---|---|---|---|---|");
                foreach (var c in stats.Characters)
                {
                    w.WriteLine($"| {c.Character} | {c.Runs} | {c.Wins} | {c.WinRate:P0} | {c.AverageFloorReached:0.0} |");
                }
                w.WriteLine();
                w.WriteLine("| 카드 | 제시 | 선택 | 선택률 |");
                w.WriteLine("|---|---|---|---|");
                foreach (var c in stats.Cards.Take(40))
                {
                    w.WriteLine($"| {c.CardId} | {c.Offered} | {c.Picked} | {c.PickRate:P0} |");
                }
                Write(w.ToString(), outFile);
                return 0;
            }
            case "calibrate":
            {
                var pack = LoadPack(packDir);
                var catalogPath = catalogFile ?? FindCommittedCatalog();
                var pool = catalogPath is not null ? ForecastCardPool.FromCatalogJson(File.ReadAllText(catalogPath)) : ForecastCardPool.FromPack(pack);
                // In co-op "me" is the Steam id of the history folder (steam/<id>/profile1/...); without it LocalPlayer picks player 1.
                var options = new ForecastOptions { Simulations = sims, PickNoise = noise, Policy = policy, Seed = 12345 };
                var named = files.Zip(runs, (f, r) => (r, f, player ?? SteamIdFromPath(f))).ToArray();
                Write(Calibration.Run(named, pack, pool, options, emit), outFile);
                return 0;
            }
            default:
                PrintUsage();
                return 1;
        }
    }

    private static DataPack LoadPack(string? dir)
    {
        if (dir is null)
        {
            Console.Error.WriteLine("경고: --pack 이 없어 빈 데이터 팩으로 실행합니다 (모든 카드가 미평가).");
            return new DataPack();
        }
        var pack = DataPackLoader.LoadDirectory(dir);
        foreach (var issue in DataPackValidator.Validate(pack))
        {
            Console.Error.WriteLine($"[{issue.Severity}] {issue.Where}: {issue.Message}");
        }
        return pack;
    }

    private static string? FindCommittedCatalog()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", "catalog");
            if (Directory.Exists(candidate)) return Directory.GetFiles(candidate, "cards.*.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        }
        return null;
    }

    private static long? SteamIdFromPath(string path)
    {
        foreach (var part in Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 17 && long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return id;
        }
        return null;
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

    private static void Write(string text, string? outFile)
    {
        if (outFile is null) Console.Out.Write(text);
        else File.WriteAllText(outFile, text);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            RunHistoryAnalyzer
              replay <runs-dir|file.run>... [--pack <dir>] [--player <steamId>] [--out <file.md>] [--summary]
              stats  <runs-dir|file.run>... [--player <steamId>] [--out <file.md>]
              validate <runs-dir|file.run>... --pack <dir> [--compare-pack <dir>] [--cv-pack <dir>] [--player <steamId>]
                       [--tuned-until <unix>] [--all-players] [--k 5] [--chrono-test <games>] [--min-net 3] [--min-games 2] [--out <file.md>]
              calibrate <runs-dir>... --pack <dir> [--catalog <cards.json>] [--sims N] [--noise σ] [--policy greedy|engine] [--emit] [--out <file.md>]
              tune-weights <runs-dir|file.run>... --pack <dir> [--player <steamId>] [--tuned-until <unix>] [--all-players] [--lambda 0.005]... [--no-zero] [--out <file.md>]
            """);
    }
}
