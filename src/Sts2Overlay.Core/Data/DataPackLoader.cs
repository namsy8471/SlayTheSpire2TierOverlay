using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Data;

/// <summary>
/// Loads a data pack directory (see docs/DATA_SCHEMA.md):
/// <code>
/// weights.json            (optional, partial overrides of Weights defaults)
/// tiers/*.json            TierPack per character (+ colorless.json)
/// archetypes/*.json       array of Archetype
/// combos/*.json           array of Combo
/// aliases.json            (optional) { "OLD_ID": "NEW_ID" }
/// text.eng.json           (optional) English for the Korean tips, combo names/reasons, archetype descriptions (PackText)
/// catalog.json            (optional) CardCatalog; usually passed in instead (catalog / catalogPath)
/// events.stats.json       (optional) EventStats: measured event/ancient choice rates
/// relics.json             (optional) RelicStats: measured relic pick rates
/// relic-fit.json          (optional) RelicFit: measured relic pick rates per build
/// </code>
/// JSON uses camelCase, allows comments and trailing commas, and tier letters as strings.
/// </summary>
/// <remarks>
/// <para>
/// <c>archetypes/*.json</c> and <c>combos/*.json</c> accept either a bare array or an object
/// <c>{ "character": "CHARACTER.X", "archetypes"|"combos": [...] }</c>; in the object form the top-level character is
/// applied to entries that do not name their own.
/// </para>
/// <para>
/// Partial <c>weights.json</c>: nested objects (e.g. <c>skip</c>) and dictionaries (e.g. <c>tierValue</c>, <c>targets</c>)
/// are merged into the defaults (a key that is present replaces that key's value); arrays (e.g. <c>weakRoles</c>) replace the default.
/// </para>
/// File-level problems in <see cref="LoadDirectory"/> throw <see cref="InvalidDataException"/> whose message starts with the file path.
/// </remarks>
public static class DataPackLoader
{
    public const string WeightsFile = "weights.json";
    public const string AliasesFile = "aliases.json";
    public const string CatalogFile = "catalog.json";
    public const string EventStatsFile = "events.stats.json";
    public const string RelicStatsFile = "relics.json";
    public const string RelicFitFile = "relic-fit.json";
    public const string CardPolicyFile = "model/card-policy.json";
    public const string TiersDir = "tiers";
    public const string ArchetypesDir = "archetypes";
    public const string CombosDir = "combos";

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Shared serializer options (camelCase, comments, trailing commas, string enums). The instance is read-only.</summary>
    public static JsonSerializerOptions JsonOptions => Options;

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null, // dictionary keys are kept exactly as written
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>
    /// Repository-relative path of the committed names-free catalog of one data version:
    /// <c>v0.111.0</c> → <c>data/catalog/cards.v0.111.0.json</c>. There is no default on purpose: the caller names the
    /// version, so a v0.111 pack never gets the v0.107.1 catalog by accident.
    /// </summary>
    public static string CatalogRelativePath(string dataVersion) => Path.Combine("data", "catalog", $"cards.{dataVersion}.json");

    /// <summary>Repository-relative path of a data version's pack: <c>v0.111.0</c> → <c>data/packs/v0.111.0</c>.</summary>
    public static string PackRelativePath(string dataVersion) => Path.Combine("data", "packs", dataVersion);

    /// <summary>
    /// The committed catalog that belongs to a pack directory by name: <c>data/packs/v0.107.1</c> →
    /// <c>data/catalog/cards.v0.107.1.json</c> (the same layout in a checkout and in an installed build). Null when the
    /// directory has no parent of a parent. The file may not exist (e.g. the <c>sample</c> pack has none).
    /// </summary>
    public static string? SiblingCatalogPath(string packDirectory)
    {
        var full = Path.GetFullPath(packDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(full);
        var dataDir = Path.GetDirectoryName(Path.GetDirectoryName(full));
        return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dataDir)
            ? null
            : Path.Combine(dataDir, "catalog", $"cards.{name}.json");
    }

