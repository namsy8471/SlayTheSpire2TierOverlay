using Sts2Overlay.Vision.Events;

namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// The page-change check on real frames (tests/fixtures/event-page-truth.txt): within one visit to an event room,
/// every pair of frames with the same button count must read <see cref="TextVerdict.Same"/> on every button when
/// the page is the same (whatever is hovered), and <see cref="TextVerdict.Changed"/> on some button when it is
/// not, whichever frame is the reference. Korean frames need the developer's captures, English ones
/// <c>STS2_EN_FRAMES_DIR</c>; skipped otherwise.
/// </summary>
public class EventPageTests
{
    private static string TruthFile => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "event-page-truth.txt");

    private static List<(string File, string Kind, string Visit, string Page)> Truth(string lang) =>
        File.ReadLines(TruthFile)
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(' '))
            .Where(p => p[1] == lang)
            .Select(p => (p[0], p[2], p[3].Split(':')[0], p[3].Split(':')[1]))
            .ToList();

    [EventCapturesFact]
    public void Korean_frames_tell_page_changes_from_the_same_page() => Check("ko", TestPaths.CapturesDir, minSame: 50, minChanged: 4);

    [EnglishFramesFact]
    public void English_frames_tell_page_changes_from_the_same_page() =>
        Check("en", Environment.GetEnvironmentVariable(EnglishFramesFactAttribute.Variable)!, minSame: 6, minChanged: 3);

    private static void Check(string lang, string dir, int minSame, int minChanged)
    {
        var frames = Truth(lang).Select(f =>
        {
            ScreenFrame frame = ImageLoader.Load(Path.Combine(dir, f.File));
            EventScreen? s = f.Kind == "ancient" ? AncientButtonDetector.Detect(frame) : EventButtonDetector.Detect(frame);
            return (f.File, f.Visit, f.Page, Screen: s);
        }).ToList();
        var wrong = frames.Where(f => f.Screen is null || f.Screen.Buttons.Any(b => b.Ink is null)).Select(f => $"{f.File}: no buttons or no ink").ToList();
        float maxSame = 0, minChange = 1;
        int same = 0, changed = 0, compared = 0;
        foreach (var a in frames)
            foreach (var b in frames)
            {
                // Every pair of buttons on any two frames (other events too): the quick "changed" proof agrees with the full search.
                if (a.Screen is not null && b.Screen is not null && a.Screen.Buttons.Count == b.Screen.Buttons.Count)
                    foreach (var (x, y) in a.Screen.Buttons.Zip(b.Screen.Buttons))
                    {
                        compared++;
                        if (TextInk.Judge(x.Ink!, y.Ink!) is var quick && quick != TextInk.JudgeExact(x.Ink!, y.Ink!))
                            wrong.Add($"{a.File} / {b.File}: quick {quick}, full {TextInk.JudgeExact(x.Ink!, y.Ink!)}");
                    }
                if (string.CompareOrdinal(a.File, b.File) >= 0 || a.Visit != b.Visit || a.Screen is null || b.Screen is null
                    || a.Screen.Buttons.Count != b.Screen.Buttons.Count) continue;
                var inks = a.Screen.Buttons.Zip(b.Screen.Buttons, (x, y) => (A: x.Ink!, B: y.Ink!)).ToList();
                float d = inks.Max(p => TextInk.Distance(p.A, p.B));
                TextVerdict ab = inks.Max(p => TextInk.Judge(p.A, p.B)), ba = inks.Max(p => TextInk.Judge(p.B, p.A));
                if (a.Page == b.Page)
                {
                    same++;
                    maxSame = Math.Max(maxSame, d);
                    if (ab != TextVerdict.Same || ba != TextVerdict.Same) wrong.Add($"{a.File} / {b.File} same page: {ab}/{ba} ({d:F4})");
                }
                else
                {
                    changed++;
                    minChange = Math.Min(minChange, d);
                    if (ab != TextVerdict.Changed || ba != TextVerdict.Changed) wrong.Add($"{a.File} / {b.File} other page: {ab}/{ba} ({d:F4})");
                }
            }
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.True(same >= minSame && changed >= minChanged, $"only {same} same-page and {changed} page-change pairs");
        Assert.True(compared >= same + changed, $"only {compared} button pairs compared quick vs full");
        // keep the measured margin: same page well under SameMax, page changes well over ChangedMin
        Assert.True(maxSame <= TextInk.SameMax / 2, $"same page up to {maxSame:F4}");
        Assert.True(minChange >= 4 * TextInk.ChangedMin, $"page changes down to {minChange:F4}");
    }
}
