using System.Text.Json;
using System.Text.Json.Serialization;
using CatalogBuilder.CardVars;
using Sts2Overlay.Resources.Catalog;

namespace CatalogBuilder.RelicVars;

/// <summary>
/// <c>--relic-vars &lt;file&gt;</c>: writes the names-free relic variable table and, when localization is present,
/// checks that every variable each relic description template (relics.json "&lt;ENTRY&gt;.description") uses is covered.
/// </summary>
internal static class RelicVarsCommand
{
    /// <summary>What each context variable means (shown in the JSON so the overlay knows how to fill it).</summary>
    private static readonly Dictionary<string, string> ContextMeaning = new(StringComparer.Ordinal)
    {
        ["energyPrefix"] = "string: EnergyIconHelper.GetPrefix(relic) = energy icon colour of the owner's character card pool during a run, "
                           + "else of the relic's pool (ironclad, silent, ..., colorless); used as {energyPrefix:energyIcons(n)} = n energy icons. "
                           + "Every EnergyVar of the relic gets the same colour",
        ["singleStarIcon"] = "string: one star icon image tag",
    };

    public static int Run(string dllPath, string gameVersion, string? locDir, string outPath)
    {
        using var game = new GameReader(dllPath);
        var extractor = new RelicVarsExtractor(game);

        var warnings = new List<string>();
        var rows = game.ConcreteSubclassesOf(game.RelicModel)
            .Select(t => (Type: t, Id: game.ModelIdOf(t)))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => extractor.Extract(x.Type, x.Id, warnings))
            .ToList();

        List<string> contextVars = extractor.DescriptionContextVars();
        var file = new RelicVarsFile(
            gameVersion,
            "static-il",
            "Game facts only (numbers and variable names, no text). vars = CanonicalVars base values (what a fresh relic shows). "
            + "strings = StringVars whose text is another model's title (value = model id + \".title\"). changedAtRuntime = vars the "
            + "relic's own code rewrites during a run (counters, card/relic names filled in on pickup) or that start from a saved "
            + "property; the listed value is the fresh relic's. unresolved = variables only known at runtime. contextVars = "
            + "variables RelicModel.DynamicDescription adds to every relic description. Regenerate: dotnet run --project "
            + "tools/CatalogBuilder -- --relic-vars data/catalog/relic-vars.<version>.json [--loc <localization dir> to also check the templates]",
            contextVars.ToDictionary(n => n, n => ContextMeaning.GetValueOrDefault(n, "added by RelicModel.DynamicDescription (meaning not documented here)")),
            rows);

        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(file, opts) + "\n");

        Console.WriteLine($"Wrote {rows.Count} relic var rows -> {outPath}");
        Console.WriteLine($"  context vars (DynamicDescription): {string.Join(", ", contextVars)}");
        Console.WriteLine($"  relics with vars: {rows.Count(r => r.Vars.Count > 0 || r.Strings != null)}, total numeric vars: {rows.Sum(r => r.Vars.Count)}");
        foreach (var r in rows.Where(r => r.Strings != null))
            Console.WriteLine($"  strings {r.Id}: {string.Join(", ", r.Strings!.Select(kv => $"{kv.Key}={kv.Value}"))}");
        foreach (var r in rows.Where(r => r.ChangedAtRuntime != null))
            Console.WriteLine($"  changed at runtime {r.Id}: {string.Join(", ", r.ChangedAtRuntime!)}");
        foreach (var r in rows.Where(r => r.Unresolved.Count > 0))
            Console.WriteLine($"  unresolved {r.Id}: {string.Join(", ", r.Unresolved)}");
        foreach (string w in warnings) Console.WriteLine($"  WARNING: {w}");

        return Verify(rows, contextVars, locDir);
    }

    /// <summary>
    /// For every relic and language: each variable its description template references must be a relic var,
    /// a string var, a context var, or listed as unresolved. Prints coverage; returns 0 when everything is covered.
    /// </summary>
    private static int Verify(List<RelicVarsRow> rows, List<string> contextVars, string? locDir)
    {
        var context = new HashSet<string>(contextVars, StringComparer.Ordinal);
        int refs = 0, covered = 0, inVars = 0, inStrings = 0, inContext = 0, inUnresolved = 0, templates = 0, noTemplate = 0;
        var missing = new List<string>();
        var unusedVars = new Dictionary<string, int>();
        var tables = CatalogGenerator.Languages
            .Select(lang => (lang, table: LoadTable(locDir, lang)))
            .Where(x => x.table != null)
            .ToList();
        if (tables.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Template check skipped: no localization loaded (pass --loc <dir with kor/ and eng/ relics.json>).");
            return 0;
        }

        foreach (RelicVarsRow row in rows)
        {
            string entry = row.Id[(row.Id.IndexOf('.') + 1)..];
            var usedAnywhere = new HashSet<string>(StringComparer.Ordinal);
            bool any = false;
            foreach (var (lang, table) in tables)
            {
                if (!table!.TryGetValue(entry + ".description", out string? template)) continue;
                any = true;
                templates++;
                foreach (string name in TemplateVars.Referenced(template))
                {
                    usedAnywhere.Add(name);
                    refs++;
                    if (row.Vars.ContainsKey(name)) { covered++; inVars++; }
                    else if (row.Strings?.ContainsKey(name) == true) { covered++; inStrings++; }
                    else if (context.Contains(name)) { covered++; inContext++; }
                    else if (row.Unresolved.Contains(name)) { covered++; inUnresolved++; }
                    else missing.Add($"{row.Id} [{lang}] {name}");
                }
            }
            if (!any) noTemplate++;
            foreach (string v in row.Vars.Keys.Where(v => !usedAnywhere.Contains(v)))
                unusedVars[v] = unusedVars.GetValueOrDefault(v) + 1;
        }

        Console.WriteLine();
        Console.WriteLine($"Template check ({templates} description templates, {string.Join("+", tables.Select(t => t.lang))}; "
                          + $"{noTemplate} relic classes have no description):");
        Console.WriteLine($"  references covered {covered}/{refs} ({(refs == 0 ? 100 : 100.0 * covered / refs):F2}%): "
                          + $"relic vars {inVars}, string vars {inStrings}, context vars {inContext}, unresolved (runtime) {inUnresolved}");
        foreach (string m in missing) Console.WriteLine($"  MISSING {m}");
        if (unusedVars.Count > 0)
            Console.WriteLine($"  vars no template mentions (fine; used by game code only): "
                              + string.Join(", ", unusedVars.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")));
        return missing.Count == 0 ? 0 : 2;
    }

    /// <summary>&lt;locDir&gt;/&lt;lang&gt;/relics.json as a flat key -> text table, or null when absent.</summary>
    private static Dictionary<string, string>? LoadTable(string? locDir, string lang)
    {
        if (locDir == null) return null;
        string path = Path.Combine(locDir, lang, "relics.json");
        if (!File.Exists(path)) return null;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String)
                table[p.Name] = p.Value.GetString()!;
        return table;
    }
}
