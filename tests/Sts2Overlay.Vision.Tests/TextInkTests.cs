using Sts2Overlay.Vision.Events;

namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// <see cref="TextInk"/>: the per-pixel text comparison that tells a page change from the same page drawn a little
/// differently (hover scale, a column read a few pixels off, a fade, a tooltip).
/// Synthetic "text" here is made of made-up glyphs (a 5x7 dot pattern per character, 2 px dots 4 px
/// apart, about as thin and as far apart as real strokes), so a changed word is a different set of dots, as on screen.
/// </summary>
public class TextInkTests
{
    private const int W = 738, H = 74;           // the event button's text area (EventButtonDetector.InkX0..InkX1, InkY0..InkY1)
    private const float Px = 368, Py = 36;       // its scale pivot

    private const int Pitch = 4, DotSize = 2;

    /// <summary>A 5x7 dot pattern per character, fixed by the character.</summary>
    private static bool Dot(char c, int col, int row)
    {
        uint h = (uint)c * 2654435761u ^ (uint)(col * 7 + row) * 40503u;
        h ^= h >> 13;
        h *= 0x5bd1e995;
        return ((h >> 7) & 1) != 0;
    }

    /// <summary>
    /// Lines of text centred on the pivot, scaled by <paramref name="scale"/> about it and moved by
    /// (<paramref name="dx"/>, <paramref name="dy"/>); <paramref name="keep"/> keeps only some of the ink (a fade).
    /// </summary>
    private static TextInk Text(string[] lines, float scale = 1, int dx = 0, int dy = 0, Func<int, int, bool>? keep = null,
        Func<int, int, bool>? extra = null)
    {
        const int pitch = Pitch, glyph = 6 * pitch, lineH = 7 * pitch + 2;
        var ink = new bool[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (extra?.Invoke(x, y) == true) { ink[y * W + x] = true; continue; }
                // where this pixel was before the scale and the move
                float ux = Px + (x - dx - Px) / scale, uy = Py + (y - dy - Py) / scale;
                int top = (int)(Py - lines.Length * lineH / 2f);
                int li = (int)MathF.Floor((uy - top) / lineH);
                if (li < 0 || li >= lines.Length) continue;
                string line = lines[li];
                int left = (int)(Px - line.Length * glyph / 2f);
                int gi = (int)MathF.Floor((ux - left) / glyph);
                if (gi < 0 || gi >= line.Length || line[gi] == ' ') continue;
                float gx = ux - left - gi * glyph, gy = uy - top - li * lineH;
                int col = (int)MathF.Floor(gx / pitch), row = (int)MathF.Floor(gy / pitch);
                if (col >= 5 || row >= 7 || gx - col * pitch >= DotSize || gy - row * pitch >= DotSize) continue;
                if (Dot(line[gi], col, row) && (keep?.Invoke(x, y) ?? true)) ink[y * W + x] = true;
            }
        return new TextInk(ink, W, H, Px, Py);
    }

    private static readonly string[] Page = ["Walk on", "Pay 7 and get 120 coins."];

    [Theory]
    [InlineData(1.01f, 0, 0)]   // hovered (NEventOptionButton._hoverScale)
    [InlineData(0.99f, 0, 0)]   // pressed
    [InlineData(1.01f, 0, 3)]   // hovered and the column read 3 px lower (a hovered top button moves its fill run)
    [InlineData(1f, -2, -4)]
    public void The_same_text_hovered_pressed_or_read_a_few_pixels_off_is_the_same(float scale, int dx, int dy)
    {
        TextInk a = Text(Page), b = Text(Page, scale, dx, dy);
        Assert.True(TextInk.Distance(a, b) <= TextInk.SameMax, $"distance {TextInk.Distance(a, b):F4}");
        Assert.Equal(TextVerdict.Same, TextInk.Judge(a, b));
        Assert.Equal(TextVerdict.Same, TextInk.Judge(b, a));
    }

