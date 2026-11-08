using System.Globalization;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Rest;

/// <summary>
/// Rest site: 휴식 (heal) or 강화 (smith), and which card to upgrade.
/// <para><b>Idea.</b> HP is only worth what it buys on the rest of the act. So instead of a fixed "heal below X%" rule
/// we reuse the path simulation (<see cref="PathScorer"/>): walk the rest of the act twice, once starting with the
/// healed HP and once with the current HP and one more upgraded card, and compare.</para>
/// <list type="bullet">
/// <item><b>Heal value</b> = (path score after healing) − (path score after smithing). The simulation already knows
/// the measured HP loss per room and act, the death chance per fight and at the boss, the future rest sites and the
/// healing between rooms ([측정] numbers in <see cref="PathWeights"/>). So healing is worth a lot right before the boss
/// or before an elite at low HP, and little when the next rooms are easy or another rest site comes soon.</item>
/// <item><b>Smith value</b> = the best upgrade's score from <see cref="UpgradeAdvisor"/> (card-score points; the path
/// points are on the same scale by design, see <see cref="PathWeights"/>).</item>
/// <item>Whichever is larger wins; a tie goes to smithing (an upgrade lasts the whole run, healed HP does not).
/// Within <see cref="Weights.ConfidenceCloseGap"/> points the call is 박빙.</item>
/// </list>
/// <para><b>Which path?</b> With a map, the best few paths of <see cref="PathAdvisor"/> from the rest site are scored
/// for both options and each option takes its best one. Without a usable map the advisor falls back to the HP
/// threshold measured from the user's own rest-site choices (<see cref="PathWeights.RestHealBelowHp"/>).</para>
/// <para>Only heal and smith are modelled; relic options (dig, cook, …) are ignored.</para>
/// <para><b>Measured</b> (243 recorded heal/smith choices, all players, <c>RunHistoryAnalyzer rest-advice</c>; a proxy,
/// not a target): agrees 62% (69% when 확신). It heals more often than the players did (77% vs 45%), almost all of the
/// difference at 50–90% HP, where the simulation values the removed boss/fight death risk (+8 to +18 points) above an
/// upgrade (~6 points). That trade-off rests on the [추정] <see cref="PathWeights.DeathCost"/> /
/// <see cref="PathWeights.BossDeathCost"/>; the death model itself is roughly calibrated on the same runs (the
/// players died in 24 of 91 boss fights, more than it predicts), so the cautious lean is kept rather than tuned away.</para>
/// </summary>
public sealed class RestSiteAdvisor
{
    private readonly DataPack _pack;
    private readonly PathWeights? _path;
    private readonly UpgradeAdvisor _upgrades;

    /// <param name="pack">Data pack (tiers for upgrade values, weights for the path simulation).</param>
    /// <param name="path">Path weights to use instead of <c>pack.Weights.Path</c> (for experiments).</param>
    public RestSiteAdvisor(DataPack pack, PathWeights? path = null)
    {
        _pack = pack;
        _path = path;
        _upgrades = new UpgradeAdvisor(pack);
    }

    private PathWeights P => _path ?? _pack.Weights.Path;

