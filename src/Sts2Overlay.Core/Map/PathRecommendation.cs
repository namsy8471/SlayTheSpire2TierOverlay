using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Map;

/// <summary>Result of <see cref="PathAdvisor.Recommend"/>.</summary>
public sealed record PathRecommendation
{
    /// <summary>Where the paths start: the current node, or the act start before the first step. Not part of any path.</summary>
    public required MapCoord Origin { get; init; }

    /// <summary>
    /// Best paths (at most <see cref="PathWeights.TopK"/>): paths within <see cref="EliteCap"/> first, each group highest
    /// score first. <c>Top[0]</c> is the recommendation. Empty when there is nowhere to go.
    /// </summary>
    public IReadOnlyList<PathOption> Top { get; init; } = [];

    /// <summary>First step of the best path, i.e. the node to click next. Null when <see cref="Top"/> is empty.</summary>
    public MapCoord? NextStep => Top.Count > 0 ? Top[0].Coords[0] : null;

    /// <summary>
    /// Every child of <see cref="Origin"/> that leads to the boss, ranked like <see cref="Top"/> by its best path;
    /// the first one is <see cref="NextStep"/> (<see cref="NextStepOption.IsBest"/>).
    /// </summary>
    public IReadOnlyList<NextStepOption> NextSteps { get; init; } = [];

    /// <summary>
    /// Most elites the recommendation may take at this HP: the count chosen one HP percent higher (see
    /// <see cref="PathAdvisor"/>). Null at 100% HP or when there is nothing to recommend.
    /// </summary>
    public int? EliteCap { get; init; }

    /// <summary>HP ratio the scores were computed at (current HP floored to a whole percent).</summary>
    public double ScoredHp { get; init; }

    /// <summary>Number of distinct paths from <see cref="Origin"/> to the boss.</summary>
    public long TotalPaths { get; init; }

    /// <summary>True when every one of <see cref="TotalPaths"/> was scored (no beam cut-off).</summary>
    public bool Exhaustive { get; init; }

    /// <summary>Deck strength 0..1 the scores were computed with (PLAN §7.6).</summary>
    public double DeckStrength { get; init; }

    public bool IsEmpty => Top.Count == 0;
}

/// <summary>One recommended path from the origin to the boss.</summary>
public sealed record PathOption
{
    /// <summary>Nodes in walking order, origin excluded, boss included as the last element.</summary>
    public required IReadOnlyList<MapCoord> Coords { get; init; }

    /// <summary>Node types along <see cref="Coords"/>, same order.</summary>
    public required IReadOnlyList<MapNodeType> Types { get; init; }

    /// <summary>How many nodes of each type the path visits (types that do not occur are absent).</summary>
    public required IReadOnlyDictionary<MapNodeType, int> Counts { get; init; }

    /// <summary>Score as labelled components; <see cref="ScoreBreakdown.Total"/> is exactly their sum.</summary>
    public required ScoreBreakdown Score { get; init; }

    /// <summary>Simulated HP ratio (0..1) when reaching the boss.</summary>
    public double ExpectedBossHp { get; init; }

    /// <summary>Shops on the path where the simulation expects to spend gold (removal and/or a purchase).</summary>
    public int ShopPurchases { get; init; }

    /// <summary>Short Korean reason chips, e.g. "엘리트 2", "휴식처 2 (보스 전 휴식 있음)", "HP 낮음: 전투 적은 길".</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];

    /// <summary>Risk patterns found on the path (keys from <see cref="PathRisks"/>).</summary>
    public IReadOnlyList<string> Risks { get; init; } = [];

    public double Total => Score.Total;

    public int CountOf(MapNodeType type) => Counts.TryGetValue(type, out var n) ? n : 0;

    public int Elites => CountOf(MapNodeType.Elite);
    public int Rests => CountOf(MapNodeType.RestSite);

    /// <summary>Monster + elite rooms (guaranteed fights; "?" rooms are not counted).</summary>
    public int Fights => CountOf(MapNodeType.Monster) + CountOf(MapNodeType.Elite);
}

/// <summary>One immediate choice from the origin, so the UI can highlight the best next node.</summary>
public sealed record NextStepOption(MapCoord Coord, MapNodeType Type, double BestScore, bool IsBest);

/// <summary>Stable component keys of path scores (UI and tests rely on them).</summary>
public static class PathComponentKeys
{
    public const string Monster = "path_monster";
    public const string Elite = "path_elite";
    public const string Unknown = "path_unknown";
    public const string Treasure = "path_treasure";
    public const string Shop = "path_shop";

    /// <summary>Rest sites: max(heal value, upgrade value) at each one.</summary>
    public const string Rest = "path_rest";

    /// <summary>HP lost in fights, valued more when HP is low (always ≤ 0).</summary>
    public const string HpLoss = "path_hp";

    /// <summary>Estimated chance of dying in a hallway/elite fight × death cost (always ≤ 0).</summary>
    public const string DeathRisk = "path_death";

    /// <summary>Estimated chance of losing to the boss at arrival HP × boss death cost (always ≤ 0).</summary>
    public const string BossRisk = "path_boss";

    /// <summary>Risk patterns: elites in a row, low-HP elite without a rest behind (always ≤ 0).</summary>
    public const string Patterns = "path_risk";
}

/// <summary>Risk pattern keys reported in <see cref="PathOption.Risks"/>.</summary>
public static class PathRisks
{
    public const string EliteChain = "elite_chain";
    public const string EliteNoRest = "elite_no_rest";
}
