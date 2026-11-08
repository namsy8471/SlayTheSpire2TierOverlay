using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Map;

/// <summary>
/// Map path recommendation (PLAN §7.6): the best <see cref="PathWeights.TopK"/> paths from the current node to the boss
/// for the current HP, gold and deck, plus a ranking of the immediate next nodes.
/// <para>Three stages:</para>
/// <list type="number">
/// <item><b>Candidates.</b> The real score is not additive (HP carried along the path changes what each room is worth),
/// so a k-best DP (<see cref="MapPaths.BestPaths"/>) first ranks suffixes by an additive per-node estimate. When a node
/// has at most <see cref="PathWeights.CandidateLimit"/> paths (true for real maps) this yields every path, so nothing
/// is lost; on bigger synthetic maps it keeps the most promising ones.</item>
/// <item><b>Elite cap (monotone guard).</b> Rule: <i>lower HP never recommends more elites than higher HP would.</i>
/// An expected-value simulation does not guarantee that by itself: HP and rest sites are substitutes, so at mid HP a
/// rest-heavy route with more elites can out-score a lean one. We therefore walk an HP grid from 100% down to the
/// current HP in 1% steps; at each step the best path may have at most as many elites as the step above chose. Every
/// call walks the same chain from the top, so the elite count is non-decreasing in HP by construction.</item>
/// <item><b>Exact scoring.</b> Every candidate is scored by <see cref="PathScorer"/> at the current HP (floored to a
/// whole percent so it lines up with the grid). Paths within the cap are ranked first, best score first.</item>
/// </list>
/// </summary>
public sealed class PathAdvisor
{
    private readonly DataPack _pack;

    public PathAdvisor(DataPack pack)
    {
        _pack = pack;
    }

    /// <summary>
    /// Recommends paths from <see cref="MapGraph.Current"/> (or the act start's children before the first step).
    /// Returns an empty result when the current node is the boss, has no way to the boss, or is not on the map.
    /// </summary>
    public PathRecommendation Recommend(RunState state, MapGraph map)
    {
        var origin = map.Current ?? map.Start;
        var children = MapPaths.ValidChildren(map, origin);
        if (children.Count == 0) return new PathRecommendation { Origin = origin, Exhaustive = true };

        var p = _pack.Weights.Path;
        var baseScorer = new PathScorer(_pack, state, map, DeckAnalyzer.Analyze(state, _pack));
        var hpPercent = (int)Math.Floor(baseScorer.StartHp * 100 + 1e-9);
        var scorer = baseScorer.WithHp(hpPercent / 100.0);

        // 1. Candidates, per child (not per origin) so every next node is represented even if the beam is tight.
        var kbest = MapPaths.BestPaths(map, scorer.NodeEstimate, p.CandidateLimit);
        var candidates = new List<Candidate>();
        foreach (var child in children)
        {
            foreach (var suffix in kbest.Suffixes(child))
            {
                MapCoord[] coords = [child, .. suffix];
                var types = scorer.TypesOf(coords);
                candidates.Add(new Candidate(coords, types, types.Count(t => t == MapNodeType.Elite)));
            }
        }

        var totalPaths = MapPaths.CountPaths(map, origin);
        var result = new PathRecommendation
        {
            Origin = origin,
            TotalPaths = totalPaths,
            Exhaustive = candidates.Count >= totalPaths,
            DeckStrength = scorer.DeckStrength,
            ScoredHp = scorer.StartHp,
        };
        if (candidates.Count == 0) return result;

        // 2. Elite cap from the HP grid above the current HP.
        var cap = EliteCap(baseScorer, candidates, hpPercent);

        // 3. Score at the current HP; in-cap paths first, then by score (ties: fewer elites; OrderBy is stable, so
        //    remaining ties keep DP order and the output is deterministic).
        var scored = candidates.Select(c => scorer.Score(c.Coords)).ToList();
        bool InCap(PathOption o) => cap is not { } k || o.Elites <= k;
        var ordered = scored
            .OrderBy(o => InCap(o) ? 0 : 1)
            .ThenByDescending(o => o.Total)
            .ThenBy(o => o.Elites)
            .ToList();

        var stats = new CandidateStats(
            MinFights: scored.Min(o => o.Fights),
            MaxElites: scored.Max(o => o.Elites),
            MaxMonsters: scored.Max(o => o.CountOf(MapNodeType.Monster)),
            CapBinds: scored.Any(o => !InCap(o) && o.Total > ordered[0].Total));

        var top = ordered.Take(Math.Max(1, p.TopK))
            .Select((o, i) => o with { Chips = BuildChips(o, i == 0, InCap(o), state, scorer.DeckStrength, p, stats) })
            .ToArray();

        // Next steps in the same order as the full ranking: each child appears where its best path first appears.
        var nextSteps = ordered
            .GroupBy(o => o.Coords[0]) // GroupBy keeps first-appearance order
            .Select((g, i) => new NextStepOption(g.Key, map.Nodes[g.Key].Type, g.First().Total, IsBest: i == 0))
            .ToArray();

        return result with { Top = top, NextSteps = nextSteps, EliteCap = cap };
    }

