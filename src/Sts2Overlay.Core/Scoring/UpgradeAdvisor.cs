using System.Globalization;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>One card of the deck that the rest-site smith could upgrade, with how much the upgrade is worth now.</summary>
public sealed record UpgradeCandidate
{
    /// <summary>The card as it is now (not yet upgraded), e.g. <c>CARD.GLACIER</c> with <c>Upgrade = 0</c>.</summary>
    public required CardRef Card { get; init; }

    /// <summary>How many copies of exactly this card (same id and upgrade level) the deck holds. Upgrading changes one.</summary>
    public int Copies { get; init; } = 1;

    /// <summary>Value of the upgrade as labelled parts; <see cref="ScoreBreakdown.Total"/> is exactly their sum.</summary>
    public required ScoreBreakdown Score { get; init; }

    /// <summary>Up to 3 reasons, largest first (the upgrade itself is usually the first one).</summary>
    public IReadOnlyList<ReasonChip> Chips { get; init; } = [];

    /// <summary>Tier letter of the card (act tier if present), or null when the card is not in the tier pack.</summary>
    public Tier? DisplayTier { get; init; }

    /// <summary>True when the value is a guess: the card is unrated or its rating is marked as an estimate → "추정" badge.</summary>
    public bool IsEstimate { get; init; }

    /// <summary>1-based rank among the deck's upgradable cards.</summary>
    public int Rank { get; init; }

    public double Total => Score.Total;
}

/// <summary>Stable component keys of <see cref="UpgradeCandidate.Score"/>.</summary>
public static class UpgradeComponentKeys
{
    /// <summary>The card's own upgrade value: the pack's <c>upgradeValue</c> (calibrated), else its <c>upgradedDelta</c>.</summary>
    public const string Upgrade = "upgrade";

    /// <summary>The card's role in a build of the deck (core, payoff, …) × that build's commit (<see cref="UpgradeWeights.BuildRole"/>).</summary>
    public const string Build = "upgrade_build";
}

/// <summary>
/// Ranks the cards of the current deck by how much upgrading them <i>now</i> is worth (rest site "강화").
/// <para><b>Model</b> (docs/research/UPGRADE_CALIBRATION.md), three parts in upgrade points:</para>
/// <list type="number">
/// <item><b>The card's upgrade value</b> = the tier pack's <see cref="CardTierEntry.UpgradeValue"/>: fitted on 8,140 smith
/// choices of top players (conditional logit over the deck's candidates). It knows what the upgrade does to <i>this</i> card:
/// upgrades that lower the cost (Hellraiser 2→1), add or remove a keyword, powers, and per-card evidence (Apotheosis, Bash high;
/// Strike, Defend low). A card without it falls back to the hand-set <see cref="CardTierEntry.UpgradedDelta"/>, and an unrated
/// card to the character's average [추정].</item>
/// <item><b>Build role</b> = <see cref="UpgradeWeights.BuildRole"/>[role] × commit of the highest-commit build the card belongs
/// to. Top players upgrade a committed build's core and payoff cards clearly more often than the same card outside the build
/// (the role terms are fitted <i>within</i> each card, next to its own value).</item>
/// <item><b>Missing roles</b> = the "need" part of the card-pick score (<see cref="CardScorer"/>: the deck lacks block,
/// AoE, draw … and this card supplies it), plus the low-HP block bonus, times
/// (<see cref="Weights.UpgradedSupplyMultiplier"/> − 1) × <see cref="UpgradeWeights.NeedWeight"/>. The top players' choices
/// give it almost no weight (0.03 in the v0.107.1 pack), so a role gap no longer lifts an AoE common over a build's core.</item>
/// </list>
/// <para>The points keep the old scale (mean candidate ≈ 4.1, mean best ≈ 5.7 on the same visits), so the rest-site advisor's
/// heal-vs-smith balance is unchanged; only the order of the cards moved.</para>
/// <para><b>Measured</b> (player-split cross-validation on the same 8,140 choices; agreement is a proxy, not the target): the
/// upgraded card is this ranking's #1 43% of the time and in its top 3 78%, up from 20% / 46% for <c>upgradedDelta</c> + need
/// (random: 10% / 30%).</para>
/// <para><b>Who can be upgraded.</b> Cards below their catalog <see cref="CatalogCard.MaxUpgrade"/> (1 when unknown;
/// v0.107.1 has no card above 1), never curses, status or quest cards.</para>
/// </summary>
public sealed class UpgradeAdvisor(DataPack pack)
{
    private const double Epsilon = 1e-9;

