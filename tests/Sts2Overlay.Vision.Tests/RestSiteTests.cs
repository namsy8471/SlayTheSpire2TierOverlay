using Sts2Overlay.Vision.Rest;

namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// Rest-site recognition. Most tests paint synthetic "icons" (each 4x3 cell filled with the signature colour of an
/// option) so they run anywhere without game images; <see cref="RestSiteRealFrameTests"/> replays the labelled
/// real frames when they exist on this PC.
/// </summary>
public class RestSiteTests
{
    // ---------- layout ----------

    [Fact]
    public void Two_buttons_sit_symmetrically_around_the_screen_centre()
    {
        RectF heal = RestSiteLayout.Slot(0, 2), smith = RestSiteLayout.Slot(1, 2);
        Assert.Equal(RestSiteLayout.RowCenterX - heal.CenterX, smith.CenterX - RestSiteLayout.RowCenterX, 3);
        Assert.Equal(RestSiteLayout.Pitch, smith.X - heal.X, 3);
        // Measured on 24 real screenshots (visible pictures): Heal ~673,265 and Smith ~1019,265, about 229x149.
        RectF hi = RestSiteLayout.Icon(heal), si = RestSiteLayout.Icon(smith);
        Assert.InRange(hi.X, 670, 676);
        Assert.InRange(si.X, 1016, 1022);
        Assert.InRange(hi.Y, 261, 267);
        Assert.InRange(hi.Width, 226, 233);
        Assert.InRange(hi.Height, 148, 155);
    }

    [Fact]
    public void Rows_of_n_and_n_plus_2_buttons_share_slots()
    {
        // Centred HBox with a constant pitch: the two middle slots of 4 buttons are the slots of 2 buttons.
        Assert.Equal(RestSiteLayout.Slot(0, 2).X, RestSiteLayout.Slot(1, 4).X, 3);
        Assert.Equal(RestSiteLayout.Slot(1, 2).X, RestSiteLayout.Slot(2, 4).X, 3);
        Assert.Equal(RestSiteLayout.Slot(1, 3).X, RestSiteLayout.Slot(0, 1).X, 3);
        Assert.Equal(RestSiteLayout.RowCenterX, RestSiteLayout.Slot(1, 3).CenterX, 3);
    }

    [Fact]
    public void Hover_scale_grows_the_icon_around_the_pivot()
    {
        RectF slot = RestSiteLayout.Slot(0, 2);
        RectF idle = RestSiteLayout.Icon(slot), hover = RestSiteLayout.Icon(slot, 1.1f);
        Assert.Equal(idle.Width * 1.1f, hover.Width, 3);
        Assert.True(hover.X < idle.X && hover.Right > idle.Right && hover.Y < idle.Y);
    }

