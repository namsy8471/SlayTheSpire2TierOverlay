using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Learning;

public class PickExportTests
{
    private static readonly Lazy<DataPack> Pack = new(() =>
    {
        var catalog = DataPackLoader.LoadCatalogFile(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json"));
        return DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1"), catalog);
    });

    public static IEnumerable<object[]> Runs => TestPaths.RunFileNames;

    [Theory]
    [MemberData(nameof(Runs))]
    public void Screens_match_the_engine_and_the_recorded_picks(string file)
    {
        var run = RunHistoryReader.ReadFile(TestPaths.Run(file));
        var export = new PickExport(Pack.Value);
        var (screens, fights) = export.Export(Path.GetFileNameWithoutExtension(file), run);
        if (run.IsMultiplayer || run.WasAbandoned)
        {
            Assert.Empty(screens);
            Assert.Empty(fights);
            return;
        }

        Assert.NotEmpty(screens);
        var engine = new RecommendationEngine(Pack.Value);
        var decisions = DecisionExtractor.Extract(run, run.Players[0].Id)
            .Where(d => d.Context != OfferContext.Shop && d.Offered.Count >= 2 && d.Picked.Count <= 1)
            .ToList();
        Assert.Equal(decisions.Count, screens.Count);

        for (var k = 0; k < screens.Count; k++)
        {
            var s = screens[k];
            var d = decisions[k];
            Assert.Equal(d.NodeIndex, s.NodeIndex);
            Assert.Equal(d.Offered.Count, s.Options.Count);
            Assert.Equal(d.StateBefore.Deck.Count, s.Deck.Count);

            // Options keep offer order, and each total is exactly the sum of its exported components.
            for (var i = 0; i < s.Options.Count; i++)
            {
                Assert.Equal(Pack.Value.Canonical(d.Offered[i].Id), s.Options[i].Card);
                Assert.Equal(d.Offered[i].Upgrade, s.Options[i].Upgrade);
                Assert.Equal(s.Options[i].Total, s.Options[i].Components.Values.Sum(), 6);
            }

            Assert.Equal(s.SkipTotal, s.SkipComponents.Values.Sum(), 6);

            // The recorded pick and the engine's recommendation point at the right option.
            if (d.Skipped)
            {
                Assert.Equal(-1, s.Pick);
            }
            else
            {
                Assert.Equal(d.Picked[0], d.Offered[s.Pick]);
            }

            var eval = engine.EvaluateOffer(d.StateBefore, d.Offered, d.Context);
            Assert.Equal(eval.Best, s.EngineBest < 0 ? null : d.Offered[s.EngineBest]);
            Assert.Equal(run.Win, s.Win);
        }

        // Every fight room is exported once, and only the last one can be the killing fight.
        Assert.All(fights.SkipLast(1), f => Assert.False(f.Died));
        Assert.Equal(fights.Select(f => f.NodeIndex).Distinct().Count(), fights.Count);
    }

    [Fact]
    public void Card_token_marks_upgrades_and_enchantments()
    {
        var export = new PickExport(Pack.Value);
        Assert.Equal("CARD.BASH", export.CardToken(new DeckCard(new CardRef("CARD.BASH"))));
        Assert.Equal("CARD.BASH+1", export.CardToken(new DeckCard(new CardRef("CARD.BASH", 1))));
        Assert.Equal("CARD.BASH+1#ENCHANTMENT.SHARP:3", export.CardToken(new DeckCard(new CardRef("CARD.BASH", 1), 0, "ENCHANTMENT.SHARP", 3)));
    }

    [Fact]
    public void Multiplayer_runs_are_counted_and_left_out()
    {
        var export = new PickExport(Pack.Value);
        foreach (var path in TestPaths.AllRuns)
        {
            export.Export(Path.GetFileNameWithoutExtension(path), RunHistoryReader.ReadFile(path));
        }

        Assert.Equal(TestPaths.AllRuns.Count, export.Stats.Runs);
        Assert.True(export.Stats.RunsSkippedMultiplayer > 0, "the fixtures hold co-op runs");
        Assert.True(export.Stats.Screens > 0);
    }
}
