using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Scoring;

/// <summary>Korean labels for roles and score components (UI chips and hints).</summary>
public static class ScoringLabels
{
    private static readonly IReadOnlyDictionary<string, string> RoleNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Roles.Frontload] = "초반 딜",
        [Roles.Aoe] = "광역",
        [Roles.Block] = "방어",
        [Roles.Scaling] = "성장",
        [Roles.Draw] = "드로우",
        [Roles.Energy] = "에너지",
        [Roles.ExhaustFodder] = "소멸 재료",
    };

    /// <summary>Korean name of a role key; unknown keys are returned as-is.</summary>
    public static string Role(string role) => RoleNames.TryGetValue(role, out var name) ? name : role;

    /// <summary>Label of the need component for its dominant role, e.g. <c>광역 부족</c>.</summary>
    public static string NeedLabel(string role) => $"{Role(role)} 부족";

    /// <summary>Inverse of <see cref="NeedLabel"/>: the role key whose need label is <paramref name="label"/>, or null.</summary>
    public static string? RoleFromNeedLabel(string label)
        => RoleNames.Keys.Concat(Roles.All).FirstOrDefault(r => NeedLabel(r) == label);

    public const string OffBuild = "오프빌드";
    public const string Combo = "콤보";
    public const string EngineMissing = "엔진 없음";
    public const string BossReward = "보스 보상 빌드";
    public const string LowHpBlock = "체력 낮음 방어";
    public const string Saturation = "자원 과잉";
    public const string PowerSaturation = "파워 과다";
    public const string Dilution = "덱 비대";
    public const string Duplicate = "중복";

    public const string SkipBase = "스킵 기준";
    public const string SkipDeckSize = "덱 크기";
    public const string SkipCommit = "빌드 완성";
    public const string SkipRelative = "스킵 대비";
    public const string CurseRemoval = "저주 제거";
    public const string BasicRemoval = "기본 카드 제거";
}
