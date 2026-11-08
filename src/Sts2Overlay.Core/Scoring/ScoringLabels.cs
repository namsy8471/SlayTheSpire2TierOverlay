using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.Core.Scoring;

/// <summary>
/// Labels for roles and score components (UI chips and hints) in the current overlay language (<see cref="Loc"/>).
/// Every member is evaluated on each call, so a label follows a language switch; logic never keys on these texts
/// (use <see cref="ComponentKeys"/> and <see cref="ScoreComponent.Role"/> instead).
/// </summary>
public static class ScoringLabels
{
    /// <summary>Role key → (Korean, English) name. English uses the game's terms where it has one (Block, Draw, Energy, Exhaust).</summary>
    private static readonly IReadOnlyDictionary<string, (string Kor, string Eng)> RoleNames = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        [Roles.Frontload] = ("초반 딜", "Frontload"),
        [Roles.Aoe] = ("광역", "AoE"),
        [Roles.Block] = ("방어", "Block"),
        [Roles.Scaling] = ("성장", "Scaling"),
        [Roles.Draw] = ("드로우", "Draw"),
        [Roles.Energy] = ("에너지", "Energy"),
        [Roles.ExhaustFodder] = ("소멸 재료", "Exhaust fodder"),
    };

    /// <summary>Name of a role key in the current language; unknown keys are returned as-is.</summary>
    public static string Role(string role) => RoleNames.TryGetValue(role, out var name) ? Loc.T(name.Kor, name.Eng) : role;

    /// <summary>Label of the need component for its dominant role, e.g. <c>광역 부족</c> / <c>Lacks AoE</c>.</summary>
    public static string NeedLabel(string role) => Loc.T($"{Role(role)} 부족", $"Lacks {Role(role)}");

    public static string OffBuild => Loc.T("오프빌드", "Off-build");
    public static string Combo => Loc.T("콤보", "Combo");
    public static string EngineMissing => Loc.T("엔진 없음", "No engine");
    public static string BossReward => Loc.T("보스 보상 빌드", "Boss reward build");
    public static string LowHpBlock => Loc.T("체력 낮음 방어", "Low HP Block");
    public static string Saturation => Loc.T("자원 과잉", "Oversupply");
    public static string PowerSaturation => Loc.T("파워 과다", "Too many Powers");
    public static string Dilution => Loc.T("덱 비대", "Deck bloat");
    public static string Duplicate => Loc.T("중복", "Duplicate");
    public static string CorePriority => Loc.T("코어 확보", "Get core");
    public static string CoreMissing => Loc.T("코어 없음", "No core");

    /// <summary>"코어", "필수 시너지", "서브 시너지" / "core", "key synergy", "sub synergy".</summary>
    public static string BuildRoleKind(BuildRoleKind kind) => kind switch
    {
        Scoring.BuildRoleKind.Core => Loc.T("코어", "core"),
        Scoring.BuildRoleKind.Essential => Loc.T("필수 시너지", "key synergy"),
        _ => Loc.T("서브 시너지", "sub synergy"),
    };

    /// <summary>"냉기·밀집 구체 코어 · 파워 서브 시너지"; null when the card is in no build.</summary>
    public static string? BuildRoleText(IEnumerable<BuildRole> roles)
    {
        var parts = roles.Select(r => $"{r.ArchetypeName} {BuildRoleKind(r.Kind)}").ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }

    public static string SkipBase => Loc.T("스킵 기준", "Skip baseline");
    public static string SkipDeckSize => Loc.T("덱 크기", "Deck size");
    public static string SkipCommit => Loc.T("빌드 완성", "Build complete");
    public static string SkipBasics => Loc.T("기본 카드 교체", "Replace basics");
    public static string SkipRelative => Loc.T("스킵 대비", "vs. Skip");
    public static string CurseRemoval => Loc.T("저주 제거", "Remove Curse");
    public static string BasicRemoval => Loc.T("기본 카드 제거", "Remove basic card");
}
