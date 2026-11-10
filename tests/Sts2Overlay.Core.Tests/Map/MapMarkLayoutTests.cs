using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;
using Xunit;

namespace Sts2Overlay.Core.Tests.Map;

/// <summary>
/// <see cref="MapMarkLayout"/>: the marks drawn on the game's map. A simple stand-in layout (columns 150 px apart,
/// rows 150 px apart, row 0 at the bottom) keeps the expected positions easy to read; the real layout comes from
/// MapScreenGeometry and is tested in GameState.
/// </summary>
public class MapMarkLayoutTests
{
    private static MapScreenLayout GridLayout(MapGraph map) => new()
    {
        Centers = map.Nodes.Keys.ToDictionary(c => c, c => new DesignPoint(500 + c.Col * 150, 1000 - c.Row * 150)),
        RowSpacing = 150,
        DefaultScroll = 0,
        ActIndex = 0,
        Seed = "TEST",
    };

    private static (MapGraph Map, MapScreenLayout Layout, PathRecommendation Rec) Setup(int seed = 3)
    {
        var map = TestMaps.Random(seed, height: 6);
        var rec = new PathAdvisor(TestMaps.Pack).Recommend(TestMaps.State(), map);
        Assert.False(rec.IsEmpty);
        return (map, GridLayout(map), rec);
    }

    [Fact]
    public void Recommended_mark_sits_on_the_next_step_node_with_the_scroll_applied()
    {
        var (map, layout, rec) = Setup();
        var plan = MapMarkLayout.Build(map, layout, scroll: 100, rec, [], null);

        var mark = Assert.Single(plan.Nodes, n => n.Kind == MapNodeMarkKind.Recommended);
        Assert.Equal(rec.NextStep, mark.Coord);
        Assert.Equal(layout.Centers[mark.Coord].X, mark.X);
        Assert.Equal(layout.Centers[mark.Coord].Y + 100, mark.Y);
        Assert.Equal("추천!", mark.Caption);
    }