    /// <summary>
    /// Loads a pack directory. Every part is optional; missing parts become defaults (weights) or empty collections.
    /// Catalog precedence: <paramref name="catalog"/>, then the file at <paramref name="catalogPath"/>, then <c>catalog.json</c>
    /// in the directory, then the committed catalog of the same version next to the packs (<see cref="SiblingCatalogPath"/>).
    /// <para>Why the last step: without a catalog the pack knows only the cards it rates, so every curse, status and
    /// quest card looks like an ordinary unrated card. The overlay once loaded its pack that way and advised upgrading
    /// the curse 탐욕 (Greed) at a rest site.</para>
    /// </summary>
    /// <param name="directory">The pack directory.</param>
    /// <param name="catalog">An already loaded catalog.</param>
    /// <param name="catalogPath">A catalog file to load, e.g. <c>data/catalog/cards.v0.107.1.json</c> (see <see cref="LoadCatalogFile"/>).</param>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    /// <exception cref="FileNotFoundException"><paramref name="catalogPath"/> is given but does not exist.</exception>
    /// <exception cref="InvalidDataException">A file is malformed or two tier files declare the same character.</exception>
    public static DataPack LoadDirectory(string directory, CardCatalog? catalog = null, string? catalogPath = null)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Data pack directory not found: {directory}");
        }

        var weightsPath = Path.Combine(directory, WeightsFile);
        var weights = File.Exists(weightsPath) ? LoadFile(weightsPath, LoadWeights) : new Weights();

        var tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal);
        var tierSources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in JsonFiles(Path.Combine(directory, TiersDir)))
        {
            var pack = LoadFile(path, LoadTierPack);
            if (string.IsNullOrWhiteSpace(pack.Character))
            {
                throw new InvalidDataException($"{path}: tier pack has no \"character\".");
            }
            if (tierSources.TryGetValue(pack.Character, out var other))
            {
                throw new InvalidDataException($"{path}: character {pack.Character} is already defined by {other}.");
            }
            tiers[pack.Character] = pack;
            tierSources[pack.Character] = path;
        }

        var archetypes = new List<Archetype>();
        foreach (var path in JsonFiles(Path.Combine(directory, ArchetypesDir)))
        {
            archetypes.AddRange(LoadFile(path, LoadArchetypes));
        }

        var combos = new List<Combo>();
        foreach (var path in JsonFiles(Path.Combine(directory, CombosDir)))
        {
            combos.AddRange(LoadFile(path, LoadCombos));
        }

        var textPath = Path.Combine(directory, PackText.EnglishFile);
        if (File.Exists(textPath))
        {
            LoadFile(textPath, PackText.Parse).ApplyTo(tiers.Values, archetypes, combos);
        }

        var aliasesPath = Path.Combine(directory, AliasesFile);
        IReadOnlyDictionary<string, string> aliases = File.Exists(aliasesPath)
            ? LoadFile(aliasesPath, LoadAliases)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        if (catalog is null && catalogPath is not null)
        {
            catalog = LoadCatalogFile(catalogPath);
        }
        var packCatalogPath = Path.Combine(directory, CatalogFile);
        if (catalog is null && File.Exists(packCatalogPath))
        {
            catalog = LoadFile(packCatalogPath, LoadCatalog);
        }
        if (catalog is null && SiblingCatalogPath(directory) is { } sibling && File.Exists(sibling))
        {
            catalog = LoadFile(sibling, LoadCatalog);
        }

        // Measured rates: optional. Absent → null, and the scorers behave exactly as without them.
        var eventStatsPath = Path.Combine(directory, EventStatsFile);
        var eventStats = File.Exists(eventStatsPath) ? LoadFile(eventStatsPath, LoadEventStats) : null;
        var relicStatsPath = Path.Combine(directory, RelicStatsFile);
        var relicStats = File.Exists(relicStatsPath) ? LoadFile(relicStatsPath, LoadRelicStats) : null;
        var relicFitPath = Path.Combine(directory, RelicFitFile);
        var relicFit = File.Exists(relicFitPath) ? LoadFile(relicFitPath, LoadRelicFit) : null;
        var policyPath = Path.Combine(directory, CardPolicyFile);
        var cardPolicy = File.Exists(policyPath) ? LoadFile(policyPath, Learning.CardPolicyModel.Load) : null;

        return new DataPack
        {
            Weights = weights,
            Tiers = tiers,
            Archetypes = archetypes,
            Combos = combos,
            IdAliases = aliases,
            Catalog = catalog,
            EventStats = eventStats,
            RelicStats = relicStats,
            RelicFit = relicFit,
            CardPolicy = cardPolicy,
        };
    }

    /// <summary><c>events.stats.json</c>: measured event and ancient choice rates.</summary>
    public static EventStats LoadEventStats(string json) => Deserialize<EventStats>(json, "event stats");

    /// <summary><c>relics.json</c>: measured relic pick rates.</summary>
    public static RelicStats LoadRelicStats(string json) => Deserialize<RelicStats>(json, "relic stats");

    /// <summary><c>relic-fit.json</c>: measured relic pick rates per build.</summary>
    public static RelicFit LoadRelicFit(string json) => Deserialize<RelicFit>(json, "relic fit");

    /// <summary>Weights with every value the JSON does not mention left at its default.</summary>
    public static Weights LoadWeights(string json) => Deserialize<Weights>(json, "weights");

    public static TierPack LoadTierPack(string json) => Deserialize<TierPack>(json, "tier pack");

    /// <summary>Accepts an array of archetypes or <c>{ "character": ..., "archetypes": [...] }</c>.</summary>
    public static IReadOnlyList<Archetype> LoadArchetypes(string json)
    {
        var (character, list) = DeserializeGroup<Archetype>(json, "archetypes");
        if (character is not null)
        {
            foreach (var a in list.Where(a => string.IsNullOrEmpty(a.Character)))
            {
                a.Character = character;
            }
        }
        return list;
    }

    /// <summary>Accepts an array of combos or <c>{ "character": ..., "combos": [...] }</c>.</summary>
    public static IReadOnlyList<Combo> LoadCombos(string json)
    {
        var (character, list) = DeserializeGroup<Combo>(json, "combos");
        if (character is not null)
        {
            foreach (var c in list.Where(c => string.IsNullOrEmpty(c.Character)))
            {
                c.Character = character;
            }
        }
        return list;
    }

    /// <summary>
    /// Parses a catalog as written (e.g. the real <c>cards.v0.107.1.json</c>: PascalCase type/rarity, <c>CARD_POOL.*</c> pools,
    /// <c>multiplayer</c> string; or older dumps with <c>multiplayerOnly</c>). Unknown fields are ignored.
    /// </summary>
    public static CardCatalog LoadCatalog(string json) => Deserialize<CardCatalog>(json, "catalog");

    /// <summary>Loads a catalog file.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is malformed; the message starts with the path.</exception>
    public static CardCatalog LoadCatalogFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Catalog file not found: {path}", path);
        }
        return LoadFile(path, LoadCatalog);
    }

    /// <summary><c>aliases.json</c>: an object mapping old ids to current ids.</summary>
    public static IReadOnlyDictionary<string, string> LoadAliases(string json)
    {
        var raw = Deserialize<Dictionary<string, string>>(json, "aliases");
        return new Dictionary<string, string>(raw, StringComparer.Ordinal);
    }

    private static T Deserialize<T>(string json, string what) where T : class
        => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Expected a {what} object but found null.");

    private static (string? Character, List<T> Items) DeserializeGroup<T>(string json, string arrayProperty)
    {
        var node = JsonNode.Parse(json, NodeOptions, DocumentOptions);
        switch (node)
        {
            case JsonArray:
                return (null, node.Deserialize<List<T>>(Options) ?? []);
            case JsonObject obj:
                string? character = null;
                if (obj.TryGetPropertyValue("character", out var c) && c is not null)
                {
                    character = c.GetValue<string>();
                }
                if (!obj.TryGetPropertyValue(arrayProperty, out var items) || items is not JsonArray)
                {
                    throw new JsonException($"Expected an array or an object with a \"{arrayProperty}\" array.");
                }
                return (character, items.Deserialize<List<T>>(Options) ?? []);
            default:
                throw new JsonException($"Expected an array or an object with a \"{arrayProperty}\" array.");
        }
    }

    private static T LoadFile<T>(string path, Func<string, T> load)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException($"{path}: cannot read file: {ex.Message}", ex);
        }

        try
        {
            return load(text);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    private static IEnumerable<string> JsonFiles(string directory)
        => Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal)
            : [];
}
