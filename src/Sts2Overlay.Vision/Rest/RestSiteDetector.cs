using System.Globalization;
using System.Text;

namespace Sts2Overlay.Vision.Rest;

/// <summary>
/// Recognises the rest-site (campfire) choice screen: are the option buttons showing, where is each one, and
/// which option does it offer. Also reports whether the Smith card grid is open (<see cref="CardGridProbe"/>).
///
/// How it works, and why:
/// <list type="number">
/// <item>The buttons can only be in a few places (<see cref="RestSiteLayout"/>): for N options the row is centred,
/// so we only test N = 1..6. Rows for N and N+2 share slots, so each distinct slot is measured once and cached.</item>
/// <item>For a slot we cut the expected icon into 4x3 cells and compare the cell colours with the signature of
/// every known icon (<see cref="RestIconSignatures"/>). The comparison first fits one brightness factor ("gain")
/// so a fading-in or slightly dimmed button still matches, then takes the RMS of what is left over.
/// Gains outside <see cref="MinGain"/>..<see cref="MaxGain"/> are clamped, which is what makes a black or
/// half-transparent frame fail: a dark slot would otherwise "match" any icon scaled down to nothing.</item>
/// <item>Each slot is tried at the three animation scales (1, 1.1 hovered, 0.9 pressed) and with the normal and the
/// brightened (hover) colours, because the game scales and brightens the picture under the mouse.</item>
/// <item>The screen counts as the choice screen when every slot of some row of N buttons matches, and we pick the
/// largest such N. A grey (disabled) icon is accepted only next to at least one coloured match, so plain grey
/// screen areas can never start a detection on their own.</item>
/// </list>
/// Pure function of the pixels, no state: ~0.2 ms per 4K frame in Release (RestBench), because it reads only about
/// 11 slots x 3 scales x 12 cells of sparse pixels plus a few tiny probes.
/// </summary>
public static class RestSiteDetector
{
    /// <summary>Largest accepted colour residual (RMS over the 36 cell values, 0..255 scale).</summary>
    /// <remarks>Measured on ~2700 real frames: settled choice-screen buttons fit within 3 (5.4 mid fade-in); the
    /// closest non-button area (map parchment, dark menus) stays above 12. 8 sits in the gap.</remarks>
    public const float MaxResidual = 8f;

    /// <summary>Brightness range a real button can have relative to its signature.</summary>
    /// <remarks>Below ~0.75 the buttons are still fading in (0.5 s tween) or out after a choice; we prefer to
    /// report nothing for those few frames rather than mark a button the player cannot click yet.</remarks>
    public const float MinGain = 0.75f, MaxGain = 1.3f;

    /// <summary>A disabled option is drawn at v = 0.6 of its grey; allow the same relative wiggle.</summary>
    private const float DisabledMinGain = 0.75f, DisabledMaxGain = 1.3f;

    private static readonly float[] Scales = [1f, 1.1f, 0.9f];

    private sealed record SlotMatch(RestOption Option, float Residual, float Margin, float Scale, float Gain, bool Enabled);

    /// <summary>Signatures prepared once: normal, hover-brightened and grey (disabled) per option.</summary>
    private static readonly (RestOption Option, float[] Normal, float[] Bright, float[] Grey)[] Signatures =
        RestIconSignatures.All.Select(s =>
        {
            float[] normal = s.Normal.Select(b => (float)b).ToArray();
            return (s.Option, normal, s.Bright.Select(b => (float)b).ToArray(), RestIconSignature.Disabled(normal));
        }).ToArray();