    /// <summary>Scores one given path (origin excluded), e.g. the path the user is hovering in the UI.</summary>
    public PathOption ScorePath(RunState state, MapGraph map, IReadOnlyList<MapCoord> coords)
        => new PathScorer(_pack, state, map, DeckAnalyzer.Analyze(state, _pack)).Score(coords);

    private sealed record Candidate(MapCoord[] Coords, MapNodeType[] Types, int Elites);

    private sealed record CandidateStats(int MinFights, int MaxElites, int MaxMonsters, bool CapBinds);

    /// <summary>
    /// Walks HP = 100%, 99%, ... down to <paramref name="hpPercent"/> + 1. At each step picks the best candidate whose
    /// elite count is within the cap so far, and lowers the cap to that path's elite count. Returns the cap that applies
    /// at <paramref name="hpPercent"/>, or null at 100% HP (no cap).
    /// <para>
    /// Why this is monotone: the chain does not depend on where it stops, so a call at 40% HP and a call at 70% HP share
    /// the steps 100%…71%, and every further step can only keep or lower the elite count.
    /// </para>
    /// </summary>
    private static int? EliteCap(PathScorer baseScorer, IReadOnlyList<Candidate> candidates, int hpPercent)
    {
        int? cap = null;
        for (var pct = 100; pct > hpPercent; pct--)
        {
            var s = baseScorer.WithHp(pct / 100.0);
            Candidate? best = null;
            var bestTotal = double.NegativeInfinity;
            foreach (var c in candidates)
            {
                if (cap is { } k && c.Elites > k) continue;
                var total = s.Total(c.Types);
                if (total > bestTotal || (total == bestTotal && best is not null && c.Elites < best.Elites))
                {
                    best = c;
                    bestTotal = total;
                }
            }
            cap = best?.Elites ?? cap;
        }
        return cap;
    }

    /// <summary>
    /// Counts first (what the path is), then state-driven reasons (why it fits now), then risks.
    /// "Relative" chips (전투 적은 길, 엘리트로 유물) compare against all scored paths, not only the top 3.
    /// </summary>
    private static IReadOnlyList<string> BuildChips(PathOption o, bool isTop, bool inCap, RunState state, double strength,
        PathWeights p, CandidateStats stats)
    {
        var chips = new List<string>();

        chips.Add(o.Elites > 0 ? $"엘리트 {o.Elites}" : "엘리트 없음");

        var restBeforeBoss = o.Types.Count >= 2 && o.Types[^1] == MapNodeType.Boss && o.Types[^2] == MapNodeType.RestSite;
        if (o.Rests > 0) chips.Add(restBeforeBoss ? $"휴식처 {o.Rests} (보스 전 휴식 있음)" : $"휴식처 {o.Rests}");
        else chips.Add("휴식처 없음");

        var shops = o.CountOf(MapNodeType.Shop);
        if (shops > 0)
        {
            chips.Add(o.ShopPurchases > 0 ? $"상점 {shops} (골드 활용)" : $"상점 {shops}");
        }
        var treasures = o.CountOf(MapNodeType.Treasure);
        if (treasures > 0) chips.Add($"보물 {treasures}");
        var events = o.CountOf(MapNodeType.Unknown);
        if (events > 0) chips.Add($"? {events}");

        chips.Add($"보스 도착 HP 예상 {Math.Round(o.ExpectedBossHp * 100):0}%");

        var hp = state.HpRatio;
        if (hp < p.LowHpThreshold && o.Fights == stats.MinFights) chips.Add("HP 낮음: 전투 적은 길");
        if (hp >= p.EliteHpThreshold && o.Elites > 0 && o.Elites == stats.MaxElites) chips.Add("HP 충분: 엘리트로 유물");
        if (strength < 0.35 && hp >= p.LowHpThreshold && o.CountOf(MapNodeType.Monster) == stats.MaxMonsters && stats.MaxMonsters > 0)
        {
            chips.Add("덱 약함: 카드 보상 많은 길");
        }
        if (isTop && stats.CapBinds) chips.Add("HP 고려: 엘리트 수 제한");
        if (!inCap) chips.Add("HP 대비 엘리트 많음");

        if (o.Risks.Contains(PathRisks.EliteChain)) chips.Add("위험: 엘리트 연속");
        if (o.Risks.Contains(PathRisks.EliteNoRest)) chips.Add("위험: 휴식 없이 엘리트");
        return chips;
    }
}
