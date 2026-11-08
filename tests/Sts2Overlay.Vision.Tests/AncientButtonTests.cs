using System.Diagnostics;
using Sts2Overlay.Vision.Events;
using Xunit.Abstractions;

namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// <see cref="AncientButtonDetector"/>: labelled real captures of ancient screens (and every other capture: nothing),
/// plus small synthetic frames for the geometry at other resolutions.
/// </summary>
public class AncientButtonTests(ITestOutputHelper output)
{
    private sealed record Label(int Count, int Hovered, string Ancient);

    /// <summary>Captures newer than this were taken after the labels were made and are only reported.</summary>
    private const string LabelledUntil = "20261109_023000";

    /// <summary>
    /// Hand-checked ancient frames (file name without .png → option buttons, hovered index). Count 0: an ancient
    /// screen without options (talking, the column sliding in, the proceed button, a card overlay on top).
    /// Every other capture: no buttons.
    /// </summary>
    private static readonly Dictionary<string, Label> Labels = new()
    {
        ["reward_20261108_202258_583"] = new(0, -1, "OROBAS (column sliding in)"),
        ["reward_20261108_202300_769"] = new(3, 1, "OROBAS"),
        ["reward_20261108_202307_230"] = new(3, 2, "OROBAS"),
        ["reward_20261108_203610_012"] = new(3, 0, "NONUPEIPE"),
        ["reward_20261108_203612_243"] = new(3, 1, "NONUPEIPE"),
        ["reward_20261108_203616_601"] = new(3, 1, "NONUPEIPE"),
        ["reward_20261108_203618_777"] = new(3, 1, "NONUPEIPE"),
        ["reward_20261108_203625_172"] = new(0, -1, "NONUPEIPE (proceed)"),
        ["reward_20261108_211526_681"] = new(3, 0, "TANX"),
        ["reward_20261108_211530_956"] = new(3, 1, "TANX"),
        ["reward_20261108_214250_561"] = new(3, 0, "OROBAS"),
        ["reward_20261108_214254_893"] = new(0, -1, "OROBAS (proceed under a card)"),
        ["reward_20261108_232105_540"] = new(3, 0, "DARV"),
        ["reward_20261108_234637_620"] = new(3, 0, "OROBAS"),
        ["reward_20261108_234639_841"] = new(3, 1, "OROBAS"),
        ["reward_20261108_234642_062"] = new(3, 2, "OROBAS"),
        ["reward_20261108_235825_121"] = new(3, 1, "TANX"),
        ["reward_20261108_235827_307"] = new(3, 2, "TANX"),
        ["reward_20261108_235835_805"] = new(3, 1, "TANX"),
        ["reward_20261109_002133_557"] = new(3, 1, "OROBAS"),
        ["reward_20261109_002135_801"] = new(3, 1, "OROBAS (the last button's ring still fading)"),
        ["reward_20261109_003116_378"] = new(0, -1, "VAKUU (talking)"),
        ["reward_20261109_003118_588"] = new(0, -1, "VAKUU (talking)"),
        ["reward_20261109_003120_800"] = new(3, 0, "VAKUU"),
        ["reward_20261109_003129_261"] = new(3, 2, "VAKUU"),
        ["reward_20261109_003133_572"] = new(3, 1, "VAKUU"),
        ["reward_20261109_003146_256"] = new(3, 0, "VAKUU"),
        ["reward_20261109_005449_400"] = new(0, -1, "DARV (talking)"),
        ["reward_20261109_005451_588"] = new(3, 1, "DARV"),
        ["reward_20261109_005455_927"] = new(0, -1, "DARV (transformed cards on top)"),
        ["reward_20261109_005458_103"] = new(3, 1, "DARV"),
        ["reward_20261109_005500_309"] = new(0, -1, "DARV (dimmed proceed)"),
        ["reward_20261109_005502_507"] = new(0, -1, "DARV (dimmed proceed)"),
        ["reward_20261109_005504_688"] = new(0, -1, "DARV (proceed)"),
        ["reward_20261109_005513_161"] = new(0, -1, "DARV (leaving to the map)"),
    };