    public static RestSiteResult Detect(in ScreenFrame frame)
    {
        RefTransform t = RefTransform.For(frame.Width, frame.Height);
        bool grid = CardGridProbe.IsOpen(frame, out string gridDetail);

        // Distinct slot positions, keyed by "half pitches from the centre" (k = 2*index - (N-1)), so the slots that
        // rows of N and N+2 buttons share are measured once.
        var cache = new Dictionary<int, (SlotMatch Enabled, SlotMatch Grey)>();

        var detail = new StringBuilder(gridDetail);
        for (int n = RestSiteLayout.MaxOptions; n >= 1; n--)
        {
            var matches = new SlotMatch?[n];
            bool ok = true, anyColour = false;
            // Outermost slots first: for large N they are the ones that are usually empty, so we stop early.
            foreach (int i in Enumerable.Range(0, n).OrderByDescending(i => Math.Abs(2 * i - (n - 1))))
            {
                int k = 2 * i - (n - 1);
                if (!cache.TryGetValue(k, out var m))
                {
                    m = MatchSlot(frame, t, RestSiteLayout.Slot(i, n));
                    cache[k] = m;
                }
                var (enabled, grey) = m;
                if (enabled.Residual <= MaxResidual) { matches[i] = enabled; anyColour = true; }
                else if (grey.Residual <= MaxResidual) matches[i] = grey;
                else { ok = false; break; }
            }
            if (!ok || !anyColour) continue;

            var buttons = new List<RestButton>(n);
            for (int i = 0; i < n; i++)
            {
                SlotMatch m = matches[i]!;
                RectF slot = RestSiteLayout.Slot(i, n);
                RestButtonState state = m.Scale > 1.05f ? RestButtonState.Hovered : m.Scale < 0.95f ? RestButtonState.Pressed : RestButtonState.Normal;
                buttons.Add(new RestButton(m.Option, i, RestSiteLayout.Icon(slot, m.Scale), slot, state, m.Enabled, m.Residual, m.Margin));
                detail.Append(CultureInfo.InvariantCulture, $" {m.Option}:r{m.Residual:F1}/m{m.Margin:F0}/g{m.Gain:F2}/s{m.Scale:F1}");
            }
            return new RestSiteResult(true, buttons, grid, $"n={n}{detail}");
        }

        // For the bench: how close the best measured slot came to looking like an icon.
        float closest = cache.Values.Select(v => v.Enabled.Residual).DefaultIfEmpty(float.PositiveInfinity).Min();
        return RestSiteResult.None(grid, string.Create(CultureInfo.InvariantCulture, $"none closest={closest:F1} {detail}"));
    }

    /// <summary>Best coloured and best grey match for one slot (the caller applies <see cref="MaxResidual"/>).</summary>
    private static (SlotMatch Enabled, SlotMatch Grey) MatchSlot(in ScreenFrame frame, RefTransform t, RectF slot)
    {
        Span<float> obs = stackalloc float[RestIconSignature.Length];
        SlotMatch? bestEnabled = null, bestGrey = null;
        foreach (float scale in Scales)
        {
            RestIconSignature.FromFrame(frame, t.ToPixels(RestSiteLayout.Icon(slot, scale)), t.Scale, obs);
            Consider(obs, scale, enabled: true, ref bestEnabled);
            Consider(obs, scale, enabled: false, ref bestGrey);
        }
        return (bestEnabled!, bestGrey!);
    }

    private static void Consider(ReadOnlySpan<float> obs, float scale, bool enabled, ref SlotMatch? best)
    {
        // Best and runner-up residual per option, so the margin compares different options only.
        float bestR = float.PositiveInfinity, secondR = float.PositiveInfinity, bestGain = 0;
        RestOption bestOption = RestOption.Unknown;
        foreach (var s in Signatures)
        {
            float r, g;
            if (enabled)
            {
                (float r1, float g1) = Fit(obs, s.Normal, MinGain, MaxGain);
                (float r2, float g2) = Fit(obs, s.Bright, MinGain, MaxGain);
                (r, g) = r1 <= r2 ? (r1, g1) : (r2, g2 * 1.2f);
            }
            else
            {
                (r, g) = Fit(obs, s.Grey, DisabledMinGain, DisabledMaxGain);
            }
            if (r < bestR) { secondR = bestR; bestR = r; bestOption = s.Option; bestGain = g; }
            else if (r < secondR) secondR = r;
        }
        if (best is null || bestR < best.Residual)
            best = new SlotMatch(bestOption, bestR, secondR - bestR, scale, bestGain, enabled);
    }

    /// <summary>
    /// Least-squares brightness factor g for obs ≈ g * sig (clamped to [min, max]) and the RMS residual.
    /// WHY a single factor: fading, the hover brightening and capture/JPEG differences scale all colours of a
    /// button together, while a wrong picture differs in the pattern of colours, which g cannot explain.
    /// </summary>
    internal static (float Residual, float Gain) Fit(ReadOnlySpan<float> obs, ReadOnlySpan<float> sig, float min, float max)
    {
        float dot = 0, norm = 0;
        for (int i = 0; i < obs.Length; i++) { dot += obs[i] * sig[i]; norm += sig[i] * sig[i]; }
        float g = norm > 0 ? Math.Clamp(dot / norm, min, max) : min;
        float sum = 0;
        for (int i = 0; i < obs.Length; i++) { float d = obs[i] - g * sig[i]; sum += d * d; }
        return (MathF.Sqrt(sum / obs.Length), g);
    }
}