    // ---------- detection on synthetic frames ----------

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3842, 2160)] // the user's 4K window
    [InlineData(1920, 1079)] // the overlay's downscaled captures
    public void Detects_heal_and_smith_with_their_rectangles(int w, int h)
    {
        var c = Dark(w, h);
        PaintRow(c, (RestOption.Heal, 1f, false), (RestOption.Smith, 1f, false));
        RestSiteResult r = RestSiteDetector.Detect(c.Frame);

        Assert.True(r.ChoiceScreen, r.Detail);
        Assert.False(r.CardGridOpen);
        Assert.Equal([RestOption.Heal, RestOption.Smith], r.Buttons.Select(b => b.Option));
        for (int i = 0; i < 2; i++)
        {
            Assert.True(RestSiteTruth.EdgeError(r.Buttons[i].Rect, RestSiteLayout.Icon(RestSiteLayout.Slot(i, 2))) < 1, r.Detail);
            Assert.Equal(RestButtonState.Normal, r.Buttons[i].State);
            Assert.True(r.Buttons[i].Enabled);
        }
        Assert.Equal(RestOption.Smith, r.Find(RestOption.Smith)?.Option);
    }

    [Fact]
    public void Three_buttons_in_coop_are_found_at_their_own_positions()
    {
        var c = Dark(1920, 1080);
        PaintRow(c, (RestOption.Heal, 1f, false), (RestOption.Smith, 1f, false), (RestOption.Mend, 1f, false));
        RestSiteResult r = RestSiteDetector.Detect(c.Frame);
        Assert.True(r.ChoiceScreen, r.Detail);
        Assert.Equal([RestOption.Heal, RestOption.Smith, RestOption.Mend], r.Buttons.Select(b => b.Option));
        Assert.Equal(RestSiteLayout.RowCenterX, r.Buttons[1].Slot.CenterX, 3);
    }

    [Fact]
    public void Relic_options_beyond_the_usual_two_are_identified()
    {
        var c = Dark(1920, 1080);
        PaintRow(c, (RestOption.Heal, 1f, false), (RestOption.Smith, 1f, false), (RestOption.Lift, 1f, false), (RestOption.Dig, 1f, false));
        RestSiteResult r = RestSiteDetector.Detect(c.Frame);
        Assert.Equal([RestOption.Heal, RestOption.Smith, RestOption.Lift, RestOption.Dig], r.Buttons.Select(b => b.Option));
    }

    [Fact]
    public void A_hovered_button_is_larger_brighter_and_reported_as_hovered()
    {
        var c = Dark(1920, 1080);
        PaintRow(c, (RestOption.Heal, 1.1f, false), (RestOption.Smith, 1f, false));
        RestSiteResult r = RestSiteDetector.Detect(c.Frame);
        Assert.True(r.ChoiceScreen, r.Detail);
        Assert.Equal(RestButtonState.Hovered, r.Buttons[0].State);
        Assert.True(RestSiteTruth.EdgeError(r.Buttons[0].Rect, RestSiteLayout.Icon(RestSiteLayout.Slot(0, 2), 1.1f)) < 1);
        Assert.Equal(RestButtonState.Normal, r.Buttons[1].State);
    }

    [Fact]
    public void A_greyed_out_smith_next_to_heal_is_reported_disabled()
    {
        var c = Dark(1920, 1080);
        PaintRow(c, (RestOption.Heal, 1f, false), (RestOption.Smith, 1f, true));
        RestSiteResult r = RestSiteDetector.Detect(c.Frame);
        Assert.True(r.ChoiceScreen, r.Detail);
        Assert.True(r.Buttons[0].Enabled);
        Assert.False(r.Buttons[1].Enabled);
        Assert.Equal(RestOption.Smith, r.Buttons[1].Option);
    }

    [Fact]
    public void Grey_icons_alone_never_start_a_detection()
    {
        // A grey patch can look like any icon's luma pattern; only a coloured match anchors the row.
        var c = Dark(1920, 1080);
        PaintRow(c, (RestOption.Heal, 1f, true), (RestOption.Smith, 1f, true));
        Assert.False(RestSiteDetector.Detect(c.Frame).ChoiceScreen);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.5f)] // mid fade-in: reported as nothing rather than as a button the player cannot click yet
    public void Black_or_half_faded_screens_are_not_choice_screens(float fade)
    {
        var c = Dark(1920, 1080);
        if (fade > 0) PaintRow(c, fade, (RestOption.Heal, 1f, false), (RestOption.Smith, 1f, false));
        Assert.False(RestSiteDetector.Detect(c.Frame).ChoiceScreen);
    }

    [Fact]
    public void A_uniform_bright_screen_is_not_a_choice_screen()
    {
        var c = new Canvas(1920, 1080);
        c.FillRef(new RectF(0, 0, 1920, 1080), 170, 160, 140); // parchment-like map background
        Assert.False(RestSiteDetector.Detect(c.Frame).ChoiceScreen);
    }

    [Fact]
    public void Fit_clamps_the_brightness_factor()
    {
        float[] sig = [100, 50, 20, 200, 120, 60];
        float[] half = sig.Select(v => v * 0.5f).ToArray();
        (float r1, float g1) = RestSiteDetector.Fit(sig, sig, 0.75f, 1.3f);
        Assert.Equal(0, r1, 3);
        Assert.Equal(1, g1, 3);
        (float r2, float g2) = RestSiteDetector.Fit(half, sig, 0.75f, 1.3f);
        Assert.Equal(0.75f, g2, 3);
        Assert.True(r2 > 10); // a half-dark copy cannot hide behind the gain
    }

    // ---------- signatures ----------

    [Fact]
    public void Signature_of_an_image_is_its_cell_means_and_hover_clips_per_pixel()
    {
        // Left half 100, right half 250: cells of columns 0-1 are 100, columns 2-3 are 250.
        var icon = new Canvas(256, 169);
        icon.FillPixels(new RectF(0, 0, 128, 169), 100, 100, 100);
        icon.FillPixels(new RectF(128, 0, 128, 169), 250, 250, 250);
        float[] normal = RestIconSignature.FromImage(icon.Frame);
        float[] bright = RestIconSignature.FromImage(icon.Frame, 1.2f);
        Assert.Equal(100, normal[0], 1);
        Assert.Equal(250, normal[3 * 3], 1);
        Assert.Equal(120, bright[0], 1);
        Assert.Equal(255, bright[3 * 3], 1); // 250 * 1.2 clipped
        float[] grey = RestIconSignature.Disabled([100, 200, 50]);
        Assert.Equal(0.6f * (0.2989f * 100 + 0.587f * 200 + 0.114f * 50), grey[0], 2);
        Assert.Equal(grey[0], grey[2]);
    }

    [Fact]
    public void Committed_signatures_cover_every_option_and_are_distinct()
    {
        var options = RestIconSignatures.All.Select(s => s.Option).ToList();
        Assert.Equal(Enum.GetValues<RestOption>().Where(o => o != RestOption.Unknown).Order(), options.Order());
        Assert.All(RestIconSignatures.All, s => Assert.Equal(RestIconSignature.Length, s.Normal.Length));
        // Every pair of icons differs clearly even after the best brightness fit (identity margin on real frames >= 27).
        foreach (var a in RestIconSignatures.All)
            foreach (var b in RestIconSignatures.All.Where(b => b.Option != a.Option))
            {
                (float r, _) = RestSiteDetector.Fit(a.Normal.Select(v => (float)v).ToArray(), b.Normal.Select(v => (float)v).ToArray(), 0.75f, 1.3f);
                Assert.True(r > 2 * RestSiteDetector.MaxResidual, $"{a.Option} vs {b.Option}: {r:F1}");
            }
    }

    // ---------- card grid ----------

    [Fact]
    public void Upgrade_grid_tickbox_on_black_backdrop_is_an_open_grid()
    {
        var c = Dark(3842, 2160);
        PaintTickbox(c);
        Assert.True(CardGridProbe.IsOpen(c.Frame, out string d), d);
        Assert.False(RestSiteDetector.Detect(c.Frame).ChoiceScreen);
    }

    [Fact]
    public void Tickbox_without_the_black_margins_is_not_a_grid()
    {
        var c = Dark(1920, 1080);
        PaintTickbox(c);
        c.FillRef(new RectF(1650, 100, 200, 900), 120, 110, 90); // something bright where the grid is always black
        Assert.False(CardGridProbe.IsOpen(c.Frame));
    }

    [Fact]
    public void A_bright_square_without_dark_centre_is_not_a_tickbox()
    {
        // The in-combat draw pile icon sits in the same corner but is bright all over.
        var c = Dark(1920, 1080);
        c.FillRef(new RectF(25, 1015, 40, 40), 190, 190, 185);
        Assert.False(CardGridProbe.IsOpen(c.Frame));
    }

    // ---------- truth file ----------

    [Fact]
    public void Truth_lines_parse_and_check()
    {
        var t = RestSiteTruth.Parse([
            "# comment",
            "a.jpg choice Heal@673,265,228,149 Smith@1019,265,229,149",
            "b.jpg grid",
            "c.jpg transition fading in",
            "d.jpg none",
        ]);
        Assert.Equal([RestTruthLabel.Choice, RestTruthLabel.Grid, RestTruthLabel.Transition, RestTruthLabel.None], t.Select(e => e.Label));
        Assert.Equal("fading in", t[2].Note);

        var heal = new RestButton(RestOption.Heal, 0, new RectF(672, 263, 230, 152), RestSiteLayout.Slot(0, 2), RestButtonState.Normal, true, 1, 30);
        var smith = heal with { Option = RestOption.Smith, Index = 1, Rect = new RectF(1019, 263, 230, 152) };
        Assert.Null(RestSiteTruth.Check(t[0], new RestSiteResult(true, [heal, smith], false, "")));
        Assert.NotNull(RestSiteTruth.Check(t[0], new RestSiteResult(true, [smith with { Index = 0 }, heal], false, "")));
        Assert.NotNull(RestSiteTruth.Check(t[0], new RestSiteResult(true, [heal with { Rect = new RectF(650, 263, 230, 152) }, smith], false, "")));
        Assert.NotNull(RestSiteTruth.Check(t[3], new RestSiteResult(true, [heal, smith], false, "")));
        Assert.Null(RestSiteTruth.Check(t[1], RestSiteResult.None(true, "")));
        Assert.Throws<FormatException>(() => RestSiteTruth.Parse(["x.jpg transition"]));
    }

    // ---------- helpers ----------

    private static Canvas Dark(int w, int h)
    {
        var c = new Canvas(w, h);
        c.FillRef(new RectF(0, 0, 1920, 1080), 12, 14, 18);
        return c;
    }

    private static void PaintRow(Canvas c, params (RestOption Option, float Scale, bool Disabled)[] row) => PaintRow(c, 1f, row);

    /// <summary>Paints each slot's icon as 4x3 flat cells in its signature colours, times <paramref name="fade"/>.</summary>
    private static void PaintRow(Canvas c, float fade, params (RestOption Option, float Scale, bool Disabled)[] row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            var sig = RestIconSignatures.All.Single(s => s.Option == row[i].Option);
            float[] colours = row[i].Disabled
                ? RestIconSignature.Disabled(sig.Normal.Select(v => (float)v).ToArray())
                : (row[i].Scale > 1.05f ? sig.Bright : sig.Normal).Select(v => (float)v).ToArray();
            RectF icon = RestSiteLayout.Icon(RestSiteLayout.Slot(i, row.Length), row[i].Scale);
            // Frame colour = mean of the cells, so the trimmed border is not black.
            c.FillRef(icon, (byte)(colours.Where((_, k) => k % 3 == 0).Average() * fade), (byte)(colours.Where((_, k) => k % 3 == 1).Average() * fade), (byte)(colours.Where((_, k) => k % 3 == 2).Average() * fade));
            for (int y = 0; y < RestSiteLayout.CellRows; y++)
                for (int x = 0; x < RestSiteLayout.CellColumns; x++)
                {
                    int o = (y * RestSiteLayout.CellColumns + x) * 3;
                    c.FillRef(RestSiteLayout.Cell(icon, x, y), (byte)(colours[o] * fade), (byte)(colours[o + 1] * fade), (byte)(colours[o + 2] * fade));
                }
        }
    }

    /// <summary>The "View Upgrades" tickbox: light ring, slate centre (colours measured on real captures).</summary>
    private static void PaintTickbox(Canvas c)
    {
        c.FillRef(new RectF(0, 0, 1920, 1080), 0, 0, 0);
        c.FillRef(new RectF(28, 1018, 34, 36), 193, 192, 185);
        c.FillRef(new RectF(34, 1025, 22, 22), 47, 72, 83);
    }
}

