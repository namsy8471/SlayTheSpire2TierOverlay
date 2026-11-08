using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Rest;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Tests.Map;
using Sts2Overlay.Core.Tests.Scoring;
using static Sts2Overlay.Core.Map.MapNodeType;

namespace Sts2Overlay.Core.Tests.Rest;

public class RestSiteAdvisorTests
{
    private static readonly RestSiteAdvisor Advisor = new(TestMaps.Pack);

    private static RunState State(double hp, int act = 1) => TestMaps.State(hpRatio: hp, act: act);

    [Fact]
    public void Right_before_the_boss_low_hp_heals_and_high_hp_smiths()
    {
        var low = Advisor.Advise(State(0.2), [Boss]);
        var high = Advisor.Advise(State(0.9), [Boss]);

        Assert.Equal(RestAction.Heal, low.Action);
        Assert.Contains("보스 직전", low.Reason);
        Assert.Contains("휴식", low.Reason);
        Assert.Equal(RestAction.Smith, high.Action);
        Assert.Contains("강화", high.Reason);
    }

    [Fact]
    public void Nothing_to_upgrade_always_heals()
    {
        var state = State(0.95) with { Deck = [new DeckCard(new CardRef(TestPacks.Strike, 1)), new DeckCard(new CardRef(TestPacks.Curse))] };
        var advice = Advisor.Advise(state, [Monster, Boss]);

        Assert.Equal(RestAction.Heal, advice.Action);
        Assert.Equal(RestConfidence.Sure, advice.Confidence);
        Assert.Empty(advice.Upgrades);
        Assert.Null(advice.BestUpgrade);
    }

    [Fact]
    public void Full_hp_always_smiths()
    {
        var advice = Advisor.Advise(State(1.0), [Elite, Boss]);

        Assert.Equal(RestAction.Smith, advice.Action);
        Assert.Equal(RestConfidence.Sure, advice.Confidence);
        Assert.NotNull(advice.BestUpgrade);
    }

    [Fact]
    public void Without_a_map_the_measured_hp_threshold_decides()
    {
        var low = Advisor.Advise(State(0.2), (MapGraph?)null);
        var high = Advisor.Advise(State(0.85), (MapGraph?)null);

        Assert.Equal(RestAction.Heal, low.Action);
        Assert.Equal(RestAction.Smith, high.Action);
        Assert.StartsWith("지도 없음", low.Reason);
        Assert.Null(low.HealScore);
        Assert.Null(low.Outlook);
        Assert.NotEmpty(high.Upgrades); // the upgrade ranking is there either way
    }

    [Fact]
    public void An_elite_ahead_makes_healing_worth_more_than_an_easy_road()
    {
        var elite = Advisor.Advise(State(0.5), [Elite, Boss]);
        var easy = Advisor.Advise(State(0.5), [Treasure, Boss]);

        Assert.True(elite.HealScore!.Total > easy.HealScore!.Total,
            $"elite road {elite.HealScore.Total:0.0} vs easy road {easy.HealScore.Total:0.0}");
        Assert.Equal(1, elite.Outlook!.ElitesAhead);
        Assert.Equal(1, elite.Outlook.RoomsBeforeBoss);
    }

    [Theory]
    [InlineData(new[] { Boss })]
    [InlineData(new[] { Monster, Boss })]
    [InlineData(new[] { Monster, Elite, Unknown, RestSite, Monster, Boss })]
    [InlineData(new[] { Unknown, Monster, MapNodeType.Shop, Elite, Monster, Treasure, Monster, RestSite, Boss })]
    public void More_hp_never_flips_smith_back_to_heal(MapNodeType[] ahead)
    {
        // Heal value falls as HP rises (less risk to remove, and the heal is capped at max HP), smith value does not
        // depend on HP: so walking HP upward, once the advice is "smith" it stays "smith".
        var sawSmith = false;
        for (var pct = 5; pct <= 100; pct += 5)
        {
            var action = Advisor.Advise(State(pct / 100.0), ahead).Action;
            if (sawSmith) Assert.Equal(RestAction.Smith, action);
            sawSmith |= action == RestAction.Smith;
        }
        Assert.True(sawSmith);
    }

    [Theory]
    [InlineData(0.15)]
    [InlineData(0.45)]
    [InlineData(0.6)]
    [InlineData(0.8)]
    public void Confidence_is_close_exactly_when_the_scores_are_within_the_close_gap(double hp)
    {
        var a = Advisor.Advise(State(hp), [Monster, Elite, Monster, Boss]);
        var gap = Math.Abs(a.HealScore!.Total - a.SmithScore!.Total);

        Assert.Equal(gap < TestMaps.Pack.Weights.ConfidenceCloseGap ? RestConfidence.Close : RestConfidence.Sure, a.Confidence);
        Assert.Equal(a.HealScore.Total > a.SmithScore.Total ? RestAction.Heal : RestAction.Smith, a.Action);
    }

    [Fact]
    public void Smith_score_is_the_best_upgrade_and_matches_the_upgrade_advisor()
    {
        var state = State(0.7);
        var advice = Advisor.Advise(state, [Monster, Boss]);
        var ranked = new UpgradeAdvisor(TestMaps.Pack).Rank(state);

        Assert.Equal(ranked.Select(c => c.Card), advice.Upgrades.Select(c => c.Card));
        Assert.Equal(ranked[0].Total, advice.SmithScore!.Total, 9);
        Assert.Equal(3, advice.TopUpgrades().Count);
    }

    [Fact]
    public void Works_on_a_branching_map_from_the_current_rest_site()
    {
        // (0,1) rest site → either an elite or a "?" → boss.
        var map = new MapBuilder(bossRow: 3, act: 1, (0, 1))
            .Add(0, 1, RestSite, (0, 2), (1, 2))
            .Add(0, 2, Elite, (0, 3))
            .Add(1, 2, Unknown, (0, 3))
            .Build(current: new MapCoord(0, 1));

        var advice = Advisor.Advise(State(0.5), map);

        Assert.NotNull(advice.Outlook);
        Assert.Equal(Boss, advice.Outlook!.PathAhead[^1]);
        Assert.Equal(2, advice.Outlook.PathAhead.Count);
        Assert.NotNull(advice.HealScore);
        Assert.InRange(advice.Outlook.HpAfterHeal, 0.5, 1.0);
    }

    [Fact]
    public void Chain_map_puts_the_visited_rooms_first_and_adds_a_missing_boss()
    {
        var map = RestSiteAdvisor.ChainMap([Ancient, Elite, RestSite], [Monster], act: 2);

        Assert.Equal(5, map.Nodes.Count);
        Assert.Equal(new MapCoord(0, 2), map.Current);
        Assert.Equal(3, map.Visited.Count);
        Assert.Equal(Boss, map.Nodes[map.Boss].Type);
        Assert.Equal(new MapCoord(0, 4), map.Boss);
        Assert.Equal(2, map.Act);
        Assert.Equal(1L, MapPaths.CountPaths(map, map.Current!.Value));

        var withBoss = RestSiteAdvisor.ChainMap([RestSite], [Monster, Boss]);
        Assert.Equal(3, withBoss.Nodes.Count); // no second boss
        Assert.Throws<ArgumentException>(() => RestSiteAdvisor.ChainMap([], [Boss]));
    }
}