    /// <summary>Card-pick components that an upgrade changes: role supply (need, low-HP block). See the class summary.</summary>
    private static readonly HashSet<string> RoleKeys = new(StringComparer.Ordinal) { ComponentKeys.Need, ComponentKeys.Situation };

    private readonly CardScorer _scorer = new(pack);

    public DataPack Pack { get; } = pack;

    /// <summary>
    /// Every upgradable card of the deck (one entry per distinct id + upgrade level), best upgrade first.
    /// Ties keep deck order, so the result is deterministic.
    /// </summary>
    /// <param name="state">The run right now (deck, act, HP).</param>
    /// <param name="profile">The deck analysis if the caller already has one; computed otherwise.</param>
    public IReadOnlyList<UpgradeCandidate> Rank(RunState state, DeckProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        profile ??= DeckAnalyzer.Analyze(state, Pack);

        // Group copies: upgrading any copy of "Glacier" is the same decision.
        var groups = state.Deck
            .Select((card, index) => (card, index))
            .Where(x => CanUpgrade(Pack, state.Character, x.card))
            .GroupBy(x => (Id: Pack.Canonical(x.card.Id), x.card.Upgrade))
            .Select(g => (Card: new CardRef(g.Key.Id, g.Key.Upgrade), Copies: g.Count(), FirstIndex: g.Min(x => x.index)))
            .ToList();

        var fallbackDelta = AverageValue(state.Character);
        var scored = groups
            .Select(g => (g.FirstIndex, Candidate: Score(state, profile, g.Card, g.Copies, fallbackDelta)))
            .OrderByDescending(x => x.Candidate.Total)
            .ThenBy(x => x.FirstIndex)
            .Select((x, i) => x.Candidate with { Rank = i + 1 })
            .ToArray();
        return scored;
    }

    /// <summary>
    /// True when the smith can upgrade <paramref name="card"/>: its upgrade level is below the catalog's
    /// <see cref="CatalogCard.MaxUpgrade"/> (1 when the catalog does not know the card), and it is not a curse,
    /// status or quest card (those have <c>maxUpgrade 0</c> in the game anyway; the type check covers a missing catalog).
    /// </summary>
    public static bool CanUpgrade(DataPack pack, string character, DeckCard card)
    {
        var type = pack.CardType(character, card.Id);
        if (type is CardTypes.Curse or CardTypes.Status or CardTypes.Quest) return false;
        if (DeckAnalyzer.IsCurse(pack, character, card.Id)) return false;
        var max = pack.FindCatalog(card.Id)?.MaxUpgrade ?? 1;
        return card.Upgrade < max;
    }

