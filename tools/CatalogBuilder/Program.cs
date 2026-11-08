// CatalogBuilder - builds a card catalog for Slay the Spire 2 by *statically* reading sts2.dll.
//
// Usage (from the repo root):
//   dotnet run --project tools/CatalogBuilder -- [--dll <sts2.dll>] [--loc <localization dir>]
//                                               [--out <file>] [--game-version v0.107.1] [--all]
//
//   --loc   folder containing <lang>/cards.json (default: data/local/pck/localization)
//   --all   also include card classes that are in no in-game pool (mocks, tests)
//   --starting-decks <file>  also write names-free starting loadouts (deck, relics, HP, gold) per
//                            character, e.g. data/catalog/starting-decks.v0.107.1.json (committed)
//
// The output contains game text when localization is present, so it goes to data/local/ (gitignored).

using System.Text.Encodings.Web;
using System.Text.Json;
using CatalogBuilder;
using Mono.Cecil;

string repoRoot = FindRepoRoot();
string gameVersion = "v0.107.1";
string dllPath = @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll";
string? locDir = Path.Combine(repoRoot, "data", "local", "pck", "localization");
string? outPath = null;
string? startingDecksPath = null;
bool includeAll = false;

// --- very small argument parser ---------------------------------------------------------------
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--dll": dllPath = Next(); break;
        case "--loc": locDir = Next(); break;
        case "--out": outPath = Next(); break;
        case "--game-version": gameVersion = Next(); break;
        case "--all": includeAll = true; break;
        case "--starting-decks": startingDecksPath = Next(); break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}
outPath ??= Path.Combine(repoRoot, "data", "local", $"catalog.{gameVersion}.json");
string[] languages = ["kor", "eng"];

Console.WriteLine($"Reading {dllPath}");
using var game = new GameReader(dllPath);

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
if (startingDecksPath != null)
{
    var loadouts = characters.Select(ch => new StartingLoadout(
        Character: game.ModelIdOf(ch),
        StartingHp: game.EvaluateIntGetter(ch, "get_StartingHp"),
        StartingGold: game.EvaluateIntGetter(ch, "get_StartingGold"),
        MaxEnergy: game.EvaluateIntGetter(ch, "get_MaxEnergy"),
        Deck: startingDecks[game.ModelIdOf(ch)].Select(game.ModelIdOf).ToList(),
        Relics: game.ModelDbGenericCalls(game.FindMethod(ch, "get_StartingRelics"), "Relic").Select(game.ModelIdOf).ToList()))
        .ToList();
    var file = new StartingLoadoutFile(
        gameVersion,
        "tools/CatalogBuilder --starting-decks (static IL of sts2.dll: CharacterModel.StartingDeck/StartingRelics/StartingHp/StartingGold)",
        "Game facts only (model ids and numbers). Ascension 5 and above also adds CARD.ASCENDERS_BANE at run start (AscensionManager.ApplyEffectsTo).",
        loadouts);
    var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(startingDecksPath))!);
    File.WriteAllText(startingDecksPath, JsonSerializer.Serialize(file, opts) + "\n");
    Console.WriteLine($"Wrote {loadouts.Count} starting loadouts -> {startingDecksPath}");
}

// --- 2. card pools: character pools first, then ModelDb.AllSharedCardPools ---------------------
List<TypeDefinition> sharedPools = game.ModelDbGenericCalls(game.FindMethod(game.ModelDb, "get_AllSharedCardPools"), "CardPool");
var inGamePools = characters
    .SelectMany(ch => game.ModelDbGenericCalls(game.FindMethod(ch, "get_CardPool"), "CardPool"))
    .Concat(sharedPools)
    .DistinctBy(p => p.FullName)
    .ToList();
// Other CardPoolModel subclasses (e.g. MockCardPool) are only used with --all.
var otherPools = game.ConcreteSubclassesOf(game.CardPoolModel).Where(p => inGamePools.All(q => q.FullName != p.FullName));

var poolOfCard = new Dictionary<string, TypeDefinition>(); // card type full name -> first pool
var poolOrder = new List<string>();
int multiPoolCards = 0;
foreach (TypeDefinition pool in inGamePools.Concat(includeAll ? otherPools : []))
{
    poolOrder.Add(game.ModelIdOf(pool));
    List<TypeDefinition> cards = game.ModelDbGenericCalls(game.FindMethod(pool, "GenerateAllCards"), "Card");
    if (cards.Count == 0) Console.WriteLine($"  WARNING: no cards decoded for pool {pool.Name}");
    foreach (TypeDefinition c in cards)
        if (!poolOfCard.TryAdd(c.FullName, pool)) multiPoolCards++;
}