    /// <summary>
    /// Advice for the rest site the player is standing on. <paramref name="map"/> should be the current act with
    /// <see cref="MapGraph.Current"/> = the rest site; null (or a map without <see cref="MapGraph.Current"/>, or with no way
    /// to the boss) uses the HP-threshold fallback.
    /// </summary>
    public RestSiteAdvice Advise(RunState run, MapGraph? map)
    {
        ArgumentNullException.ThrowIfNull(run);
        var profile = DeckAnalyzer.Analyze(run, _pack);
        var upgrades = _upgrades.Rank(run, profile);
        var hp = run.HpRatio;

        // Trivial cases first: they need no simulation and are always "sure".
        if (upgrades.Count == 0)
        {
            return new RestSiteAdvice { Action = RestAction.Heal, Confidence = RestConfidence.Sure, Reason = "강화할 카드가 없어 휴식", Upgrades = upgrades };
        }
        if (run.MaxHp > 0 && run.Hp >= run.MaxHp)
        {
            return new RestSiteAdvice { Action = RestAction.Smith, Confidence = RestConfidence.Sure, Reason = "HP가 가득 차 있어 강화", Upgrades = upgrades };
        }

        // Without a current node the map cannot say where we are (Recommend would start at the act start), so fall back.
        var paths = map?.Current is null ? [] : new PathAdvisor(_pack, _path).Recommend(run, map).Top.Select(o => o.Coords).ToArray();
        if (map is null || paths.Length == 0) return Fallback(run, upgrades);

        // The two futures. Healing: more HP, same deck. Smithing: same HP, the best card upgraded.
        var healed = run with { Hp = Math.Min(run.MaxHp, run.Hp + (int)Math.Round(P.RestHeal * run.MaxHp)) };
        var smithed = run with { Deck = UpgradeOne(run.Deck, upgrades[0].Card) };
        var healScorer = new PathScorer(_pack, healed, map, profile, _path);
        var smithScorer = new PathScorer(_pack, smithed, map, DeckAnalyzer.Analyze(smithed, _pack), _path);
        var healPath = paths.Select(healScorer.Score).MaxBy(o => o.Total)!;
        var smithPath = paths.Select(smithScorer.Score).MaxBy(o => o.Total)!;

        var healScore = Difference(healPath.Score, smithPath.Score);
        var best = upgrades[0];
        var smithScore = new ScoreBreakdown([new ScoreComponent(RestComponentKeys.Upgrade, "카드 강화", best.Total, best.Card.Id)]);

        var heal = healScore.Total > smithScore.Total;
        var gap = Math.Abs(healScore.Total - smithScore.Total);
        var outlook = Outlook(heal ? healPath : smithPath, healPath, smithPath, healed.HpRatio);
        return new RestSiteAdvice
        {
            Action = heal ? RestAction.Heal : RestAction.Smith,
            Confidence = gap < _pack.Weights.ConfidenceCloseGap ? RestConfidence.Close : RestConfidence.Sure,
            Reason = Reason(heal, hp, outlook),
            HealScore = healScore,
            SmithScore = smithScore,
            Upgrades = upgrades,
            Outlook = outlook,
        };
    }

    /// <summary>
    /// Advice when only the room types ahead are known (after the rest site, the boss last; a missing boss is added).
    /// Builds a one-path map with <see cref="ChainMap"/>; the run-history backtest uses this with the rooms actually walked.
    /// </summary>
    public RestSiteAdvice Advise(RunState run, IReadOnlyList<MapNodeType> pathAhead)
        => Advise(run, ChainMap([MapNodeType.RestSite], pathAhead, run.Act));

    /// <summary>
    /// A map that is a single chain: <paramref name="visited"/> (act start first, the current room last) followed by
    /// <paramref name="ahead"/>, ending at a boss (added when <paramref name="ahead"/> does not end with one).
    /// <see cref="MapGraph.Current"/> is the last visited room and <see cref="MapGraph.Visited"/> lists all of them, so
    /// elites already fought this act count like on a real map.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="visited"/> is empty.</exception>
    public static MapGraph ChainMap(IReadOnlyList<MapNodeType> visited, IReadOnlyList<MapNodeType> ahead, int act = 1)
    {
        ArgumentNullException.ThrowIfNull(visited);
        ArgumentNullException.ThrowIfNull(ahead);
        if (visited.Count == 0) throw new ArgumentException("At least the current room is needed.", nameof(visited));

        var types = visited.Concat(ahead).ToList();
        if (types.Count == visited.Count || types[^1] != MapNodeType.Boss) types.Add(MapNodeType.Boss);
        var nodes = new Dictionary<MapCoord, MapNode>();
        for (var row = 0; row < types.Count; row++)
        {
            var coord = new MapCoord(0, row);
            nodes[coord] = new MapNode(coord, types[row], row + 1 < types.Count ? [new MapCoord(0, row + 1)] : []);
        }
        return new MapGraph
        {
            Act = Math.Max(1, act),
            Width = 1,
            Height = types.Count - 1,
            Nodes = nodes,
            Start = new MapCoord(0, 0),
            Boss = new MapCoord(0, types.Count - 1),
            Current = new MapCoord(0, visited.Count - 1),
            Visited = Enumerable.Range(0, visited.Count).Select(r => new MapCoord(0, r)).ToArray(),
        };
    }