    private UpgradeCandidate Score(RunState state, DeckProfile profile, CardRef card, int copies, double fallbackDelta)
    {
        var w = Pack.Weights;
        var entry = Pack.FindTier(state.Character, card.Id);
        var parts = new List<ScoreComponent>();

        // 1. The card's own upgrade value: calibrated upgradeValue, else the hand-set upgradedDelta (a zero delta means
        //    "not filled in"), else the character average.
        var calibrated = entry?.UpgradeValue is { } uv && double.IsFinite(uv);
        var rated = calibrated || (entry is not null && entry.UpgradedDelta > 0 && double.IsFinite(entry.UpgradedDelta));
        var value = calibrated ? entry!.UpgradeValue!.Value : rated ? entry!.UpgradedDelta : fallbackDelta;
        parts.Add(new ScoreComponent(UpgradeComponentKeys.Upgrade, Loc.T($"강화 {Signed(value)}", $"Upgrade {Signed(value)}"), value,
            calibrated ? Loc.T("랭커 강화 선택으로 보정한 카드별 강화 가치", "Per-card upgrade value calibrated on top players' upgrades")
            : rated ? Loc.T("티어 팩 upgradedDelta", "Tier pack upgradedDelta")
            : Loc.T("팩에 값 없음: 캐릭터 평균 (추정)", "Not in pack: character average (estimate)")));

        // 2. Build role: top players upgrade a committed build's core/payoff cards more than the same card outside a build.
        if (BuildRoleOf(profile, card.Id) is { } b
            && w.Upgrade.BuildRole.TryGetValue(b.Role, out var perCommit) && double.IsFinite(perCommit))
        {
            var v = perCommit * b.Commit;
            if (Math.Abs(v) >= Epsilon)
            {
                var name = b.Status.Archetype.DisplayName;
                parts.Add(new ScoreComponent(UpgradeComponentKeys.Build, $"{name} {RoleName(b.Role)}", v,
                    Loc.T($"{name} 빌드의 {RoleName(b.Role)} 카드 (빌드 진행 {b.Commit * 100:0}%)",
                        $"{RoleName(b.Role)} card of the {name} build ({b.Commit * 100:0}% committed)")));
            }
        }

        // 3. Missing roles: an upgraded card supplies its roles 1.2× (UpgradedSupplyMultiplier), so the upgrade is worth
        //    0.2 of the "need" the card answers, times the calibrated NeedWeight. The pick scorer computes that need.
        var share = Math.Max(0.0, w.UpgradedSupplyMultiplier - 1.0) * Math.Max(0.0, w.Upgrade.NeedWeight);
        var eval = _scorer.Score(state, profile, card);
        if (share > 0)
        {
            foreach (var c in eval.Score.Components.Where(c => RoleKeys.Contains(c.Key)))
            {
                var v = c.Value * share;
                if (Math.Abs(v) < Epsilon || !double.IsFinite(v)) continue;
                parts.Add(c with { Value = v });
            }
        }

        var breakdown = new ScoreBreakdown(parts);
        return new UpgradeCandidate
        {
            Card = card,
            Copies = copies,
            Score = breakdown,
            // Unlike pick chips, the base part (the upgrade itself) is the main reason here, so it may be a chip too.
            Chips = breakdown.Components
                .Where(c => Math.Abs(c.Value) >= 0.05)
                .OrderByDescending(c => Math.Abs(c.Value))
                .Take(3)
                .Select(c => new ReasonChip(c.Key, c.Label, c.Value))
                .ToArray(),
            DisplayTier = entry is null ? null : eval.DisplayTier,
            IsEstimate = !rated || eval.IsEstimate,
        };
    }

    /// <summary>
    /// The build that decides a card's build-role part: among the deck's builds (<see cref="DeckProfile.Archetypes"/>) that list
    /// the card, the one with the highest commit (the first on ties). Null when no build lists it. Shared with the calibration
    /// export (<c>RunHistoryAnalyzer upgrade-export</c>), so the fitted weights and the advisor read the same role.
    /// </summary>
    public static (ArchetypeStatus Status, string Role, double Commit)? BuildRoleOf(DeckProfile profile, string cardId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        (ArchetypeStatus Status, string Role, double Commit)? best = null;
        foreach (var s in profile.Archetypes)
        {
            var role = s.Archetype.RoleOf(cardId);
            if (role is null) continue;
            var commit = double.IsFinite(s.Commit) ? Math.Clamp(s.Commit, 0.0, 1.0) : 0.0;
            if (best is null || commit > best.Value.Commit) best = (s, role, commit);
        }
        return best;
    }

    private static string RoleName(string role) => role switch
    {
        ArchetypeRoles.Core => Loc.T("코어", "core"),
        ArchetypeRoles.Payoff => Loc.T("핵심 보상", "payoff"),
        ArchetypeRoles.Enabler => Loc.T("준비", "enabler"),
        ArchetypeRoles.Support => Loc.T("보조", "support"),
        _ => role,
    };

    /// <summary>
    /// Average upgrade value of the character's tier pack (else of all packs, else 0): the value given to cards the pack does not
    /// rate. Calibrated <c>upgradeValue</c>s when the pack has them, else positive <c>upgradedDelta</c>s.
    /// [추정] "an unknown card's upgrade is worth an average upgrade".
    /// </summary>
    private double AverageValue(string character)
    {
        static double[] Values(IEnumerable<CardTierEntry> entries)
        {
            var list = entries.ToArray();
            var calibrated = list.Select(e => e.UpgradeValue).Where(v => v is { } x && double.IsFinite(x)).Select(v => v!.Value).ToArray();
            return calibrated.Length > 0 ? calibrated : list.Select(e => e.UpgradedDelta).Where(d => d > 0 && double.IsFinite(d)).ToArray();
        }

        var own = Pack.Tiers.TryGetValue(character, out var p) ? Values(p.Cards.Values) : [];
        if (own.Length > 0) return own.Average();
        var all = Values(Pack.Tiers.Values.SelectMany(t => t.Cards.Values));
        return all.Length > 0 ? all.Average() : 0.0;
    }

    private static string Signed(double v) => v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
}