    [Theory]
    [InlineData("Turn back", "Pay 7 and get 120 coins.")]   // the title alone changed
    [InlineData("Walk on", "Pay 7 and get 120 cards.")]    // one word of the description
    public void Other_text_is_a_change_even_hovered(string title, string description)
    {
        TextInk a = Text(Page), b = Text([title, description], 1.01f);
        Assert.True(TextInk.Distance(a, b) >= TextInk.ChangedMin, $"distance {TextInk.Distance(a, b):F4}");
        Assert.Equal(TextVerdict.Changed, TextInk.Judge(a, b));
        Assert.Equal(TextVerdict.Changed, TextInk.Judge(b, a));
    }

    [Fact]
    public void Wholly_other_text_is_far()
    {
        TextInk a = Text(Page), b = Text(["Rest", "Sleep until morning."]);
        Assert.True(TextInk.Distance(a, b) > 0.5f);
        Assert.Equal(TextVerdict.Changed, TextInk.Judge(a, b));
    }

    /// <summary>
    /// A fading button keeps part of its text's ink (the dimmest strokes drop under the light threshold first):
    /// still the same text while enough is left, nothing to tell once almost all of it is gone, never a change.
    /// </summary>
    [Theory]
    [InlineData(2, TextVerdict.Same)]     // half the ink left
    [InlineData(20, TextVerdict.Unsure)]  // a twentieth left
    public void A_fading_text_is_the_same_until_too_little_is_left(int every, TextVerdict want)
    {
        TextInk full = Text(Page), faded = Text(Page, keep: (x, y) => (x * 7 + y * 13) % every == 0);
        Assert.Equal(want, TextInk.Judge(full, faded));
        // fading in (the reference was the dim frame): the same text
        Assert.Equal(TextVerdict.Same, TextInk.Judge(faded, full));
    }

    [Fact]
    public void A_blank_button_is_no_evidence_but_text_appearing_on_one_is_the_same()
    {
        TextInk blank = Text([]), text = Text(Page);
        Assert.Equal(0, blank.Count);
        Assert.Equal(TextVerdict.Unsure, TextInk.Judge(text, blank));
        Assert.Equal(TextVerdict.Same, TextInk.Judge(blank, text));
        Assert.Equal(TextVerdict.Same, TextInk.Judge(blank, blank));
    }

    [Fact]
    public void A_light_tooltip_over_part_of_the_text_keeps_it_the_same()
    {
        // the tooltip adds light pixels over the right part
        TextInk full = Text(Page), covered = Text(Page, extra: (x, y) => x >= 500 && x < 720 && y >= 10 && y < 70);
        Assert.Equal(TextVerdict.Same, TextInk.Judge(full, covered));
    }

    /// <summary>
    /// The quick "changed" proof in <see cref="TextInk.Judge"/> must never change a verdict: every pair here, in both
    /// orders, gets what the full search (<see cref="TextInk.JudgeExact"/>) says, page changes included.
    /// </summary>
    [Fact]
    public void Judge_says_what_the_full_search_says_for_every_kind_of_pair()
    {
        string[][] pages =
        [
            Page, ["Turn back", "Pay 7 and get 120 coins."], ["Walk on", "Pay 7 and get 120 cards."],
            ["Rest", "Sleep until morning."], ["Walk on"], [],
        ];
        var inks = new List<TextInk>();
        foreach (var p in pages)
        {
            inks.Add(Text(p));
            inks.Add(Text(p, 1.01f, 1, 3));
            inks.Add(Text(p, 0.99f, -2, -4));
            inks.Add(Text(p, keep: (x, y) => (x * 7 + y * 13) % 2 == 0));
            inks.Add(Text(p, keep: (x, y) => (x * 7 + y * 13) % 20 == 0));
            inks.Add(Text(p, extra: (x, y) => x >= 500 && x < 720 && y >= 10 && y < 70));
        }
        var verdicts = new int[3];
        foreach (var a in inks)
            foreach (var b in inks)
            {
                var want = TextInk.JudgeExact(a, b);
                Assert.Equal(want, TextInk.Judge(a, b));
                verdicts[(int)want]++;
            }
        Assert.All(verdicts, n => Assert.True(n > 0, "every verdict is exercised"));
    }

