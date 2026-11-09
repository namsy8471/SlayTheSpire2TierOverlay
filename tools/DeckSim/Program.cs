using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Simulation;

namespace Sts2Overlay.Tools.DeckSim;

/// <summary>
/// Simulated runs that follow the overlay vs top players' winning decks.
/// <code>
/// DeckSim --pack data/packs/v0.107.1 --rankers &lt;dir of .run&gt; [--n 400] [--policies engine,tier] [--asc 10] [--mp]
///         [--out report.md] [--json summary.json] [--diag 12] [--set key=value ...] [--line]
/// </code>
/// <c>--set</c> overrides a weight for a calibration sweep (see <c>Override</c>), <c>--line</c> prints a one-line summary to compare runs.
/// Results and method: docs/research/DECK_SIM.md.
/// Starting decks and relics come from the first ranker run of each character. Ranker runs never leave this machine:
/// the report holds only aggregates and OP.GG run ids.
/// </summary>
internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static int Main(string[] args)
    {
        string? packDir = null, rankersDir = null, outFile = null, jsonFile = null, catalogFile = null, cardsFile = null;
        var n = 400;
        var asc = 10;
        var mp = false;
        var diag = 12;
        var policies = new[] { "engine", "tier" };
        var overrides = new List<(string Key, double Value)>();
        var line = false;
        var losses = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pack": packDir = args[++i]; break;
                case "--rankers": rankersDir = args[++i]; break;
                case "--catalog": catalogFile = args[++i]; break;
                case "--n": n = int.Parse(args[++i], Inv); break;
                case "--asc": asc = int.Parse(args[++i], Inv); break;
                case "--mp": mp = true; break;
                case "--diag": diag = int.Parse(args[++i], Inv); break;
                case "--policies": policies = args[++i].Split(','); break;
                case "--out": outFile = args[++i]; break;
                case "--json": jsonFile = args[++i]; break;
                case "--set":
                    var kv = args[++i].Split('=', 2);
                    overrides.Add((kv[0], double.Parse(kv[1], Inv)));
                    break;
                case "--line": line = true; break;
                case "--cards": cardsFile = args[++i]; break;
                case "--losses": losses = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument {args[i]}");
                    return 1;
            }
        }
        if (packDir is null || rankersDir is null)
        {
            Console.Error.WriteLine("usage: DeckSim --pack <packDir> --rankers <dir of .run> [--n 400] [--policies engine,tier] [--asc 10] [--mp] [--out report.md] [--json summary.json] [--diag 12] [--set key=value ...] [--line]");
            return 1;
        }

        var pack = DataPackLoader.LoadDirectory(packDir, catalogPath: catalogFile);
        foreach (var (key, value) in overrides)
        {
            if (!Override(pack.Weights, key, value))
            {
                Console.Error.WriteLine($"unknown --set key {key}");
                return 1;
            }
        }
        // The committed catalog names pools CARD_POOL.*; FromCatalogJson understands that format (FromPack would find no character cards).
        var catalogPath = catalogFile ?? DataPackLoader.SiblingCatalogPath(packDir);
        var pool = catalogPath is not null && File.Exists(catalogPath)
            ? ForecastCardPool.FromCatalogJson(File.ReadAllText(catalogPath))
            : ForecastCardPool.FromPack(pack);
        Console.Error.WriteLine($"card pool: {pool.All.Count} cards from {catalogPath ?? "the pack"}");
        var rankers = LoadRankers(rankersDir);
        Console.Error.WriteLine($"ranker runs: {rankers.Count}");

        if (cardsFile is not null)
        {
            WriteCardRows(pack, losses ? LoadRankers(rankersDir, includeLosses: true) : rankers, cardsFile);
            Console.Error.WriteLine($"card rows: {cardsFile}");
            if (policies.Length == 0 || (policies.Length == 1 && policies[0] == "none")) return 0;
        }

        var characters = pack.Archetypes.Select(a => a.Character).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var starts = new Dictionary<string, (IReadOnlyList<DeckCard> Deck, IReadOnlyList<string> Relics)>(StringComparer.Ordinal);
        foreach (var c in characters)
        {
            var r = rankers.FirstOrDefault(x => x.Character == c);
            if (r is null) continue;
            var s0 = r.Timeline.StateBefore(0);
            starts[c] = (r.Timeline.StartingDeck, s0.Relics);
        }

        var summary = new Summary { Simulations = n, Ascension = asc, Multiplayer = mp };
        var sb = new StringBuilder();
        var simulator = new RunSimulator(pack, pool);
        var options = new SimOptions { Ascension = asc, Multiplayer = mp };
        var runsByPolicy = new Dictionary<string, Dictionary<string, SimRun[]>>(StringComparer.Ordinal);

        foreach (var policyName in policies)
        {
            var byChar = new Dictionary<string, SimRun[]>(StringComparer.Ordinal);
            foreach (var c in characters)
            {
                if (!starts.TryGetValue(c, out var start)) continue;
                var runs = new SimRun[n];
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Parallel.For(0, n, i =>
                {
                    ISimPolicy policy = policyName == "tier" ? new TierOnlyPolicy(pack) : new EnginePolicy(pack);
                    runs[i] = simulator.Run(c, start.Deck, start.Relics, policy, seed: 1000 + i, options);
                });
                byChar[c] = runs;
                Console.Error.WriteLine($"{policyName} {c}: {n} runs in {sw.Elapsed.TotalSeconds:0.0}s");
            }
            runsByPolicy[policyName] = byChar;
        }

        // ---- aggregates ----
        foreach (var (policyName, byChar) in runsByPolicy)
        {
            foreach (var (c, runs) in byChar)
            {
                for (var act = 1; act <= 3; act++)
                {
                    var rows = runs.Select(r => Row.Of(DeckMetrics.Compute(pack, c, r.ActEndDecks[act - 1], r.Relics))).ToList();
                    summary.Rows.Add(Agg.Of(policyName, c, act, rows));
                }
                summary.Skip.Add(new SkipRow(policyName, c,
                    Enumerable.Range(1, 3).Select(act => SkipRate(runs, act)).ToArray()));
                var finals = runs.Select(r => DeckMetrics.Compute(pack, c, r.ActEndDecks[2], r.Relics)).ToList();
                summary.Builds.Add(BuildDist(policyName, c, finals));
                summary.ByBuild.AddRange(ByBuild(policyName, c, finals));
            }
        }
        foreach (var c in characters)
        {
            var mine = rankers.Where(r => r.Character == c).ToList();
            if (mine.Count == 0) continue;
            for (var act = 1; act <= 3; act++)
            {
                var rows = mine.Where(r => r.ActEnd.Count >= act)
                    .Select(r => Row.Of(DeckMetrics.Compute(pack, c, r.ActEnd[act - 1], r.Relics))).ToList();
                summary.Rows.Add(Agg.Of("ranker", c, act, rows));
            }
            var rankerFinals = mine.Select(r => DeckMetrics.Compute(pack, c, r.ActEnd[^1], r.Relics)).ToList();
            summary.Builds.Add(BuildDist("ranker", c, rankerFinals));
            summary.ByBuild.AddRange(ByBuild("ranker", c, rankerFinals));
        }

        // ---- build axes: do winning decks over-produce what their build needs? ----
        foreach (var (policyName, byChar) in runsByPolicy)
        {
            foreach (var (c, runs) in byChar) summary.Axes.AddRange(Axes(pack, policyName, c, runs.Select(r => (IReadOnlyList<DeckCard>)r.ActEndDecks[2]).ToList()));
        }
        foreach (var c in characters)
        {
            var mine = rankers.Where(r => r.Character == c).Select(r => (IReadOnlyList<DeckCard>)r.ActEnd[^1]).ToList();
            if (mine.Count > 0) summary.Axes.AddRange(Axes(pack, "ranker", c, mine));
        }

        // ---- agreement with rankers' picks ----
        summary.Agreement.AddRange(Agreement(pack, rankers, diag, summary.AgreementExamples));

        // ---- why the engine leaves its build (simulated engine runs) ----
        if (runsByPolicy.TryGetValue("engine", out var engineRuns))
        {
            summary.Leaks.AddRange(Leaks(pack, engineRuns, diag, out var examples));
            summary.LeakExamples.AddRange(examples);
        }

        if (line) Console.Out.WriteLine(OneLine(summary, overrides));
        Report.Write(sb, summary, rankers.Count);
        Write(sb.ToString(), outFile);
        if (jsonFile is not null)
        {
            File.WriteAllText(jsonFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }

    // =============================================================================================
    // Rankers
    // =============================================================================================

    /// <summary>Normal 3-act runs have about 45–55 map points.</summary>
    private const int MaxMapPoints = 60;

    internal sealed record RankerRun(string RunId, string Character, RunHistory Run, long PlayerId, DeckTimeline Timeline,
        IReadOnlyList<IReadOnlyList<DeckCard>> ActEnd, IReadOnlyList<string> Relics);

    /// <param name="includeLosses">Also keep losing runs (any length): only the per-card dump uses them, deck comparisons need winning decks.</param>
    private static List<RankerRun> LoadRankers(string dir, bool includeLosses = false)
    {
        var list = new List<RankerRun>();
        foreach (var file in Directory.GetFiles(dir, "*.run").Order(StringComparer.Ordinal))
        {
            RunHistory run;
            try
            {
                run = RunHistoryReader.ReadFile(file);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"skip {Path.GetFileName(file)}: {e.Message}");
                continue;
            }
            if ((!run.Win && !includeLosses) || run.IsMultiplayer || run.Players.Count == 0) continue;
            // A few cited v0.107.0/1 runs have 170+ map points over 3 acts (a long-map mode the run file does not flag): not comparable.
            if (run.Nodes.Count > MaxMapPoints)
            {
                Console.Error.WriteLine($"skip {Path.GetFileName(file)}: {run.Nodes.Count} map points");
                continue;
            }
            var player = run.Players[0];
            var timeline = DeckTimeline.Build(run, player.Id);
            var actEnd = new List<IReadOnlyList<DeckCard>>();
            for (var act = 1; act <= 3; act++)
            {
                var last = -1;
                for (var i = 0; i < run.Nodes.Count; i++) if (run.Nodes[i].Act == act) last = i;
                if (last < 0) break;
                actEnd.Add(timeline.DeckAfter(last));
            }
            if (actEnd.Count < 3 && !includeLosses) continue;
            var relics = player.Relics.Select(r => r.Id).ToArray();
            list.Add(new RankerRun(Path.GetFileNameWithoutExtension(file), player.Character, run, player.Id, timeline, actEnd, relics));
        }
        return list;
    }

    /// <summary>
    /// Engine vs ranker on every recorded reward screen. "Current build" = the main build of the ranker's deck right before the
    /// screen, when it is established (<see cref="Established"/>). Mixed screen = the offer holds a card listed in that build and a card
    /// outside it (on-theme cards count as outside: the build lists are the claim being tested). For screens where the ranker took the
    /// build card and the engine did not, the mean component difference (engine's pick − ranker's pick) shows what pulled the engine away.
    /// </summary>
    private static IEnumerable<AgreementRow> Agreement(DataPack pack, List<RankerRun> rankers, int examples, List<string> lines)
    {
        var engine = new RecommendationEngine(pack);
        foreach (var group in rankers.GroupBy(r => r.Character).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var row = new AgreementRow { Character = group.Key };
            var diff = new Dictionary<string, double>(StringComparer.Ordinal);
            var shown = 0;
            var overDiff = new Dictionary<string, double>(StringComparer.Ordinal);
            var overShown = 0;
            foreach (var r in group)
            {
                foreach (var d in DecisionExtractor.Extract(r.Run, r.PlayerId, r.Timeline))
                {
                    if (d.Context == OfferContext.Shop || d.Offered.Count < 2) continue;
                    var profile = DeckAnalyzer.Analyze(d.StateBefore, pack);
                    var eval = engine.EvaluateOffer(d.StateBefore, profile, d.Offered, d.Context);
                    var rankerPick = d.Picked.Count > 0 ? d.Picked[0] : (CardRef?)null;
                    row.Screens++;
                    if (eval.Best == rankerPick || (eval.Best is null && rankerPick is null)) row.Top1++;
                    if (rankerPick is null) row.RankerSkips++;
                    if (eval.Best is null) row.EngineSkips++;
                    var ai = Math.Clamp(d.StateBefore.Act, 1, 3) - 1;
                    row.ScreensByAct[ai]++;
                    if (rankerPick is null) row.RankerSkipsByAct[ai]++;
                    if (eval.Best is null) row.EngineSkipsByAct[ai]++;
                    if (rankerPick is { } taken)
                    {
                        // Where the ranker's card sits in the engine's own ranking of the offered cards (skip aside).
                        var rank = eval.Cards.OrderByDescending(e => e.Total).Select(e => e.Card).ToList().IndexOf(taken);
                        if (rank == 0) row.RankerCardFirst[ai]++;
                        else if (rank == 1) row.RankerCardSecond[ai]++;
                        if (eval.Best is null) row.EngineSkipOnPick[ai]++;
                    }
                    var primary = profile.Primary;
                    if (primary is not null && !Established(primary) && primary.CoreTarget > 0 && primary.Commit >= 0.3)
                    {
                        // Leaning toward a build whose core is not owned yet: does the ranker already take its non-core cards?
                        var lean = primary.Archetype;
                        bool Listed(CardRef c) => lean.RoleOf(pack.Canonical(c.Id)) is { } role && role != ArchetypeRoles.Core;
                        if (d.Offered.Any(Listed) && !d.Offered.All(Listed) && !d.Offered.Any(c => lean.RoleOf(pack.Canonical(c.Id)) == ArchetypeRoles.Core))
                        {
                            row.LeanMixed++;
                            if (rankerPick is { } lp && Listed(lp)) row.LeanRankerBuild++;
                            if (eval.Best is { } le && Listed(le)) row.LeanEngineBuild++;
                        }
                    }
                    if (primary is null || !Established(primary)) continue;
                    var target = primary.Archetype;
                    bool InTarget(CardRef c) => target.RoleOf(pack.Canonical(c.Id)) is not null;
                    if (!d.Offered.Any(InTarget) || d.Offered.All(InTarget)) continue;
                    row.Mixed++;
                    var rIn = rankerPick is { } rp && InTarget(rp);
                    var eIn = eval.Best is { } ep && InTarget(ep);
                    if (rIn) row.RankerTookBuild++;
                    if (eIn) row.EngineTookBuild++;
                    if (rIn && !eIn)
                    {
                        row.EngineLeftBuild++;
                        var mine = eval.Best is { } eb ? eval.Cards.First(e => e.Card == eb).Score : eval.Skip;
                        var theirs = eval.Cards.First(e => e.Card == rankerPick!.Value);
                        foreach (var k in mine.Components.Select(x => x.Key).Concat(theirs.Score.Components.Select(x => x.Key)).Distinct())
                        {
                            diff[k] = diff.GetValueOrDefault(k) + mine.ValueOf(k) - theirs.Score.ValueOf(k);
                        }
                        if (eval.Best is { } pick && (pack.FindTier(r.Character, pack.Canonical(pick.Id))?.Roles.GetValueOrDefault(Roles.Draw) ?? 0) >= DeckMetrics.DrawRoleMin)
                        {
                            row.LeftForDraw++;
                        }
                        if (eval.Best is null) row.LeftForSkip++;
                        if (shown < examples)
                        {
                            shown++;
                            var mineName = eval.Best is { } b2 ? Short(b2.Id) : "스킵";
                            lines.Add($"{Short(r.Character)} op.gg {r.RunId} {d.StateBefore.Floor}층 {target.Id} commit {primary.Commit:0.00} 코어 {primary.OwnedCore}/{primary.CoreTarget}: "
                                      + $"엔진 {mineName} {mine.Total:0.0} [{Parts(mine)}] vs 랭커 {Short(theirs.Card.Id)} {theirs.Total:0.0} [{Parts(theirs.Score)}]");
                        }
                    }
                    if (!rIn && eIn)
                    {
                        row.EngineOverBuild++;
                        if (rankerPick is null) row.OverRankerSkip++;
                        var mine = eval.Cards.First(e => e.Card == eval.Best!.Value).Score;
                        var theirs = rankerPick is { } rp2 ? eval.Cards.First(e => e.Card == rp2).Score : eval.Skip;
                        foreach (var k in mine.Components.Select(x => x.Key).Concat(theirs.Components.Select(x => x.Key)).Distinct())
                        {
                            overDiff[k] = overDiff.GetValueOrDefault(k) + mine.ValueOf(k) - theirs.ValueOf(k);
                        }
                        if (overShown < examples / 2)
                        {
                            overShown++;
                            var theirName = rankerPick is { } rp3 ? Short(rp3.Id) : "스킵";
                            lines.Add($"(엔진만 빌드) {Short(r.Character)} op.gg {r.RunId} {d.StateBefore.Floor}층 {target.Id} commit {primary.Commit:0.00}: "
                                      + $"엔진 {Short(eval.Best!.Value.Id)} {mine.Total:0.0} [{Parts(mine)}] vs 랭커 {theirName} {theirs.Total:0.0} [{Parts(theirs)}]");
                        }
                    }
                }
            }
            row.MeanDiff = diff.ToDictionary(kv => kv.Key, kv => row.EngineLeftBuild == 0 ? 0 : kv.Value / row.EngineLeftBuild, StringComparer.Ordinal);
            row.OverDiff = overDiff.ToDictionary(kv => kv.Key, kv => row.EngineOverBuild == 0 ? 0 : kv.Value / row.EngineOverBuild, StringComparer.Ordinal);
            yield return row;
        }
    }

    /// <summary>
    /// One CSV row per offered card on every ranker reward screen (shop and single-card screens left out), with the engine's score and
    /// rank and the ranker's pick, for per-card calibration (<c>--cards file.csv</c>). Join players by run id outside (single-player runs store player id 1).
    /// </summary>
    private static void WriteCardRows(DataPack pack, List<RankerRun> rankers, string file)
    {
        var engine = new RecommendationEngine(pack);
        using var w = new StreamWriter(file, false, new UTF8Encoding(false));
        w.WriteLine("run,character,screen,act,floor,context,card,upgrade,ranker_pick,ranker_skip,engine_best,engine_skip,engine_rank,total,skip_total,base,build,primary,primary_commit,primary_established,role,deck_size,need_frontload,need_aoe,need_block,need_scaling,need_draw,need_other,components");
        foreach (var r in rankers)
        {
            var screen = 0;
            foreach (var d in DecisionExtractor.Extract(r.Run, r.PlayerId, r.Timeline))
            {
                if (d.Context == OfferContext.Shop || d.Offered.Count < 2) continue;
                screen++;
                var profile = DeckAnalyzer.Analyze(d.StateBefore, pack);
                var eval = engine.EvaluateOffer(d.StateBefore, profile, d.Offered, d.Context);
                var rankerPick = d.Picked.Count > 0 ? d.Picked[0] : (CardRef?)null;
                var order = eval.Cards.OrderByDescending(e => e.Total).Select(e => e.Card).ToList();
                var primary = profile.Primary;
                foreach (var e in eval.Cards)
                {
                    var id = pack.Canonical(e.Card.Id);
                    var role = primary?.Archetype.RoleOf(id) ?? "";
                    // Need split by role (CardScorer: WNeed × gap × urgency × strength), for per-role calibration.
                    var entry = pack.FindTier(r.Character, id);
                    var urgency = Weights.ForAct(pack.Weights.Urgency, d.StateBefore.Act, new Dictionary<string, double>(StringComparer.Ordinal));
                    var needByRole = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var (needRole, gap) in profile.RoleGap)
                    {
                        var v = pack.Weights.WNeed * gap * urgency.GetValueOrDefault(needRole, 1.0) * (entry?.Roles.GetValueOrDefault(needRole) ?? 0.0);
                        if (!double.IsFinite(v)) continue;
                        var k = needRole is Roles.Frontload or Roles.Aoe or Roles.Block or Roles.Scaling or Roles.Draw ? needRole : "other";
                        needByRole[k] = needByRole.GetValueOrDefault(k) + v;
                    }
                    string N(string k) => needByRole.GetValueOrDefault(k).ToString("0.##", Inv);
                    var comps = string.Join(";", e.Score.Components.Where(c => Math.Abs(c.Value) > 1e-6).Select(c => $"{c.Key}={c.Value.ToString("0.##", Inv)}"));
                    w.WriteLine(string.Join(",",
                        r.RunId, r.Character, screen.ToString(Inv), d.StateBefore.Act.ToString(Inv), d.StateBefore.Floor.ToString(Inv), d.Context,
                        id, e.Card.Upgrade.ToString(Inv),
                        rankerPick == e.Card ? "1" : "0", rankerPick is null ? "1" : "0",
                        eval.Best == e.Card ? "1" : "0", eval.Best is null ? "1" : "0",
                        order.IndexOf(e.Card).ToString(Inv),
                        e.Total.ToString("0.##", Inv), eval.Skip.Total.ToString("0.##", Inv),
                        e.Score.ValueOf(ComponentKeys.Base).ToString("0.##", Inv), e.Score.ValueOf(ComponentKeys.Build).ToString("0.##", Inv),
                        primary?.Archetype.Id ?? "", (primary?.Commit ?? 0).ToString("0.###", Inv), primary is not null && Established(primary) ? "1" : "0",
                        role, d.StateBefore.Deck.Count.ToString(Inv),
                        N(Roles.Frontload), N(Roles.Aoe), N(Roles.Block), N(Roles.Scaling), N(Roles.Draw), N("other"), comps));
                }
            }
        }
    }

    /// <summary>A build is established once it owns its core target (builds without a core list: commit ≥ 0.4).</summary>
    internal static bool Established(Sts2Overlay.Core.Archetypes.ArchetypeStatus s)
        => s.CoreTarget > 0 ? s.OwnedCore >= s.CoreTarget : s.Commit >= 0.4;

    // =============================================================================================
    // Leak diagnosis on simulated engine runs
    // =============================================================================================

    /// <summary>
    /// A "leak" = a reward screen where the deck's main build is established (core secured, or commit ≥ 0.4 when the build has no
    /// core list), the offer has a card listed in that build, and the engine took a card outside it (or skipped).
    /// For each leak: the mean component difference (taken card − best build card) tells which score part decided it.
    /// </summary>
    private static IEnumerable<LeakRow> Leaks(DataPack pack, Dictionary<string, SimRun[]> runs, int examples, out List<string> lines)
    {
        lines = [];
        var engine = new RecommendationEngine(pack);
        var rows = new List<LeakRow>();
        foreach (var (c, list) in runs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var row = new LeakRow { Character = c };
            var diff = new Dictionary<string, double>(StringComparer.Ordinal);
            var shown = 0;
            foreach (var run in list)
            {
                foreach (var d in run.Decisions)
                {
                    if (d.Context == OfferContext.Shop) continue;
                    var profile = DeckAnalyzer.Analyze(d.StateBefore, pack);
                    var primary = profile.Primary;
                    if (primary is null) continue;
                    if (!Established(primary)) continue;
                    var buildCards = d.Offered.Where(o => primary.Archetype.RoleOf(pack.Canonical(o.Id)) is not null).ToList();
                    if (buildCards.Count == 0) continue;
                    row.Eligible++;
                    var coreless = primary.CoreTarget == 0;
                    if (coreless) row.EligibleCoreless++;
                    var picked = d.Picked.Count > 0 ? d.Picked[0] : (CardRef?)null;
                    if (picked is { } p && primary.Archetype.RoleOf(pack.Canonical(p.Id)) is not null) continue;
                    if (picked is not null && coreless) row.LeakedCoreless++;
                    var eval = engine.EvaluateOffer(d.StateBefore, profile, d.Offered, d.Context);
                    var bestBuild = eval.Cards.Where(e => buildCards.Contains(e.Card)).OrderByDescending(e => e.Total).First();
                    if (picked is null)
                    {
                        row.Skipped++;
                        continue;
                    }
                    row.Leaked++;
                    // Critical = the passed build card is a core / enabler / payoff the deck does not own yet; flexible = a support card or
                    // another copy of an owned build card (rankers take good generic cards over those: DECK_SIM §5 mixed screens 50 %).
                    var passedId = pack.Canonical(bestBuild.Card.Id);
                    var passedRole = primary.Archetype.RoleOf(passedId);
                    if (passedRole is ArchetypeRoles.Core or ArchetypeRoles.Enabler or ArchetypeRoles.Payoff && profile.Copies.GetValueOrDefault(passedId) == 0)
                    {
                        row.LeakedCritical++;
                    }
                    var taken = eval.Cards.First(e => e.Card == picked);
                    var tid = pack.Canonical(taken.Card.Id);
                    var drawCard = (pack.FindTier(c, tid)?.Roles.GetValueOrDefault(Roles.Draw) ?? 0) >= DeckMetrics.DrawRoleMin;
                    if (drawCard) row.LeakedToDraw++;
                    if (primary.Archetype.IsOnTheme(tid, pack.FindTier(c, tid), pack.Canonical)) row.LeakedToOnTheme++;
                    var keys = taken.Score.Components.Select(x => x.Key).Concat(bestBuild.Score.Components.Select(x => x.Key)).Distinct();
                    foreach (var k in keys) diff[k] = diff.GetValueOrDefault(k) + taken.Score.ValueOf(k) - bestBuild.Score.ValueOf(k);
                    if (shown < examples)
                    {
                        shown++;
                        lines.Add($"{Short(c)} {d.Act}막 {d.Floor}층 {primary.Archetype.Id} commit {primary.Commit:0.00} 코어 {primary.OwnedCore}/{primary.CoreTarget}: "
                                  + $"추천 {Short(taken.Card.Id)} {taken.Total:0.0} [{Parts(taken)}] vs 빌드 {Short(bestBuild.Card.Id)} {bestBuild.Total:0.0} [{Parts(bestBuild)}]");
                    }
                }
            }
            row.MeanDiff = diff.ToDictionary(kv => kv.Key, kv => row.Leaked == 0 ? 0 : kv.Value / row.Leaked, StringComparer.Ordinal);
            rows.Add(row);
        }
        return rows;
    }

    private static string Parts(CardEvaluation e)
        => Parts(e.Score);

    private static string Parts(ScoreBreakdown b)
        => string.Join(" ", b.Components.Select(c => $"{c.Key} {c.Value.ToString("+0.#;-0.#", Inv)}"));

    internal static string Short(string id)
    {
        var dot = id.IndexOf('.');
        return dot >= 0 ? id[(dot + 1)..] : id;
    }

    private static double SkipRate(SimRun[] runs, int act)
    {
        var screens = runs.SelectMany(r => r.Decisions).Where(d => d.Act == act && d.Context != OfferContext.Shop).ToList();
        return screens.Count == 0 ? 0 : (double)screens.Count(d => d.Picked.Count == 0) / screens.Count;
    }

    private static IEnumerable<Agg> ByBuild(string source, string character, List<DeckMetrics> finals)
        => finals.Where(m => m.Primary is not null).GroupBy(m => m.Primary!.Archetype.Id)
            .Where(g => g.Count() >= 5)
            .Select(g =>
            {
                var agg = Agg.Of(source, character, 3, g.Select(Row.Of).ToList());
                agg.Build = g.Key;
                return agg;
            });

    private static BuildRow BuildDist(string source, string character, IEnumerable<DeckMetrics> finals)
    {
        var list = finals.ToList();
        var row = new BuildRow { Source = source, Character = character, Runs = list.Count };
        foreach (var g in list.GroupBy(m => m.Primary?.Archetype.Id ?? "(none)"))
        {
            row.Share[g.Key] = (double)g.Count() / list.Count;
            row.CompleteGiven[g.Key] = (double)g.Count(m => m.PrimaryComplete) / g.Count();
        }
        return row;
    }

    private static IEnumerable<AxisRow> Axes(DataPack pack, string source, string character, List<IReadOnlyList<DeckCard>> decks)
    {
        var w = pack.Weights.Saturation;
        var groups = new Dictionary<(string Build, string Axis), List<(int P, int C)>>();
        foreach (var deck in decks)
        {
            var metrics = DeckMetrics.Compute(pack, character, deck);
            if (metrics.Primary is not { } primary) continue;
            var profile = DeckAnalyzer.Analyze(new RunState { Character = character, Deck = deck, Act = 3 }, pack);
            foreach (var need in primary.Archetype.Needs.Where(n => !string.IsNullOrEmpty(n.Tag)))
            {
                var key = (primary.Archetype.Id, need.Tag!);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                list.Add((profile.Provides.GetValueOrDefault(need.Tag!), profile.Consumes.GetValueOrDefault(need.Tag!)));
            }
        }
        foreach (var ((build, axis), list) in groups.Where(g => g.Value.Count >= 5).OrderBy(g => g.Key.Build, StringComparer.Ordinal))
        {
            double Factor((int P, int C) x)
            {
                var excess = x.P - w.Ratio * Math.Max(1, x.C);
                return excess <= 0 ? 1.0 : Math.Max(w.MinFactor, 1 - w.PerExcess * excess);
            }
            yield return new AxisRow(source, build, axis, list.Count, list.Average(x => x.P), list.Average(x => x.C),
                list.Count(x => Factor(x) < 1.0) / (double)list.Count, list.Average(Factor));
        }
    }

    /// <summary>Weight overrides for calibration sweeps (<c>--set skip.t0.3=52</c>); returns false for an unknown key.</summary>
    private static bool Override(Weights w, string key, double v)
    {
        switch (key)
        {
            case "skip.t0.1": w.Skip.T0[1] = v; return true;
            case "skip.t0.2": w.Skip.T0[2] = v; return true;
            case "skip.t0.3": w.Skip.T0[3] = v; return true;
            case "skip.lambdaSize": w.Skip.LambdaSize = v; return true;
            case "skip.lambdaCommit": w.Skip.LambdaCommit = v; return true;
            case "skip.basicPickBias.1": w.Skip.BasicPickBias[1] = v; return true;
            case "skip.basicPickBias.2": w.Skip.BasicPickBias[2] = v; return true;
            case "lambdaDilution": w.LambdaDilution = v; return true;
            case "rhoDuplicate": w.RhoDuplicate = v; return true;
            case "buildDuplicateFactor": w.BuildDuplicateFactor = v; return true;
            case "offBuildPenalty": w.OffBuildPenalty = v; return true;
            case "saturation.exemptBuildAxes": w.Saturation.ExemptBuildAxes = v != 0; return true;
            case "corePriority.securedEssentialFloor": w.CorePriority.SecuredEssentialFloor = v; return true;
            case "corePriority.noCoreFactor": foreach (var k in w.CorePriority.NoCoreFactor.Keys.ToList()) w.CorePriority.NoCoreFactor[k] = v; return true;
            case "idealDeckSize.3": w.IdealDeckSize[3] = (int)v; return true;
            default: return false;
        }
    }

    /// <summary>One tab-separated line for sweeps: replay top-1, skip per act (ranker/engine), mixed-screen build picks, sim leaks and dupes.</summary>
    private static string OneLine(Summary s, List<(string Key, double Value)> overrides)
    {
        var a = s.Agreement;
        int screens = a.Sum(x => x.Screens), top1 = a.Sum(x => x.Top1), mixed = a.Sum(x => x.Mixed);
        string Skip(int act) => $"{100.0 * a.Sum(x => x.RankerSkipsByAct[act - 1]) / Math.Max(1, a.Sum(x => x.ScreensByAct[act - 1])):0}/"
                                + $"{100.0 * a.Sum(x => x.EngineSkipsByAct[act - 1]) / Math.Max(1, a.Sum(x => x.ScreensByAct[act - 1])):0}";
        var leaks = s.Leaks;
        var eng3 = s.Rows.Where(r => r.Source == "engine" && r.Act == 3).ToList();
        var rk3 = s.Rows.Where(r => r.Source == "ranker" && r.Act == 3).ToList();
        string Mean(List<Agg> rows, string key) => rows.Count == 0 ? "-" : rows.Average(r => r.Mean.GetValueOrDefault(key)).ToString("0.00", Inv);
        var simSkip = s.Skip.Where(x => x.Source == "engine").ToList();
        string SimSkip(int act) => simSkip.Count == 0 ? "-" : (100 * simSkip.Average(x => x.ByAct[act - 1])).ToString("0", Inv);
        return string.Join('\t',
            string.Join(' ', overrides.Select(o => $"{o.Key}={o.Value.ToString(Inv)}")) is { Length: > 0 } o2 ? o2 : "(default)",
            $"top1 {100.0 * top1 / Math.Max(1, screens):0.0}",
            $"skip r/e {Skip(1)} {Skip(2)} {Skip(3)}",
            $"sim skip {SimSkip(1)}/{SimSkip(2)}/{SimSkip(3)}",
            $"mixed r/e {100.0 * a.Sum(x => x.RankerTookBuild) / Math.Max(1, mixed):0}/{100.0 * a.Sum(x => x.EngineTookBuild) / Math.Max(1, mixed):0}",
            $"leak {100.0 * leaks.Sum(l => l.Leaked) / Math.Max(1, leaks.Sum(l => l.Eligible)):0.0} (critical {100.0 * leaks.Sum(l => l.LeakedCritical) / Math.Max(1, leaks.Sum(l => l.Eligible)):0.0})",
            $"dupes e/r {Mean(eng3, "BuildDupes")}/{Mean(rk3, "BuildDupes")}",
            $"size e/r {Mean(eng3, "Size")}/{Mean(rk3, "Size")}",
            $"coh e/r {Mean(eng3, "Coherence")}/{Mean(rk3, "Coherence")}",
            $"core e/r {Mean(eng3, "Core")}/{Mean(rk3, "Core")}");
    }

    private static void Write(string text, string? file)
    {
        if (file is null) Console.Out.Write(text);
        else
        {
            if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } dir) Directory.CreateDirectory(dir);
            File.WriteAllText(file, text, new UTF8Encoding(false));
        }
    }
}

