using System.Globalization;
using System.Text;

namespace Sts2Overlay.Vision;

/// <summary>
/// Digit templates for the shop price label (Kreon Bold, the font of merchant_card.tscn's CostLabel).
///
/// A glyph is a <see cref="Width"/> x <see cref="Height"/> grid of "text colour coverage" (0 = background or
/// outline, 1 = the digit's fill), sampled in a window whose height is the digit height and whose width is
/// <see cref="Aspect"/> times that, centred on the digit. The window is the same for every digit, so a narrow
/// "1" stays narrow: its shape AND its width are both part of the template.
///
/// The defaults are NOT game images: they are per-cell averages of hundreds of digits segmented from real
/// captures (<c>VisionBench price-glyphs</c>), quantised to one hex character per cell. That is a tiny numeric
/// summary of a freely licensed font (Kreon, SIL OFL), regenerated whenever the reader's sampling changes.
/// </summary>
public sealed partial class PriceGlyphs
{
    public const int Width = 12, Height = 16, Length = Width * Height;

    /// <summary>Sampling window width / digit height.</summary>
    public const float Aspect = 0.8f;

    /// <summary>
    /// Measured width / height of each digit's pixels on the captures (<c>VisionBench price-glyphs</c>, min and max
    /// over 704 digits). A digit cut in half by something covering it is much narrower than its class allows,
    /// even when its remaining stroke looks like another digit (half a "2" resembles a "1").
    /// </summary>
    public static readonly IReadOnlyList<(float Min, float Max)> WidthRange =
    [
        (0.667f, 0.706f), (0.444f, 0.529f), (0.556f, 0.647f), (0.591f, 0.706f), (0.588f, 0.882f),
        (0.588f, 0.647f), (0.647f, 0.706f), (0.556f, 0.647f), (0.611f, 0.714f), (0.611f, 0.706f),
    ];

    private readonly float[][] _normalised;

    /// <summary>Raw (un-normalised) coverage grids, index = digit.</summary>
    public IReadOnlyList<float[]> Raw { get; }

    public PriceGlyphs(IReadOnlyList<float[]> raw)
    {
        if (raw.Count != 10 || raw.Any(g => g.Length != Length))
            throw new ArgumentException($"Need 10 glyphs of {Length} cells.");
        Raw = raw.Select(g => (float[])g.Clone()).ToArray();
        _normalised = Raw.Select(g =>
        {
            float[] n = (float[])g.Clone();
            Normalise(n);
            return n;
        }).ToArray();
    }

    /// <summary>Templates committed with the code (PriceGlyphs.Data.cs).</summary>
    public static PriceGlyphs Default => LazyDefault.Value;

    private static readonly Lazy<PriceGlyphs> LazyDefault = new(() => Parse(Data));

    /// <summary>Best digit for a raw coverage grid: (digit, NCC score, gap to the second best digit).</summary>
    public (int Digit, float Score, float Margin) Classify(ReadOnlySpan<float> raw)
    {
        Span<float> q = stackalloc float[Length];
        raw.CopyTo(q);
        if (!Normalise(q)) return (-1, 0, 0);
        int best = -1;
        float s1 = float.NegativeInfinity, s2 = float.NegativeInfinity;
        for (int d = 0; d < 10; d++)
        {
            float s = Ncc.Dot(q, _normalised[d]);
            if (s > s1)
            {
                s2 = s1;
                s1 = s;
                best = d;
            }
            else if (s > s2) s2 = s;
        }
        return (best, s1, s1 - s2);
    }

    /// <summary>NCC of a raw coverage grid against one digit's template.</summary>
    public float Score(ReadOnlySpan<float> raw, int digit)
    {
        Span<float> q = stackalloc float[Length];
        raw.CopyTo(q);
        return Normalise(q) ? Ncc.Dot(q, _normalised[digit]) : 0;
    }

    /// <summary>Mean-subtract and scale to unit length, so NCC is a dot product. False for a flat grid.</summary>
    internal static bool Normalise(Span<float> v)
    {
        float mean = 0;
        foreach (float x in v) mean += x;
        mean /= v.Length;
        float len2 = 0;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] -= mean;
            len2 += v[i] * v[i];
        }
        if (len2 < 1e-6f)
        {
            v.Clear();
            return false;
        }
        float inv = 1 / MathF.Sqrt(len2);
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
        return true;
    }

    /// <summary>One line per digit, <see cref="Length"/> hex characters (row-major, 0..f = coverage 0..1).</summary>
    public static PriceGlyphs Parse(IReadOnlyList<string> lines)
    {
        if (lines.Count != 10) throw new FormatException($"Expected 10 glyph lines, got {lines.Count}.");
        var raw = new float[10][];
        for (int d = 0; d < 10; d++)
        {
            string s = lines[d].Trim();
            if (s.Length != Length) throw new FormatException($"Glyph {d}: expected {Length} hex digits, got {s.Length}.");
            raw[d] = s.Select(c => int.Parse(c.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 15f).ToArray();
        }
        return new PriceGlyphs(raw);
    }

    /// <summary>The inverse of <see cref="Parse"/>.</summary>
    public IReadOnlyList<string> Format() =>
        Raw.Select(g =>
        {
            var sb = new StringBuilder(Length);
            foreach (float v in g) sb.Append(((int)MathF.Round(Math.Clamp(v, 0, 1) * 15)).ToString("x", CultureInfo.InvariantCulture));
            return sb.ToString();
        }).ToArray();
}
