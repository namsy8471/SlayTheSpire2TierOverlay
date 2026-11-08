using Sts2Overlay.Vision.Events;

namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// <see cref="EventButtonDetector"/>: synthetic screens (always run) and every capture on the developer's machine
/// (skipped elsewhere). The capture check is all-or-nothing: each labelled event frame must give its exact button
/// count and locked buttons, and every other capture up to <see cref="LabelledUntil"/> must give none.
/// </summary>
public class EventButtonTests
{
    private static readonly (byte R, byte G, byte B) Idle = (37, 69, 74), Hover = (48, 89, 96), Locked = (46, 41, 41);

    private static void Button(Canvas c, int i, (byte R, byte G, byte B) fill, bool text = true, bool glow = false)
    {
        float top = EventButtonDetector.RefTop + EventButtonDetector.RefStep * i;
        c.FillRef(new RectF(EventButtonDetector.RefLeft, top, EventButtonDetector.RefWidth, EventButtonDetector.RefHeight), fill.R, fill.G, fill.B);
        if (text) c.FillRef(new RectF(1000 + 40 * i, top + 20, 300, 22), 240, 230, 200); // a "title" whose place differs per button
        if (glow) c.FillRef(new RectF(EventButtonDetector.RefLeft, top - 8, EventButtonDetector.RefWidth, 7), 60, 130, 140);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3842, 2160)]
    [InlineData(1920, 1079)]
    public void Counts_buttons_from_the_top_and_reads_hover_and_lock(int w, int h)
    {
        var c = new Canvas(w, h);
        c.FillRef(new RectF(0, 0, 1920, 1080), 28, 30, 42);
        Button(c, 0, Idle);
        Button(c, 1, Hover, glow: true);
        Button(c, 2, Locked);
        Button(c, 3, Hover); // bright fill without the glow: the mouse just left, still fading
        var s = EventButtonDetector.Detect(c.Frame);
        Assert.NotNull(s);
        Assert.Equal([EventButtonState.Idle, EventButtonState.Hovered, EventButtonState.Locked, EventButtonState.Idle], s.Buttons.Select(b => b.State));
        Assert.Equal(1, s.HoveredIndex);
        Assert.InRange(s.Buttons[0].Bounds.X, 930 * w / 1920 - 2, 930 * w / 1920 + 2);
    }

    [Fact]
    public void A_gap_ends_the_column_and_no_button_means_no_screen()
    {
        var c = new Canvas(1920, 1080);
        Button(c, 1, Idle); // slot 0 empty: not an event screen
        Assert.Null(EventButtonDetector.Detect(c.Frame));
    }

    [Fact]
    public void The_shop_rug_teal_is_not_a_button()
    {
        var c = new Canvas(1920, 1080);
        c.FillRef(new RectF(0, 0, 1920, 1080), 55, 84, 74); // green above blue
        Assert.Null(EventButtonDetector.Detect(c.Frame));
    }

    [Fact]
    public void Text_signature_tells_pages_apart()
    {
        var a = new Canvas(1920, 1080);
        Button(a, 0, Idle);
        var b = new Canvas(1920, 1080);
        b.FillRef(new RectF(EventButtonDetector.RefLeft, EventButtonDetector.RefTop, EventButtonDetector.RefWidth, EventButtonDetector.RefHeight), Idle.R, Idle.G, Idle.B);
        b.FillRef(new RectF(1400, EventButtonDetector.RefTop + 20, 200, 22), 240, 230, 200);
        var sa = EventButtonDetector.Detect(a.Frame)!.Buttons[0].TextSignature;
        var sb = EventButtonDetector.Detect(b.Frame)!.Buttons[0].TextSignature;
        Assert.Equal(0, EventButtonDetector.SignatureDistance(sa, sa));
        Assert.True(EventButtonDetector.SignatureDistance(sa, sb) > 0.02f);
    }

    /// <summary>Captures newer than this were taken after the labels were made and are only reported.</summary>
    private const string LabelledUntil = "20261108_235300";

    private sealed record Label(int Count, string Event, int[]? Locked = null);

    /// <summary>Hand-checked event frames (file name without .png → buttons on screen). Everything else: none.</summary>
    private static readonly Dictionary<string, Label> Labels = new()
    {
        ["reward_20261108_202000_430"] = new(3, "SELF_HELP_BOOK"),
        ["reward_20261108_202002_570"] = new(3, "SELF_HELP_BOOK"),
        ["reward_20261108_202008_953"] = new(3, "SELF_HELP_BOOK"),
        ["reward_20261108_202413_215"] = new(2, "SPIRIT_GRAFTER"),
        ["reward_20261108_202541_067"] = new(2, "COLOSSAL_FLOWER"),
        ["reward_20261108_203748_354"] = new(2, "THIS_OR_THAT"),
        ["reward_20261108_203750_501"] = new(2, "THIS_OR_THAT"),
        ["reward_20261108_203752_640"] = new(1, "THIS_OR_THAT"),
        ["reward_20261108_204838_206"] = new(2, "HUNGRY_FOR_MUSHROOMS"),
        ["reward_20261108_204840_361"] = new(2, "HUNGRY_FOR_MUSHROOMS"),
        ["reward_20261108_204842_509"] = new(1, "HUNGRY_FOR_MUSHROOMS"),
        ["reward_20261108_210653_575"] = new(2, "THE_FUTURE_OF_POTIONS"),
        ["reward_20261108_210708_606"] = new(1, "THE_FUTURE_OF_POTIONS"),
        ["reward_20261108_211227_719"] = new(2, "FIELD_OF_MAN_SIZED_HOLES"),
        ["reward_20261108_211709_669"] = new(2, "CRYSTAL_SPHERE"),
        ["reward_20261108_211711_808"] = new(2, "CRYSTAL_SPHERE"),
        ["reward_20261108_211713_975"] = new(2, "CRYSTAL_SPHERE"),
        ["reward_20261108_212603_530"] = new(3, "RANWID_THE_ELDER"),
        ["reward_20261108_212607_766"] = new(3, "RANWID_THE_ELDER"),
        ["reward_20261108_212609_936"] = new(3, "RANWID_THE_ELDER"),
        ["reward_20261108_212612_076"] = new(0, "RANWID_THE_ELDER (fading out)"),
        ["reward_20261108_213237_714"] = new(3, "WOOD_CARVINGS"),
        ["reward_20261108_213239_901"] = new(3, "WOOD_CARVINGS"),
        ["reward_20261108_213246_413"] = new(3, "WOOD_CARVINGS"),
        ["reward_20261108_213248_556"] = new(3, "WOOD_CARVINGS"),
        ["reward_20261108_213339_832"] = new(1, "WHISPERING_HOLLOW"),
        ["reward_20261108_213633_406"] = new(2, "ROOM_FULL_OF_CHEESE"),
        ["reward_20261108_214818_592"] = new(2, "BUGSLAYER"),
        ["reward_20261108_214820_800"] = new(2, "BUGSLAYER"),
        ["reward_20261108_214822_976"] = new(1, "BUGSLAYER"),
        ["reward_20261108_231427_122"] = new(2, "BRAIN_LEECH"),
        ["reward_20261108_231652_522"] = new(2, "SPIRALING_WHIRLPOOL"),
        ["reward_20261108_231656_741"] = new(1, "SPIRALING_WHIRLPOOL"),
        ["reward_20261108_232735_414"] = new(2, "SLIPPERY_BRIDGE"),
        ["reward_20261108_232737_551"] = new(2, "SLIPPERY_BRIDGE"),
        ["reward_20261108_233706_885"] = new(3, "SELF_HELP_BOOK", [2]),
        ["reward_20261108_233713_230"] = new(3, "SELF_HELP_BOOK", [2]),
        ["reward_20261108_233717_546"] = new(1, "SELF_HELP_BOOK"),
        ["reward_20261108_234054_574"] = new(2, "ROOM_FULL_OF_CHEESE"),
        ["reward_20261108_235224_551"] = new(2, "INFESTED_AUTOMATON"),
        ["reward_20261108_235226_691"] = new(2, "INFESTED_AUTOMATON"),
        ["reward_20261108_235228_851"] = new(1, "INFESTED_AUTOMATON"),
        ["shop_20261108_210150_500"] = new(2, "THIS_OR_THAT"),
        ["shop_20261108_210152_665"] = new(1, "THIS_OR_THAT"),
        ["shop_20261108_214940_631"] = new(2, "SLIPPERY_BRIDGE"),
        ["shop_20261108_214942_761"] = new(2, "SLIPPERY_BRIDGE"),
        ["shop_20261108_231159_732"] = new(2, "TRASH_HEAP"),
        ["shop_20261108_232409_757"] = new(3, "RANWID_THE_ELDER"),
        ["shop_20261108_232413_970"] = new(3, "RANWID_THE_ELDER"),
        ["shop_20261108_232416_112"] = new(3, "RANWID_THE_ELDER"),
        ["shop_20261108_232418_280"] = new(1, "RANWID_THE_ELDER"),
    };

    [EventCapturesFact]
    public void Every_capture_gives_its_labelled_buttons()
    {
        var files = Directory.GetFiles(TestPaths.CapturesDir, "*.png")
            .Where(f => string.CompareOrdinal(Stamp(f), LabelledUntil) <= 0)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var wrong = new List<string>();
        int events = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var want = Labels.GetValueOrDefault(name) ?? new Label(0, "-");
            if (want.Count > 0) events++;
            var got = EventButtonDetector.Detect(ImageLoader.Load(file));
            var count = got?.Buttons.Count ?? 0;
            var locked = got?.Buttons.Where(b => b.State == EventButtonState.Locked).Select(b => b.Index).ToArray() ?? [];
            if (count != want.Count || !locked.SequenceEqual(want.Locked ?? []))
                wrong.Add($"{name}: {count} buttons (locked {string.Join(",", locked)}), want {want.Count} ({want.Event})");
            else if (got is not null && got.Buttons.Count(b => b.State == EventButtonState.Hovered) > 1)
                wrong.Add($"{name}: more than one hovered button");
        }
        Assert.True(events >= 40, $"only {events} labelled event frames found");
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {files.Count} captures wrong:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    private static string Stamp(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var i = name.IndexOf('_');
        return i < 0 ? name : name[(i + 1)..];
    }
}

/// <summary>Skips unless the developer's captures folder holds the labelled event frames.</summary>
internal sealed class EventCapturesFactAttribute : FactAttribute
{
    public EventCapturesFactAttribute()
    {
        if (!File.Exists(Path.Combine(TestPaths.CapturesDir, "reward_20261108_202008_953.png")))
            Skip = $"needs the event captures in {TestPaths.CapturesDir}";
    }
}
