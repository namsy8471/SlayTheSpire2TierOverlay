using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VisionSpike;

// Usage:
//   VisionSpike build-hashes [artDir] [outFile]
//   VisionSpike match <capture.png> <x,y,w,h> [hashFile]   (rect in capture pixels, the card's art area)
//   VisionSpike selftest [hashFile]                         (hash each art after a blur+resize round trip)
string repo = FindRepoRoot();
string defaultArt = Path.Combine(repo, "data", "local", "pck", "card_art");
string defaultHashes = Path.Combine(repo, "data", "local", "cardart-hashes.v0.107.1.json");

switch (args.FirstOrDefault())
{
    case "build-hashes":
        BuildHashes(args.ElementAtOrDefault(1) ?? defaultArt, args.ElementAtOrDefault(2) ?? defaultHashes);
        break;
    case "match":
        Match(args[1], ParseRect(args[2]), args.ElementAtOrDefault(3) ?? defaultHashes);
        break;
    case "selftest":
        SelfTest(defaultArt, args.ElementAtOrDefault(1) ?? defaultHashes);
        break;
    default:
        Console.WriteLine("commands: build-hashes | match <png> <x,y,w,h> | selftest");
        break;
}

static void BuildHashes(string artDir, string outFile)
{
    var entries = new List<HashEntry>();
    foreach (string file in Directory.EnumerateFiles(artDir, "*.webp", SearchOption.AllDirectories))
    {
        string rel = Path.GetRelativePath(artDir, file).Replace('\\', '/');
        string[] parts = rel.Split('/');
        bool beta = parts.Contains("beta");
        string name = Path.GetFileNameWithoutExtension(file);
        // card_portraits/<pool>/<id lower>.png -> CARD.<ID>
        string id = "CARD." + name.ToUpperInvariant();
        using Image<Rgba32> img = Image.Load<Rgba32>(file);
        entries.Add(new HashEntry(id, parts[0], beta, PHash.Compute(img).ToString("x16")));
    }

    // Prefer main art; keep beta art only for cards that have no main art.
    var mainIds = entries.Where(e => !e.Beta).Select(e => e.Id).ToHashSet();
    entries = entries.Where(e => !e.Beta || !mainIds.Contains(e.Id)).OrderBy(e => e.Id).ToList();

    Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
    File.WriteAllText(outFile, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{entries.Count} hashes -> {outFile}");

    // How distinct are the cards from each other? Small minimum distances mean easy confusions.
    var hashes = entries.Select(e => (e.Id, H: Convert.ToUInt64(e.Hash, 16))).ToList();
    var nearest = hashes.Select(a => (a.Id, D: hashes.Where(b => b.Id != a.Id).Min(b => PHash.Distance(a.H, b.H)))).ToList();
    Console.WriteLine($"nearest-neighbour distance: min {nearest.Min(n => n.D)}, median {nearest.OrderBy(n => n.D).ElementAt(nearest.Count / 2).D}");
    foreach (var n in nearest.OrderBy(n => n.D).Take(8)) Console.WriteLine($"  close: {n.Id} d={n.D}");
}

static void Match(string capture, Rectangle rect, string hashFile)
{
    var db = LoadDb(hashFile);
    using Image<Rgba32> img = Image.Load<Rgba32>(capture);
    using Image<Rgba32> crop = img.Clone(c => c.Crop(rect));
    ulong h = PHash.Compute(crop);
    foreach (var (id, d) in db.Select(e => (e.Id, D: PHash.Distance(h, e.H))).OrderBy(x => x.D).Take(5))
        Console.WriteLine($"{d,3}  {id}");
}

static void SelfTest(string artDir, string hashFile)
{
    // Simulate "seen on screen": shrink to the in-game 250x190, blur a bit, then hash and look up.
    var db = LoadDb(hashFile);
    int ok = 0, total = 0;
    foreach (var e in db)
    {
        string? file = Directory.EnumerateFiles(artDir, e.Id[5..].ToLowerInvariant() + ".webp", SearchOption.AllDirectories).FirstOrDefault();
        if (file is null) continue;
        using Image<Rgba32> img = Image.Load<Rgba32>(file);
        img.Mutate(c => c.Resize(250, 190).GaussianBlur(0.8f).Brightness(0.92f));
        ulong h = PHash.Compute(img);
        string best = db.OrderBy(x => PHash.Distance(h, x.H)).First().Id;
        total++;
        if (best == e.Id) ok++;
    }
    Console.WriteLine($"selftest: {ok}/{total} correct after resize+blur+darken");
}

static List<(string Id, ulong H)> LoadDb(string hashFile) =>
    JsonSerializer.Deserialize<List<HashEntry>>(File.ReadAllText(hashFile))!
        .Select(e => (e.Id, Convert.ToUInt64(e.Hash, 16))).ToList();

static Rectangle ParseRect(string s)
{
    int[] v = s.Split(',').Select(int.Parse).ToArray();
    return new Rectangle(v[0], v[1], v[2], v[3]);
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

record HashEntry(string Id, string Pool, bool Beta, string Hash);
