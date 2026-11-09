using System.Globalization;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>
/// PLAN §7.1: score = base + build + combo + need + situation − dilution − duplicates,
/// plus the DECK_COMPASS rules (requires gate, saturation, weakness-first build cap)
/// and "core first" (<c>core_priority</c> for a build's missing core card; <c>core_missing</c> = the synergy a non-core card loses
/// while its build has no core, so before any core cards rank on their own strength; then essential before support synergy;
/// see <see cref="CorePriorityWeights"/>); after the core (a coreless build: after its essential target), <c>secured_essential</c> lifts the build's essential cards to a floor that beats a generic
/// card one tier higher, a secured build skips the weak-deck build cap unless the deck is in an emergency, and on-theme cards are not off-build.
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
            baseDetail = Loc.T("티어 미등록 카드 (추정)", "Unrated card (estimate)");
        }
        else
        {
            var hasActTier = entry.ActTier is not null && entry.ActTier.TryGetValue(act, out _);
            tier = hasActTier ? entry.ActTier![act] : entry.Tier;
            confidence = entry.Confidence;
            estimate = entry.IsEstimate;
            rated = true;
            baseDetail = hasActTier ? Loc.T($"{act}막 티어 {tier}", $"Act {act} tier {tier}") : Loc.T($"티어 {tier}", $"Tier {tier}");
        }
        var baseValue = Finite(w.TierValue.GetValueOrDefault(tier));
        if (entry is not null && card.Upgrade > 0 && entry.UpgradedDelta != 0)
        {
            baseValue += Finite(entry.UpgradedDelta);
            baseDetail += Loc.T($", 강화 {Signed(entry.UpgradedDelta)}", $", upgraded {Signed(entry.UpgradedDelta)}");
        }
        if (entry is not null && entry.BaseAdjust != 0)
        {
            baseValue += Finite(entry.BaseAdjust);
            baseDetail += Loc.T($", 랭커 선택 보정 {Signed(entry.BaseAdjust)}", $", top-player pick adjustment {Signed(entry.BaseAdjust)}");
        }
        parts.Add(new ScoreComponent(ComponentKeys.Base, Loc.T($"기본 {tier}", $"Base {tier}"), baseValue, baseDetail));

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
        // Weakness first (DECK_COMPASS rule 3) caps the build on a weak deck, but not for a card of a build whose core is already in,
        // unless the deck is in an emergency (block nearly missing or low HP): then the weak cap is back.
        var emergency = IsEmergency(state, profile, w);
        var securedMember = memberships.Any(m => m.Role is not null && BuildSecured(m.Status.Archetype, Pack, profile.Copies));
        var buildCap = profile.IsWeak && (emergency || !securedMember) ? w.BuildCapWeak : w.BuildCap;
        var build = Finite(Math.Min(buildCap, buildRaw));
        var coreMissing = Finite(Math.Min(0.0, Math.Min(buildCap, buildRawGated) - build));
        string buildLabel = Loc.T("빌드 시너지", "Build synergy"), buildDetail = "";
        if (memberships.Count > 0)
        {
            var top = memberships.OrderByDescending(m => m.Commit * m.Weight).First();
            var name = top.Status.Archetype.DisplayName;
            buildLabel = Loc.T($"{name} 시너지", $"{name} synergy");
            buildDetail = $"{name} {top.Commit * 100:0}%"
                + (buildRaw > buildCap + Epsilon
                    ? Loc.T($", 상한 {Num(buildCap)}", $", cap {Num(buildCap)}") + (profile.IsWeak ? Loc.T(" (약점 우선)", " (weakness first)") : "")
                    : "");
            var staged = memberships.FirstOrDefault(m => m.Stage < 1.0);
            if (staged.Status is not null)
            {
                buildDetail += Loc.T(
                    $", 보조 시너지 {staged.Stage * 100:0}% (필수 시너지 {staged.Essential.Owned}/{staged.Essential.Target} 전)",
                    $", sub synergy {staged.Stage * 100:0}% (until key synergy {staged.Essential.Owned}/{staged.Essential.Target})");
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
                    ? Loc.T($"{name} 코어 전: 개별 성능으로 평가 · 먼저 노릴 코어: {missingCore}",
                        $"{name} before core: rated on its own · core to aim for: {missingCore}")
                    : Loc.T($"{name} 코어 전: 시너지 {worst.Gate * 100:0}% 반영 · 먼저 노릴 코어: {missingCore}",
                        $"{name} before core: {worst.Gate * 100:0}% of synergy counts · core to aim for: {missingCore}"))
                : Loc.T($"{name} 코어 {worst.Core.Owned}/{worst.Core.Target}: 시너지 {worst.Gate * 100:0}% 반영 · 남은 코어: {missingCore}",
                    $"{name} core {worst.Core.Owned}/{worst.Core.Target}: {worst.Gate * 100:0}% of synergy counts · cores left: {missingCore}");
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
                comboNames.Add(combo.LocalName);
            }
            else if (cards.Count >= 3 && present > 0)
            {
                comboRaw += combo.W / 2;
                comboNames.Add(combo.LocalName + Loc.T(" (일부)", " (partial)"));
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
        var topCommit = profile.Archetypes.Select(Commit).DefaultIfEmpty(0.0).Max();
        if (!engineMissing)
        {
            var weakFactor = profile.IsWeak ? Math.Clamp(Finite(cp.WeakFactor), 0.0, 1.0) : 1.0;
            var bonus = Math.Max(0.0, Finite(cp.Bonus));
            // Starter part only while no build is secured (BuildSecured): once the deck has a working build, other
            // builds' cores get only the lean part (a near-hybrid second build still qualifies).
            var anySecured = profile.Archetypes.Any(s => BuildSecured(s.Archetype, Pack, profile.Copies));
            var starter = act <= cp.StarterMaxAct && !anySecured ? Math.Clamp(Finite(cp.StarterBonus), 0.0, bonus) : 0.0;
            foreach (var m in memberships.Where(m => m.Role == ArchetypeRoles.Core && m.Core.Owned < m.Core.Target))
            {
                var leaning = m.Commit >= cp.LeanMin - Epsilon && m.Commit >= topCommit - w.HybridMargin - Epsilon;
                var ramp = !leaning ? 0.0 : cp.LeanFull > Epsilon ? Math.Clamp(m.Commit / cp.LeanFull, 0.0, 1.0) : 1.0;
                var value = (starter + (bonus - starter) * ramp) * (1 - m.CoreFactor) * weakFactor;
                if (value <= Epsilon) continue;
                var archetypeName = m.Status.Archetype.DisplayName;
                var detail = Loc.T($"{archetypeName} 코어 {m.Core.Owned}/{m.Core.Target}장", $"{archetypeName} core {m.Core.Owned}/{m.Core.Target}")
                    + (leaning ? Loc.T($", 몰입 {m.Commit * 100:0}%", $", commit {m.Commit * 100:0}%") : Loc.T(" (초반 방향 잡기)", " (early direction)"));
                if (profile.IsWeak) detail += Loc.T($" (약점 우선 ×{Num(weakFactor)})", $" (weakness first ×{Num(weakFactor)})");
                if (value > corePriority + Epsilon)
                {
                    corePriority = value;
                    corePriorityDetail = detail;
                }
            }
        }

        // ---- secured build essential ("빌드 필수 카드") ----
        // Once the deck's build owns its core, its essential cards (payoff/enabler) count at least `floor` of synergy, so they beat a
        // generic card one tier higher with a typical need, but not one two tiers higher (see off-build below). Full as soon as the core is in
        // (whatever the commit, if it is the deck's build), tapers once the build holds essentialTarget + extra essentials, halves in an emergency. Max over builds.
        double securedEssential = 0;
        string? securedDetail = null;
        if (!engineMissing)
        {
            var bestFloor = 0.0;
            foreach (var m in memberships.Where(m => m.Role is ArchetypeRoles.Payoff or ArchetypeRoles.Enabler))
            {
                var f = SecuredFloor(m.Status, profile, topCommit, emergency);
                if (f.Floor <= bestFloor + Epsilon) continue;
                bestFloor = f.Floor;
                securedDetail = Loc.T($"{m.Status.Archetype.DisplayName} 코어 확보: 필수 시너지 최소 {Num(f.Floor)}점",
                        $"{m.Status.Archetype.DisplayName} core secured: key synergy at least {Num(f.Floor)} pts")
                    + (f.Taper < 1.0 ? Loc.T($", 필수 {m.Essential.Owned}장 보유 (감쇠 ×{Num(f.Taper)})", $", {m.Essential.Owned} essentials owned (taper ×{Num(f.Taper)})") : "")
                    + (emergency ? Loc.T($" (위급: 방어·체력 우선 ×{Num(f.EmergencyFactor)})", $" (emergency: Block/HP first ×{Num(f.EmergencyFactor)})") : "");
            }
            securedEssential = Finite(Math.Max(0.0, bestFloor - Math.Max(0.0, build + coreMissing)));
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
        string? offBuildDetail = committed.Count > 0
            ? Loc.T($"{committed[0].Archetype.DisplayName} 밖의 카드", $"Outside {committed[0].Archetype.DisplayName}")
            : null;
        // A card that fits a committed build without being one of its roles (on-theme: `onTheme` list or a needed axis tag) is not off-build.
        if (committed.Count > 0 && !committed.Any(s => s.Archetype.WeightOf(id) > 0 || s.Archetype.IsOnTheme(id, entry, Pack.Canonical))
            && need <= 0)
        {
            offBuild = -w.OffBuildPenalty;
            // The committed build's essentials already get the secured floor: penalty + floor stays below two tier steps, so a generic
            // card two tiers higher still beats an essential (the floor alone already beats one tier higher).
            var deckFloor = committed.Select(s => SecuredFloor(s, profile, topCommit, emergency).Floor).DefaultIfEmpty(0.0).Max();
            if (deckFloor > Epsilon)
            {
                var twoTiers = 2 * (w.TierValue.GetValueOrDefault(Tier.A) - w.TierValue.GetValueOrDefault(Tier.B));
                var cap = Math.Max(0.0, twoTiers - deckFloor - TwoTierMargin);
                if (cap < w.OffBuildPenalty)
                {
                    offBuild = -cap;
                    offBuildDetail += Loc.T($" (빌드 필수 카드 최소 {Num(deckFloor)}점과 합쳐 티어 두 칸 미만)",
                        $" (with the build essentials' {Num(deckFloor)}-pt floor, under two tier steps)");
                }
            }
        }

        // ---- saturation (DECK_COMPASS rule 2) ----
        double saturation = 0;
        string saturationDetail = "";
        if (entry is not null)
        {
            var worstFactor = 1.0;
            foreach (var axis in entry.Provides.Distinct(StringComparer.Ordinal))
            {
                // An axis the deck's leaning build needs (needs.tag) is what the build is made of: its payoffs are the build's listed
                // cards, not cards tagged "consumes", so top players' winning decks routinely produce it past the ratio (DECK_SIM.md).
                // The same goes for a card the leaning build lists or that makes one of its needs.tag axes: its other (secondary)
                // tags such as ORB or DRAW are a side effect of a build card, not a reason to cut it (DECK_SIM.md §3.2).
                if (w.Saturation.ExemptBuildAxes && profile.Archetypes.Any(s => Commit(s) >= cp.LeanMin - Epsilon
                        && (s.Archetype.WeightOf(id) > 0 || s.Archetype.Needs.Any(n => entry.Provides.Contains(n.Tag, StringComparer.Ordinal)))))
                {
                    continue;
                }
                var produced = profile.Provides.GetValueOrDefault(axis);
                var consumed = profile.Consumes.GetValueOrDefault(axis);
                var excess = produced - w.Saturation.Ratio * Math.Max(1, consumed);
                if (excess <= 0) continue;
                var factor = Math.Max(w.Saturation.MinFactor, 1 - w.Saturation.PerExcess * excess);
                if (factor < worstFactor)
                {
                    worstFactor = factor;
                    saturationDetail = Loc.T($"{axis} 생산 {produced} / 소비 {consumed}", $"{axis} made {produced} / used {consumed}");
                }
            }
            if (worstFactor < 1.0)
            {
                var positive = Math.Max(0, build + coreMissing) + securedEssential + Math.Max(0, comboValue) + Math.Max(0, need);
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
        // Another copy of a card the deck's build lists makes the build show up more often: top players stack them (DECK_SIM.md),
        // so the penalty shrinks toward BuildDuplicateFactor as that build's core-gated commit grows.
        var copies = profile.Copies.GetValueOrDefault(id);
        var buildRelief = engineMissing ? 0.0 : memberships.Select(m => m.Commit * m.Gate).DefaultIfEmpty(0.0).Max();
        var dupScale = 1 - (1 - Math.Clamp(Finite(w.BuildDuplicateFactor), 0.0, 1.0)) * buildRelief;
        var duplicate = Finite(-w.RhoDuplicate * copies * (entry?.DupFactor ?? 1.0) * dupScale);

        // ---- assemble (zero components omitted, base always kept) ----
        Add(parts, ComponentKeys.Build, buildLabel, build, buildDetail);
        Add(parts, ComponentKeys.CoreMissing, ScoringLabels.CoreMissing, coreMissing, coreMissingDetail);
        Add(parts, ComponentKeys.CorePriority, ScoringLabels.CorePriority, corePriority, corePriorityDetail);
        Add(parts, ComponentKeys.SecuredEssential, ScoringLabels.SecuredEssential, securedEssential, securedDetail);
        Add(parts, ComponentKeys.OffBuild, ScoringLabels.OffBuild, offBuild, offBuildDetail);
        Add(parts, ComponentKeys.Combo, ScoringLabels.Combo, comboValue, comboNames.Count > 0 ? string.Join(", ", comboNames) : null);
        if (engineMissing)
        {
            Add(parts, ComponentKeys.EngineMissing, ScoringLabels.EngineMissing, -w.RequiresPenalty,
                Loc.T($"필요 축 없음: {string.Join("/", entry!.Requires)}", $"Needs axis: {string.Join("/", entry!.Requires)}"));
        }
        if (needParts.Count > 0)
        {
            var dominant = needParts.OrderByDescending(p => p.Value).First().Role;
            Add(parts, ComponentKeys.Need, ScoringLabels.NeedLabel(dominant), need,
                string.Join(", ", needParts.OrderByDescending(p => p.Value).Select(p => $"{ScoringLabels.Role(p.Role)} {Signed(p.Value)}")),
                role: dominant);
        }
        Add(parts, ComponentKeys.Situation, ScoringLabels.BossReward, boss, Loc.T($"빌드 몰입 {bossCommit * 100:0}% (코어 반영)", $"Build commit {bossCommit * 100:0}% (core-adjusted)"));
        Add(parts, ComponentKeys.Situation, ScoringLabels.LowHpBlock, lowHp, $"HP {state.HpRatio * 100:0}%");
        Add(parts, ComponentKeys.Saturation, ScoringLabels.Saturation, saturation, saturationDetail);
        Add(parts, ComponentKeys.PowerSaturation, ScoringLabels.PowerSaturation, powerSat, Loc.T($"파워 {profile.PowerCount}장", $"{profile.PowerCount} Powers"));
        Add(parts, ComponentKeys.Dilution, ScoringLabels.Dilution, dilution, Loc.T($"덱 {profile.DeckSize}장 / 이상 {ideal}장", $"Deck {profile.DeckSize} / ideal {ideal}"));
        Add(parts, ComponentKeys.Duplicate, ScoringLabels.Duplicate, duplicate, Loc.T($"보유 {copies}장", $"{copies} owned")
            + (dupScale < 1 - Epsilon ? Loc.T($" (빌드 카드 ×{Num(dupScale)})", $" (build card ×{Num(dupScale)})") : ""));

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

    /// <summary>
    /// Build commit after the core gate: <c>commit × (floor + (1 − floor) × coreFactor)</c>, <c>floor = </c><see cref="SynergyUnlock"/>,
    /// <c>coreFactor = min(1, ownedCore / coreTarget)</c> (1 for a build without core cards). Before a core it is at most
    /// <c>noCoreMaxUnlock × commit</c>. Used by <see cref="SkipScorer"/> so skip follows the same "코어 먼저" rule as the cards.
    /// </summary>
    public static double GatedCommit(ArchetypeStatus s, DeckProfile profile, DataPack pack, int act)
    {
        var core = ArchetypeDetector.CoreProgress(s.Archetype, pack, profile.Copies);
        var coreFactor = core.Target > 0 ? Math.Min(1.0, (double)core.Owned / core.Target) : 1.0;
        var floor = coreFactor < 1.0 ? SynergyUnlock(s, act, pack.Weights) : 1.0;
        return Commit(s) * (floor + (1 - floor) * coreFactor);
    }

    /// <summary>Points a generic card two tiers higher keeps over a build essential at the floor once the off-build penalty is applied.</summary>
    public const double TwoTierMargin = 1.0;

    /// <summary>
    /// Secured-essential floor of build <paramref name="s"/>: 0 unless it is secured (<see cref="BuildSecured"/>) and is the deck's build
    /// (top commit or within <see cref="Weights.HybridMargin"/> of it). Then the full <c>SecuredEssentialFloor × taper × emergency</c>
    /// whatever the commit ("once the core arrives, essential synergy gets its points"), never above <see cref="Weights.BuildCapWeak"/>
    /// on a weak deck in an emergency.
    /// </summary>
    private (double Floor, double Taper, double EmergencyFactor) SecuredFloor(
        ArchetypeStatus s, DeckProfile profile, double topCommit, bool emergency)
    {
        var w = Pack.Weights;
        var cp = w.CorePriority;
        if (!(Finite(cp.SecuredEssentialFloor) > 0)) return default;
        if (!BuildSecured(s.Archetype, Pack, profile.Copies)) return default;
        if (Commit(s) < topCommit - w.HybridMargin - Epsilon) return default;
        var essential = ArchetypeDetector.EssentialProgress(s.Archetype, Pack, profile.Copies);
        var extra = essential.Owned - (essential.Target + cp.SecuredEssentialExtra);
        var taper = extra <= 0 ? 1.0 : Math.Max(0.0, 1 - extra * Math.Clamp(Finite(cp.SecuredEssentialTaper), 0.0, 1.0));
        var emergencyFactor = emergency ? Math.Clamp(Finite(cp.EmergencyFactor), 0.0, 1.0) : 1.0;
        var floor = Finite(cp.SecuredEssentialFloor) * taper * emergencyFactor;
        if (emergency && profile.IsWeak) floor = Math.Min(floor, w.BuildCapWeak);
        return (floor, taper, emergencyFactor);
    }

    /// <summary>
    /// Emergency: HP below <see cref="SituationWeights.LowHpThreshold"/>, or an <see cref="CorePriorityWeights.EmergencyRoles"/> role
    /// (block by default) with a gap of at least <see cref="CorePriorityWeights.EmergencyGap"/>. Then a secured build gets the weak cap
    /// again and its essentials only <see cref="CorePriorityWeights.EmergencyFactor"/> of the floor.
    /// </summary>
    public static bool IsEmergency(RunState state, DeckProfile profile, Weights w)
    {
        if (state.HpRatio < w.Situation.LowHpThreshold) return true;
        var cp = w.CorePriority;
        return cp.EmergencyRoles.Any(r => profile.RoleGap.TryGetValue(r, out var g) && g >= cp.EmergencyGap - Epsilon);
    }

    /// <summary>
    /// The build works: it owns its core target, or (a build without core cards) its essential target of distinct payoff + enabler
    /// cards (<see cref="ArchetypeDetector.EssentialProgress"/>). Gates the secured-essential floor and the weak-cap exemption, so a
    /// coreless build gets the same protection once it stands (before, they never did: in the deck sim the overlay left an established
    /// coreless build for another card on 20–28% of the screens that offered one of its cards, against 4–10% for builds with a core).
    /// </summary>
    public static bool BuildSecured(Archetype a, DataPack pack, IReadOnlyDictionary<string, int> copies)
    {
        var core = ArchetypeDetector.CoreProgress(a, pack, copies);
        if (core.Target > 0) return core.Owned >= core.Target;
        var essential = ArchetypeDetector.EssentialProgress(a, pack, copies);
        return essential.Target > 0 && essential.Owned >= essential.Target;
    }

    private bool CoreSecured(Archetype a, DeckProfile profile)
    {
        var core = ArchetypeDetector.CoreProgress(a, Pack, profile.Copies);
        return core.Owned >= core.Target;
    }

    private static void Add(List<ScoreComponent> parts, string key, string label, double value, string? detail, string? role = null)
    {
        if (Math.Abs(value) < Epsilon || !double.IsFinite(value)) return;
        parts.Add(new ScoreComponent(key, label, value, string.IsNullOrEmpty(detail) ? null : detail) { Role = role });
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