    [Fact]
    public void Recommended_mark_is_left_out_when_its_node_is_scrolled_off_screen()
    {
        var (map, layout, rec) = Setup();
        // Push everything far below the screen.
        var plan = MapMarkLayout.Build(map, layout, scroll: 2000, rec, [], null);

        Assert.DoesNotContain(plan.Nodes, n => n.Kind == MapNodeMarkKind.Recommended);
        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void Best_path_has_one_segment_per_visible_step_and_stops_short_of_the_icons()
    {
        var (map, layout, rec) = Setup();
        // Scroll so that every node of the 6-row stand-in map (y 1000 .. 250, boss above) lies on screen.
        float scroll = 50;
        var plan = MapMarkLayout.Build(map, layout, scroll, rec, [], null);

        var best = plan.Lines.Where(l => l.Kind == MapLineKind.Best).ToList();
        var steps = new List<(MapCoord From, MapCoord To)>();
        var from = rec.Origin;
        foreach (var to in rec.Top[0].Coords)
        {
            steps.Add((from, to));
            from = to;
        }
        var visible = steps.Where(s =>
        {
            float y1 = layout.Centers[s.From].Y + scroll, y2 = layout.Centers[s.To].Y + scroll;
            return Math.Max(y1, y2) >= MapMarkLayout.VisibleTop && Math.Min(y1, y2) <= MapMarkLayout.VisibleBottom;
        }).ToList();
        Assert.Equal(visible.Count, best.Count);

        // The first segment starts just outside the origin's icon: 34 design px from a normal node's centre, 95 from
        // the big "ancient" start scene.
        var o = layout.Centers[rec.Origin];
        float gap = rec.Origin == map.Start && map.Nodes[map.Start].Type == MapNodeType.Ancient ? 95 : 34;
        var a = best[0];
        float d = MathF.Sqrt((a.X1 - o.X) * (a.X1 - o.X) + (a.Y1 - (o.Y + scroll)) * (a.Y1 - (o.Y + scroll)));
        Assert.InRange(d, gap - 0.1f, gap + 0.1f);
    }

    [Fact]
    public void Alternatives_only_draw_where_they_leave_the_recommended_path()
    {
        for (int seed = 1; seed <= 20; seed++)
        {
            var (map, layout, rec) = Setup(seed);
            var plan = MapMarkLayout.Build(map, layout, 50, rec, [], null);
            var best = plan.Lines.Where(l => l.Kind == MapLineKind.Best).Select(l => (l.X1, l.Y1, l.X2, l.Y2)).ToHashSet();
            Assert.All(plan.Lines.Where(l => l.Kind == MapLineKind.Alternative), l => Assert.DoesNotContain((l.X1, l.Y1, l.X2, l.Y2), best));
            // No segment drawn twice.
            Assert.Equal(plan.Lines.Count, plan.Lines.Distinct().Count());
        }
    }

    [Fact]
    public void No_recommendation_draws_no_path_but_still_shows_partner_votes()
    {
        var (map, layout, _) = Setup();
        var vote = map.Nodes.Keys.First(c => c.Row == 1);
        var plan = MapMarkLayout.Build(map, layout, 0, null, [vote, vote], null);

        Assert.Empty(plan.Lines);
        var tag = Assert.Single(plan.Nodes);
        Assert.Equal(MapNodeMarkKind.Partner, tag.Kind);
        Assert.Equal(vote, tag.Coord);
        Assert.Equal("동료 ×2", tag.Caption);
    }

    [Theory]
    [InlineData(Lang.Kor, "내 투표", "동료")]
    [InlineData(Lang.Eng, "My vote", "Ally")]
    public void My_vote_gets_its_own_tag_and_the_recommendation_still_starts_from_where_we_are(Lang lang, string mine, string ally)
    {
        using var _ = Loc.Use(lang);
        var (map, layout, rec) = Setup();
        // Co-op: I voted for a next-row node other than the recommended one, the partner for yet another (if any).
        var choices = rec.NextSteps.Select(s => s.Coord).ToList();
        Assert.True(choices.Count >= 3, "the stand-in map offers three first steps");
        MapCoord myVote = choices[1], partnerVote = choices[2]; // neither is the recommended one (choices[0])
        var plan = MapMarkLayout.Build(map, layout, 50, rec, [partnerVote], null, myVote);

        var tag = Assert.Single(plan.Nodes, n => n.Kind == MapNodeMarkKind.MyVote);
        Assert.Equal((myVote, mine), (tag.Coord, tag.Caption));
        Assert.Equal((layout.Centers[myVote].X, layout.Centers[myVote].Y + 50), (tag.X, tag.Y));
        var partner = Assert.Single(plan.Nodes, n => n.Kind == MapNodeMarkKind.Partner);
        Assert.Equal((partnerVote, ally), (partner.Coord, partner.Caption));

        // The votes change nothing else: the path still starts from the real current node and "추천!" stays on.
        Assert.Equal(map.Current ?? map.Start, rec.Origin);
        Assert.Equal(MapMarkLayout.Build(map, layout, 50, rec, [], null).Lines, plan.Lines);
        Assert.Equal(rec.NextStep, Assert.Single(plan.Nodes, n => n.Kind == MapNodeMarkKind.Recommended).Coord);
    }

    [Theory]
    [InlineData(Lang.Kor, "투표 일치")]
    [InlineData(Lang.Eng, "Both voted")]
    public void Same_vote_as_the_partner_is_one_combined_tag(Lang lang, string caption)
    {
        using var _ = Loc.Use(lang);
        var (map, layout, rec) = Setup();
        var vote = rec.NextStep!.Value;
        var plan = MapMarkLayout.Build(map, layout, 50, rec, [vote], null, vote);

        var tag = Assert.Single(plan.Nodes, n => n.Coord == vote && n.Kind is MapNodeMarkKind.BothVoted or MapNodeMarkKind.MyVote or MapNodeMarkKind.Partner);
        Assert.Equal((MapNodeMarkKind.BothVoted, caption), (tag.Kind, tag.Caption));
        Assert.Contains(plan.Nodes, n => n.Kind == MapNodeMarkKind.Recommended && n.Coord == vote);
    }

    [Fact]
    public void No_vote_of_mine_draws_no_my_vote_tag()
    {
        var (map, layout, rec) = Setup();
        var plan = MapMarkLayout.Build(map, layout, 50, rec, [rec.NextStep!.Value], null);
        Assert.DoesNotContain(plan.Nodes, n => n.Kind is MapNodeMarkKind.MyVote or MapNodeMarkKind.BothVoted);
        Assert.Single(plan.Nodes, n => n.Kind == MapNodeMarkKind.Partner);
    }

    [Fact]
    public void Unknown_odds_go_on_next_choice_question_marks_and_the_first_question_mark_of_the_best_path()
    {
        var odds = new UnknownRoomOdds(0.1, -1, 0.03, 0.02);
        for (int seed = 1; seed <= 40; seed++)
        {
            var (map, layout, rec) = Setup(seed);
            var plan = MapMarkLayout.Build(map, layout, 50, rec, [], odds);

            var expected = rec.NextSteps.Where(s => s.Type == MapNodeType.Unknown).Select(s => s.Coord).ToHashSet();
            var best = rec.Top[0];
            int first = best.Types.ToList().IndexOf(MapNodeType.Unknown);
            if (first >= 0) expected.Add(best.Coords[first]);
            expected.RemoveWhere(c => layout.Centers[c].Y + 50 is < MapMarkLayout.VisibleTop or > MapMarkLayout.VisibleBottom);

            var got = plan.Nodes.Where(n => n.Kind == MapNodeMarkKind.UnknownOdds).Select(n => n.Coord).ToHashSet();
            Assert.Equal(expected, got);
            Assert.All(plan.Nodes.Where(n => n.Kind == MapNodeMarkKind.UnknownOdds), n => Assert.Equal(MapNodeType.Unknown, map.Nodes[n.Coord].Type));
        }
    }

    [Fact]
    public void Odds_text_leaves_out_impossible_outcomes_and_gives_events_the_rest()
    {
        Assert.Equal("전투 10% · 상점 3% · 보물 2% · 이벤트 85%", MapMarkLayout.OddsText(new UnknownRoomOdds(0.1, -1, 0.03, 0.02)));
        Assert.Equal("전투 20% · 엘리트 5% · 이벤트 75%", MapMarkLayout.OddsText(new UnknownRoomOdds(0.2, 0.05, 0, 0)));
        Assert.Equal("전투 2.5% · 이벤트 97.5%", MapMarkLayout.OddsText(new UnknownRoomOdds(0.025, -1, -1, -1)));
    }

    [Fact]
    public void Odds_are_read_from_the_save_keys()
    {
        var odds = UnknownRoomOdds.From(new Dictionary<string, double>
        {
            ["unknown_map_point_elite_odds_value"] = -1,
            ["unknown_map_point_monster_odds_value"] = 0.1,
            ["unknown_map_point_shop_odds_value"] = 0.03,
            ["unknown_map_point_treasure_odds_value"] = 0.02,
        });
        Assert.Equal(new UnknownRoomOdds(0.1, -1, 0.03, 0.02), odds);
        Assert.Equal(0.85, odds!.Event, 6);
        Assert.Null(UnknownRoomOdds.From(new Dictionary<string, double>()));
    }
}
