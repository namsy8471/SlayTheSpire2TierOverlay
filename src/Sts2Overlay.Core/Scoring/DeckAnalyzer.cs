using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>Builds a <see cref="DeckProfile"/> from a run state.</summary>
public static class DeckAnalyzer
{
    /// <summary>Full analysis, including archetype detection via <see cref="ArchetypeDetector.Detect"/>.</summary>
    public static DeckProfile Analyze(RunState state, DataPack pack)
    {
        var profile = AnalyzeDeck(state, pack);
        return profile with
        {
            Archetypes = ArchetypeDetector.Detect(state, pack, profile.RoleSupply, profile.Provides),
        };
    }

    /// <summary>
    /// Everything except <see cref="DeckProfile.Archetypes"/> (left empty): role supply/targets/gaps, axes,
    /// copies, power/basic/curse counts and weak roles. Callers that already know the archetype statuses
    /// can attach them with <c>with { Archetypes = ... }</c>.
    /// </summary>
    public static DeckProfile AnalyzeDeck(RunState state, DataPack pack)
    {
        var w = pack.Weights;
        var supply = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var r in Roles.All) supply[r] = 0.0;
        var provides = new Dictionary<string, int>(StringComparer.Ordinal);
        var consumes = new Dictionary<string, int>(StringComparer.Ordinal);
        var copies = new Dictionary<string, int>(StringComparer.Ordinal);
        int powers = 0, strikes = 0, defends = 0, curses = 0, basics = 0;

        foreach (var dc in state.Deck)
        {
            var id = pack.Canonical(dc.Id);
            copies[id] = copies.GetValueOrDefault(id) + 1;

            var entry = pack.FindTier(state.Character, id);
            if (entry is not null)
            {
                var mult = dc.Upgrade > 0 ? w.UpgradedSupplyMultiplier : 1.0;
                foreach (var (role, strength) in entry.Roles)
                {
                    if (!double.IsFinite(strength)) continue;
                    supply[role] = supply.GetValueOrDefault(role) + strength * mult;
                }
                foreach (var axis in entry.Provides.Distinct(StringComparer.Ordinal)) provides[axis] = provides.GetValueOrDefault(axis) + 1;
                foreach (var axis in entry.Consumes.Distinct(StringComparer.Ordinal)) consumes[axis] = consumes.GetValueOrDefault(axis) + 1;
            }

            var type = pack.CardType(state.Character, id);
            if (type == CardTypes.Power) powers++;
            if (IsCurse(pack, state.Character, id)) curses++;
            if (CardIdHeuristics.IsBasicStrike(id)) strikes++;
            else if (CardIdHeuristics.IsBasicDefend(id)) defends++;
            if (IsBasic(pack, state.Character, id)) basics++;
        }

        var targets = TargetsFor(state, w);
        var gap = new Dictionary<string, double>(StringComparer.Ordinal);
        var weak = new List<string>();
        foreach (var (role, t) in targets)
        {
            if (!(t > 0) || !double.IsFinite(t)) continue;
            var s = supply.GetValueOrDefault(role);
            gap[role] = Math.Max(0.0, t - s) / t;
        }
        foreach (var role in w.WeakRoles)
        {
            if (targets.TryGetValue(role, out var t) && t > 0 && supply.GetValueOrDefault(role) < w.WeakThreshold * t) weak.Add(role);
        }

        return new DeckProfile
        {
            DeckSize = state.Deck.Count,
            RoleSupply = supply,
            RoleTarget = targets,
            RoleGap = gap,
            Provides = provides,
            Consumes = consumes,
            Copies = copies,
            PowerCount = powers,
            BasicStrikes = strikes,
            BasicDefends = defends,
            Curses = curses,
            BasicCards = basics,
            WeakRoles = weak,
        };
    }

    /// <summary>Role targets for the character (falling back to <c>"default"</c>) and act.</summary>
    public static IReadOnlyDictionary<string, double> TargetsFor(RunState state, Weights w)
    {
        if (!w.Targets.TryGetValue(state.Character, out var byAct) && !w.Targets.TryGetValue("default", out byAct))
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
        return Weights.ForAct(byAct, state.Act, new Dictionary<string, double>(StringComparer.Ordinal));
    }

    /// <summary>
    /// True for a starter card: rarity "basic" from the catalog (else the tier entry); only when neither knows the rarity,
    /// the Strike/Defend id heuristic.
    /// </summary>
    public static bool IsBasic(DataPack pack, string character, string cardId)
        => pack.CardRarity(character, cardId) is { } rarity
            ? rarity == "basic"
            : CardIdHeuristics.IsBasicStrikeOrDefend(pack.Canonical(cardId));

    /// <summary>True when the card is a curse by type, or the catalog marks its rarity as curse.</summary>
    public static bool IsCurse(DataPack pack, string character, string cardId)
        => pack.CardType(character, cardId) == CardTypes.Curse
           || string.Equals(pack.FindCatalog(cardId)?.Rarity, CardTypes.Curse, StringComparison.OrdinalIgnoreCase);
}
