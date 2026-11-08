using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Sts2Overlay.Vision;

/// <summary>One database entry (a card's portrait). Several entries may share a card id (art variants).</summary>
public sealed record ArtEntry(int Index, string CardId, string Pool, string? Character, string ArtPath, bool IsBeta);

/// <summary>A database candidate with its NCC score.</summary>
public readonly record struct ArtMatch(int EntryIndex, string CardId, float Score);

/// <summary>Two entries whose artwork is (near) identical, so the picture alone cannot tell them apart.</summary>
public sealed record ArtTwin(string CardIdA, string CardIdB, float Score);

/// <summary>
/// NCC thumbnails of every card portrait, built once from the locally extracted art and cached in a small
/// binary file so the app starts fast.
///
/// Memory layout: all full vectors live in ONE contiguous float array (entry i at i*FullLength), likewise the
/// coarse vectors. A top-k scan then streams through memory linearly, which the hardware prefetcher handles
/// well; an array of separate float[] objects would scatter them across the heap.
/// </summary>
public sealed class CardArtDatabase
{
    private const string Magic = "STS2ART";
    /// <summary>Bump when the vector definition (sizes, inner crop, sampling) changes: old caches get rebuilt.</summary>
    public const int FormatVersion = 1;

    private readonly float[] _full;
    private readonly float[] _coarse;

    public IReadOnlyList<ArtEntry> Entries { get; }
    public IReadOnlyList<ArtTwin> Twins { get; }
    /// <summary>Identifies the source files the cache was built from (see <see cref="Fingerprint"/>).</summary>
    public string SourceFingerprint { get; }
    public int Count => Entries.Count;

    private readonly Dictionary<string, List<int>> _byCard;
    private readonly Dictionary<string, HashSet<string>> _twinsOf;

    internal CardArtDatabase(IReadOnlyList<ArtEntry> entries, float[] full, float[] coarse, IReadOnlyList<ArtTwin> twins, string fingerprint)
    {
        Entries = entries;
        _full = full;
        _coarse = coarse;
        Twins = twins;
        SourceFingerprint = fingerprint;
        _byCard = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (ArtEntry e in entries)
        {
            if (!_byCard.TryGetValue(e.CardId, out List<int>? list)) _byCard[e.CardId] = list = [];
            list.Add(e.Index);
        }
        _twinsOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (ArtTwin t in twins)
        {
            Add(t.CardIdA, t.CardIdB);
            Add(t.CardIdB, t.CardIdA);
        }

        void Add(string a, string b)
        {
            if (!_twinsOf.TryGetValue(a, out HashSet<string>? s)) _twinsOf[a] = s = new HashSet<string>(StringComparer.Ordinal);
            s.Add(b);
        }
    }

    public ReadOnlySpan<float> FullVector(int entry) => _full.AsSpan(entry * Ncc.FullLength, Ncc.FullLength);
    public ReadOnlySpan<float> CoarseVector(int entry) => _coarse.AsSpan(entry * Ncc.CoarseLength, Ncc.CoarseLength);

    /// <summary>Card ids whose art is indistinguishable from <paramref name="cardId"/>'s.</summary>
    public IReadOnlySet<string> TwinsOf(string cardId) =>
        _twinsOf.TryGetValue(cardId, out HashSet<string>? s) ? s : (IReadOnlySet<string>)new HashSet<string>();

    public IEnumerable<int> EntriesOf(string cardId) => _byCard.TryGetValue(cardId, out List<int>? l) ? l : [];

    /// <summary>
    /// Best <paramref name="k"/> entries by NCC against a normalised query vector (full or coarse, chosen by
    /// length), keeping only the best entry per card id. <paramref name="allowed"/> restricts candidates.
    /// </summary>
    public ArtMatch[] TopK(ReadOnlySpan<float> query, int k, Func<ArtEntry, bool>? allowed = null)
    {
        bool full = query.Length == Ncc.FullLength;
        if (!full && query.Length != Ncc.CoarseLength) throw new ArgumentException("Query length must be full or coarse.");
        float[] data = full ? _full : _coarse;
        int len = query.Length;
        var best = new List<ArtMatch>(k + 1);
        for (int i = 0; i < Entries.Count; i++)
        {
            ArtEntry e = Entries[i];
            if (allowed != null && !allowed(e)) continue;
            float s = Ncc.Dot(query, data.AsSpan(i * len, len));
            Insert(best, new ArtMatch(i, e.CardId, s), k);
        }
        return best.ToArray();
    }