    /// <summary>
    /// No map: heal below the HP ratio at which the user healed half the time in the run history
    /// (<see cref="PathWeights.RestHealBelowHp"/>, [측정] 0.466); 박빙 within one <see cref="PathWeights.RestHealSoftness"/> of it.
    /// </summary>
    private RestSiteAdvice Fallback(RunState run, IReadOnlyList<UpgradeCandidate> upgrades)
    {
        var threshold = P.RestHealBelowHp ?? 0.5; // [추정] 50% when the weights switch the measured threshold off
        var hp = run.HpRatio;
        var heal = hp < threshold;
        var close = P.RestHealSoftness > 0 && Math.Abs(hp - threshold) < P.RestHealSoftness;
        return new RestSiteAdvice
        {
            Action = heal ? RestAction.Heal : RestAction.Smith,
            Confidence = close ? RestConfidence.Close : RestConfidence.Sure,
            Reason = $"지도 없음: HP {Pct(hp)} {(heal ? "<" : "≥")} 기록 기준 {Pct(threshold)} → {(heal ? "휴식" : "강화")}",
            Upgrades = upgrades,
        };
    }

    /// <summary>The deck with one copy of <paramref name="card"/> (first in deck order) upgraded once.</summary>
    private IReadOnlyList<DeckCard> UpgradeOne(IReadOnlyList<DeckCard> deck, CardRef card)
    {
        var result = deck.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i].Upgrade == card.Upgrade && _pack.Canonical(result[i].Id) == card.Id)
            {
                result[i] = result[i] with { Card = result[i].Card with { Upgrade = card.Upgrade + 1 } };
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// heal − smith per path component, regrouped into the few reasons a player cares about. The total is exactly the
    /// difference of the two path totals.
    /// </summary>
    private static ScoreBreakdown Difference(ScoreBreakdown heal, ScoreBreakdown smith)
    {
        double D(string key) => heal.ValueOf(key) - smith.ValueOf(key);
        var known = new[] { PathComponentKeys.BossRisk, PathComponentKeys.DeathRisk, PathComponentKeys.HpLoss, PathComponentKeys.Rest };
        var rewards = heal.Total - smith.Total - known.Sum(D);
        var parts = new List<ScoreComponent>
        {
            new(PathComponentKeys.BossRisk, "보스 위험 감소", D(PathComponentKeys.BossRisk)),
            new(PathComponentKeys.DeathRisk, "전투 사망 위험 감소", D(PathComponentKeys.DeathRisk)),
            new(PathComponentKeys.HpLoss, "이후 HP 손실 부담", D(PathComponentKeys.HpLoss)),
            new(PathComponentKeys.Rest, "이후 휴식처", D(PathComponentKeys.Rest)),
            new(RestComponentKeys.Rewards, "보상까지 살아남기", rewards),
        };
        return new ScoreBreakdown(parts.Where(c => Math.Abs(c.Value) >= 1e-9));
    }

    private static RestOutlook Outlook(PathOption chosen, PathOption healPath, PathOption smithPath, double hpAfterHeal)
    {
        var types = chosen.Types;
        var beforeBoss = types.Count > 0 && types[^1] == MapNodeType.Boss ? types.Count - 1 : types.Count;
        return new RestOutlook
        {
            PathAhead = types,
            RoomsBeforeBoss = beforeBoss,
            ElitesAhead = types.Count(t => t == MapNodeType.Elite),
            RestsAhead = types.Take(beforeBoss).Count(t => t == MapNodeType.RestSite),
            BossHpIfHeal = healPath.ExpectedBossHp,
            BossHpIfSmith = smithPath.ExpectedBossHp,
            HpAfterHeal = hpAfterHeal,
        };
    }

    private static string Reason(bool heal, double hp, RestOutlook o)
    {
        var road = o.RoomsBeforeBoss == 0
            ? "보스 직전"
            : $"보스까지 {o.RoomsBeforeBoss}칸·엘리트 {o.ElitesAhead}·휴식처 {o.RestsAhead}";
        return heal
            ? $"HP {Pct(hp)}, {road} → 휴식이 더 안전 (보스 도착 HP 예상 {Pct(o.BossHpIfSmith)}→{Pct(o.BossHpIfHeal)})"
            : $"HP {Pct(hp)}, {road} → 버틸 만해 강화 (보스 도착 HP 예상 {Pct(o.BossHpIfSmith)})";
    }

    private static string Pct(double ratio) => (Math.Round(ratio * 100)).ToString("0", CultureInfo.InvariantCulture) + "%";
}
