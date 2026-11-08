namespace Sts2Overlay.Vision.Tests;

/// <summary>Locates repository and local-only assets regardless of the test runner's working directory.</summary>
internal static class TestPaths
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SlayTheSpire2TierOverlay.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    });

    public static string RepoRoot => Root.Value;

    /// <summary>Card portraits extracted from the game's .pck (local only, never committed).</summary>
    public static string ArtDir => Path.Combine(RepoRoot, "data", "local", "pck", "card_art");

    public static string Catalog => Path.Combine(RepoRoot, "data", "catalog", "cards.v0.107.1.json");

    public static string DbCache => Path.Combine(RepoRoot, "data", "local", "vision", "card-art.bin");

    /// <summary>Screenshots taken by the capture tool on the developer's machine.</summary>
    public static string CapturesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "captures");
}

/// <summary>
/// A fact that is skipped (not failed) when local game data is missing, e.g. on CI or a fresh clone.
/// xunit 2 has no runtime "skip", but an attribute can set <see cref="FactAttribute.Skip"/> in its constructor.
/// </summary>
internal sealed class LocalDataFactAttribute : FactAttribute
{
    public LocalDataFactAttribute(params string[] relativeToCaptures)
    {
        if (!Directory.Exists(TestPaths.ArtDir) || !File.Exists(TestPaths.Catalog))
        {
            Skip = "needs data/local/pck/card_art (extract it with tools/PckTool)";
            return;
        }
        foreach (string f in relativeToCaptures)
        {
            if (!File.Exists(Path.Combine(TestPaths.CapturesDir, f)))
            {
                Skip = $"needs capture {f} in {TestPaths.CapturesDir}";
                return;
            }
        }
    }
}

/// <summary>Draws simple synthetic screens so the pipeline can be tested without any game images.</summary>
internal sealed class Canvas
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public RefTransform T { get; }

    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        T = RefTransform.For(width, height);
        for (int i = 3; i < Pixels.Length; i += 4) Pixels[i] = 255;
    }

    public ScreenFrame Frame => ScreenFrame.FromPacked(Pixels, Width, Height);

    public void Set(int x, int y, byte r, byte g, byte b)
    {
        int p = (y * Width + x) * 4;
        Pixels[p] = b;
        Pixels[p + 1] = g;
        Pixels[p + 2] = r;
    }

    /// <summary>Fills a rectangle given in 1920x1080 reference units (pixels whose centre lies inside).</summary>
    public void FillRef(RectF refBox, byte r, byte g, byte b) => FillPixels(T.ToPixels(refBox), r, g, b);

    public void FillPixels(RectF box, byte r, byte g, byte b)
    {
        (int x0, int y0, int x1, int y1) = Span(box);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                Set(x, y, r, g, b);
    }

    /// <summary>Paints a texture (packed BGRA) stretched into a reference-unit box with bilinear sampling.</summary>
    public void DrawImageRef(RectF refBox, ScreenFrame tex)
    {
        RectF box = T.ToPixels(refBox);
        (int x0, int y0, int x1, int y1) = Span(box);
        ReadOnlySpan<byte> src = tex.Bgra.Span;
        for (int y = y0; y < y1; y++)
        {
            float v = (y + 0.5f - box.Y) / box.Height * tex.Height - 0.5f;
            for (int x = x0; x < x1; x++)
            {
                float u = (x + 0.5f - box.X) / box.Width * tex.Width - 0.5f;
                int p = (y * Width + x) * 4;
                for (int c = 0; c < 3; c++) Pixels[p + c] = (byte)Math.Clamp(Bilinear(src, tex, u, v, c) + 0.5f, 0, 255);
            }
        }
    }

    private (int, int, int, int) Span(RectF box) => (
        Math.Clamp((int)MathF.Round(box.X), 0, Width), Math.Clamp((int)MathF.Round(box.Y), 0, Height),
        Math.Clamp((int)MathF.Round(box.Right), 0, Width), Math.Clamp((int)MathF.Round(box.Bottom), 0, Height));

    private static float Bilinear(ReadOnlySpan<byte> src, ScreenFrame tex, float u, float v, int c)
    {
        u = Math.Clamp(u, 0, tex.Width - 1);
        v = Math.Clamp(v, 0, tex.Height - 1);
        int x0 = Math.Min((int)u, tex.Width - 2), y0 = Math.Min((int)v, tex.Height - 2);
        float fx = u - x0, fy = v - y0;
        int i00 = y0 * tex.Stride + x0 * 4 + c, i10 = i00 + tex.Stride;
        return (1 - fy) * ((1 - fx) * src[i00] + fx * src[i00 + 4]) + fy * ((1 - fx) * src[i10] + fx * src[i10 + 4]);
    }

    /// <summary>
    /// Deterministic "artwork": a sum of random sinusoid blobs per channel. Smooth like painted art, and two
    /// different seeds are nearly uncorrelated, so they behave like two different cards.
    /// </summary>
    public static ScreenFrame Texture(int seed, int width = 500, int height = 380)
    {
        var rng = new Random(seed);
        var waves = new (float fx, float fy, float ph, float amp)[3, 6];
        for (int c = 0; c < 3; c++)
            for (int k = 0; k < 6; k++)
                waves[c, k] = ((float)rng.NextDouble() * 12 - 6, (float)rng.NextDouble() * 12 - 6, (float)(rng.NextDouble() * Math.Tau), 15 + 20 * (float)rng.NextDouble());
        byte[] px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float s = 128;
                    for (int k = 0; k < 6; k++)
                    {
                        (float fx, float fy, float ph, float amp) = waves[c, k];
                        s += amp * MathF.Sin(fx * x / width * MathF.Tau / 2 + fy * y / height * MathF.Tau / 2 + ph) / 2;
                    }
                    px[p + c] = (byte)Math.Clamp(s, 0, 255);
                }
                px[p + 3] = 255;
            }
        }
        return ScreenFrame.FromPacked(px, width, height);
    }
}