    /// <summary>NCC of the query against the given entries only (used inside the local box search).</summary>
    public float BestOf(ReadOnlySpan<float> query, ReadOnlySpan<int> entries, out int bestEntry)
    {
        bool full = query.Length == Ncc.FullLength;
        float[] data = full ? _full : _coarse;
        int len = query.Length;
        float best = float.NegativeInfinity;
        bestEntry = -1;
        foreach (int i in entries)
        {
            float s = Ncc.Dot(query, data.AsSpan(i * len, len));
            if (s > best)
            {
                best = s;
                bestEntry = i;
            }
        }
        return best;
    }

    private static void Insert(List<ArtMatch> best, ArtMatch m, int k)
    {
        // Keep one entry per card id (art variants of the same card must not push other cards out).
        int same = best.FindIndex(b => b.CardId == m.CardId);
        if (same >= 0)
        {
            if (best[same].Score >= m.Score) return;
            best.RemoveAt(same);
        }
        int pos = best.Count;
        while (pos > 0 && best[pos - 1].Score < m.Score) pos--;
        if (pos >= k) return;
        best.Insert(pos, m);
        if (best.Count > k) best.RemoveAt(best.Count - 1);
    }

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Decodes every portrait and computes its vectors (in parallel: decoding a 1000x760 WebP dominates).
    /// Also finds identical-art pairs (NCC &gt;= <paramref name="twinThreshold"/>) between different card ids.
    /// </summary>
    public static CardArtDatabase Build(string artRoot, VisionCatalog? catalog, float twinThreshold = 0.97f, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        IReadOnlyList<ArtFile> files = CardArtSource.Enumerate(artRoot, catalog);
        var entries = files.Select((f, i) => new ArtEntry(i, f.CardId, f.Pool, f.Character, f.RelativePath, f.IsBeta)).ToList();
        float[] full = new float[entries.Count * Ncc.FullLength];
        float[] coarse = new float[entries.Count * Ncc.CoarseLength];
        Parallel.For(0, entries.Count, i =>
        {
            ScreenFrame art = ImageLoader.Load(Path.Combine(artRoot, entries[i].ArtPath));
            ComputeArtVectors(art, full.AsSpan(i * Ncc.FullLength, Ncc.FullLength), coarse.AsSpan(i * Ncc.CoarseLength, Ncc.CoarseLength));
        });
        log?.Invoke($"art vectors: {entries.Count} entries in {sw.ElapsedMilliseconds} ms");

        var twins = new List<ArtTwin>();
        for (int a = 0; a < entries.Count; a++)
        {
            for (int b = a + 1; b < entries.Count; b++)
            {
                if (entries[a].CardId == entries[b].CardId) continue;
                float s = Ncc.Dot(full.AsSpan(a * Ncc.FullLength, Ncc.FullLength), full.AsSpan(b * Ncc.FullLength, Ncc.FullLength));
                if (s >= twinThreshold) twins.Add(new ArtTwin(entries[a].CardId, entries[b].CardId, s));
            }
        }
        log?.Invoke($"identical-art pairs (NCC >= {twinThreshold}): {twins.Count}");
        return new CardArtDatabase(entries, full, coarse, twins, Fingerprint(artRoot, files, catalog));
    }

