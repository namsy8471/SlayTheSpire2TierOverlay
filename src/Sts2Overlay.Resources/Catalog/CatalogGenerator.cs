using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mono.Cecil;

namespace Sts2Overlay.Resources.Catalog;

/// <summary>Input of <see cref="CatalogGenerator.Build"/>.</summary>
/// <param name="DllPath">The game's <c>sts2.dll</c> (read as data, never loaded).</param>
/// <param name="LocalizationDir">Folder containing <c>&lt;lang&gt;/cards.json</c>; null or missing = no names.</param>
/// <param name="GameVersion">Written into the catalog, e.g. <c>v0.107.1</c>.</param>
/// <param name="IncludeAll">Also include card classes that are in no in-game pool (mocks, tests).</param>
/// <param name="WithStartingLoadouts">Also decode each character's starting deck, relics, HP and gold.</param>
/// <param name="WithRelics">Also decode the names-free relic table (rarity and the constant <c>IsTradable</c> inputs).</param>
public sealed record CatalogOptions(
    string DllPath,
    string? LocalizationDir,
    string GameVersion,
    bool IncludeAll = false,
    bool WithStartingLoadouts = false,
    bool WithRelics = false);

/// <summary>Everything <see cref="CatalogGenerator.Build"/> found; the tool prints a summary from it.</summary>
public sealed record CatalogBuildResult(
    CatalogFile Catalog,
    IReadOnlyList<string> CharacterClasses,
    IReadOnlyCollection<string> LoadedLanguages,
    int MultiPoolCards,
    IReadOnlyDictionary<string, IReadOnlyList<string>> StartingDeckClasses,
    StartingLoadoutFile? StartingLoadouts,
    RelicFile? Relics,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds the card catalog by *statically* reading sts2.dll (no game code runs). Shared by tools/CatalogBuilder and the
/// in-process <see cref="ResourcePreparer"/>. See docs/SPIKE.md §1 for how values are decoded.
/// </summary>
public static class CatalogGenerator
{
    public static readonly string[] Languages = ["kor", "eng"];

    /// <summary>
    /// JSON settings for the catalog file: camelCase, indented, Korean kept readable instead of \uXXXX.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CatalogBuildResult Build(CatalogOptions options)
    {
        using var game = new GameReader(options.DllPath);

        // --- 1. characters, their pools and starting decks (ModelDb.AllCharacters) --------------------
        List<TypeDefinition> characters = game.ModelDbGenericCalls(game.FindMethod(game.ModelDb, "get_AllCharacters"), "Character");
        var characterByPool = new Dictionary<string, string>();          // pool type full name -> character id
        var startingDecks = new Dictionary<string, List<TypeDefinition>>(); // character id -> cards
        foreach (TypeDefinition ch in characters)
        {
            string chId = game.ModelIdOf(ch);
            foreach (TypeDefinition pool in game.ModelDbGenericCalls(game.FindMethod(ch, "get_CardPool"), "CardPool"))
                characterByPool[pool.FullName] = chId;
            startingDecks[chId] = game.ModelDbGenericCalls(game.FindMethod(ch, "get_StartingDeck"), "Card");
        }

        // --- 1b. optional: names-free starting loadouts (committed; used to rebuild a co-op guest's deck) ---
        StartingLoadoutFile? loadoutFile = null;
        if (options.WithStartingLoadouts)
        {
            var loadouts = characters.Select(ch => new StartingLoadout(
                Character: game.ModelIdOf(ch),
                StartingHp: game.EvaluateIntGetter(ch, "get_StartingHp"),
                StartingGold: game.EvaluateIntGetter(ch, "get_StartingGold"),
                MaxEnergy: game.EvaluateIntGetter(ch, "get_MaxEnergy"),
                Deck: startingDecks[game.ModelIdOf(ch)].Select(game.ModelIdOf).ToList(),
                Relics: game.ModelDbGenericCalls(game.FindMethod(ch, "get_StartingRelics"), "Relic").Select(game.ModelIdOf).ToList()))
                .ToList();
            loadoutFile = new StartingLoadoutFile(
                options.GameVersion,
                "tools/CatalogBuilder --starting-decks (static IL of sts2.dll: CharacterModel.StartingDeck/StartingRelics/StartingHp/StartingGold)",
                "Game facts only (model ids and numbers). Ascension 5 and above also adds CARD.ASCENDERS_BANE at run start (AscensionManager.ApplyEffectsTo).",
                loadouts);
        }

        // --- 1c. optional: names-free relic table (committed; RelicModel.IsTradable for Relic Trader / Ranwid) ------
        RelicFile? relicFile = null;
        if (options.WithRelics)
        {
            var relics = game.ConcreteSubclassesOf(game.RelicModel)
                .Select(game.DecodeRelic)
                .Select(r => new RelicRow(r.Id, r.Rarity, r.HasUponPickupEffect, r.SpawnsPets, r.UsedUpOverride, r.AddsPet, r.AllowedOverride))
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .ToList();
            relicFile = new RelicFile(
                options.GameVersion,
                "tools/CatalogBuilder --relics (static IL of sts2.dll: RelicModel.Rarity/HasUponPickupEffect/SpawnsPets/AddsPet, IsUsedUp and IsAllowed overrides)",
                "Game facts only. RelicModel.IsTradable needs: not IsUsedUp, no HasUponPickupEffect, not IsMelted, no SpawnsPets, rarity other than Starter, Event and Ancient. usedUpOverride = IsUsedUp reads the saved state of the relic. addsPet = Player.HasEventPet. allowedOverride = RelicModel.IsAllowed has its own rule (relic bag pulls).",
                relics);
        }

        // --- 2. card pools: character pools first, then ModelDb.AllSharedCardPools ---------------------
        List<TypeDefinition> sharedPools = game.ModelDbGenericCalls(game.FindMethod(game.ModelDb, "get_AllSharedCardPools"), "CardPool");
        var inGamePools = characters
            .SelectMany(ch => game.ModelDbGenericCalls(game.FindMethod(ch, "get_CardPool"), "CardPool"))
            .Concat(sharedPools)
            .DistinctBy(p => p.FullName)
            .ToList();
        // Other CardPoolModel subclasses (e.g. MockCardPool) are only used with IncludeAll.
        var otherPools = game.ConcreteSubclassesOf(game.CardPoolModel).Where(p => inGamePools.All(q => q.FullName != p.FullName));

        var poolOfCard = new Dictionary<string, TypeDefinition>(); // card type full name -> first pool
        var poolOrder = new List<string>();
        int multiPoolCards = 0;
        var warnings = new List<string>();
        foreach (TypeDefinition pool in inGamePools.Concat(options.IncludeAll ? otherPools : []))
        {
            poolOrder.Add(game.ModelIdOf(pool));
            List<TypeDefinition> cards = game.ModelDbGenericCalls(game.FindMethod(pool, "GenerateAllCards"), "Card");
            if (cards.Count == 0) warnings.Add($"no cards decoded for pool {pool.Name}");
            foreach (TypeDefinition c in cards)
                if (!poolOfCard.TryAdd(c.FullName, pool)) multiPoolCards++;
        }

        // ModelDb.AllCards = all pool cards + starting decks. Anything else is not reachable in game.
        var inGameCards = new HashSet<string>(poolOfCard.Keys.Concat(startingDecks.Values.SelectMany(d => d).Select(t => t.FullName)));

        // --- 3. decode every card class ---------------------------------------------------------------
        Localization loc = Localization.Load(options.LocalizationDir, Languages);
        var rows = new List<CardRow>();
        foreach (TypeDefinition cardType in game.ConcreteSubclassesOf(game.CardModel))
        {
            if (!options.IncludeAll && !inGameCards.Contains(cardType.FullName)) continue;

            DecodedCard d = game.DecodeCard(cardType);
            poolOfCard.TryGetValue(cardType.FullName, out TypeDefinition? pool);
            string? character = pool != null ? characterByPool.GetValueOrDefault(pool.FullName) : null;
            string entry = d.Id[(d.Id.IndexOf('.') + 1)..]; // "CARD.STRIKE_DEFECT" -> "STRIKE_DEFECT"

            rows.Add(new CardRow(
                Id: d.Id,
                Class: cardType.Name,
                Pool: pool != null ? game.ModelIdOf(pool) : null,
                Character: character,
                Type: d.Type_,
                Rarity: d.Rarity,
                Cost: d.Cost,
                CostX: d.CostX,
                StarCost: d.StarCost,
                StarCostX: d.StarCostX,
                Target: d.Target,
                GainsBlock: d.GainsBlock,
                MaxUpgrade: d.MaxUpgrade,
                Multiplayer: d.Multiplayer,
                ShowInLibrary: d.ShowInLibrary,
                Keywords: d.Keywords,
                Tags: d.Tags,
                Names: Languages.ToDictionary(l => l, l => loc.Get(l, entry, "title")),
                DescriptionTemplate: Languages.ToDictionary(l => l, l => loc.Get(l, entry, "description")),
                Unresolved: d.Unresolved,
                Dynamic: d.Dynamic));
        }

        // Stable order: by pool (character pools first), then by id.
        rows = rows.OrderBy(r => r.Pool == null ? int.MaxValue : poolOrder.IndexOf(r.Pool)).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();

        return new CatalogBuildResult(
            new CatalogFile(options.GameVersion, "static-il", rows),
            characters.Select(c => c.Name).ToList(),
            loc.LoadedLanguages.ToList(),
            multiPoolCards,
            startingDecks.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Select(t => t.Name).ToList()),
            loadoutFile,
            relicFile,
            warnings);
    }

    /// <summary>Writes the catalog JSON (creates the folder).</summary>
    public static void Write(CatalogFile catalog, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(catalog, JsonOptions));
    }

    /// <summary>Writes the names-free starting loadouts (no relaxed escaping needed: ids and numbers only).</summary>
    public static void Write(StartingLoadoutFile file, string path) => WriteNamesFree(file, path);

    /// <summary>Writes the names-free relic table.</summary>
    public static void Write(RelicFile file, string path) => WriteNamesFree(file, path);

    private static void WriteNamesFree<T>(T file, string path)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(file, opts) + "\n");
    }
}

