using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.History;

public class DecisionExtractorTests
{
    public static IEnumerable<object[]> Runs => TestPaths.RunFileNames;

    private static RunHistory Load(string file) => RunHistoryReader.ReadFile(TestPaths.Run(file));

    [Theory]
    [MemberData(nameof(Runs))]
    public void DecisionsAreWellFormedForEveryPlayer(string file)
    {
        var run = Load(file);
        foreach (var player in run.Players)
        {
            var decisions = DecisionExtractor.Extract(run, player.Id);
            Assert.NotEmpty(decisions);

            foreach (var d in decisions)
            {
                Assert.NotEmpty(d.Offered);
                var remaining = d.Offered.ToList();
                foreach (var pick in d.Picked)
                {
                    Assert.True(remaining.Remove(pick), $"floor {d.Node.Floor}: picked {pick} not offered");
                }

                if (d.Context != OfferContext.Shop)
                {
                    Assert.True(d.Picked.Count <= 1, $"floor {d.Node.Floor} group {d.GroupIndex}: {d.Picked.Count} picks");
                }

                Assert.Same(run.Nodes[d.NodeIndex], d.Node);
                Assert.Equal(d.Node.Floor, d.StateBefore.Floor);
                Assert.Equal(player.Character, d.StateBefore.Character);
                Assert.Equal(d.Picked.Count == 0, d.Skipped);
            }

            // Decisions come in node order, groups in order inside a node.
            var keys = decisions.Select(d => (d.NodeIndex, d.GroupIndex)).ToList();
            Assert.Equal(keys.OrderBy(k => k.NodeIndex).ThenBy(k => k.GroupIndex), keys);

            // Every non-shop reward pick shows up in the reconstructed deck right after the node.
            var timeline = DeckTimeline.Build(run, player.Id);
            foreach (var d in decisions.Where(d => d.Context != OfferContext.Shop && !d.Skipped))
            {
                Assert.Contains(timeline.DeckAfter(d.NodeIndex), c => c.Id == d.Picked[0].Id);
            }
        }
    }

    [Fact]
    public void SinglePlayerDecisions()
    {
        var run = Load("1794130978.run");
        var decisions = DecisionExtractor.Extract(run, 1);

        var first = decisions[0];
        Assert.Equal(1, first.NodeIndex);
        Assert.Equal(OfferContext.CardReward, first.Context);
        Assert.Equal(new[] { new CardRef("CARD.ANGER"), new CardRef("CARD.TRUE_GRIT"), new CardRef("CARD.STOMP") }, first.Offered);
        Assert.Equal(new[] { new CardRef("CARD.ANGER") }, first.Picked);
        Assert.Equal(10, first.StateBefore.Deck.Count);
        Assert.DoesNotContain(first.StateBefore.Deck, c => c.Id == "CARD.ANGER");

        // An unknown map point that holds a shop is a shop decision.
        var shop = Assert.Single(decisions, d => d.Node.Floor == 9);
        Assert.Equal(OfferContext.Shop, shop.Context);
        Assert.Equal(7, shop.Offered.Count);
        Assert.True(shop.Skipped);
    }

    [Fact]
    public void TwoRewardScreensSplitIntoGroups()
    {
        var run = Load("1773379374.run");
        var floor33 = DecisionExtractor.Extract(run, LocalPlayer.FixtureSelfId).Where(d => d.Node.Floor == 33).ToList();

        Assert.Equal(2, floor33.Count);
        Assert.Equal(new[] { 0, 1 }, floor33.Select(d => d.GroupIndex));
        Assert.All(floor33, d => Assert.Equal(3, d.Offered.Count));
        Assert.Equal(new[] { new CardRef("CARD.SHRUG_IT_OFF") }, floor33[0].Picked);
        Assert.True(floor33[1].Skipped);
        Assert.Equal(new CardRef("CARD.TREMBLE", 1), floor33[1].Offered[0]);

        // The second screen sees the first screen's pick in the deck.
        Assert.Equal(floor33[0].StateBefore.Deck.Count + 1, floor33[1].StateBefore.Deck.Count);
    }

    [Fact]
    public void ContextsFollowRooms()
    {
        var run = Load("1773379374.run");
        var decisions = DecisionExtractor.Extract(run, LocalPlayer.FixtureSelfId);

        Assert.Contains(decisions, d => d.Context == OfferContext.BossReward && d.Node.MapPointType == "boss");
        Assert.Contains(decisions, d => d.Context == OfferContext.EliteReward && d.Node.MapPointType == "elite");
        Assert.Contains(decisions, d => d.Context == OfferContext.Shop && d.Node.MapPointType == "shop");
        Assert.All(decisions.Where(d => d.Node.MapPointType == "monster"), d => Assert.Equal(OfferContext.CardReward, d.Context));
    }

    [Fact]
    public void ReturnedStolenCardIsNotAnOffer()
    {
        // Friend's floor 19 (schema 8): the stolen Stampede is listed first among 4 choices.
        var run = Load("1773379374.run");
        var d = Assert.Single(DecisionExtractor.Extract(run, 76561190000000002), d => d.Node.Floor == 19);
        Assert.Equal(3, d.Offered.Count);
        Assert.DoesNotContain(new CardRef("CARD.STAMPEDE"), d.Offered);
        Assert.Equal(new[] { new CardRef("CARD.BLOODLETTING") }, d.Picked);

        // Schema 9: a stolen card that was not recovered is listed last with its old floor.
        var run9 = Load("1794065818.run");
        var groups = DecisionExtractor.Extract(run9, 76561190000000002).Where(x => x.Node.Floor == 19).ToList();
        Assert.Equal(2, groups.Count);
        Assert.DoesNotContain(groups, g => g.Offered.Contains(new CardRef("CARD.TACTICIAN")));
    }

    [Fact]
    public void AncientMultiPickSplitsIntoFiveGroups()
    {
        var run = Load("1773418777.run");
        var ancient = DecisionExtractor.Extract(run, LocalPlayer.FixtureSelfId).Where(d => d.Node.Floor == 17).ToList();
        Assert.Equal(5, ancient.Count);
        Assert.All(ancient, d => Assert.Equal(OfferContext.Event, d.Context));
        Assert.All(ancient, d => Assert.Single(d.Picked));
    }
}
