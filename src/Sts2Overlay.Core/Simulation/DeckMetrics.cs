using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Simulation;

/// <summary>Where a deck card stands relative to the deck's main build (<see cref="DeckMetrics.Primary"/>).</summary>
public enum CardFit
{
    /// <summary>Starter card (Strike, Defend, the character's basic card).</summary>
    Basic,

    /// <summary>Listed in the main build (core, payoff, enabler or support).</summary>
    Build,

    /// <summary>Not listed, but on-theme for the main build (<see cref="Archetype.IsOnTheme"/>).</summary>
    OnTheme,

    /// <summary>Listed in another build of the character, not the main one.</summary>
    OtherBuild,

    /// <summary>In no build of the character (a "good stuff" card).</summary>
    Generic,

    /// <summary>Curse or status.</summary>
    Junk,
}

/// <summary>
/// Deck shape numbers used to compare simulated decks with top players' winning decks: build commitment, coherence (how many
/// non-starter cards serve the main build), cost curve and draw density. Card roles come only from the pack (archetype lists,
/// tier roles, catalog costs), never from card ids in code.
/// </summary>
public sealed record DeckMetrics
{
    /// <summary>A card counts as a draw card when its tier <c>roles.draw</c> is at least this strong.</summary>
    public const double DrawRoleMin = 0.4;

    public int DeckSize { get; init; }
    public int BasicCards { get; init; }

    /// <summary>Main build: highest commit (ties: detector order). Null when no build has any commit.</summary>
    public ArchetypeStatus? Primary { get; init; }

    public double PrimaryCommit => Primary?.Commit ?? 0.0;
    public bool PrimaryCoreSecured => Primary is { } p && p.CoreTarget > 0 && p.OwnedCore >= p.CoreTarget;
    public bool PrimaryComplete => Primary is { } p && p.Commit >= p.Archetype.CommitThreshold - 1e-9;

    /// <summary>Card count per <see cref="CardFit"/> (copies counted).</summary>
    public IReadOnlyDictionary<CardFit, int> Fit { get; init; } = new Dictionary<CardFit, int>();

    /// <summary>Non-starter, non-junk cards (the cards the player chose).</summary>
    public int ChosenCards => Fit.Where(kv => kv.Key is not (CardFit.Basic or CardFit.Junk)).Sum(kv => kv.Value);

    /// <summary>(Build + OnTheme) / chosen cards; 0 for a deck with no chosen card.</summary>
    public double Coherence => ChosenCards == 0 ? 0.0
        : (double)(Fit.GetValueOrDefault(CardFit.Build) + Fit.GetValueOrDefault(CardFit.OnTheme)) / ChosenCards;

    /// <summary>Generic / chosen cards.</summary>
    public double GenericShare => ChosenCards == 0 ? 0.0 : (double)Fit.GetValueOrDefault(CardFit.Generic) / ChosenCards;

    /// <summary>Builds with at least two distinct listed cards in the deck.</summary>
    public int BuildsTouched { get; init; }

    /// <summary>Chosen cards whose tier <c>roles.draw</c> ≥ <see cref="DrawRoleMin"/>.</summary>
    public int DrawCards { get; init; }

    /// <summary>Of <see cref="DrawCards"/>, those that are neither listed in nor on-theme for the main build.</summary>
    public int OffBuildDrawCards { get; init; }

    /// <summary>Mean energy cost of chosen cards with a fixed cost (X-cost and unknown-cost cards left out).</summary>
    public double MeanCost { get; init; }

    /// <summary>Chosen cards by cost bucket: 0, 1, 2, 3+ and X.</summary>
    public IReadOnlyList<int> CostBuckets { get; init; } = [0, 0, 0, 0, 0];

    /// <summary>Σ fixed costs of all deck cards / deck size (starter cards included): energy a random hand of 5 asks for, per card.</summary>
    public double EnergyPerCard { get; init; }

    public int Powers { get; init; }

    /// <summary>Copies beyond the first of cards listed in the main build (e.g. a third Blade Dance counts 2).</summary>
    public int BuildExtraCopies { get; init; }

    /// <summary>Copies beyond the first of chosen cards outside the main build.</summary>
    public int OtherExtraCopies { get; init; }

    public static DeckMetrics Compute(DataPack pack, string character, IReadOnlyList<DeckCard> deck, IReadOnlyList<string>? relics = null)
    {
        var state = new RunState { Character = character, Deck = deck, Relics = relics ?? [] };
        var statuses = ArchetypeDetector.Detect(state, pack);
        var primary = statuses.Count > 0 && statuses[0].Commit > 0 ? statuses[0] : null;
        var archetypes = pack.ArchetypesFor(character).ToArray();

        var fit = new Dictionary<CardFit, int>();
        int draw = 0, offDraw = 0, powers = 0, basics = 0;
        double costSum = 0, allCostSum = 0;
        var costCount = 0;
        var buckets = new int[5];
        var distinctPerBuild = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int buildExtra = 0, otherExtra = 0;

        foreach (var dc in deck)
        {
            var id = pack.Canonical(dc.Id);
            var entry = pack.FindTier(character, id);
            var catalog = pack.FindCatalog(id);
            var type = pack.CardType(character, id);
            if (type == CardTypes.Power) powers++;
            var cost = catalog?.Cost ?? entry?.Cost;
            if (cost is int c0 && c0 >= 0 && catalog?.CostX != true) allCostSum += c0;

            CardFit f;
            if (DeckAnalyzer.IsCurse(pack, character, id) || type == CardTypes.Status) f = CardFit.Junk;
            else if (DeckAnalyzer.IsBasic(pack, character, id)) f = CardFit.Basic;
            else if (primary?.Archetype.RoleOf(id) is not null) f = CardFit.Build;
            else if (primary is not null && primary.Archetype.IsOnTheme(id, entry, pack.Canonical)) f = CardFit.OnTheme;
            else if (archetypes.Any(a => a.RoleOf(id) is not null)) f = CardFit.OtherBuild;
            else f = CardFit.Generic;
            fit[f] = fit.GetValueOrDefault(f) + 1;
            if (f == CardFit.Basic) basics++;

            foreach (var a in archetypes)
            {
                if (a.RoleOf(id) is null) continue;
                if (!distinctPerBuild.TryGetValue(a.Id, out var set)) distinctPerBuild[a.Id] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(id);
            }

            if (f is CardFit.Basic or CardFit.Junk) continue;
            if (!seen.Add(id))
            {
                if (f == CardFit.Build) buildExtra++;
                else otherExtra++;
            }
            if ((entry?.Roles.GetValueOrDefault(Roles.Draw) ?? 0.0) >= DrawRoleMin)
            {
                draw++;
                if (f is not (CardFit.Build or CardFit.OnTheme)) offDraw++;
            }
            if (catalog?.CostX == true) buckets[4]++;
            else if (cost is int c && c >= 0)
            {
                buckets[Math.Min(c, 3)]++;
                costSum += c;
                costCount++;
            }
        }

        return new DeckMetrics
        {
            DeckSize = deck.Count,
            BasicCards = basics,
            Primary = primary,
            Fit = fit,
            BuildsTouched = distinctPerBuild.Values.Count(s => s.Count >= 2),
            DrawCards = draw,
            OffBuildDrawCards = offDraw,
            MeanCost = costCount == 0 ? 0.0 : costSum / costCount,
            CostBuckets = buckets,
            EnergyPerCard = deck.Count == 0 ? 0.0 : allCostSum / deck.Count,
            Powers = powers,
            BuildExtraCopies = buildExtra,
            OtherExtraCopies = otherExtra,
        };
    }
}