    /// <summary>Vectors of a full portrait image (the reference side of the comparison).</summary>
    public static void ComputeArtVectors(in ScreenFrame art, Span<float> full, Span<float> coarse)
    {
        // Step 2: the portrait is 1000x760 and each 48x36 cell covers ~17x15 px, so pre-averaging 2x2 loses nothing.
        using IntegralImage img = IntegralImage.Build(art, new PixelRect(0, 0, art.Width, art.Height), 2);
        var box = new RectF(0, 0, img.Width * img.Step, img.Height * img.Step);
        Ncc.Extract(img, box, Ncc.FullW, Ncc.FullH, full);
        Ncc.Extract(img, box, Ncc.CoarseW, Ncc.CoarseH, coarse);
    }

    /// <summary>
    /// Cheap description of the inputs: if any portrait is added/changed, or the catalog or the vector
    /// format changes, the fingerprint changes and the cache is rebuilt.
    /// </summary>
    public static string Fingerprint(string artRoot, IReadOnlyList<ArtFile> files, VisionCatalog? catalog)
    {
        long bytes = 0, newest = 0;
        foreach (ArtFile f in files)
        {
            var fi = new FileInfo(Path.Combine(artRoot, f.RelativePath));
            bytes += fi.Length;
            newest = Math.Max(newest, fi.LastWriteTimeUtc.Ticks);
        }
        return $"v{FormatVersion};{Ncc.FullW}x{Ncc.FullH};{Ncc.CoarseW}x{Ncc.CoarseH};files={files.Count};bytes={bytes};newest={newest};catalog={catalog?.Cards.Count ?? 0}";
    }

    // ------------------------------------------------------------------ cache file

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(SourceFingerprint);
            w.Write(Entries.Count);
            foreach (ArtEntry e in Entries)
            {
                w.Write(e.CardId);
                w.Write(e.Pool);
                w.Write(e.Character ?? "");
                w.Write(e.ArtPath);
                w.Write(e.IsBeta);
            }
            w.Write(Twins.Count);
            foreach (ArtTwin t in Twins)
            {
                w.Write(t.CardIdA);
                w.Write(t.CardIdB);
                w.Write(t.Score);
            }
            // Raw little-endian floats: loading is a single memcpy-like read instead of parsing text.
            w.Write(MemoryMarshal.AsBytes(_full.AsSpan()));
            w.Write(MemoryMarshal.AsBytes(_coarse.AsSpan()));
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Loads a cache file; returns null if it is missing, corrupt or of another format version.</summary>
    public static CardArtDatabase? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (r.ReadString() != Magic || r.ReadInt32() != FormatVersion) return null;
            string fingerprint = r.ReadString();
            int n = r.ReadInt32();
            var entries = new List<ArtEntry>(n);
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString(), pool = r.ReadString(), ch = r.ReadString(), art = r.ReadString();
                bool beta = r.ReadBoolean();
                entries.Add(new ArtEntry(i, id, pool, ch.Length == 0 ? null : ch, art, beta));
            }
            int tn = r.ReadInt32();
            var twins = new List<ArtTwin>(tn);
            for (int i = 0; i < tn; i++) twins.Add(new ArtTwin(r.ReadString(), r.ReadString(), r.ReadSingle()));
            float[] full = new float[n * Ncc.FullLength];
            float[] coarse = new float[n * Ncc.CoarseLength];
            r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(full.AsSpan()));
            r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(coarse.AsSpan()));
            return new CardArtDatabase(entries, full, coarse, twins, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// App start-up entry point: load the cache if it matches the current art files, otherwise build it
    /// (a few seconds, once) and save it.
    /// </summary>
    public static CardArtDatabase LoadOrBuild(string artRoot, string? catalogPath, string cachePath, Action<string>? log = null)
    {
        VisionCatalog? catalog = catalogPath != null && File.Exists(catalogPath) ? VisionCatalog.Load(catalogPath) : null;
        CardArtDatabase? cached = TryLoad(cachePath);
        if (cached != null)
        {
            string current = Fingerprint(artRoot, CardArtSource.Enumerate(artRoot, catalog), catalog);
            if (current == cached.SourceFingerprint) return cached;
            log?.Invoke("art cache is stale, rebuilding");
        }
        CardArtDatabase db = Build(artRoot, catalog, log: log);
        db.Save(cachePath);
        return db;
    }
}
