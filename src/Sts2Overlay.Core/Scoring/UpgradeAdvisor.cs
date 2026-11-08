using System.Globalization;
using Sts2Overlay.Core.Data;
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
    /// <summary>The tier pack's <c>upgradedDelta</c>: what the upgrade itself adds to the card.</summary>
    public const string Upgrade = "upgrade";
}

/// <summary>
/// Ranks the cards of the current deck by how much upgrading them <i>now</i> is worth (rest site "강화").
/// <para><b>Model.</b> An upgrade's value has two parts, both in card-score points:</para>
/// <list type="number">
/// <item><b>The upgrade itself</b> = the tier pack's <see cref="CardTierEntry.UpgradedDelta"/>. The pack authors set it
/// per card (basic 3, B or lower 4, A 6, cards whose upgrade changes a lot 6–8; see the pack README), so it already
/// says "upgrading a good card is worth more than upgrading a Strike". Unrated cards get the average delta of the
/// character's pack [추정].</item>
/// <item><b>Missing roles</b> = the "need" part of the card-pick score (<see cref="CardScorer"/>: the deck lacks block,
/// AoE, draw … and this card supplies it), plus the low-HP block bonus, times
/// (<see cref="Weights.UpgradedSupplyMultiplier"/> − 1). Why only these? <see cref="DeckAnalyzer"/> counts an upgraded
/// card as 1.2 of a card <i>for role supply only</i>, so upgrading closes 0.2 of the card's share of a role gap — that is
/// the one deck effect of an upgrade the model can see. Archetype commit, combos, "engine missing" and saturation do not
/// change when a card is upgraded, so they are left out (they describe whether to <i>take</i> a card).</item>
/// </list>
/// <para><b>Measured</b> (134 recorded smith choices, <c>RunHistoryAnalyzer rest-advice</c>; agreement is a proxy, not the
/// target): the played card is this ranking's #1 28% of the time and in its top 3 54% (random: 10% / 30%; pack delta
/// alone with ties split evenly: 27% / 60%, the same within noise). Also adding 20% of the archetype/combo parts dropped
/// top-1 to 18%, which is one more reason to leave them out.</para>
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

        var fallbackDelta = AverageDelta(state.Character);
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

        // 1. The upgrade itself (tier pack). A zero delta in the pack means "not filled in", so it also falls back.
        var rated = entry is not null && entry.UpgradedDelta > 0 && double.IsFinite(entry.UpgradedDelta);
        var delta = rated ? entry!.UpgradedDelta : fallbackDelta;
        parts.Add(new ScoreComponent(UpgradeComponentKeys.Upgrade, $"강화 {Signed(delta)}", delta,
            rated ? "티어 팩 upgradedDelta" : "팩에 값 없음: 캐릭터 평균 (추정)"));

        // 2. Missing roles: an upgraded card supplies its roles 1.2× (UpgradedSupplyMultiplier), so the upgrade is worth
        //    0.2 of the "need" the card answers. The pick scorer already computes that need for this deck and act.
        var share = Math.Max(0.0, w.UpgradedSupplyMultiplier - 1.0);
        var eval = _scorer.Score(state, profile, card);
        if (share > 0)
        {
            foreach (var c in eval.Score.Components.Where(c => RoleKeys.Contains(c.Key)))
            {
                var v = c.Value * share;
                if (Math.Abs(v) < Epsilon || !double.IsFinite(v)) continue;
                parts.Add(new ScoreComponent(c.Key, c.Label, v, c.Detail));
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
    /// Average positive <c>upgradedDelta</c> of the character's tier pack (else of all packs, else 0): the value given to
    /// cards the pack does not rate. [추정] "an unknown card's upgrade is worth an average upgrade".
    /// </summary>
    private double AverageDelta(string character)
    {
        static IEnumerable<double> Deltas(IEnumerable<CardTierEntry> entries)
            => entries.Select(e => e.UpgradedDelta).Where(d => d > 0 && double.IsFinite(d));

        var own = Pack.Tiers.TryGetValue(character, out var p) ? Deltas(p.Cards.Values).ToArray() : [];
        if (own.Length > 0) return own.Average();
        var all = Deltas(Pack.Tiers.Values.SelectMany(t => t.Cards.Values)).ToArray();
        return all.Length > 0 ? all.Average() : 0.0;
    }

    private static string Signed(double v) => v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
}
