using System.Text.RegularExpressions;
using Sts2Overlay.Vision.Events;
using Sts2Overlay.Vision.Rest;

namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// Replays tests/fixtures/english-capture-truth.txt (an English-UI run, 797 4K frames) when the frames are on this PC:
/// set <c>STS2_EN_FRAMES_DIR</c> to their folder. Skipped otherwise; game images are never committed.
/// Checks the language-sensitive layout readers that need no card database: event option buttons (count, hover, the
/// column's place, which a long English text pushes down), the ancient's options, the rest-site choice and the
/// shop's card-removal service. Transition frames (fades, slides) are not checked.
/// </summary>
public class EnglishCaptureTests
{
    private static string TruthFile => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "english-capture-truth.txt");

    private static IEnumerable<(string File, string Label, string Fields)> Truth() =>
        File.ReadLines(TruthFile)
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(' ', 3))
            .Select(p => (p[0], p[1], p.Length > 2 ? p[2] : ""));

    private static int Field(string text, string name) => int.Parse(Regex.Match(text, $@"\b{name}=(-?\d+)").Groups[1].Value);

    [EnglishFramesFact]
    public void Every_english_frame_gives_its_event_rest_and_removal_truth()
    {
        string dir = Environment.GetEnvironmentVariable(EnglishFramesFactAttribute.Variable)!;
        var wrong = new List<string>();
        int checkedFrames = 0;
        foreach (var (file, label, rest) in Truth())
        {
            if (label == "transition") continue;
            var frame = ImageLoader.Load(Path.Combine(dir, file));
            checkedFrames++;

            var ev = EventButtonDetector.Detect(frame);
            if (label == "event")
            {
                int n = Field(rest, "buttons"), hovered = Field(rest, "hovered"), top = Field(rest, "top");
                var t = RefTransform.For(frame.Width, frame.Height);
                bool ok = ev is not null && ev.Buttons.Count == n && ev.HoveredIndex == hovered
                    && ev.Buttons.All(b => Math.Abs(b.Bounds.Y - t.ToPixelY(top + EventButtonDetector.RefStep * b.Index)) <= t.Scale);
                if (!ok) wrong.Add($"{file}: event {Describe(ev)}, want {rest}");
            }
            else if (ev is not null) wrong.Add($"{file}: {label} but event buttons {Describe(ev)}");

            var an = AncientButtonDetector.Detect(frame);
            int ancient = label == "ancient" ? Field(rest, "buttons") : 0;
            if ((an?.Buttons.Count ?? 0) != ancient || (an is not null && an.HoveredIndex != Field(rest, "hovered")))
                wrong.Add($"{file}: ancient {Describe(an)}, want {ancient}");

            var site = RestSiteDetector.Detect(frame);
            if (label == "rest")
            {
                string hovered = Regex.Match(rest, @"hovered=(\w+)").Groups[1].Value;
                string got = site.Buttons.FirstOrDefault(b => b.State == RestButtonState.Hovered)?.Option.ToString().ToUpperInvariant() ?? "NONE";
                if (!site.ChoiceScreen || got != hovered.ToUpperInvariant()) wrong.Add($"{file}: rest {site.Detail}, want hovered {hovered}");
            }
            else if (site.ChoiceScreen) wrong.Add($"{file}: {label} but a rest-site choice");

            if (label == "shop")
            {
                bool available = rest.Contains(":available", StringComparison.Ordinal);
                if (CardRemovalProbe.Read(frame).Available != available) wrong.Add($"{file}: removal, want {(available ? "available" : "used")}");
            }
        }
        Assert.True(checkedFrames >= 780, $"only {checkedFrames} frames in the truth file");
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {checkedFrames} frames wrong:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    private static string Describe(EventScreen? s) =>
        s is null ? "none" : string.Join(" ", s.Buttons.Select(b => $"{b.Index}{b.State.ToString()[0]}@{b.Bounds.Y}"));
}

/// <summary>Skips unless <c>STS2_EN_FRAMES_DIR</c> points at the English frames of the truth file.</summary>
internal sealed class EnglishFramesFactAttribute : FactAttribute
{
    public const string Variable = "STS2_EN_FRAMES_DIR";

    public EnglishFramesFactAttribute()
    {
        string? dir = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "f_20261109_205241_965_3842x2160.png")))
            Skip = $"set {Variable} to the folder of the English frames in tests/fixtures/english-capture-truth.txt";
    }
}