    [EventCapturesFact]
    public void Every_capture_gives_its_labelled_buttons()
    {
        var files = Directory.GetFiles(TestPaths.CapturesDir, "*.png")
            .Where(f => string.CompareOrdinal(Stamp(f), LabelledUntil) <= 0)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var wrong = new List<string>();
        int ancients = 0, labelled = 0;
        var sw = new Stopwatch();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var want = Labels.GetValueOrDefault(name);
            if (want is not null) labelled++;
            want ??= new Label(0, -1, "-");
            if (want.Count > 0) ancients++;
            var frame = ImageLoader.Load(file);
            sw.Start();
            var got = AncientButtonDetector.Detect(frame);
            sw.Stop();
            int count = got?.Buttons.Count ?? 0, hovered = got?.HoveredIndex ?? -1;
            if (count != want.Count || hovered != want.Hovered)
                wrong.Add($"{name}: {count} buttons (hovered {hovered}), want {want.Count} (hovered {want.Hovered}) {want.Ancient}");
            else if (got is not null && got.Buttons.Count(b => b.State == EventButtonState.Hovered) > 1)
                wrong.Add($"{name}: more than one hovered button");
        }
        output.WriteLine($"{files.Count} captures, {ancients} ancient option frames; {sw.Elapsed.TotalMilliseconds / Math.Max(1, files.Count):F3} ms per call");
        Assert.Equal(Labels.Count, labelled);
        Assert.True(ancients >= 24, $"only {ancients} labelled ancient frames found");
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {files.Count} captures wrong:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Slot_tops_follow_the_scene_formula()
    {
        Assert.Equal(750, AncientButtonDetector.SlotTop(3, 0));
        Assert.Equal(942, AncientButtonDetector.SlotTop(3, 2));
        Assert.Equal(942, AncientButtonDetector.SlotTop(1, 0));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1920, 1079)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void Finds_the_column_at_every_count_and_size(int w, int h)
    {
        var t = RefTransform.For(w, h);
        for (int n = 1; n <= AncientButtonDetector.MaxButtons; n++)
        {
            var c = Scene(w, h);
            for (int i = 0; i < n; i++) Option(c, AncientButtonDetector.SlotTop(n, i), titleWidth: 120 + 40 * i);
            var s = AncientButtonDetector.Detect(c.Frame);
            Assert.True(s is not null, $"{n} buttons at {w}x{h}: none found");
            Assert.Equal(n, s.Buttons.Count);
            Assert.Equal(-1, s.HoveredIndex);
            for (int i = 0; i < n; i++)
            {
                var b = s.Buttons[i].Bounds;
                Assert.InRange(b.X, (int)t.ToPixelX(460) - 1, (int)t.ToPixelX(460) + 1);
                Assert.InRange(b.Y, (int)t.ToPixelY(AncientButtonDetector.SlotTop(n, i)) - 1, (int)t.ToPixelY(AncientButtonDetector.SlotTop(n, i)) + 1);
            }
        }
    }

    [Fact]
    public void The_strongest_ring_is_the_hovered_button()
    {
        var c = Scene(1920, 1080);
        for (int i = 0; i < 3; i++) Option(c, AncientButtonDetector.SlotTop(3, i), titleWidth: 150);
        Ring(c, AncientButtonDetector.SlotTop(3, 1), (90, 220, 220));
        Ring(c, AncientButtonDetector.SlotTop(3, 2), (60, 150, 150)); // fading out
        var s = AncientButtonDetector.Detect(c.Frame);
        Assert.NotNull(s);
        Assert.Equal(1, s.HoveredIndex);
        Assert.Single(s.Buttons, b => b.State == EventButtonState.Hovered);
    }

    [Fact]
    public void Text_off_the_title_column_or_on_a_busy_picture_is_not_an_option()
    {
        // The dialogue bubble's line starts further in.
        var a = Scene(1920, 1080);
        Option(a, 942, titleWidth: 150, titleX: 600);
        Assert.Null(AncientButtonDetector.Detect(a.Frame));

        // Gold reaching left past the text column (a card frame, art): not a title.
        var b = Scene(1920, 1080);
        Option(b, 942, titleWidth: 150);
        b.FillRef(new RectF(530, 942 + 25, 20, 14), 232, 190, 80);
        Assert.Null(AncientButtonDetector.Detect(b.Frame));

        // A title at the right x over a busy, colourful picture (a card hand): no.
        var c = Scene(1920, 1080);
        Option(c, 942, titleWidth: 150);
        for (int x = 700; x < 1400; x += 40) c.FillRef(new RectF(x, 942 + 10, 20, 80), 200, 40, 30);
        Assert.Null(AncientButtonDetector.Detect(c.Frame));

        // The proceed button: cream text, no gold title.
        var d = Scene(1920, 1080);
        d.FillRef(new RectF(460, 942, 1000, 100), 28, 38, 46);
        d.FillRef(new RectF(488, 942 + 38, 60, 22), 255, 246, 226);
        Assert.Null(AncientButtonDetector.Detect(d.Frame));
    }

    /// <summary>A dark painted backdrop (the ancient scenes are dark, sometimes very).</summary>
    private static Canvas Scene(int w, int h)
    {
        var c = new Canvas(w, h);
        c.FillRef(new RectF(0, 0, 1920, 1080), 44, 70, 76);
        return c;
    }

    /// <summary>One option: the darker panel, relic icon, gold title (from <paramref name="titleX"/>) and cream description.</summary>
    private static void Option(Canvas c, float top, int titleWidth, float titleX = 552)
    {
        c.FillRef(new RectF(460, top, 1000, 100), 28, 38, 46);
        c.FillRef(new RectF(482, top + 20, 60, 60), 120, 90, 160);
        for (int x = 0; x < titleWidth; x += 12) c.FillRef(new RectF(titleX + x, top + 24, 8, 16), 239, 200, 81);
        for (int x = 0; x < 520; x += 10) c.FillRef(new RectF(552 + x, top + 54, 7, 14), 255, 246, 226);
    }

    /// <summary>The hover outline's top line (additive cyan, here just painted).</summary>
    private static void Ring(Canvas c, float top, (byte R, byte G, byte B) color) =>
        c.FillRef(new RectF(449, top + 5, 1033, 3), color.R, color.G, color.B);

    private static string Stamp(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var i = name.IndexOf('_');
        return i < 0 ? name : name[(i + 1)..];
    }
}
