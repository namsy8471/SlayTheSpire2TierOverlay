using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Overlay.Resources.Catalog;

namespace CatalogBuilder.CardVars;

/// <summary>
/// <c>--card-vars &lt;file&gt;</c>: writes the names-free card variable table and, when localization is present,
/// checks that every variable each description template uses is covered.
/// </summary>
internal static class CardVarsCommand
{
    /// <summary>What each context variable means (shown in the JSON so the overlay knows how to fill it).</summary>
    private static readonly Dictionary<string, string> ContextMeaning = new(StringComparer.Ordinal)
    {
        ["IfUpgraded"] = "IfUpgradedVar: {IfUpgraded:show:upgraded|normal} picks the first branch on an upgraded card (green in the upgrade preview), else the second",
        ["OnTable"] = "bool: the card is in hand or being played",
        ["InCombat"] = "bool: a combat is running and the card is in a combat pile (false in rewards, shop, deck view, library)",
        ["IsTargeting"] = "bool: the card is currently aimed at a target",
        ["TargetType"] = "string: the card's TargetType enum name (AnyEnemy, Self, ...)",
        ["GainsBlock"] = "bool: CardModel.GainsBlock",
        ["IsOstyAlive"] = "bool: the owner's Osty is alive (Necrobinder); false outside a run",
        ["energyPrefix"] = "string: energy icon colour of the card's pool (ironclad, silent, ..., colorless); used as {energyPrefix:energyIcons(n)} = n energy icons",
        ["singleStarIcon"] = "string: one star icon image tag",
    };

    public static int Run(string dllPath, string gameVersion, IReadOnlyList<CardRow> catalog, string outPath)
    {
        using var game = new GameReader(dllPath);
        var extractor = new CardVarsExtractor(game.CardModel);
        var typeById = game.ConcreteSubclassesOf(game.CardModel).ToDictionary(game.ModelIdOf);

        var warnings = new List<string>();
        var rows = new List<CardVarsRow>();
        foreach (CardRow card in catalog.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (!typeById.TryGetValue(card.Id, out var type)) { warnings.Add($"{card.Id}: class not found"); continue; }
            rows.Add(extractor.Extract(type, card.Id, card.Cost, warnings));
        }

        List<string> contextVars = extractor.DescriptionContextVars();
        var file = new CardVarsFile(
            gameVersion,
            "static-il",
            "Game facts only (numbers and variable names, no text). vars = CanonicalVars base values; upgrade.vars = amount added by one "
            + "upgrade (OnUpgrade), costDelta = energy cost change, add/removeKeywords = keyword changes. calculated: value shown out of combat "
            + "= vars[base]; in combat it is vars[base] + vars[extra] x a runtime count. unresolved = variables only known at runtime. "
            + "contextVars = variables CardModel.GetDescriptionForPile adds to every card description. Regenerate: dotnet run --project "
            + "tools/CatalogBuilder -- --card-vars data/catalog/card-vars.<version>.json [--loc <localization dir> to also check the templates]",
            contextVars.ToDictionary(n => n, n => ContextMeaning.GetValueOrDefault(n, "added by CardModel.GetDescriptionForPile (meaning not documented here)")),
            rows);

        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep '+', '<', '\'' readable
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(file, opts) + "\n");

        Console.WriteLine($"Wrote {rows.Count} card var rows -> {outPath}");
        Console.WriteLine($"  context vars (GetDescriptionForPile): {string.Join(", ", contextVars)}");
        Console.WriteLine($"  cards with vars: {rows.Count(r => r.Vars.Count > 0)}, total vars: {rows.Sum(r => r.Vars.Count)}");
        Console.WriteLine($"  upgrades: var changes {rows.Count(r => r.Upgrade.Vars.Count > 0)}, cost changes {rows.Count(r => r.Upgrade.CostDelta != 0)}, "
                          + $"+keywords {rows.Count(r => r.Upgrade.AddKeywords.Count > 0)}, -keywords {rows.Count(r => r.Upgrade.RemoveKeywords.Count > 0)}, "
                          + $"star cost {rows.Count(r => r.Upgrade.StarCostDelta != null)}");
        foreach (var r in rows.Where(r => r.Upgrade.Other != null))
            Console.WriteLine($"  OTHER upgrade effects {r.Id}: {string.Join(", ", r.Upgrade.Other!)}");
        foreach (var r in rows.Where(r => r.Unresolved.Count > 0))
            Console.WriteLine($"  unresolved {r.Id}: {string.Join(", ", r.Unresolved)}");
        foreach (string w in warnings) Console.WriteLine($"  WARNING: {w}");

        return Verify(catalog, rows, contextVars);
    }

    /// <summary>
    /// For every card and language: each variable its description template references must be a card var,
    /// a context var, or listed as unresolved. Prints coverage; returns 0 when everything is covered.
    /// </summary>
    private static int Verify(IReadOnlyList<CardRow> catalog, List<CardVarsRow> rows, List<string> contextVars)
    {
        var byId = rows.ToDictionary(r => r.Id);
        var context = new HashSet<string>(contextVars, StringComparer.Ordinal);
        int refs = 0, covered = 0, inVars = 0, inContext = 0, inUnresolved = 0, templates = 0;
        var missing = new List<string>();
        var unusedVars = new Dictionary<string, int>();

        foreach (CardRow card in catalog)
        {
            if (!byId.TryGetValue(card.Id, out var row)) continue;
            var usedAnywhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (lang, template) in card.DescriptionTemplate)
            {
                if (template is null) continue;
                templates++;
                foreach (string name in TemplateVars.Referenced(template))
                {
                    usedAnywhere.Add(name);
                    refs++;
                    if (row.Vars.ContainsKey(name)) { covered++; inVars++; }
                    else if (context.Contains(name)) { covered++; inContext++; }
                    else if (row.Unresolved.Contains(name)) { covered++; inUnresolved++; }
                    else missing.Add($"{card.Id} [{lang}] {name}");
                }
            }
            foreach (string v in row.Vars.Keys.Where(v => !usedAnywhere.Contains(v)))
                unusedVars[v] = unusedVars.GetValueOrDefault(v) + 1;
        }

        Console.WriteLine();
        if (templates == 0)
        {
            Console.WriteLine("Template check skipped: no localization loaded (pass --loc <dir with kor/ and eng/ cards.json>).");
            return 0;
        }
        Console.WriteLine($"Template check ({templates} description templates, kor+eng):");
        Console.WriteLine($"  references covered {covered}/{refs} ({(refs == 0 ? 100 : 100.0 * covered / refs):F2}%): "
                          + $"card vars {inVars}, context vars {inContext}, unresolved (runtime) {inUnresolved}");
        foreach (string m in missing) Console.WriteLine($"  MISSING {m}");
        if (unusedVars.Count > 0)
            Console.WriteLine($"  vars no template mentions (fine; used by game code only): "
                              + string.Join(", ", unusedVars.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")));
        return missing.Count == 0 ? 0 : 2;
    }
}
