using System.Globalization;

namespace Sts2Overlay.Vision.Rest;

/// <summary>What a hand-labelled frame shows.</summary>
public enum RestTruthLabel
{
    /// <summary>Nothing rest-related (must not be detected).</summary>
    None,
    /// <summary>The campfire choice screen with its buttons.</summary>
    Choice,
    /// <summary>The upgrade-style deck grid (or its preview) is open.</summary>
    Grid,
    /// <summary>Mid-fade frame that is neither clearly on nor off; excluded from scoring, with a written reason.</summary>
    Transition,
}

/// <summary>One labelled button: option and visible picture in design px.</summary>
public sealed record RestTruthButton(RestOption Option, RectF Rect);

/// <summary>One line of the ground-truth file.</summary>
public sealed record RestTruthEntry(string File, RestTruthLabel Label, IReadOnlyList<RestTruthButton> Buttons, string Note);

/// <summary>
/// Ground truth for the rest-site bench and tests: a plain text file with one frame per line, names and numbers
/// only (the frames themselves never enter the repo).
/// <code>
/// # comment
/// f_20261108_235211_3842x2160.jpg choice Heal@673,265,228,150 Smith@1019,265,230,150
/// f_20261108_234104_3842x2160.jpg grid
/// f_20261108_234436_3842x2160.jpg transition buttons fading out after the choice
/// f_20261108_233644_3842x2160.jpg none
/// </code>
/// </summary>
public static class RestSiteTruth
{
    /// <summary>Largest allowed difference of any rectangle edge, in design px.</summary>
    public const float MaxEdgeError = 10f;

    public static IReadOnlyList<RestTruthEntry> Parse(IEnumerable<string> lines)
    {
        var list = new List<RestTruthEntry>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) throw new FormatException($"truth line needs a file and a label: '{line}'");
            var label = Enum.Parse<RestTruthLabel>(p[1], ignoreCase: true);
            var buttons = new List<RestTruthButton>();
            string note = "";
            if (label == RestTruthLabel.Choice)
            {
                foreach (string b in p.Skip(2))
                {
                    string[] at = b.Split('@');
                    float[] n = at[1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    buttons.Add(new RestTruthButton(Enum.Parse<RestOption>(at[0], ignoreCase: true), new RectF(n[0], n[1], n[2], n[3])));
                }
            }
            else
            {
                note = string.Join(' ', p.Skip(2));
            }
            if (label == RestTruthLabel.Transition && note.Length == 0)
                throw new FormatException($"a transition label needs a reason: '{line}'");
            list.Add(new RestTruthEntry(p[0], label, buttons, note));
        }
        return list;
    }

    /// <summary>
    /// Compares a detection with its label. Returns null when it passes, else a short reason.
    /// Transition frames always pass (they are reported separately).
    /// </summary>
    public static string? Check(RestTruthEntry truth, RestSiteResult r)
    {
        switch (truth.Label)
        {
            case RestTruthLabel.Transition:
                return null;
            case RestTruthLabel.None:
                if (r.ChoiceScreen) return "false choice screen";
                if (r.CardGridOpen) return "false grid";
                return null;
            case RestTruthLabel.Grid:
                if (r.ChoiceScreen) return "choice screen on a grid frame";
                return r.CardGridOpen ? null : "grid missed";
            case RestTruthLabel.Choice:
                if (!r.ChoiceScreen) return "choice screen missed";
                if (r.CardGridOpen) return "false grid on the choice screen";
                if (r.Buttons.Count != truth.Buttons.Count) return $"{r.Buttons.Count} buttons, expected {truth.Buttons.Count}";
                for (int i = 0; i < truth.Buttons.Count; i++)
                {
                    RestTruthButton want = truth.Buttons[i];
                    RestButton got = r.Buttons[i];
                    if (got.Option != want.Option) return $"button {i}: {got.Option}, expected {want.Option}";
                    float err = EdgeError(got.Rect, want.Rect);
                    if (err > MaxEdgeError) return $"button {i}: rect {got.Rect} off by {err:F1} px from {want.Rect}";
                }
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(truth));
        }
    }

    /// <summary>Largest distance between corresponding edges of two rectangles.</summary>
    public static float EdgeError(RectF a, RectF b) =>
        Math.Max(Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)), Math.Max(Math.Abs(a.Right - b.Right), Math.Abs(a.Bottom - b.Bottom)));
}