// --- output shape -------------------------------------------------------------------------------
// Records give us immutable data classes with a constructor in one line. System.Text.Json turns
// property names into camelCase ("GainsBlock" -> "gainsBlock") via the naming policy above.

/// <summary>The catalog JSON root: <c>{ "gameVersion": ..., "source": "static-il", "cards": [...] }</c>.</summary>
public sealed record CatalogFile(string GameVersion, string Source, List<CardRow> Cards);

public sealed record StartingLoadoutFile(string GameVersion, string Source, string Note, List<StartingLoadout> Characters);

public sealed record RelicFile(string GameVersion, string Source, string Note, List<RelicRow> Relics);

/// <summary>One relic. A null field is a getter with logic the static reader could not evaluate.</summary>
public sealed record RelicRow(string Id, string? Rarity, bool? HasUponPickupEffect, bool? SpawnsPets, bool UsedUpOverride, bool? AddsPet, bool AllowedOverride);

public sealed record StartingLoadout(string Character, int? StartingHp, int? StartingGold, int? MaxEnergy, List<string> Deck, List<string> Relics);

public sealed record CardRow(
    string Id,
    [property: JsonPropertyName("class")] string Class,
    string? Pool,
    string? Character,
    string? Type,
    string? Rarity,
    int? Cost,
    bool? CostX,
    int? StarCost,
    bool? StarCostX,
    string? Target,
    bool? GainsBlock,
    int? MaxUpgrade,
    string? Multiplayer,
    bool? ShowInLibrary,
    List<string>? Keywords,
    List<string>? Tags,
    Dictionary<string, string?> Names,
    Dictionary<string, string?> DescriptionTemplate,
    List<string> Unresolved,
    List<string> Dynamic);
