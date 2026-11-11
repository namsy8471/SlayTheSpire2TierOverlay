// StateProbe - prints what GameStateTracker reads from the save files and godot.log, live.
// Everything is read-only: nothing is ever written to the game's folders.
//
// Usage (from the repo root):
//   dotnet run --project tools/StateProbe                 live: snapshot on every change (Ctrl+C to quit)
//   dotnet run --project tools/StateProbe -- --events     live, also print every parsed log event
//   dotnet run --project tools/StateProbe -- --once       read save + replay log once, print, exit
//   dotnet run --project tools/StateProbe -- --save <file> [--net-id <id>]   parse one save file and print it
//   dotnet run --project tools/StateProbe -- --eval-guest  replay every co-op run in logs/ as each player with no save
//                                                          and compare the rebuilt deck with the .run history file
//   dotnet run --project tools/StateProbe -- --extract-run <N.run> <outDir>   write a test fixture of one co-op run:
//                                                          only the log lines the parser understands (IDs, no game
//                                                          text) + the .run file, Steam IDs replaced by 7656119000000000k
// Overrides: --root <SlayTheSpire2 dir>  --saves <dir>  --log <godot.log>  --net-id <steam id>

using System.Globalization;
using Sts2Overlay.GameState;
using Sts2Overlay.GameState.Log;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;

string? root = null, saves = null, log = null, saveFile = null, extractRun = null, extractOut = null;
ulong? netId = null;
bool events = false, once = false, evalGuest = false;
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--root": root = Next(); break;
        case "--saves": saves = Next(); break;
        case "--log": log = Next(); break;
        case "--net-id": netId = ulong.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--events": events = true; break;
        case "--once": once = true; break;
        case "--save": saveFile = Next(); break;
        case "--eval-guest": evalGuest = true; break;
        case "--extract-run": extractRun = Next(); extractOut = Next(); break;
        default: Console.Error.WriteLine($"Unknown argument: {args[i]}"); return 1;
    }
}

if (saveFile != null)
{
    ParsedSave? parsed = RunSaveParser.ParseFile(saveFile, netId ?? GamePaths.SteamIdFromPath(Path.GetDirectoryName(Path.GetFullPath(saveFile))!));
    if (parsed == null) { Console.Error.WriteLine("file not found"); return 1; }
    PrintSave(parsed);
    return 0;
}

GamePaths paths = GamePaths.Discover(root, saves, log);

if (extractRun != null)
{
    string logsDir = Path.GetDirectoryName(paths.LogFile ?? "") ?? "";
    string historyDir = Path.Combine(paths.SavesDirectory ?? "", "history");
    return Sts2Overlay.GameState.Diagnostics.FixtureExtractor.Extract(logsDir, historyDir, extractRun, extractOut!, Console.Out) ? 0 : 1;
}

