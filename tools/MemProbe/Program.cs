using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Vision;
using Sts2Overlay.Vision.Events;
using Sts2Overlay.Vision.Rest;
using AppPixelRect = Sts2Overlay.App.Integration.PixelRect;

namespace MemProbe;

// MemProbe: where does the overlay's memory go? Offline, console only.
//
//   MemProbe steps [--local dir] [--frames 300] [--larges 60] [--cold] [--d3d off]
//       loads what the app loads, step by step, and prints process / GC numbers after each step
//   MemProbe dump --out file [--local dir] [--limit n] [--every k] [--upscale 2] [--times file]
//       raw recognition output of every saved capture (bit-exact scores), for before/after identity checks;
//       per-frame recognition times go to --times (they differ run to run, the output must not)
//   MemProbe allocs [--frames 60] [--upscale 2]
//       managed bytes allocated per frame by each per-frame vision stage, and by type (GC allocation ticks)
//   MemProbe heap [--frames 60] [--upscale 2] [--idle 0,30] [--min 65536] [--threads 1]
//       what stays alive between frames: every big array and its holder (ClrMD snapshot of this process), ArrayPool.Shared
//       contents, after recognising and after idle waits (HeapCensus.cs)
//
// --local: the generated data folder (card art, localization, catalog, test-captures). Default: the nearest data/local
// above this checkout (a clone under .worktrees\ uses the main checkout's). It is only READ: the art cache is copied to
// a temp folder first, so a stale cache is rebuilt there, never in data/local.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var opts = ParseOptions(args.Skip(1).ToArray());
        string repo = FindRepoRoot();
        string local = opts.GetValueOrDefault("local") ?? FindLocal(repo);
        switch (args.FirstOrDefault())
        {
            case "steps":
                return Steps(repo, local, opts);
            case "dump":
                return Dump(local, opts);
            case "allocs":
                return Allocs(local, opts);
            case "heap":
                return HeapCensus.Run(local, opts);
            case "builddb":
            {
                // The art database build alone (what a cache miss costs at start), with allocations by type.
                var layout = new Sts2Overlay.Resources.LocalLayout(local);
                string catalogPath = layout.ManifestCatalog() ?? Path.Combine(repo, DataPackLoader.DefaultCatalogRelativePath);
                var catalog = VisionCatalog.Load(catalogPath);
                var report = new MemReport(Console.Out);
                report.Header();
                report.Row("before build");
                using var sampler = new AllocSampler { Active = true };
                var sw = Stopwatch.StartNew();
                var db = CardArtDatabase.Build(layout.CardArtDir, catalog);
                Console.WriteLine($"   ({db.Count} entries in {sw.ElapsedMilliseconds} ms)");
                sampler.Active = false;
                report.Row("after build");
                Thread.Sleep(3000);
                report.Row("after build + 3 s");
                sampler.Print(Console.Out, 12);
                GC.KeepAlive(db);
                return 0;
            }
            default:
                Console.WriteLine("commands: steps | dump --out file | allocs");
                return 1;
        }
    }

    // ------------------------------------------------------------------ steps

    private static int Steps(string repo, string local, Dictionary<string, string> opts)
    {
        int frames = int.Parse(opts.GetValueOrDefault("frames") ?? "300", CultureInfo.InvariantCulture);
        int larges = int.Parse(opts.GetValueOrDefault("larges") ?? "60", CultureInfo.InvariantCulture);
        bool cold = opts.ContainsKey("cold");
        Console.WriteLine($"MemProbe steps: {Environment.ProcessorCount} logical cores, server GC {System.Runtime.GCSettings.IsServerGC}, " +
            $"latency {System.Runtime.GCSettings.LatencyMode}, frames {frames}, large images {larges}, art db {(cold ? "built" : "from cache")}");
        // Machine memory load matters: at >= 90 % of the GC's "high" threshold, ArrayPool.Shared drops every pooled array
        // on each gen2 collection, so pooled scratch buffers get allocated again.
        var load = GC.GetGCMemoryInfo();
        Console.WriteLine($"machine memory load {load.MemoryLoadBytes >> 20} MB of {load.TotalAvailableMemoryBytes >> 20} MB (GC high-load threshold {load.HighMemoryLoadThresholdBytes >> 20} MB)");
        var report = new MemReport(Console.Out);
        report.Header();
        report.Row("a. baseline");

        // b. what AppController's constructor and LocalResourceService load before any frame
        string packDir = Path.Combine(repo, "data", "packs", "v0.107.1");
        var layout = new Sts2Overlay.Resources.LocalLayout(local);
        string committed = Path.Combine(repo, DataPackLoader.DefaultCatalogRelativePath);
        string catalog = layout.ManifestCatalog() ?? committed;
        var pack = DataPackLoader.LoadDirectory(packDir);
        var engine = new RecommendationEngine(pack);
        var cards = CardInfoProvider.Load(catalog, pack);
        report.Row("b1. data pack + engine + names");
        var cardText = CardTextProvider.Load(pack, packDir, layout.LocalizationDir);
        var relicInfo = RelicInfoProvider.Load(packDir, layout.LocalizationDir, layout.RelicIconDir);
        var eventText = EventTextProvider.Load(layout.LocalizationDir);
        report.Row("b2. card/relic/event text (kor+eng)");

        // c. the recogniser: art database (cache or build), relic templates, price glyphs
        var sw = Stopwatch.StartNew();
        using var loaded = LoadedRecognizer.Create(local, cold);
        var recognizer = loaded.Recognizer;
        Console.WriteLine($"   (recogniser ready in {sw.ElapsedMilliseconds} ms, {recognizer.Loaded!.Database.Count} art entries, relics {(recognizer.Loaded.Relics is null ? "off" : "on")})");
        report.Row(cold ? "c. recogniser, art db BUILT" : "c. recogniser, art db from cache");

        // d. saved captures through the app's recogniser, one reused frame buffer per resolution. Almost every saved
        // capture is 1920x1079, so the user's 4K screen is played by blowing those up 2x (VisionBench --upscale 2).
        string capturesDir = Path.Combine(local, "test-captures");
        var all = Captures.List(capturesDir);
        var fhd = all.Where(c => c.Width == 1920).ToList();
        Console.WriteLine($"   (captures: {all.Count}, {fhd.Count} at 1920 wide)");
        var buffer = new ReusedFrameBuffer();
        foreach (var (k, n) in new[] { (2, frames * 2 / 3), (1, frames - frames * 2 / 3) })
        {
            var pick = Captures.Spread(fhd, n);
            string res = k == 2 ? "4K (FHD x2)" : "FHD";

            // d0: decoding alone (harness cost, not the app's: the app gets pixels from the GPU)
            for (int i = 0; i < pick.Count; i++)
            {
                buffer.Decode(pick[i], k);
                if ((i + 1) % 50 == 0 || i + 1 == pick.Count) Console.WriteLine($"   decode {res} {i + 1}/{pick.Count}");
            }
            report.Row($"d0. decode only {res} x{pick.Count}");

            // d1: the capture worker's per-frame vision work: card recogniser (+ rest probe, removal probe), event buttons
            var times = new List<double>();
            long allocBefore = GC.GetTotalAllocatedBytes(false);
            for (int i = 0; i < pick.Count; i++)
            {
                buffer.Decode(pick[i], k);
                var frame = new CapturedFrame(buffer.Buffer, buffer.Width, buffer.Height, buffer.Width * 4,
                    new AppPixelRect(0, 0, buffer.Width, buffer.Height), DateTime.Now);
                var t0 = Stopwatch.GetTimestamp();
                recognizer.Recognize(frame, null);
                var vf = new ScreenFrame(frame.Bgra, frame.Width, frame.Height, frame.Stride);
                EventButtonDetector.Detect(vf);
                AncientButtonDetector.Detect(vf);
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                if ((i + 1) % 50 == 0 || i + 1 == pick.Count) Console.WriteLine($"   recognise {res} {i + 1}/{pick.Count}");
            }
            long allocPerFrame = (GC.GetTotalAllocatedBytes(false) - allocBefore) / Math.Max(1, pick.Count);
            times.Sort();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"   ({res}: median {times[times.Count / 2]:F1} ms per frame, managed allocations ~{allocPerFrame / 1024.0 / 1024.0:F2} MB per frame)"));
            report.Row($"d1. recognise {res} x{pick.Count}");
        }

        // e. the panel's images: every card thumbnail (the tier list shows all cards), some hover images, relic icons
        var art = new CardArtProvider(layout.CardArtDir, catalog);
        var ids = pack.Catalog?.Cards.Select(c => c.Id).ToList() ?? [];
        int thumbs = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            if (art.Thumb(ids[i]) is not null) thumbs++;
            if ((i + 1) % 100 == 0 || i + 1 == ids.Count) Console.WriteLine($"   thumbs {i + 1}/{ids.Count}");
        }
        report.Row($"e1. card thumbs x{thumbs}");
        int bigs = 0;
        for (int i = 0; i < ids.Count && bigs < larges; i++)
        {
            if (art.Large(ids[i]) is not null) bigs++;
        }
        report.Row($"e2. card hover images x{bigs}");
        var relicIds = Directory.Exists(layout.RelicIconDir)
            ? Directory.EnumerateFiles(layout.RelicIconDir, "*.png").Select(f => Path.GetFileNameWithoutExtension(f).ToUpperInvariant()).ToList()
            : [];
        foreach (var r in relicIds)
        {
            var vm = relicInfo.For("RELIC." + r, "kor");
            _ = vm.Thumb;
            _ = vm.Large;
        }
        report.Row($"e3. relic icons x{relicIds.Count} (thumb+large)");

        // f. capture objects (WgcCapturer): D3D11 device, 2 frame pool buffers, staging texture + map
        if (opts.GetValueOrDefault("d3d") != "off")
        {
            using var d3d = new D3DProbe();
            d3d.CreateDevice();
            report.Row("f1. D3D11 device");
            d3d.CreatePool(3842, 2160, 2);
            report.Row("f2. + 2 pool textures 3842x2160");
            d3d.CreateStagingAndMap(3840, 2160);
            report.Row("f3. + staging 3840x2160, mapped");
        }

        GC.KeepAlive(engine);
        GC.KeepAlive(cards);
        GC.KeepAlive(cardText);
        GC.KeepAlive(eventText);
        GC.KeepAlive(buffer);
        GC.KeepAlive(art);
        return 0;
    }

    // ------------------------------------------------------------------ dump

    private static int Dump(string local, Dictionary<string, string> opts)
    {
        string outFile = opts["out"];
        int limit = int.Parse(opts.GetValueOrDefault("limit") ?? int.MaxValue.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int every = int.Parse(opts.GetValueOrDefault("every") ?? "1", CultureInfo.InvariantCulture);
        int k = int.Parse(opts.GetValueOrDefault("upscale") ?? "1", CultureInfo.InvariantCulture);
        using var loaded = LoadedRecognizer.Create(local, cold: false);
        CardRecognizer rec = loaded.Recognizer.Loaded!;

        var files = Captures.List(Path.Combine(local, "test-captures")).Where((_, i) => i % every == 0).Take(limit).ToList();
        var buffer = new ReusedFrameBuffer();
        var times = new Dictionary<string, List<double>>();
        using var w = new StreamWriter(outFile, false, new UTF8Encoding(false));
        using var tw = opts.TryGetValue("times", out var tf) ? new StreamWriter(tf, false, new UTF8Encoding(false)) : null;
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            // Upscaling makes sense for the FHD captures only (a native 4K one stays as it is).
            buffer.Decode(f, f.Width < 3840 ? k : 1);
            var vf = new ScreenFrame(buffer.Buffer, buffer.Width, buffer.Height, buffer.Width * 4);
            var t0 = Stopwatch.GetTimestamp();
            ScreenRecognition r = rec.Recognize(vf);
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            string res = $"{buffer.Width}x{buffer.Height}";
            if (!times.TryGetValue(res, out var list)) times[res] = list = [];
            list.Add(ms);
            tw?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(f.Path)}\t{res}\t{ms:F3}"));
            w.WriteLine(Describe(Path.GetFileName(f.Path), r, RestSiteDetector.Detect(vf), CardRemovalProbe.Read(vf),
                EventButtonDetector.Detect(vf), AncientButtonDetector.Detect(vf)));
            if ((i + 1) % 100 == 0 || i + 1 == files.Count) Console.WriteLine($"   dump {i + 1}/{files.Count}");
        }
        foreach (var (res, list) in times.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            list.Sort();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"recognise {res}: {list.Count} frames, median {list[list.Count / 2]:F2} ms, p90 {list[list.Count * 9 / 10]:F2} ms"));
        }
        return 0;
    }

    // ------------------------------------------------------------------ allocs

    /// <summary>Managed allocations of each per-frame vision stage (precise byte counts, all threads), then by type.</summary>
    private static int Allocs(string local, Dictionary<string, string> opts)
    {
        int frames = int.Parse(opts.GetValueOrDefault("frames") ?? "60", CultureInfo.InvariantCulture);
        int k = int.Parse(opts.GetValueOrDefault("upscale") ?? "2", CultureInfo.InvariantCulture);
        using var loaded = LoadedRecognizer.Create(local, cold: false);
        CardRecognizer rec = loaded.Recognizer.Loaded!;
        var pick = Captures.Spread(Captures.List(Path.Combine(local, "test-captures")).Where(c => c.Width == 1920).ToList(), frames);
        var buffer = new ReusedFrameBuffer();
        string[] stages = ["cards", "rest", "removal", "event", "ancient"];
        var bytes = new long[stages.Length];
        using var sampler = new AllocSampler();
        // Warm-up frame: JIT, lazies (tile matchers) and the first pool rentals are not per-frame costs.
        buffer.Decode(pick[0], k);
        rec.Recognize(new ScreenFrame(buffer.Buffer, buffer.Width, buffer.Height, buffer.Width * 4));
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        for (int i = 0; i < pick.Count; i++)
        {
            buffer.Decode(pick[i], k);
            var vf = new ScreenFrame(buffer.Buffer, buffer.Width, buffer.Height, buffer.Width * 4);
            for (int s = 0; s < stages.Length; s++)
            {
                sampler.Active = s == 0;
                long a = GC.GetTotalAllocatedBytes(precise: true);
                switch (s)
                {
                    case 0: rec.Recognize(vf); break;
                    case 1: RestSiteDetector.Detect(vf); break;
                    case 2: CardRemovalProbe.Read(vf); break;
                    case 3: EventButtonDetector.Detect(vf); break;
                    case 4: AncientButtonDetector.Detect(vf); break;
                }
                bytes[s] += GC.GetTotalAllocatedBytes(precise: true) - a;
                sampler.Active = false;
            }
            if ((i + 1) % 20 == 0 || i + 1 == pick.Count) Console.WriteLine($"   allocs {i + 1}/{pick.Count}");
        }
        Console.WriteLine($"managed bytes allocated per frame ({pick.Count} frames, {buffer.Width}x{buffer.Height}):");
        for (int s = 0; s < stages.Length; s++)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   {stages[s],-8} {bytes[s] / (double)pick.Count / 1024.0,10:F1} KB"));
        Console.WriteLine($"GCs during the loop (harness included): g0/g1/g2 {GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}");
        Console.WriteLine("card recogniser, by type (allocation ticks, ~100 KB each, all frames):");
        sampler.Print(Console.Out, 25);
        return 0;
    }

    private static string R(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Everything the recognisers return for one frame, scores with all their bits (round-trip format).</summary>
    private static string Describe(string name, ScreenRecognition r, RestSiteResult rest, CardRemovalReading removal, EventScreen? ev, EventScreen? ancient)
    {
        var sb = new StringBuilder();
        sb.Append(name).Append(" kind=").Append(r.Kind).Append(" gate=").Append(r.Gate.RewardLikely).Append('/').Append(r.Gate.ShopLikely)
            .Append('/').Append(r.Gate.InspectLikely).Append(" [").Append(r.Gate.Detail).Append("] detail=").Append(r.Detail).AppendLine();
        foreach (var c in r.Slots)
        {
            sb.Append("  slot ").Append(c).Append(" | s=").Append(R(c.Score)).Append(" m=").Append(R(c.Margin)).Append(" up=").Append(R(c.UpgradeEvidence))
                .Append(" scale=").Append(R(c.NodeScale)).Append(" art=").Append(c.ArtBounds).Append(" card=").Append(c.CardBounds)
                .Append(" priceBox=").Append(c.PriceBounds).Append(" price=").Append(c.Price).Append(" rug=").Append(R(c.RugShare)).Append(" empty=").Append(c.SlotEmpty)
                .Append(" accepted=").Append(r.Cards.Contains(c)).Append(" cands=");
            foreach (var m in c.Candidates) sb.Append(m.CardId).Append(':').Append(R(m.Score)).Append(',');
            sb.AppendLine();
        }
        foreach (var x in r.Relics)
            sb.Append("  relic ").Append(x).Append(" | s=").Append(R(x.Score)).Append(" m=").Append(R(x.Margin)).Append(" icon=").Append(x.IconBounds).AppendLine();
        sb.Append("  rest ").Append(rest.ChoiceScreen).Append('/').Append(rest.CardGridOpen).Append(" [").Append(rest.Detail).Append(']');
        foreach (var b in rest.Buttons) sb.Append(' ').Append(b);
        sb.AppendLine();
        sb.Append("  removal ").Append(removal).Append(" | ").Append(R(removal.FaceGold)).Append('/').Append(R(removal.FaceRed)).AppendLine();
        foreach (var (label, s) in new[] { ("event", ev), ("ancient", ancient) })
        {
            sb.Append("  ").Append(label);
            if (s is null) sb.Append(" none");
            else
                foreach (var b in s.Buttons)
                {
                    double sig = 0;
                    foreach (float v in b.TextSignature) sig = sig * 1.000001 + v;
                    sb.Append(" #").Append(b.Index).Append(' ').Append(b.Bounds).Append(' ').Append(b.State).Append(" sig=").Append(sig.ToString("R", CultureInfo.InvariantCulture));
                }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            string key = args[i][2..];
            o[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "on";
        }
        return o;
    }

    private static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SlayTheSpire2TierOverlay.sln"))) return d.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    /// <summary>This checkout's data/local, else the nearest one above it (as the Vision tests' TestPaths do).</summary>
    private static string FindLocal(string repo)
    {
        for (var d = new DirectoryInfo(repo); d is not null; d = d.Parent)
        {
            var local = Path.Combine(d.FullName, "data", "local");
            if (Directory.Exists(Path.Combine(local, "pck"))) return local;
        }
        throw new DirectoryNotFoundException("no data/local with extracted game files found; pass --local");
    }
}
