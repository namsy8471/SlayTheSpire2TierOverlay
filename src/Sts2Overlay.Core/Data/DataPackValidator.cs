using System.Text.RegularExpressions;

namespace Sts2Overlay.Core.Data;

/// <summary>Finds data problems before they become silent bad advice.</summary>
public static partial class DataPackValidator
{
    /// <summary>Inclusive upper bound for <see cref="CardTierEntry.DupFactor"/>.</summary>
    public const double MaxDupFactor = 3.0;

    [GeneratedRegex(@"^CARD\.[A-Z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CardIdRegex();

    [GeneratedRegex(@"^RELIC\.[A-Z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RelicIdRegex();

    [GeneratedRegex(@"^CHARACTER\.[A-Z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterIdRegex();

    public static bool IsCardId(string? id) => id is not null && CardIdRegex().IsMatch(id);

    public static bool IsRelicId(string? id) => id is not null && RelicIdRegex().IsMatch(id);

    /// <summary><c>CHARACTER.X</c> or <see cref="DataPack.Colorless"/>.</summary>
    public static bool IsCharacterId(string? id) => id is not null && (id == DataPack.Colorless || CharacterIdRegex().IsMatch(id));

    /// <summary>
    /// Errors and warnings: malformed ids (must look like <c>CARD.X</c>/<c>RELIC.X</c>), role strengths outside 0..1,
    /// archetype cards without tier entries, duplicate archetype ids, combos with fewer than 2 cards,
    /// and (when a catalog is present) ids missing from the catalog.
    /// </summary>
    /// <remarks>
    /// <see cref="ValidationSeverity.Error"/>: the data is wrong (malformed ids, out-of-range numbers, duplicate archetype ids,
    /// combos with fewer than 2 cards, empty archetypes, non-positive <c>fullWeight</c>).
    /// <see cref="ValidationSeverity.Warning"/>: the data is probably incomplete (archetype/combo cards without a tier entry,
    /// ids missing from the catalog, archetype characters without a tier pack, unknown role keys, ...).
    /// The full rule list is in docs/DATA_SCHEMA.md.
    /// </remarks>
    public static IReadOnlyList<ValidationIssue> Validate(DataPack pack)
    {
        var issues = new List<ValidationIssue>();
        void Error(string where, string message) => issues.Add(new ValidationIssue(ValidationSeverity.Error, where, message));
        void Warn(string where, string message) => issues.Add(new ValidationIssue(ValidationSeverity.Warning, where, message));

        var catalogIds = pack.Catalog?.Cards.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        void CheckCardRef(string where, string id, string? character, bool requireTier)
        {
            if (!IsCardId(id))
            {
                Error(where, $"Malformed card id '{id}' (expected CARD.X).");
                return;
            }
            var canonical = pack.Canonical(id);
            if (!string.Equals(canonical, id, StringComparison.Ordinal))
            {
                Warn(where, $"'{id}' is an alias of '{canonical}'; use the current id.");
            }
            if (requireTier && pack.FindTier(character ?? "", canonical) is null)
            {
                Warn(where, $"'{canonical}' has no tier entry.");
            }
            if (catalogIds is not null && !catalogIds.Contains(canonical))
            {
                Warn(where, $"'{canonical}' is not in the catalog.");
            }
        }

        ValidateWeights(pack.Weights, Error, Warn);

        // ---- Tier packs ----
        foreach (var (key, tp) in pack.Tiers.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var where = $"tiers[{key}]";
            if (!IsCharacterId(tp.Character))
            {
                Error(where, $"Malformed character '{tp.Character}' (expected CHARACTER.X or {DataPack.Colorless}).");
            }
            if (!string.Equals(key, tp.Character, StringComparison.Ordinal))
            {
                Warn(where, $"Dictionary key differs from the pack's character '{tp.Character}'.");
            }
            foreach (var (cardId, e) in tp.Cards.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var w = $"{where}.cards[{cardId}]";
                if (!IsCardId(cardId))
                {
                    Error(w, $"Malformed card id '{cardId}' (expected CARD.X).");
                }
                else if (catalogIds is not null && !catalogIds.Contains(pack.Canonical(cardId)))
                {
                    Warn(w, $"'{cardId}' is not in the catalog.");
                }
                foreach (var (role, strength) in e.Roles)
                {
                    if (!Roles.All.Contains(role)) Warn($"{w}.roles.{role}", $"Unknown role '{role}'.");
                    if (!InRange(strength, 0, 1)) Error($"{w}.roles.{role}", $"Role strength {strength} is outside 0..1.");
                }
                if (!InRange(e.Confidence, 0, 1)) Error($"{w}.confidence", $"Confidence {e.Confidence} is outside 0..1.");
                if (!InRange(e.DupFactor, 0, MaxDupFactor)) Error($"{w}.dupFactor", $"dupFactor {e.DupFactor} is outside 0..{MaxDupFactor}.");
                if (!InRange(e.Cycle, 0, 1)) Error($"{w}.cycle", $"cycle {e.Cycle} is outside 0..1.");
                if (!double.IsFinite(e.UpgradedDelta)) Error($"{w}.upgradedDelta", "upgradedDelta must be a finite number.");
                if (e.ActTier is not null)
                {
                    foreach (var act in e.ActTier.Keys.Where(a => a < 1))
                    {
                        Error($"{w}.actTier", $"Act {act} must be 1 or higher.");
                    }
                }
                if (e.Cost is < 0) Error($"{w}.cost", $"Cost {e.Cost} is negative.");
            }
        }

        // ---- Archetypes ----
        var seenArchetypes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < pack.Archetypes.Count; i++)
        {
            var a = pack.Archetypes[i];
            var where = string.IsNullOrEmpty(a.Id) ? $"archetypes[{i}]" : $"archetypes[{a.Id}]";
            if (string.IsNullOrWhiteSpace(a.Id)) Error(where, "Archetype has no id.");
            else if (!seenArchetypes.Add(a.Id)) Error(where, $"Duplicate archetype id '{a.Id}'.");

            if (!IsCharacterId(a.Character)) Error(where, $"Malformed character '{a.Character}' (expected CHARACTER.X).");
            else if (!pack.Tiers.ContainsKey(a.Character)) Warn(where, $"No tier pack for character '{a.Character}'.");

            if (!a.Cards.All.Any()) Error($"{where}.cards", "Archetype has no cards.");
            var seenCards = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (role, ids) in RoleLists(a.Cards))
            {
                foreach (var id in ids)
                {
                    var w = $"{where}.cards.{role}[{id}]";
                    CheckCardRef(w, id, a.Character, requireTier: true);
                    if (seenCards.TryGetValue(id, out var firstRole)) Warn(w, $"'{id}' is listed again (first as {firstRole}).");
                    else seenCards[id] = role;
                }
            }

            foreach (var (role, weight) in a.RoleWeight)
            {
                if (role is not (ArchetypeRoles.Core or ArchetypeRoles.Enabler or ArchetypeRoles.Payoff or ArchetypeRoles.Support))
                {
                    Warn($"{where}.roleWeight.{role}", $"Unknown archetype role '{role}'.");
                }
                if (!double.IsFinite(weight) || weight < 0) Error($"{where}.roleWeight.{role}", $"Role weight {weight} must be ≥ 0.");
            }
            if (!double.IsFinite(a.FullWeight) || a.FullWeight <= 0) Error($"{where}.fullWeight", $"fullWeight {a.FullWeight} must be > 0.");
            if (!(a.CommitThreshold > 0 && a.CommitThreshold <= 1)) Error($"{where}.commitThreshold", $"commitThreshold {a.CommitThreshold} must be in (0, 1].");
            if (a.CoreTarget < 1) Error($"{where}.coreTarget", $"coreTarget {a.CoreTarget} must be ≥ 1.");
            var coreCount = a.Cards.Core.Select(pack.Canonical).Distinct(StringComparer.Ordinal).Count();
            if (coreCount == 0) Warn($"{where}.cards.core", "Archetype has no core card; the core-first rule never applies to it.");
            else if (a.CoreTarget > coreCount) Warn($"{where}.coreTarget", $"coreTarget {a.CoreTarget} is more than the {coreCount} core card(s); it is clamped to {coreCount}.");

            foreach (var r in a.KeyRelics)
            {
                var w = $"{where}.keyRelics[{r.Id}]";
                if (!IsRelicId(r.Id)) Error(w, $"Malformed relic id '{r.Id}' (expected RELIC.X).");
                if (!double.IsFinite(r.W) || r.W < 0) Error(w, $"Relic weight {r.W} must be ≥ 0.");
            }

            for (var n = 0; n < a.Needs.Count; n++)
            {
                var need = a.Needs[n];
                var w = $"{where}.needs[{n}]";
                if (need.Tag is null && need.Role is null) Error(w, "Need has neither \"tag\" nor \"role\".");
                if (need.Role is not null && !Roles.All.Contains(need.Role)) Warn(w, $"Unknown role '{need.Role}'.");
                if (!double.IsFinite(need.Min) || need.Min < 0) Error(w, $"min {need.Min} must be ≥ 0.");
            }

            if (a.GuideHeroCard is not null)
            {
                if (!IsCardId(a.GuideHeroCard)) Error($"{where}.guideHeroCard", $"Malformed card id '{a.GuideHeroCard}'.");
                else if (!seenCards.ContainsKey(a.GuideHeroCard)) Warn($"{where}.guideHeroCard", $"'{a.GuideHeroCard}' is not one of the archetype's cards.");
            }
        }

        // ---- Combos ----
        for (var i = 0; i < pack.Combos.Count; i++)
        {
            var c = pack.Combos[i];
            var where = string.IsNullOrEmpty(c.Name) ? $"combos[{i}]" : $"combos[{i}:{c.Name}]";
            if (c.Character is not null)
            {
                if (!IsCharacterId(c.Character)) Error(where, $"Malformed character '{c.Character}'.");
                else if (!pack.Tiers.ContainsKey(c.Character)) Warn(where, $"No tier pack for character '{c.Character}'.");
            }
            if (c.Cards.Distinct(StringComparer.Ordinal).Count() < 2) Error(where, "A combo needs at least 2 different cards.");
            foreach (var id in c.Cards)
            {
                CheckCardRef($"{where}.cards[{id}]", id, c.Character, requireTier: true);
            }
            if (!double.IsFinite(c.W) || c.W <= 0) Warn($"{where}.w", $"Combo weight {c.W} is not positive; the combo has no effect.");
        }

        // ---- Aliases ----
        foreach (var (from, to) in pack.IdAliases.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var where = $"aliases[{from}]";
            if (!IsCardId(from) && !IsRelicId(from)) Error(where, $"Malformed alias source '{from}'.");
            if (!IsCardId(to) && !IsRelicId(to)) Error(where, $"Malformed alias target '{to}'.");
            if (pack.IdAliases.ContainsKey(to)) Warn(where, $"Alias target '{to}' is itself an alias; chains are not followed.");
            if (catalogIds is not null && IsCardId(to) && !catalogIds.Contains(to)) Warn(where, $"'{to}' is not in the catalog.");
        }

        return issues;
    }

    private static void ValidateWeights(Weights w, Action<string, string> error, Action<string, string> warn)
    {
        foreach (var t in Enum.GetValues<Tier>())
        {
            if (!w.TierValue.ContainsKey(t)) warn("weights.tierValue", $"No value for tier {t}.");
        }
        if (!InRange(w.UnknownConfidence, 0, 1)) error("weights.unknownConfidence", "Must be within 0..1.");
        if (!InRange(w.CommitThreshold, 0, 1)) error("weights.commitThreshold", "Must be within 0..1.");
        if (!InRange(w.HybridMargin, 0, 1)) error("weights.hybridMargin", "Must be within 0..1.");
        if (!InRange(w.WeakThreshold, 0, 1)) error("weights.weakThreshold", "Must be within 0..1.");
        if (!InRange(w.Skip.LowConfidence, 0, 1)) error("weights.skip.lowConfidence", "Must be within 0..1.");
        if (w.Completion.CardsPerReward < 1) error("weights.completion.cardsPerReward", "Must be at least 1.");
        var odds = w.CardOdds;
        foreach (var (name, src) in new[]
                 {
                     ("cardReward", odds.CardReward), ("eliteReward", odds.EliteReward), ("bossReward", odds.BossReward), ("shop", odds.Shop),
                 })
        {
            var where = $"weights.cardOdds.{name}";
            if (!InRange(src.Rare, 0, 1)) error($"{where}.rare", $"Odds {src.Rare} are outside 0..1.");
            if (!InRange(src.Uncommon, 0, 1)) error($"{where}.uncommon", $"Odds {src.Uncommon} are outside 0..1.");
            if (src.RareHighAscension is { } rh && !InRange(rh, 0, 1)) error($"{where}.rareHighAscension", $"Odds {rh} are outside 0..1.");
            if (src.Rare + src.Uncommon > 1.0 + 1e-9) warn(where, "Rare + uncommon odds are more than 1.");
        }
        if (!InRange(odds.RareMultiplierHighAscension, 0, 1)) error("weights.cardOdds.rareMultiplierHighAscension", "Must be within 0..1.");
        if (!InRange(odds.Pity.Cap, -1, 1) || !InRange(odds.Pity.Start, -1, 1) || odds.Pity.Start > odds.Pity.Cap)
        {
            error("weights.cardOdds.pity", "start and cap must be within -1..1 with start ≤ cap.");
        }
        if (!InRange(odds.Pity.Step, 0, 1) || !InRange(odds.Pity.StepHighAscension, 0, 1)) error("weights.cardOdds.pity", "Steps must be within 0..1.");
        if (!InRange(odds.UpgradeChancePerAct, 0, 1) || !InRange(odds.UpgradeChancePerActHighAscension, 0, 1))
        {
            error("weights.cardOdds.upgradeChancePerAct", "Upgrade chances must be within 0..1.");
        }
        foreach (var (rarity, price) in w.Shop.CardPrice)
        {
            if (price <= 0) error($"weights.shop.cardPrice.{rarity}", $"Price {price} must be > 0.");
        }
        if (!(w.Shop.PriceJitterMin > 0 && w.Shop.PriceJitterMin <= w.Shop.PriceJitterMax && double.IsFinite(w.Shop.PriceJitterMax)))
        {
            error("weights.shop.priceJitter", "Need 0 < priceJitterMin ≤ priceJitterMax.");
        }
        if (!(w.Shop.ColorlessPriceMultiplier > 0) || !double.IsFinite(w.Shop.ColorlessPriceMultiplier)) error("weights.shop.colorlessPriceMultiplier", "Must be > 0.");
        if (!InRange(w.Shop.SaleMultiplier, 0, 1)) error("weights.shop.saleMultiplier", "Must be within 0..1.");
        var cp = w.CorePriority;
        if (!double.IsFinite(cp.Bonus) || cp.Bonus < 0) error("weights.corePriority.bonus", "Must be ≥ 0.");
        if (!double.IsFinite(cp.StarterBonus) || cp.StarterBonus < 0) error("weights.corePriority.starterBonus", "Must be ≥ 0.");
        if (!InRange(cp.LeanMin, 0, 1)) error("weights.corePriority.leanMin", "Must be within 0..1.");
        if (!(cp.LeanFull > 0 && cp.LeanFull <= 1)) error("weights.corePriority.leanFull", "Must be within (0, 1].");
        else if (cp.LeanFull < cp.LeanMin) warn("weights.corePriority.leanFull", "leanFull is below leanMin; every leaning build gets the full bonus.");
        if (!InRange(cp.WeakFactor, 0, 1)) error("weights.corePriority.weakFactor", "Must be within 0..1.");
        foreach (var (role, factor) in cp.NoCoreFactor)
        {
            if (role is not (ArchetypeRoles.Enabler or ArchetypeRoles.Payoff or ArchetypeRoles.Support))
            {
                warn($"weights.corePriority.noCoreFactor.{role}", $"'{role}' is not a non-core archetype role (enabler/payoff/support); ignored.");
            }
            if (!InRange(factor, 0, 1)) error($"weights.corePriority.noCoreFactor.{role}", $"Factor {factor} is outside 0..1.");
        }
        foreach (var (rarity, size) in w.Completion.PoolSize)
        {
            if (size <= 0) error($"weights.completion.poolSize.{rarity}", $"Pool size {size} must be > 0.");
        }
    }

    private static IEnumerable<(string Role, List<string> Ids)> RoleLists(ArchetypeCards c)
    {
        yield return (ArchetypeRoles.Core, c.Core);
        yield return (ArchetypeRoles.Payoff, c.Payoff);
        yield return (ArchetypeRoles.Enabler, c.Enabler);
        yield return (ArchetypeRoles.Support, c.Support);
    }

    private static bool InRange(double v, double min, double max) => double.IsFinite(v) && v >= min && v <= max;
}

public enum ValidationSeverity
{
    Warning,
    Error,
}

public sealed record ValidationIssue(ValidationSeverity Severity, string Where, string Message);