// =================================================================================================
// Summary model (also written as JSON for before/after comparisons)
// =================================================================================================

internal sealed class Summary
{
    public int Simulations { get; set; }
    public int Ascension { get; set; }
    public bool Multiplayer { get; set; }
    public List<Agg> Rows { get; set; } = [];

    /// <summary>Act-3 decks grouped by their main build (builds with at least 5 decks in a source).</summary>
    public List<Agg> ByBuild { get; set; } = [];
    public List<SkipRow> Skip { get; set; } = [];
    public List<BuildRow> Builds { get; set; } = [];
    public List<AgreementRow> Agreement { get; set; } = [];
    public List<string> AgreementExamples { get; set; } = [];
    public List<LeakRow> Leaks { get; set; } = [];
    public List<string> LeakExamples { get; set; } = [];
    public List<AxisRow> Axes { get; set; } = [];
}

/// <summary>Act-3 decks whose main build needs <see cref="Axis"/> (a <c>needs.tag</c>): producers vs consumers of that axis.</summary>
internal sealed record AxisRow(string Source, string Build, string Axis, int Decks, double Produced, double Consumed, double OverShare, double MeanFactor);

internal sealed record Row(double Size, double Commit, double Complete, double Core, double Coherence, double Generic, double Other,
    double Builds, double Draw, double OffDraw, double MeanCost, double Cost0, double Cost1, double Cost2, double Cost3, double CostX,
    double EnergyPerCard, double Powers, double BuildDupes, double OtherDupes)
{
    public static Row Of(DeckMetrics m)
    {
        var chosen = Math.Max(1, m.ChosenCards);
        return new Row(m.DeckSize, m.PrimaryCommit, m.PrimaryComplete ? 1 : 0, m.PrimaryCoreSecured ? 1 : 0, m.Coherence, m.GenericShare,
            (double)m.Fit.GetValueOrDefault(CardFit.OtherBuild) / chosen, m.BuildsTouched, m.DrawCards, m.OffBuildDrawCards, m.MeanCost,
            (double)m.CostBuckets[0] / chosen, (double)m.CostBuckets[1] / chosen, (double)m.CostBuckets[2] / chosen, (double)m.CostBuckets[3] / chosen,
            (double)m.CostBuckets[4] / chosen, m.EnergyPerCard, m.Powers, m.BuildExtraCopies, m.OtherExtraCopies);
    }
}

