using Sts2Overlay.Vision.Map;

namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// <see cref="MapScrollEstimator"/> on synthetic map screens only: a flat parchment between black margins, the game's
/// UI as dark panels over the masked areas, and every node icon as a ~40x40 design px blob drawn at the node's
/// centre shifted by a known scroll.
/// </summary>
public class MapScrollEstimatorTests
{
    private static readonly (byte R, byte G, byte B) Paper = (156, 150, 98);

    /// <summary>Dark icon: mean colour distance from the paper ≈ 100.</summary>
    private static readonly (byte R, byte G, byte B) Ink = (40, 35, 30);

    /// <summary>Washed-out icon (map fading in): mean colour distance from the paper = 45.</summary>
    private static readonly (byte R, byte G, byte B) FadedInk = (111, 105, 53);

    /// <summary>The parchment: x 330..1590 (the margin probe at x 150..300 stays black), full height.</summary>
    private static readonly RectF PaperArea = new(330, 0, 1260, 1080);

    /// <summary>
    /// A layout like a real act map (MapScreenGeometry): 7 columns 150 px apart, rows 1..14 155 px apart going up,
    /// 3..5 nodes per row, each jittered by up to ±21 / ±25 px. Centres at scroll 0.
    /// </summary>
    private static List<MapMark> Layout(int seed = 11)
    {
        var rng = new Random(seed);
        var marks = new List<MapMark>();
        for (int row = 1; row <= 14; row++)
        {
            int count = rng.Next(3, 6);
            foreach (int col in Enumerable.Range(0, 7).OrderBy(_ => rng.Next()).Take(count).Order())
            {
                float jx = (float)(rng.NextDouble() * 42 - 21), jy = (float)(rng.NextDouble() * 50 - 25);
                marks.Add(new MapMark(488 + col * 150 + jx, 1308 - row * 155 + jy));
            }
        }
        return marks;
    }

    /// <summary>Black screen, parchment, the game's UI panels, then one icon blob per point.</summary>
    private static Canvas MapScreen(int width, int height, IEnumerable<(float X, float Y)> icons, (byte R, byte G, byte B) ink)
    {
        var canvas = new Canvas(width, height);
        canvas.FillRef(PaperArea, Paper.R, Paper.G, Paper.B);
        DrawUi(canvas);
        foreach ((float x, float y) in icons) canvas.FillRef(new RectF(x - 20, y - 20, 40, 40), ink.R, ink.G, ink.B);
        return canvas;
    }

    /// <summary>Dark grey panels where the estimator's UI masks are (top bar, relic row, legend, back button, drawing tools).</summary>
    private static void DrawUi(Canvas canvas)
    {
        RectF[] ui =
        [
            new(0, 0, 1920, 90),
            new(0, 90, 360, 70),
            new(1555, 270, 365, 470),
            new(0, 715, 165, 130),
            new(40, 950, 240, 110),
        ];
        foreach (RectF r in ui) canvas.FillRef(r, 50, 52, 60);
    }

    private static IEnumerable<(float X, float Y)> Shifted(IEnumerable<MapMark> marks, float scroll) =>
        marks.Select(m => (m.X, m.Y0 + scroll));

    [Theory]
    [InlineData(1920, 1080, -600f)]
    [InlineData(1920, 1080, -317f)]
    [InlineData(1920, 1080, 0f)]
    [InlineData(1920, 1080, 455.5f)]
    [InlineData(1920, 1080, 1000f)]
    [InlineData(1920, 1080, 1500f)]
    [InlineData(3840, 2160, -600f)]
    [InlineData(3840, 2160, 0f)]
    [InlineData(3840, 2160, 777f)]
    [InlineData(3840, 2160, 1500f)]
    public void Finds_the_scroll_of_a_synthetic_map(int width, int height, float scroll)
    {
        List<MapMark> marks = Layout();
        Canvas canvas = MapScreen(width, height, Shifted(marks, scroll), Ink);

        MapFit fit = new MapScrollEstimator().Estimate(canvas.Frame, marks);

        Assert.True(fit.IsMap, fit.ToString());
        Assert.InRange(fit.Scroll, scroll - 2, scroll + 2);
        Assert.True(fit.Visible >= MapScrollEstimator.MinVisible, fit.ToString());
    }