if (evalGuest)
{
    string logsDir = Path.GetDirectoryName(paths.LogFile ?? "") ?? "";
    string? historyDir = paths.SavesDirectory is { } sd ? Path.Combine(sd, "history") : null;
    if (!Directory.Exists(logsDir) || historyDir == null || !Directory.Exists(historyDir))
    {
        Console.Error.WriteLine("logs or history folder not found");
        return 1;
    }
    var results = Sts2Overlay.GameState.Diagnostics.GuestDeckEvaluation.Run(logsDir, historyDir);
    foreach (var group in results.GroupBy(r => r.LogFile))
    {
        Console.WriteLine($"== {group.Key}");
        foreach (var r in group) Console.WriteLine("  " + r);
    }
    int total = results.Sum(r => r.TrueDeck.Count);
    Console.WriteLine($"runs {results.Select(r => r.RunFile).Distinct().Count()}  player-runs {results.Count}  perfect {results.Count(r => r.Perfect)}  " +
                      $"id {results.Sum(r => r.IdMatches)}/{total}  exact {results.Sum(r => r.ExactMatches)}/{total}  " +
                      $"silent-errors {results.Sum(r => r.SilentErrors)}  run-end exact {results.Count(r => r.ReconciledExact)}/{results.Count}");
    return 0;
}
Console.WriteLine($"saves : {paths.SavesDirectory ?? "(not found)"}");
Console.WriteLine($"log   : {paths.LogFile}");
Console.WriteLine($"steam : {paths.SteamId?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
Console.WriteLine();

var options = new TrackerOptions { Paths = paths, LocalNetIdOverride = netId };

if (once)
{
    // Same pipeline as live, but synchronous: read the saves, replay the log, print once.
    var tracker = new GameStateTracker(options with { Paths = null });
    if (paths.SavesDirectory != null)
        foreach (string name in RunSaveParser.RunSaveFileNames)
        {
            string p = Path.Combine(paths.SavesDirectory, name);
            if (Sts2Overlay.GameState.IO.SharedFile.ReadAllBytes(p) is not { } bytes) continue;
            ParsedSave s = RunSaveParser.Parse(Sts2Overlay.GameState.IO.SharedFile.Decode(bytes), netId ?? paths.SteamId);
            tracker.OnSave(new SaveSnapshot(p, s, File.GetLastWriteTimeUtc(p), bytes.Length));
            Console.WriteLine($"loaded {name} ({bytes.Length} bytes, seed {s.Seed})");
        }
    if (paths.LogFile != null && File.Exists(paths.LogFile))
    {
        var tailer = new LogTailer(paths.LogFile);
        int count = 0;
        for (IReadOnlyList<string> lines; (lines = tailer.Poll()).Count > 0; count += lines.Count)
            tracker.ProcessLines(lines);
        Console.WriteLine($"replayed {count} log lines");
    }
    Console.WriteLine();
    Print(tracker.Current);
    return 0;
}

using (var tracker = new GameStateTracker(options))
{
    string last = "";
    var gate = new object();
    tracker.Changed += (_, run) =>
    {
        string text = Describe(run);
        lock (gate)
        {
            if (text == last) return; // the tracker fires often; only print real changes
            last = text;
            Console.WriteLine($"---- {DateTime.Now:HH:mm:ss.fff} ----");
            Console.WriteLine(text);
        }
    };
    tracker.Diagnostic += (_, msg) => Console.WriteLine($"[diag] {msg}");
    if (events) tracker.LogEventParsed += (_, e) => Console.WriteLine($"  event: {e}");
    tracker.Start();
    Console.WriteLine("Watching. Ctrl+C to quit.");
    var quit = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
    quit.Wait();
}
return 0;

static void Print(TrackedRun? run) => Console.WriteLine(Describe(run));

static string Describe(TrackedRun? run)
{
    if (run == null) return "(no run)";
    var s = run.State;
    var w = new StringWriter(CultureInfo.InvariantCulture);
    w.WriteLine($"{s.Character} A{s.Ascension}  act {s.Act} floor {s.Floor}  hp {s.Hp}/{s.MaxHp}  gold {s.Gold}" +
                $"{(run.HpGoldReliable ? "" : " (hp/gold approx)")}  {(s.IsMultiplayer ? "co-op" : "solo")} me={run.LocalPlayerId}");
    w.WriteLine($"screen {run.Screen}  offer {run.OfferContext?.ToString() ?? "-"}  last combat {run.LastCombatKind?.ToString() ?? "-"} {run.LastEncounterId}  outcome {run.Outcome}");
    w.WriteLine($"source {(run.FromSave ? "save" : "LOG ONLY")}  log patches {run.AppliedLogPatches}{(run.DeckApproximate ? "  (deck approx)" : "")}  seed {run.Seed}");
    w.WriteLine($"pity {(run.RarityPityOffset is { } p ? p.ToString("0.###", CultureInfo.InvariantCulture) : "?")}  " +
                $"last rolls [{string.Join(", ", run.LastRewardRolls.Select(r => $"{r.Source}{(r.IsRare ? " RARE" : "")} {r.Roll:0.###}<{r.Need:0.###}"))}]");
    w.WriteLine($"removals bought {s.CardRemovalsBought}  potions [{string.Join(", ", run.Potions.Select(x => $"{x.Slot}:{x.Id}"))}]");
    w.WriteLine($"deck ({s.Deck.Count}): {string.Join(", ", s.Deck.GroupBy(c => c.Card.ToString() + (c.EnchantmentId != null ? $"[{c.EnchantmentId}]" : "")).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key))}");
    if (run.UncertainCards.Count > 0 || run.UnidentifiedCards > 0 || run.UnknownUpgrades > 0 || run.UnknownRemovals > 0)
        w.WriteLine($"uncertain [{string.Join(", ", run.UncertainCards.Select(u => $"{u.CardId}:{u.Reason}"))}]  " +
                    $"unidentified {run.UnidentifiedCards}  unplaced upgrades {run.UnknownUpgrades}  unplaced removals {run.UnknownRemovals}");
    w.WriteLine($"relics ({s.Relics.Count}): {string.Join(", ", s.Relics)}");
    if (run.Map is { } m)
        w.WriteLine($"map {m.ActId} {m.Width}x{m.Height} nodes {m.Nodes.Count} current {m.Current?.ToString() ?? "-"} boss {m.Boss}");
    return w.ToString().TrimEnd();
}

static void PrintSave(ParsedSave s)
{
    Console.WriteLine($"schema {s.SchemaVersion}  seed {s.Seed}  mode {s.GameMode}  act {s.ActId}  saved {s.SaveTime:u}");
    Console.WriteLine($"players [{string.Join(", ", s.PlayerNetIds)}]  me {s.MyNetId}{(s.MyPlayerMatched ? "" : " (NOT matched, fell back to first)")}");
    Console.WriteLine($"pity {s.CardRarityOffset}  potion odds {s.PotionRewardOdds}  ? odds {string.Join(", ", s.UnknownMapPointOdds.Select(kv => $"{kv.Key}={kv.Value}"))}");
    Print(new TrackedRun { State = s.State, Map = s.Map, Potions = s.Potions, RarityPityOffset = s.CardRarityOffset, FromSave = true, Seed = s.Seed, LocalPlayerId = s.MyNetId });
}