internal sealed class Agg
{
    public string Source { get; set; } = "";
    public string Character { get; set; } = "";
    public string? Build { get; set; }
    public int Act { get; set; }
    public int N { get; set; }
    public Dictionary<string, double> Mean { get; set; } = new(StringComparer.Ordinal);

    public static Agg Of(string source, string character, int act, List<Row> rows)
    {
        var agg = new Agg { Source = source, Character = character, Act = act, N = rows.Count };
        foreach (var p in typeof(Row).GetProperties().Where(p => p.PropertyType == typeof(double)))
        {
            agg.Mean[p.Name] = rows.Count == 0 ? 0 : rows.Average(r => (double)p.GetValue(r)!);
        }
        return agg;
    }
}

internal sealed record SkipRow(string Source, string Character, double[] ByAct);

internal sealed class BuildRow
{
    public string Source { get; set; } = "";
    public string Character { get; set; } = "";
    public int Runs { get; set; }
    public Dictionary<string, double> Share { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> CompleteGiven { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class AgreementRow
{
    public string Character { get; set; } = "";
    public int Screens { get; set; }
    public int Top1 { get; set; }
    public int RankerSkips { get; set; }
    public int EngineSkips { get; set; }
    public int Mixed { get; set; }
    public int RankerTookBuild { get; set; }
    public int EngineTookBuild { get; set; }
    public int EngineLeftBuild { get; set; }
    public int EngineOverBuild { get; set; }
    public int[] ScreensByAct { get; set; } = new int[3];
    public int[] RankerSkipsByAct { get; set; } = new int[3];
    public int[] EngineSkipsByAct { get; set; } = new int[3];

    /// <summary>Ranker took a card that is the engine's best card (skip aside) / second-best card / engine said skip.</summary>
    public int[] RankerCardFirst { get; set; } = new int[3];
    public int[] RankerCardSecond { get; set; } = new int[3];
    public int[] EngineSkipOnPick { get; set; } = new int[3];
    public int LeanMixed { get; set; }
    public int LeanRankerBuild { get; set; }
    public int LeanEngineBuild { get; set; }
    public int LeftForDraw { get; set; }
    public int LeftForSkip { get; set; }
    public int OverRankerSkip { get; set; }
    public Dictionary<string, double> OverDiff { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> MeanDiff { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class LeakRow
{
    public string Character { get; set; } = "";
    public int Eligible { get; set; }
    public int Leaked { get; set; }
    public int LeakedCritical { get; set; }
    public int Skipped { get; set; }
    public int LeakedToDraw { get; set; }
    public int LeakedToOnTheme { get; set; }
    public int EligibleCoreless { get; set; }
    public int LeakedCoreless { get; set; }
    public Dictionary<string, double> MeanDiff { get; set; } = new(StringComparer.Ordinal);
}