    [Theory]
    [InlineData(1920, 1080, -600f)]
    [InlineData(1920, 1080, 640f)]
    [InlineData(3840, 2160, 1500f)]
    public void Follows_faded_icons(int width, int height, float scroll)
    {
        List<MapMark> marks = Layout();
        Canvas canvas = MapScreen(width, height, Shifted(marks, scroll), FadedInk);

        MapFit fit = new MapScrollEstimator().Estimate(canvas.Frame, marks);

        Assert.True(fit.IsMap, fit.ToString());
        Assert.InRange(fit.Scroll, scroll - 2, scroll + 2);
    }

    [Fact]
    public void Same_estimator_can_be_reused_across_frames_and_resolutions()
    {
        List<MapMark> marks = Layout(seed: 23);
        var estimator = new MapScrollEstimator();
        foreach ((int w, int h, float scroll) in new[] { (3840, 2160, 300f), (1920, 1080, -450f), (3840, 2160, 1200f) })
        {
            MapFit fit = estimator.Estimate(MapScreen(w, h, Shifted(marks, scroll), Ink).Frame, marks);
            Assert.True(fit.IsMap, $"{w}x{h} @ {scroll}: {fit}");
            Assert.InRange(fit.Scroll, scroll - 2, scroll + 2);
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void A_dark_screen_is_not_the_map(int width, int height)
    {
        var canvas = new Canvas(width, height);
        canvas.FillRef(new RectF(0, 0, 1920, 1080), 30, 28, 35);

        MapFit fit = new MapScrollEstimator().Estimate(canvas.Frame, Layout());

        Assert.False(fit.IsMap, fit.ToString());
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void Empty_parchment_is_not_the_map(int width, int height)
    {
        Canvas canvas = MapScreen(width, height, [], Ink);

        MapFit fit = new MapScrollEstimator().Estimate(canvas.Frame, Layout());

        Assert.False(fit.IsMap, fit.ToString());
    }

    [Theory]
    [InlineData(1920, 1080, 1)]
    [InlineData(1920, 1080, 2)]
    [InlineData(1920, 1080, 3)]
    [InlineData(3840, 2160, 4)]
    public void Icons_at_the_wrong_places_are_not_the_map(int width, int height, int seed)
    {
        // As many icons as a real screen shows, scattered over the parchment with no relation to the layout.
        var rng = new Random(seed);
        var icons = Enumerable.Range(0, 24)
            .Select(_ => ((float)(420 + rng.NextDouble() * 1080), (float)(130 + rng.NextDouble() * 880)))
            .ToList();
        Canvas canvas = MapScreen(width, height, icons, Ink);

        MapFit fit = new MapScrollEstimator().Estimate(canvas.Frame, Layout());

        Assert.False(fit.IsMap, fit.ToString());
    }

    [Fact]
    public void Icons_of_a_different_map_are_not_this_map()
    {
        // The screen shows another act's layout (different jitter and rows); the fit must not lock onto it.
        Canvas canvas = MapScreen(1920, 1080, Shifted(Layout(seed: 99), 400f), Ink);

        MapFit fit = new MapScrollEstimator().Estimate(canvas.Frame, Layout(seed: 11));

        Assert.False(fit.IsMap, fit.ToString());
    }

    [Fact]
    public void Too_few_marks_are_rejected_without_looking()
    {
        Canvas canvas = MapScreen(1920, 1080, [], Ink);
        MapFit fit = new MapScrollEstimator().Estimate(canvas.Frame, Layout().Take(5).ToList());
        Assert.False(fit.IsMap);
        Assert.Contains("too few", fit.Detail);
    }

    [Theory]
    [InlineData(100f, 40f, true)]     // top bar
    [InlineData(960f, 60f, true)]
    [InlineData(1700f, 500f, true)]   // legend
    [InlineData(80f, 780f, true)]     // back button
    [InlineData(150f, 1000f, true)]   // drawing tools
    [InlineData(960f, 540f, false)]   // middle of the parchment
    [InlineData(488f, 300f, false)]   // leftmost column
    [InlineData(1409f, 900f, false)]  // rightmost column with the largest jitter
    public void Ui_masks_cover_the_game_ui_but_not_the_node_columns(float x, float y, bool masked)
        => Assert.Equal(masked, MapScrollEstimator.IsMasked(x, y));
}
