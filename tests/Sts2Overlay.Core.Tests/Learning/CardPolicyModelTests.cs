using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Learning;

public class CardPolicyModelTests
{
    private static string PackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1");

    private static readonly Lazy<DataPack> Pack = new(() =>
        DataPackLoader.LoadDirectory(PackDir, DataPackLoader.LoadCatalogFile(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json"))));

    private sealed record FixtureOption(string Card, int Upgrade, int Copies, Dictionary<string, double> Components, double BaseAdjust);

    private sealed record FixtureScreen(int Act, string[] Deck, FixtureOption[] Options, Dictionary<string, double> SkipComponents, double[] Utilities);

    /// <summary>The C# model gives the utilities the Python trainer gave (tools/CardPolicy/export_model.py --fixture).</summary>
    [Fact]
    public void Utilities_match_the_python_model()
    {
        var model = Pack.Value.CardPolicy ?? throw new InvalidOperationException("the v0.107.1 pack ships model/card-policy.json");
        var json = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "learning", "card-policy-parity.json"));
        var screens = JsonSerializer.Deserialize<FixtureScreen[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(200, screens.Length);

        var worst = 0.0;
        foreach (var s in screens)
        {
            var deck = s.Deck.Select(t => t.Split('#')[0].Split('+')[0]).ToArray();
            var options = s.Options.Select(o => new PolicyOption(o.Card, o.Upgrade, o.Copies, o.Components, o.BaseAdjust)).ToArray();
            var u = model.Utilities(s.Act, deck, options, s.SkipComponents);
            Assert.Equal(s.Utilities.Length, u.Length);
            for (var i = 0; i < u.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(u[i] - s.Utilities[i]));
            }
        }

        // Weights are stored with 6 decimals; utilities are sums of tens of terms.
        Assert.True(worst < 1e-3, $"largest utility difference {worst}");
    }

    [Theory]
    [MemberData(nameof(PickExportTests.Runs), MemberType = typeof(PickExportTests))]
    public void Learned_ranking_follows_the_model_and_keeps_totals_exact(string file)
    {
        var run = RunHistoryReader.ReadFile(TestPaths.Run(file));
        var learned = new RecommendationEngine(Pack.Value);
        var engine = new RecommendationEngine(Pack.Value) { UseLearnedModel = false };
        Assert.True(learned.LearnedModelActive);
        Assert.False(engine.LearnedModelActive);

        foreach (var player in run.Players)
        {
            foreach (var d in DecisionExtractor.Extract(run, player.Id).Where(d => d.Context != OfferContext.Shop && d.Offered.Count >= 2))
            {
                var eval = learned.EvaluateOffer(d.StateBefore, d.Offered, d.Context);
                foreach (var e in eval.Cards)
                {
                    Assert.Equal(e.Total, e.Score.Components.Sum(c => c.Value), 9);
                }

                // Rank 1 is the card with the highest learned total.
                var top = eval.Cards.Single(e => e.Rank == 1);
                Assert.Equal(eval.Cards.Max(e => e.Total), top.Total, 9);
                Assert.True(eval.Best is null || d.Offered.Contains(eval.Best.Value));

                // The engine-only path is untouched by the model.
                var plain = engine.EvaluateOffer(d.StateBefore, d.Offered, d.Context);
                Assert.DoesNotContain(plain.Cards.SelectMany(e => e.Score.Components), c => c.Key is PolicyKeys.CardValue or PolicyKeys.DeckFit);
            }
        }
    }

    [Fact]
    public void Base_weight_sets_the_point_scale()
    {
        var model = Pack.Value.CardPolicy!;
        Assert.Equal(1.0, model.RelativeWeight(ComponentKeys.Base), 12);
        Assert.True(model.PointUtility > 0);
        Assert.Equal(0.0, model.RelativeWeight("not_a_component"));
    }
}