/// <summary>
/// Replays tests/fixtures/rest-site-truth.txt on the real frames, when they are on this PC: the overlay's captures
/// (%LOCALAPPDATA%\Sts2Overlay\captures) and the frame collector's JPEGs (%LOCALAPPDATA%\Sts2Overlay\frames, or the
/// folder named by STS2_FRAMES_DIR). Skipped when none of the labelled files exist.
/// </summary>
public class RestSiteRealFrameTests
{
    private static string TruthFile => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "rest-site-truth.txt");

    internal static IEnumerable<string> FrameDirs()
    {
        yield return TestPaths.CapturesDir;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay", "frames");
        if (Environment.GetEnvironmentVariable("STS2_FRAMES_DIR") is { Length: > 0 } extra) yield return extra;
    }

    [RestFramesFact]
    public void Every_labelled_frame_that_exists_is_recognised_correctly()
    {
        var truth = RestSiteTruth.Parse(File.ReadAllLines(TruthFile));
        string[] dirs = FrameDirs().Where(Directory.Exists).ToArray();
        int found = 0, choice = 0;
        var failures = new List<string>();
        foreach (RestTruthEntry t in truth)
        {
            string? path = dirs.Select(d => Path.Combine(d, t.File)).FirstOrDefault(File.Exists);
            if (path is null) continue;
            found++;
            if (t.Label == RestTruthLabel.Choice) choice++;
            RestSiteResult r = RestSiteDetector.Detect(ImageLoader.Load(path));
            if (RestSiteTruth.Check(t, r) is { } why) failures.Add($"{t.File}: {why} | {r.Detail}");
        }
        Assert.True(found > 0);
        Assert.True(failures.Count == 0, $"{failures.Count} of {found} frames wrong ({choice} choice screens):\n" + string.Join('\n', failures));
    }
}

/// <summary>Skips when none of the frame folders exist (fresh clone, CI).</summary>
internal sealed class RestFramesFactAttribute : FactAttribute
{
    public RestFramesFactAttribute()
    {
        if (!RestSiteRealFrameTests.FrameDirs().Any(Directory.Exists))
            Skip = "needs real frames (%LOCALAPPDATA%\\Sts2Overlay\\captures or \\frames)";
    }
}
