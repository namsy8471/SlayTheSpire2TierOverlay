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

    /// <summary>
    /// Resource axis / build-need tag (tier <c>provides</c>, archetype <c>needs.tag</c>) → (Korean, English) display name, in the
    /// game's own terms (data/local localization: 밀집 Focus, 단조 Forge, 단도 Shiv, 골골이 Osty, 종말 Doom).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Kor, string Eng)> TagNames = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["BLOCK"] = ("방어도", "Block"),
        ["CREATE"] = ("카드 생성", "card creation"),
        ["DISCARD"] = ("버리기", "Discard"),
        ["DOOM"] = ("종말", "Doom"),
        ["DRAW"] = ("드로우", "Draw"),
        ["ETHEREAL"] = ("휘발성", "Ethereal"),
        ["EXHAUST"] = ("소멸", "Exhaust"),
        ["FOCUS"] = ("밀집", "Focus"),
        ["FORGE"] = ("단조", "Forge"),
        ["ORB"] = ("구체", "Orbs"),
        ["ORB_DARK"] = ("암흑 구체", "Dark Orbs"),
        ["ORB_FROST"] = ("냉기 구체", "Frost Orbs"),
        ["ORB_GLASS"] = ("유리 구체", "Glass Orbs"),
        ["ORB_LIGHTNING"] = ("전기 구체", "Lightning Orbs"),
        ["ORB_PLASMA"] = ("플라즈마 구체", "Plasma Orbs"),
        ["ORB_SLOT"] = ("구체 슬롯", "Orb slots"),
        ["OSTY_ATTACK"] = ("골골이 공격", "Osty attacks"),
        ["POISON"] = ("중독", "Poison"),
        ["POWER"] = ("파워", "Powers"),
        ["SELF_DAMAGE"] = ("자해", "self-damage"),
        ["SHIV"] = ("단도", "Shivs"),
        ["SOUL"] = ("영혼", "Souls"),
        ["STAR"] = ("별", "Stars"),
        ["STATUS"] = ("상태이상 카드", "Status cards"),
        ["STRENGTH"] = ("힘", "Strength"),
        ["STRIKE"] = ("타격", "Strikes"),
        ["SUMMON"] = ("소환", "Summon"),
        ["VULNERABLE"] = ("취약", "Vulnerable"),
        ["WEAK"] = ("약화", "Weak"),
        ["ZERO_COST"] = ("비용 0 카드", "0-cost cards"),
    };

    /// <summary>
    /// Display name of a resource axis / build-need tag in the current language. A tag missing from the table is humanised
    /// (<c>NEW_AXIS</c> → <c>new axis</c>), so the raw upper-case key never reaches the UI.
    /// </summary>
    public static string Tag(string tag)
        => TagNames.TryGetValue(tag, out var name) ? Loc.T(name.Kor, name.Eng) : tag.Replace('_', ' ').Trim().ToLowerInvariant();

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

    public static string SecuredEssential => Loc.T("빌드 필수 카드", "Build essential");
    public static string RelicBase => Loc.T("유물 기본", "Relic base");
    public static string RelicFit => Loc.T("빌드 적합 유물", "Build-fit relic");
    public static string RelicMisfit => Loc.T("빌드와 안 맞는 유물", "Off-build relic");

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
