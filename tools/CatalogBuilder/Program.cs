// CatalogBuilder - builds a card catalog for Slay the Spire 2 by *statically* reading sts2.dll.
// The decoding logic lives in src/Sts2Overlay.Resources (Catalog/CatalogGenerator.cs) so the overlay can run it
// in-process on first start; this tool is the console front-end that also prints a summary.
//
// Usage (from the repo root):
//   dotnet run --project tools/CatalogBuilder -- [--dll <sts2.dll>] [--loc <localization dir>]
//                                               [--out <file>] [--game-version v0.107.1] [--all]
//
//   --dll   default: sts2.dll of the install found by GameInstall.Locate (Steam libraries)
//   --game-version  default: "version" from the install's release_info.json, else v0.107.1
//   --loc   folder containing <lang>/cards.json (default: data/local/pck/localization)
//   --all   also include card classes that are in no in-game pool (mocks, tests)
//   --starting-decks <file>  also write names-free starting loadouts (deck, relics, HP, gold) per
//                            character, e.g. data/catalog/starting-decks.v0.107.1.json (committed)
//   --relics <file>  also write the names-free relic table (rarity + IsTradable inputs),
//                    e.g. data/catalog/relics.v0.107.1.json (committed)
//
// The output contains game text when localization is present, so it goes to data/local/ (gitignored).

using Sts2Overlay.Resources;
using Sts2Overlay.Resources.Catalog;

string repoRoot = FindRepoRoot();
var install = GameInstall.Locate();
string? gameVersion = null;
string dllPath = install?.DllPath ?? @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll";
string? locDir = Path.Combine(repoRoot, "data", "local", "pck", "localization");
string? outPath = null;
string? startingDecksPath = null;
string? relicsPath = null;
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
        case "--relics": relicsPath = Next(); break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}
// release_info.json sits two folders above sts2.dll (<install>/data_sts2_windows_x86_64/sts2.dll).
gameVersion ??= ReleaseInfo.TryLoad(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dllPath)!)!, "release_info.json"))?.Version ?? "v0.107.1";
outPath ??= Path.Combine(repoRoot, "data", "local", $"catalog.{gameVersion}.json");

Console.WriteLine($"Reading {dllPath} ({gameVersion})");
var result = CatalogGenerator.Build(new CatalogOptions(dllPath, locDir, gameVersion, includeAll, startingDecksPath != null, relicsPath != null));
foreach (var w in result.Warnings) Console.WriteLine($"  WARNING: {w}");

if (startingDecksPath != null && result.StartingLoadouts is { } loadouts)
{
    CatalogGenerator.Write(loadouts, startingDecksPath);
    Console.WriteLine($"Wrote {loadouts.Characters.Count} starting loadouts -> {startingDecksPath}");
}

if (relicsPath != null && result.Relics is { } relicFile)
{
    CatalogGenerator.Write(relicFile, relicsPath);
    Console.WriteLine($"Wrote {relicFile.Relics.Count} relics -> {relicsPath}");
    foreach (var g in relicFile.Relics.GroupBy(r => r.Rarity ?? "(unresolved)").OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Key,-14} {g.Count(),4}");
    var open = relicFile.Relics.Where(r => r.Rarity is null || r.HasUponPickupEffect is null || r.SpawnsPets is null || r.AddsPet is null).Select(r => r.Id).ToList();
    Console.WriteLine($"  relics with unresolved fields: {open.Count} {string.Join(", ", open)}");
}

var rows = result.Catalog.Cards;
CatalogGenerator.Write(result.Catalog, outPath);
Console.WriteLine($"Wrote {rows.Count} cards -> {outPath}");

// --- summary + sanity checks -----------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"Characters ({result.CharacterClasses.Count}): {string.Join(", ", result.CharacterClasses)}");
Console.WriteLine($"Localization loaded: {(result.LoadedLanguages.Count == 0 ? "none (names are null; rerun with --loc <dir>)" : string.Join(", ", result.LoadedLanguages))}");

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
Console.WriteLine($"Cards listed in more than one pool (first pool kept): {result.MultiPoolCards}");

Console.WriteLine("\nStarting decks:");
foreach (var (chId, deck) in result.StartingDeckClasses)
    Console.WriteLine($"  {chId,-24} {string.Join(", ", deck.GroupBy(t => t).Select(g => $"{g.Key} x{g.Count()}"))}");

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
