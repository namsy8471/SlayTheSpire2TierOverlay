using System.Globalization;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>
/// PLAN §7.1: score = base + build + combo + need + situation − dilution − duplicates,
/// plus the DECK_COMPASS rules (requires gate, saturation, weakness-first build cap).
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
        var memberships = profile.Archetypes
            .Select(s => (Status: s, Weight: Finite(s.Archetype.WeightOf(id)), Commit: Commit(s)))
            .Where(m => m.Weight > 0)
            .ToList();

        // ---- build ----
        var buildRaw = w.WArch * memberships.Sum(m => m.Commit * m.Weight);
        var buildCap = profile.IsWeak ? w.BuildCapWeak : w.BuildCap;
        var build = Finite(Math.Min(buildCap, buildRaw));
        string buildLabel = "빌드 시너지", buildDetail = "";
        if (memberships.Count > 0)
        {
            var top = memberships.OrderByDescending(m => m.Commit * m.Weight).First();
            var name = top.Status.Archetype.DisplayName;
            buildLabel = $"{name} 시너지";
            buildDetail = $"{name} {top.Commit * 100:0}%"
                + (buildRaw > buildCap + Epsilon ? $", 상한 {Num(buildCap)}" + (profile.IsWeak ? " (약점 우선)" : "") : "");
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
            comboValue = 0;
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
        if (context == OfferContext.BossReward && memberships.Count > 0 && !engineMissing)
        {
            boss = Finite(w.Situation.BossRewardBuildBonus * memberships.Max(m => m.Commit));
        }

        // ---- off-build ----
        var committed = profile.Archetypes.Where(s => Commit(s) >= w.CommitThreshold).ToList();
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
                var positive = Math.Max(0, build) + Math.Max(0, comboValue) + Math.Max(0, need);
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
        Add(parts, ComponentKeys.Situation, ScoringLabels.BossReward, boss,
            memberships.Count > 0 ? $"빌드 몰입 {memberships.Max(m => m.Commit) * 100:0}%" : null);
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
        };
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

    private static string Num(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Signed(double v) => v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
}