/// <summary>Builds tiny art databases from synthetic textures.</summary>
internal static class SyntheticDb
{
    public const string Ironclad = "CARD_POOL.IRONCLAD_CARD_POOL";
    public const string Silent = "CARD_POOL.SILENT_CARD_POOL";

    /// <param name="cards">(card id, pool, texture seed); equal seeds give identical art (twins).</param>
    public static CardArtDatabase Create(params (string Id, string Pool, int Seed)[] cards)
    {
        var entries = new List<ArtEntry>();
        float[] full = new float[cards.Length * Ncc.FullLength];
        float[] coarse = new float[cards.Length * Ncc.CoarseLength];
        for (int i = 0; i < cards.Length; i++)
        {
            entries.Add(new ArtEntry(i, cards[i].Id, cards[i].Pool, null, $"{cards[i].Id}.webp", false));
            CardArtDatabase.ComputeArtVectors(Canvas.Texture(cards[i].Seed),
                full.AsSpan(i * Ncc.FullLength, Ncc.FullLength), coarse.AsSpan(i * Ncc.CoarseLength, Ncc.CoarseLength));
        }
        var twins = new List<ArtTwin>();
        for (int a = 0; a < cards.Length; a++)
            for (int b = a + 1; b < cards.Length; b++)
                if (cards[a].Seed == cards[b].Seed && cards[a].Id != cards[b].Id)
                    twins.Add(new ArtTwin(cards[a].Id, cards[b].Id, 1f));
        return new CardArtDatabase(entries, full, coarse, twins, "synthetic");
    }

    /// <summary>Ten ironclad cards CARD.C0..C9 with seeds 100..109.</summary>
    public static CardArtDatabase Ten() =>
        Create(Enumerable.Range(0, 10).Select(i => ($"CARD.C{i}", Ironclad, 100 + i)).ToArray());
}
