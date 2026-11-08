using System.Globalization;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>
/// PLAN §7.1: score = base + build + combo + need + situation − dilution − duplicates,
/// plus the DECK_COMPASS rules (requires gate, saturation, weakness-first build cap)
/// and "core first" (<c>core_priority</c> for a build's missing core card; <c>core_missing</c> = the synergy a non-core card loses
/// while its build has no core, so before any core cards rank on their own strength; then essential before support synergy;
/// see <see cref="CorePriorityWeights"/>).
/// Every tunable comes from <see cref="DataPack.Weights"/>; the total is always the sum of the emitted components.
/// </summary>
public sealed class CardScorer(DataPack pack)
{
    private const double Epsilon = 1e-9;

    public DataPack Pack { get; } = pack;

    public CardEvaluation Score(RunState state, DeckProfile profile, CardRef card, OfferContext context = OfferContext.CardReward)
    {
        var w = Pack.Weights;
        var id = Pack.Canonical(card.Id);
        var entry = Pack.FindTier(state.Character, id);
        var act = state.Act;
        var parts = new List<ScoreComponent>();

        // ---- base ----
        Tier tier;
        double confidence;
        bool estimate, rated;
        string baseDetail;
        if (entry is null)
        {
            tier = w.UnknownTier;
            confidence = w.UnknownConfidence;
            estimate = true;
            rated = false;
            baseDetail = "티어 미등록 카드 (추정)";
        }
        else
        {
            var hasActTier = entry.ActTier is not null && entry.ActTier.TryGetValue(act, out _);
            tier = hasActTier ? entry.ActTier![act] : entry.Tier;
            confidence = entry.Confidence;
            estimate = entry.IsEstimate;
            rated = true;
            baseDetail = hasActTier ? $"{act}막 티어 {tier}" : $"티어 {tier}";
        }
        var baseValue = Finite(w.TierValue.GetValueOrDefault(tier));
        if (entry is not null && card.Upgrade > 0 && entry.UpgradedDelta != 0)
        {
            baseValue += Finite(entry.UpgradedDelta);
            baseDetail += $", 강화 {Signed(entry.UpgradedDelta)}";
        }
        parts.Add(new ScoreComponent(ComponentKeys.Base, $"기본 {tier}", baseValue, baseDetail));

        // ---- archetype membership ----
        // 코어 먼저: a non-core card's value from build A is gated by A's core progress (Gate, 0 with no core by default);
        // after a core, A's support cards count SubBeforeEssential until A owns its essential (payoff + enabler) target (Stage).
        var cp = w.CorePriority;
        var memberships = profile.Archetypes
            .Select(s =>
            {
                var role = s.Archetype.RoleOf(id);
                var core = ArchetypeDetector.CoreProgress(s.Archetype, Pack, profile.Copies);
                var essential = ArchetypeDetector.EssentialProgress(s.Archetype, Pack, profile.Copies);
                var coreFactor = core.Target > 0 ? Math.Min(1.0, (double)core.Owned / core.Target) : 1.0;
                var gate = 1.0;
                if (role is not null && role != ArchetypeRoles.Core && coreFactor < 1.0 && cp.NoCoreFactor.TryGetValue(role, out var f))
                {
                    var floor = Math.Max(Math.Clamp(Finite(f), 0.0, 1.0), SynergyUnlock(s, act, w));
                    gate = floor + (1 - floor) * coreFactor;
                }
                var stage = role == ArchetypeRoles.Support && core.Owned > 0 && essential.Owned < essential.Target
                    ? Math.Clamp(Finite(cp.SubBeforeEssential), 0.0, 1.0)
                    : 1.0;
                return new Membership(s, role, Finite(s.Archetype.WeightOf(id)), Commit(s), core, coreFactor, essential, gate, stage);
            })
            .Where(m => m.Weight > 0)
            .ToList();

        // ---- build ----
        // build = what the card would add with the build's core in place (support staged), core_missing = what the core gate takes away.
        double Ungated(Membership m) => w.WArch * m.Commit * m.Weight * m.Stage;
        var buildRaw = memberships.Sum(Ungated);
        var buildRawGated = memberships.Sum(m => Ungated(m) * m.Gate);
        var buildCap = profile.IsWeak ? w.BuildCapWeak : w.BuildCap;
        var build = Finite(Math.Min(buildCap, buildRaw));
        var coreMissing = Finite(Math.Min(0.0, Math.Min(buildCap, buildRawGated) - build));
        string buildLabel = "빌드 시너지", buildDetail = "";
        if (memberships.Count > 0)
        {
            var top = memberships.OrderByDescending(m => m.Commit * m.Weight).First();
            var name = top.Status.Archetype.DisplayName;
            buildLabel = $"{name} 시너지";
            buildDetail = $"{name} {top.Commit * 100:0}%"
                + (buildRaw > buildCap + Epsilon ? $", 상한 {Num(buildCap)}" + (profile.IsWeak ? " (약점 우선)" : "") : "");
            var staged = memberships.FirstOrDefault(m => m.Stage < 1.0);
            if (staged.Status is not null)
            {
                buildDetail += $", 보조 시너지 {staged.Stage * 100:0}% (필수 시너지 {staged.Essential.Owned}/{staged.Essential.Target} 전)";
            }
        }
        string? coreMissingDetail = null;
        if (coreMissing < -Epsilon)
        {
            var worst = memberships.Where(m => m.Gate < 1.0).OrderByDescending(m => Ungated(m) * (1 - m.Gate)).First();
            var missingCore = string.Join(", ", worst.Status.Archetype.Cards.Core.Select(Pack.Canonical).Distinct(StringComparer.Ordinal)
                .Where(c => profile.Copies.GetValueOrDefault(c) == 0)
                .Select(ShortId));
            var name = worst.Status.Archetype.DisplayName;
            coreMissingDetail = worst.Core.Owned == 0
                ? (worst.Gate <= Epsilon
                    ? $"{name} 코어 전: 개별 성능으로 평가 · 먼저 노릴 코어: {missingCore}"
                    : $"{name} 코어 전: 시너지 {worst.Gate * 100:0}% 반영 · 먼저 노릴 코어: {missingCore}")
                : $"{name} 코어 {worst.Core.Owned}/{worst.Core.Target}: 시너지 {worst.Gate * 100:0}% 반영 · 남은 코어: {missingCore}";
        }

        // ---- combo ----
        double comboRaw = 0;
        var comboNames = new List<string>();
        foreach (var combo in Pack.CombosFor(state.Character))
        {
            var cards = combo.Cards.Select(Pack.Canonical).ToList();
            if (!cards.Contains(id, StringComparer.Ordinal)) continue;
            var others = cards.Where(c => !string.Equals(c, id, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
            if (others.Count == 0) others = [id]; // a combo of two copies of the same card
            var present = others.Count(c => profile.Copies.GetValueOrDefault(c) > 0);
            if (present == others.Count)
            {
                comboRaw += combo.W;
                comboNames.Add(combo.Name);
            }
            else if (cards.Count >= 3 && present > 0)
            {
                comboRaw += combo.W / 2;
                comboNames.Add(combo.Name + " (일부)");
            }
        }
        var comboValue = Finite(Math.Min(w.ComboCap, comboRaw));

        // ---- requires gate (DECK_COMPASS rule 1) ----
        var engineMissing = entry is not null && entry.Requires.Count > 0
            && !entry.Requires.Any(a => profile.Provides.GetValueOrDefault(a) > 0);
        if (engineMissing)
        {
            build = 0;
            coreMissing = 0;
            comboValue = 0;
        }

        // ---- core priority ("코어 확보") ----
        // Missing core card of a build that is not core-secured: early-act starter part + lean part (ramps with the build's commit,
        // i.e. its non-core cards already owned), scaled by the share of the core target still missing; max over builds, never summed.
        double corePriority = 0;
        string? corePriorityDetail = null;
        if (!engineMissing)
        {
            var topCommit = profile.Archetypes.Select(Commit).DefaultIfEmpty(0.0).Max();
            var weakFactor = profile.IsWeak ? Math.Clamp(Finite(cp.WeakFactor), 0.0, 1.0) : 1.0;
            var bonus = Math.Max(0.0, Finite(cp.Bonus));
            var starter = act <= cp.StarterMaxAct ? Math.Clamp(Finite(cp.StarterBonus), 0.0, bonus) : 0.0;
            foreach (var m in memberships.Where(m => m.Role == ArchetypeRoles.Core && m.Core.Owned < m.Core.Target))
            {
                var leaning = m.Commit >= cp.LeanMin - Epsilon && m.Commit >= topCommit - w.HybridMargin - Epsilon;
                var ramp = !leaning ? 0.0 : cp.LeanFull > Epsilon ? Math.Clamp(m.Commit / cp.LeanFull, 0.0, 1.0) : 1.0;
                var value = (starter + (bonus - starter) * ramp) * (1 - m.CoreFactor) * weakFactor;
                if (value <= Epsilon) continue;
                var detail = $"{m.Status.Archetype.DisplayName} 코어 {m.Core.Owned}/{m.Core.Target}장"
                    + (leaning ? $", 몰입 {m.Commit * 100:0}%" : " (초반 방향 잡기)");
                if (profile.IsWeak) detail += $" (약점 우선 ×{Num(weakFactor)})";
                if (value > corePriority + Epsilon)
                {
                    corePriority = value;
                    corePriorityDetail = detail;
                }
            }
        }

        // ---- need ----
        var urgency = Weights.ForAct(w.Urgency, act, new Dictionary<string, double>(StringComparer.Ordinal));
        double needSum = 0;
        var needParts = new List<(string Role, double Value)>();
        foreach (var (role, gap) in profile.RoleGap)
        {
            var strength = entry?.Roles.GetValueOrDefault(role) ?? 0.0;
            var contribution = Finite(w.WNeed * gap * UrgencyOf(urgency, role) * strength);
            if (Math.Abs(contribution) < Epsilon) continue;
            needSum += contribution;
            needParts.Add((role, contribution));
        }
        var need = Finite(needSum);

        // ---- situation ----
        double lowHp = 0;
        if (state.HpRatio < w.Situation.LowHpThreshold)
        {
            var strength = entry?.Roles.GetValueOrDefault(Roles.Block) ?? 0.0;
            lowHp = Finite(w.WNeed * profile.RoleGap.GetValueOrDefault(Roles.Block) * UrgencyOf(urgency, Roles.Block) * strength * w.Situation.LowHpBlockBoost);
        }
        double boss = 0;
        var bossCommit = memberships.Select(m => m.Commit * m.Gate).DefaultIfEmpty(0.0).Max();
        if (context == OfferContext.BossReward && !engineMissing)
        {
            boss = Finite(w.Situation.BossRewardBuildBonus * bossCommit);
        }

        // ---- off-build ----
        // Scorer only: a build without a secured core is never "committed" (no off-build penalty before the core).
        var committed = profile.Archetypes
            .Where(s => Commit(s) >= w.CommitThreshold && CoreSecured(s.Archetype, profile))
            .ToList();
        double offBuild = 0;
        if (committed.Count > 0 && !committed.Any(s => s.Archetype.WeightOf(id) > 0) && need <= 0)
        {
            offBuild = -w.OffBuildPenalty;
        }

        // ---- saturation (DECK_COMPASS rule 2) ----
        double saturation = 0;
        string saturationDetail = "";
        if (entry is not null)
        {
            var worstFactor = 1.0;
            foreach (var axis in entry.Provides.Distinct(StringComparer.Ordinal))
            {
                var produced = profile.Provides.GetValueOrDefault(axis);
                var consumed = profile.Consumes.GetValueOrDefault(axis);
                var excess = produced - w.Saturation.Ratio * Math.Max(1, consumed);
                if (excess <= 0) continue;
                var factor = Math.Max(w.Saturation.MinFactor, 1 - w.Saturation.PerExcess * excess);
                if (factor < worstFactor)
                {
                    worstFactor = factor;
                    saturationDetail = $"{axis} 생산 {produced} / 소비 {consumed}";
                }
            }
            if (worstFactor < 1.0)
            {
                var positive = Math.Max(0, build + coreMissing) + Math.Max(0, comboValue) + Math.Max(0, need);
                saturation = Finite(-positive * (1 - worstFactor));
            }
        }

        // ---- power saturation ----
        double powerSat = 0;
        if (Pack.CardType(state.Character, id) == CardTypes.Power && profile.PowerCount >= w.PowerSaturation.Threshold)
        {
            powerSat = -w.PowerSaturation.PenaltyPerExtra * (profile.PowerCount - w.PowerSaturation.Threshold + 1);
        }

        // ---- dilution ----
        var ideal = Weights.ForAct(w.IdealDeckSize, act, int.MaxValue);
        var over = Math.Max(0.0, (double)profile.DeckSize - ideal);
        var cycle = Math.Clamp(Finite(entry?.Cycle ?? 0.0), 0.0, 1.0);
        var dilution = Finite(-w.LambdaDilution * over * (1 - cycle));

        // ---- duplicate ----
        var copies = profile.Copies.GetValueOrDefault(id);
        var duplicate = Finite(-w.RhoDuplicate * copies * (entry?.DupFactor ?? 1.0));

        // ---- assemble (zero components omitted, base always kept) ----
        Add(parts, ComponentKeys.Build, buildLabel, build, buildDetail);
        Add(parts, ComponentKeys.CoreMissing, ScoringLabels.CoreMissing, coreMissing, coreMissingDetail);
        Add(parts, ComponentKeys.CorePriority, ScoringLabels.CorePriority, corePriority, corePriorityDetail);
        Add(parts, ComponentKeys.OffBuild, ScoringLabels.OffBuild, offBuild,
            committed.Count > 0 ? $"{committed[0].Archetype.DisplayName} 밖의 카드" : null);
        Add(parts, ComponentKeys.Combo, ScoringLabels.Combo, comboValue, comboNames.Count > 0 ? string.Join(", ", comboNames) : null);
        if (engineMissing)
        {
            Add(parts, ComponentKeys.EngineMissing, ScoringLabels.EngineMissing, -w.RequiresPenalty,
                $"필요 축 없음: {string.Join("/", entry!.Requires)}");
        }
        if (needParts.Count > 0)
        {
            var dominant = needParts.OrderByDescending(p => p.Value).First().Role;
            Add(parts, ComponentKeys.Need, ScoringLabels.NeedLabel(dominant), need,
                string.Join(", ", needParts.OrderByDescending(p => p.Value).Select(p => $"{ScoringLabels.Role(p.Role)} {Signed(p.Value)}")));
        }
        Add(parts, ComponentKeys.Situation, ScoringLabels.BossReward, boss, $"빌드 몰입 {bossCommit * 100:0}% (코어 반영)");
        Add(parts, ComponentKeys.Situation, ScoringLabels.LowHpBlock, lowHp, $"HP {state.HpRatio * 100:0}%");
        Add(parts, ComponentKeys.Saturation, ScoringLabels.Saturation, saturation, saturationDetail);
        Add(parts, ComponentKeys.PowerSaturation, ScoringLabels.PowerSaturation, powerSat, $"파워 {profile.PowerCount}장");
        Add(parts, ComponentKeys.Dilution, ScoringLabels.Dilution, dilution, $"덱 {profile.DeckSize}장 / 이상 {ideal}장");
        Add(parts, ComponentKeys.Duplicate, ScoringLabels.Duplicate, duplicate, $"보유 {copies}장");

        var breakdown = new ScoreBreakdown(parts);
        return new CardEvaluation
        {
            Card = card,
            DisplayTier = tier,
            Score = breakdown,
            Confidence = confidence,
            IsEstimate = estimate,
            IsRated = rated,
            Chips = breakdown.TopReasons(3).Select(c => new ReasonChip(c.Key, c.Label, c.Value)).ToArray(),
            Synergy = memberships
                .Select(m => new ArchetypeSynergy(m.Status.Archetype.Id, m.Role!,
                    engineMissing ? 0.0 : Finite(Ungated(m) * m.Gate), engineMissing ? 0.0 : Finite(Ungated(m))))
                .ToArray(),
            BuildRoles = BuildRolesOf(profile, id),
        };
    }

    /// <summary>Most build roles shown per card.</summary>
    public const int MaxBuildRoles = 2;

    /// <summary>
    /// Builds that list <paramref name="cardId"/> (canonical) with its role there — core, essential (payoff/enabler) or sub (support) —
    /// ordered by the build's current commit (highest first, ties in profile order), at most <see cref="MaxBuildRoles"/>.
    /// </summary>
    public static IReadOnlyList<BuildRole> BuildRolesOf(DeckProfile profile, string cardId)
        => profile.Archetypes
            .Select((s, i) => (Status: s, Index: i, Role: s.Archetype.RoleOf(cardId)))
            .Where(x => x.Role is not null)
            .OrderByDescending(x => Commit(x.Status)).ThenBy(x => x.Index)
            .Take(MaxBuildRoles)
            .Select(x => new BuildRole(x.Status.Archetype.Id, x.Status.Archetype.DisplayName, x.Role switch
            {
                ArchetypeRoles.Core => BuildRoleKind.Core,
                ArchetypeRoles.Payoff or ArchetypeRoles.Enabler => BuildRoleKind.Essential,
                _ => BuildRoleKind.Sub,
            }))
            .ToArray();

    /// <summary>One archetype that lists the scored card: core gate (<see cref="Gate"/>) and staged-synergy factor (<see cref="Stage"/>).</summary>
    private readonly record struct Membership(
        ArchetypeStatus Status, string? Role, double Weight, double Commit,
        (int Owned, int Target) Core, double CoreFactor, (int Owned, int Target) Essential, double Gate, double Stage);

    /// <summary>
    /// Share of a non-core card's synergy that counts while <paramref name="s"/>'s build owns no core (before
    /// <see cref="CorePriorityWeights.NoCoreFactor"/>): <c>clamp(noCoreMaxUnlock − CoreChance, 0, noCoreMaxUnlock)</c>, or the per-act
    /// fallback when <see cref="ArchetypeStatus.CoreChance"/> is unknown. Depends only on how likely the core still is, never on how
    /// many of the build's other cards the deck holds.
    /// </summary>
    public static double SynergyUnlock(ArchetypeStatus s, int act, Weights w)
    {
        var cp = w.CorePriority;
        var cap = Math.Clamp(Finite(cp.NoCoreMaxUnlock), 0.0, 1.0);
        if (s.CoreChance is { } p && double.IsFinite(p)) return Math.Clamp(cap - p, 0.0, cap);
        return Math.Clamp(Finite(Weights.ForAct(cp.NoCoreUnlockByAct, act, 0.0)), 0.0, cap);
    }

    private bool CoreSecured(Archetype a, DeckProfile profile)
    {
        var core = ArchetypeDetector.CoreProgress(a, Pack, profile.Copies);
        return core.Owned >= core.Target;
    }

    private static void Add(List<ScoreComponent> parts, string key, string label, double value, string? detail)
    {
        if (Math.Abs(value) < Epsilon || !double.IsFinite(value)) return;
        parts.Add(new ScoreComponent(key, label, value, string.IsNullOrEmpty(detail) ? null : detail));
    }

    private static double UrgencyOf(IReadOnlyDictionary<string, double> urgency, string role)
        => urgency.TryGetValue(role, out var u) ? u : 1.0;

    /// <summary>Commit clamped to 0..1 (NaN → 0) so bad data can never poison a score.</summary>
    internal static double Commit(ArchetypeStatus s) => double.IsFinite(s.Commit) ? Math.Clamp(s.Commit, 0.0, 1.0) : 0.0;

    private static double Finite(double v) => double.IsFinite(v) ? v : 0.0;

    private static string ShortId(string id)
    {
        var dot = id.IndexOf('.');
        return dot >= 0 ? id[(dot + 1)..] : id;
    }

    private static string Num(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Signed(double v) => v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
}