// ModelDb.AllCards = all pool cards + starting decks. Anything else is not reachable in game.
var inGameCards = new HashSet<string>(poolOfCard.Keys.Concat(startingDecks.Values.SelectMany(d => d).Select(t => t.FullName)));

// --- 3. decode every card class ---------------------------------------------------------------
Localization loc = Localization.Load(locDir, languages);
var rows = new List<CardRow>();
foreach (TypeDefinition cardType in game.ConcreteSubclassesOf(game.CardModel))
{
    if (!includeAll && !inGameCards.Contains(cardType.FullName)) continue;

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
        Names: languages.ToDictionary(l => l, l => loc.Get(l, entry, "title")),
        DescriptionTemplate: languages.ToDictionary(l => l, l => loc.Get(l, entry, "description")),
        Unresolved: d.Unresolved,
        Dynamic: d.Dynamic));
}

// Stable order: by pool (character pools first), then by id.
rows = rows.OrderBy(r => r.Pool == null ? int.MaxValue : poolOrder.IndexOf(r.Pool)).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();

// --- 4. write JSON ------------------------------------------------------------------------------
var catalog = new Catalog(gameVersion, "static-il", rows);
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Korean readable instead of \uXXXX
};
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
File.WriteAllText(outPath, JsonSerializer.Serialize(catalog, jsonOptions));
Console.WriteLine($"Wrote {rows.Count} cards -> {outPath}");

// --- 5. summary + sanity checks -----------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"Characters ({characters.Count}): {string.Join(", ", characters.Select(c => c.Name))}");
Console.WriteLine($"Localization loaded: {(loc.LoadedLanguages.Count == 0 ? "none (names are null; rerun with --loc <dir>)" : string.Join(", ", loc.LoadedLanguages))}");

Console.WriteLine("\nCards per pool (character):");
foreach (var g in rows.GroupBy(r => (r.Pool, r.Character)))
    Console.WriteLine($"  {g.Key.Pool ?? "(no pool)",-32} {g.Key.Character ?? "-",-24} {g.Count(),4}");

Console.WriteLine("\nCards per rarity:");
foreach (var g in rows.GroupBy(r => r.Rarity ?? "(unresolved)").OrderByDescending(g => g.Count()))
    Console.WriteLine($"  {g.Key,-14} {g.Count(),4}");

Console.WriteLine("\nCards per type:");
foreach (var g in rows.GroupBy(r => r.Type ?? "(unresolved)").OrderByDescending(g => g.Count()))
    Console.WriteLine($"  {g.Key,-14} {g.Count(),4}");

int unresolvedCards = rows.Count(r => r.Unresolved.Count > 0);
Console.WriteLine($"\nCards with unresolved fields: {unresolvedCards}");
foreach (var g in rows.SelectMany(r => r.Unresolved.Select(f => (f, r.Class))).GroupBy(x => x.f))
    Console.WriteLine($"  {g.Key,-12} {g.Count(),4}  e.g. {string.Join(", ", g.Take(5).Select(x => x.Class))}");
Console.WriteLine($"Cards with dynamic getters (constructor value used): {rows.Count(r => r.Dynamic.Count > 0)}");
foreach (var r in rows.Where(r => r.Dynamic.Count > 0))
    Console.WriteLine($"  {r.Class}: {string.Join(", ", r.Dynamic)}");
Console.WriteLine($"Cards listed in more than one pool (first pool kept): {multiPoolCards}");

Console.WriteLine("\nStarting decks:");
foreach (var (chId, deck) in startingDecks)
    Console.WriteLine($"  {chId,-24} {string.Join(", ", deck.GroupBy(t => t.Name).Select(g => $"{g.Key} x{g.Count()}"))}");

return 0;

// Walks up from the current directory (and the exe directory) to the folder with Directory.Build.props.
static string FindRepoRoot()
{
    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
    return Directory.GetCurrentDirectory();
}

// --- output shape -------------------------------------------------------------------------------
// Records give us immutable data classes with a constructor in one line. System.Text.Json turns
// property names into camelCase ("GainsBlock" -> "gainsBlock") via the naming policy above.
record Catalog(string GameVersion, string Source, List<CardRow> Cards);

record StartingLoadoutFile(string GameVersion, string Source, string Note, List<StartingLoadout> Characters);

record StartingLoadout(string Character, int? StartingHp, int? StartingGold, int? MaxEnergy, List<string> Deck, List<string> Relics);

record CardRow(
    string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("class")] string Class,
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
