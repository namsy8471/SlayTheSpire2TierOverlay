using System.Text.RegularExpressions;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;
using static Sts2Overlay.Core.Tests.Map.TestMaps;

namespace Sts2Overlay.Core.Tests.Map;

/// <summary>Path reason chips, score labels and map marks in English (<see cref="Loc"/>): no Korean may leak.</summary>
public class PathEnglishTests
{
    private static readonly Regex Hangul = new("[가-힣]", RegexOptions.Compiled);

    private static MapScreenLayout GridLayout(MapGraph map) => new()
    {
        Centers = map.Nodes.Keys.ToDictionary(c => c, c => new DesignPoint(500 + c.Col * 150, 1000 - c.Row * 150)),
        RowSpacing = 150,
        DefaultScroll = 0,
        ActIndex = 0,
        Seed = "TEST",
    };

    private static IEnumerable<string> Texts(PathRecommendation rec)
        => rec.Top.SelectMany(o => o.Chips.Concat(o.Score.Components.Select(c => c.Label)));

    [Fact]
    public void Path_advice_over_the_fixture_and_random_maps_has_no_Korean_in_English()
    {
        var maps = new List<MapGraph> { MapFixture.Load("map_act2.json"), MapFixture.Load("map_act2.json") with { Current = null } };
        maps.AddRange(Enumerable.Range(1, 12).Select(seed => TestMaps.Random(seed)));
        double[] hps = [1.0, 0.75, 0.45, 0.2, 0.05];

        var texts = new List<string>();
        var marks = new List<string>();
        using (Loc.Use(Lang.Eng))
        {
            var advisor = new PathAdvisor(Pack);
            foreach (var map in maps)
            {
                foreach (var hp in hps)
                {
                    foreach (var gold in new[] { 0, 300 })
                    {
                        var rec = advisor.Recommend(State(hp, gold: gold, act: map.Act), map);
                        texts.AddRange(Texts(rec));
                        if (rec.IsEmpty) continue;
                        texts.AddRange(advisor.ScorePath(State(hp), map, rec.Top[0].Coords).Score.Components.Select(c => c.Label));

                        var odds = new UnknownRoomOdds(0.1, 0.05, 0.03, 0.02);
                        var plan = MapMarkLayout.Build(map, GridLayout(map), scroll: 0, rec, [rec.Top[0].Coords[0], rec.Top[0].Coords[0]], odds);
                        marks.AddRange(plan.Nodes.Select(n => n.Caption).OfType<string>());
                    }
                }
            }
        }

        Assert.NotEmpty(texts);
        Assert.NotEmpty(marks);
        var korean = texts.Concat(marks).Where(t => Hangul.IsMatch(t)).Distinct().ToList();
        Assert.True(korean.Count == 0, "Korean in English path texts: " + string.Join(" | ", korean.Take(10)));
        Assert.Contains(texts, t => t.StartsWith("Est. HP at Boss ", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Rest Sites ", StringComparison.Ordinal) || t == "No Rest Sites");
        Assert.Contains("Best!", marks);
        Assert.Contains("Ally ×2", marks);
    }

    [Fact]
    public void Chips_switch_language_with_the_same_numbers()
    {
        var map = new MapBuilder(3, 1, (0, 1), (1, 1))
            .Add(0, 1, MapNodeType.Elite, (0, 2))
            .Add(1, 1, MapNodeType.Monster, (0, 2))
            .Add(0, 2, MapNodeType.RestSite, (0, 3))
            .Build();
        var advisor = new PathAdvisor(Pack);

        var kor = advisor.Recommend(State(1.0), map);
        PathRecommendation eng;
        using (Loc.Use(Lang.Eng)) eng = advisor.Recommend(State(1.0), map);

        Assert.Contains("엘리트 1", kor.Top[0].Chips);
        Assert.Contains("휴식처 1 (보스 전 휴식 있음)", kor.Top[0].Chips);
        Assert.Contains("Elites 1", eng.Top[0].Chips);
        Assert.Contains("Rest Sites 1 (Rest before Boss)", eng.Top[0].Chips);
        Assert.Contains("HP to spare: Elites for Relics", eng.Top[0].Chips);
        Assert.Equal(kor.Top[0].Total, eng.Top[0].Total);
        Assert.Equal(kor.Top[0].Score.Components.Select(c => c.Key), eng.Top[0].Score.Components.Select(c => c.Key));
        Assert.Contains(eng.Top[0].Score.Components, c => c.Label == "Elite");
    }

    [Fact]
    public void Unknown_room_odds_in_English()
    {
        using var _ = Loc.Use(Lang.Eng);
        Assert.Equal("Fight 10% · Shop 3% · Treasure 2% · Event 85%", MapMarkLayout.OddsText(new UnknownRoomOdds(0.1, -1, 0.03, 0.02)));
    }
}