    [Fact]
    public void Read_takes_the_light_pixels_of_the_area_at_any_resolution()
    {
        foreach (var (w, h) in new[] { (1920, 1080), (3842, 2160) })
        {
            var c = new Canvas(w, h);
            c.FillRef(new RectF(0, 0, 1920, 1080), 37, 69, 74); // button fill: not ink
            c.FillRef(new RectF(1000, 640, 100, 10), 240, 230, 200);
            var ink = TextInk.Read(c.Frame.Bgra.Span, c.Frame, c.T, new RectF(954, 610, 738, 74), 1322, 646);
            Assert.Equal(100 * 10, ink.Count);
            Assert.Equal(1322 - 954, ink.PivotX);
        }
    }

    /// <summary>
    /// Through the detector: a hovered button (drawn 1 % larger with a glow) shows the same ink as idle; a hovered
    /// button with other text does not, which the old idle-only signature check could not see.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3842, 2160)]
    public void Detected_buttons_carry_ink_that_survives_hover_and_sees_new_text(int w, int h)
    {
        EventScreen Draw(bool hover, string title)
        {
            var c = new Canvas(w, h);
            c.FillRef(new RectF(0, 0, 1920, 1080), 28, 30, 42);
            for (int i = 0; i < 2; i++)
            {
                bool hov = hover && i == 0;
                float grow = hov ? 4 : 0, top = EventButtonDetector.RefTop + EventButtonDetector.RefStep * i;
                c.FillRef(new RectF(EventButtonDetector.RefLeft - grow, top - grow, EventButtonDetector.RefWidth + 2 * grow, EventButtonDetector.RefHeight + 2 * grow),
                    (byte)(hov ? 48 : 37), (byte)(hov ? 89 : 69), (byte)(hov ? 96 : 74));
                if (hov) c.FillRef(new RectF(EventButtonDetector.RefLeft, top - grow - 8, EventButtonDetector.RefWidth, 7), 60, 130, 140);
                // the title's glyphs as 2 px dots, scaled 1.01 about the button centre when hovered
                string text = i == 0 ? title : "Walk on";
                float s = hov ? 1.01f : 1, cx = 1322, cy = top + 42;
                for (int g = 0; g < text.Length; g++)
                    for (int col = 0; col < 5; col++)
                        for (int row = 0; row < 7; row++)
                        {
                            if (!Dot(text[g], col, row)) continue;
                            float x = 1100 + g * 6 * Pitch + col * Pitch, y = top + 20 + row * Pitch;
                            c.FillRef(new RectF(cx + (x - cx) * s, cy + (y - cy) * s, DotSize * s, DotSize * s), 240, 230, 200);
                        }
            }
            return EventButtonDetector.Detect(c.Frame)!;
        }
        EventScreen idle = Draw(false, "Open the door"), hovered = Draw(true, "Open the door"), next = Draw(true, "Take the gold");
        Assert.Equal(0, hovered.HoveredIndex);
        Assert.All(idle.Buttons.Concat(hovered.Buttons), b => Assert.True(b.Ink is { Count: > 100 }));
        Assert.Equal(TextVerdict.Same, TextInk.Judge(idle.Buttons[0].Ink!, hovered.Buttons[0].Ink!));
        Assert.Equal(TextVerdict.Changed, TextInk.Judge(hovered.Buttons[0].Ink!, next.Buttons[0].Ink!));
        Assert.Equal(TextVerdict.Same, TextInk.Judge(hovered.Buttons[1].Ink!, next.Buttons[1].Ink!));
    }
}
