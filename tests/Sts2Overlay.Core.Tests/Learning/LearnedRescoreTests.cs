using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Learning;

/// <summary>
/// The learned re-score explains itself truthfully: an engine component the model weighs negatively is not shown under its
/// engine label with the opposite sign (a "자원 과잉 +19" chip); its value moves into the learned preference, so the totals,
/// and with them the ranking, stay exactly the model's utilities. And the pack's data warnings survive the model.
/// </summary>
public class LearnedRescoreTests
{
    private static string PackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1");

    private static DataPack LoadPack()
        => DataPackLoader.LoadDirectory(PackDir, DataPackLoader.LoadCatalogFile(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json")));

    private static readonly Lazy<DataPack> Pack = new(LoadPack);

    /// <summary>Engine components that are penalties (always ≤ 0 in the engine) and bonuses (always ≥ 0).</summary>
    private static readonly string[] Penalties =
    [
        ComponentKeys.Saturation, ComponentKeys.PowerSaturation, ComponentKeys.CoreMissing, ComponentKeys.Dilution,
        ComponentKeys.Duplicate, ComponentKeys.OffBuild, ComponentKeys.EngineMissing,
    ];

    private static readonly string[] Bonuses =
    [
        ComponentKeys.Build, ComponentKeys.Combo, ComponentKeys.CorePriority, ComponentKeys.SecuredEssential,
    ];

    private static Dictionary<string, double> Sum(ScoreBreakdown s)
        => s.Components.GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(c => c.Value), StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(PickExportTests.Runs), MemberType = typeof(PickExportTests))]
    public void Totals_and_order_are_the_model_utilities_and_no_chip_flips_its_label(string file)
    {
        var pack = Pack.Value;
        var model = pack.CardPolicy!;
        var learned = new RecommendationEngine(pack);
        var engine = new RecommendationEngine(pack) { UseLearnedModel = false };
        var run = RunHistoryReader.ReadFile(TestPaths.Run(file));
        var screens = 0;

        foreach (var player in run.Players)
        {
            foreach (var d in DecisionExtractor.Extract(run, player.Id).Where(d => d.Context != OfferContext.Shop && d.Offered.Count >= 1))
            {
                screens++;
                var profile = learned.Analyze(d.StateBefore);
                var plain = engine.EvaluateOffer(d.StateBefore, profile, d.Offered, d.Context);
                var eval = learned.EvaluateOffer(d.StateBefore, profile, d.Offered, d.Context);

                // The model's own utilities from the engine components: what the total was before chips were made truthful.
                var options = plain.Cards.Select(e =>
                {
                    var id = pack.Canonical(e.Card.Id);
                    return new PolicyOption(id, e.Card.Upgrade, profile.Copies.GetValueOrDefault(id), Sum(e.Score),
                        pack.FindTier(d.StateBefore.Character, id)?.BaseAdjust ?? 0.0);
                }).ToArray();
                var deck = d.StateBefore.Deck.Select(c => pack.Canonical(c.Card.Id)).ToArray();
                var u = model.Utilities(d.StateBefore.Act, deck, options, Sum(plain.Skip));
                var unit = model.PointUtility;

                Assert.Equal(u[0] / unit, eval.Skip.Total, 6);
                for (var i = 0; i < eval.Cards.Count; i++)
                {
                    Assert.Equal(u[i + 1] / unit, eval.Cards[i].Total, 6);
                }

                // Same order as ranking by utility (ties by offer order).
                var expected = Enumerable.Range(0, u.Length - 1).OrderByDescending(i => u[i + 1]).ThenBy(i => i).ToArray();
                for (var k = 0; k < expected.Length; k++)
                {
                    Assert.Equal(k + 1, eval.Cards[expected[k]].Rank);
                }

                // No engine label shows the opposite sign of what the engine meant.
                for (var i = 0; i < eval.Cards.Count; i++)
                {
                    var before = plain.Cards[i].Score;
                    foreach (var c in eval.Cards[i].Score.Components.Where(c => c.Key != ComponentKeys.Base && c.Key != PolicyKeys.CardValue
                                 && c.Key != PolicyKeys.DeckFit && c.Key != PolicyKeys.Upgraded && c.Key != PolicyKeys.Copies))
                    {
                        Assert.True(c.Value * before.ValueOf(c.Key) >= 0, $"{file}: {c.Key} {c.Value} vs engine {before.ValueOf(c.Key)}");
                    }

                    foreach (var chip in eval.Cards[i].Chips)
                    {
                        if (Penalties.Contains(chip.Key)) Assert.True(chip.Value <= 0, $"{file}: penalty chip {chip.Label} {chip.Value}");
                        if (Bonuses.Contains(chip.Key)) Assert.True(chip.Value >= 0, $"{file}: bonus chip {chip.Label} {chip.Value}");
                    }
                }

                foreach (var c in eval.Skip.Components.Where(c => c.Key is not PolicyKeys.SkipAct and not PolicyKeys.DeckFit))
                {
                    Assert.True(c.Value * plain.Skip.ValueOf(c.Key) >= 0, $"{file}: skip {c.Key} {c.Value} vs engine {plain.Skip.ValueOf(c.Key)}");
                }
            }
        }

        Assert.True(screens > 0);
    }

    /// <summary>The fixtures do reach the flipped components, so the test above is not vacuous.</summary>
    [Fact]
    public void Fixtures_contain_engine_penalties_the_model_turns_around()
    {
        var pack = Pack.Value;
        var model = pack.CardPolicy!;
        Assert.True(model.RelativeWeight(ComponentKeys.Saturation) < 0);
        Assert.True(model.RelativeWeight(ComponentKeys.CoreMissing) < 0);
        Assert.True(model.RelativeWeight(ComponentKeys.PowerSaturation) < 0);

        var engine = new RecommendationEngine(pack) { UseLearnedModel = false };
        var learned = new RecommendationEngine(pack);
        int flipped = 0, folded = 0;
        foreach (var path in TestPaths.AllRuns)
        {
            var run = RunHistoryReader.ReadFile(path);
            foreach (var player in run.Players)
            {
                foreach (var d in DecisionExtractor.Extract(run, player.Id).Where(d => d.Context != OfferContext.Shop && d.Offered.Count >= 1))
                {
                    var profile = engine.Analyze(d.StateBefore);
                    var plain = engine.EvaluateOffer(d.StateBefore, profile, d.Offered, d.Context);
                    var eval = learned.EvaluateOffer(d.StateBefore, profile, d.Offered, d.Context);
                    for (var i = 0; i < plain.Cards.Count; i++)
                    {
                        var turned = plain.Cards[i].Score.Components.Where(c => Math.Abs(c.Value) > 1e-9 && model.RelativeWeight(c.Key) < 0).ToList();
                        if (turned.Count == 0) continue;
                        flipped++;
                        Assert.All(turned, c => Assert.DoesNotContain(eval.Cards[i].Score.Components, x => x.Key == c.Key));
                        var pref = eval.Cards[i].Score.Components.Single(c => c.Key == PolicyKeys.CardValue);
                        Assert.Contains(turned[0].Label, pref.Detail);
                        folded++;
                    }
                }
            }
        }

        Assert.True(flipped > 0, "no fixture screen has a sign-flipped component");
        Assert.Equal(flipped, folded);
    }

    [Fact]
    public void Model_knowledge_does_not_erase_the_packs_estimate_flags()
    {
        var pack = LoadPack(); // a private copy: two tier entries are changed below
        var model = pack.CardPolicy!;
        const string character = "CHARACTER.IRONCLAD";
        var known = pack.Tiers[character].Cards
            .Where(kv => model.Knows(kv.Key) && kv.Value.ChangedIn is null && !kv.Value.IsEstimate)
            .Select(kv => kv.Key).Take(3).ToArray();
        Assert.Equal(3, known.Length);
        var (patched, untiered, plain) = (known[0], known[1], known[2]);
        pack.Tiers[character].Cards[patched].ChangedIn = "v0.200.0";
        foreach (var tier in pack.Tiers.Values) tier.Cards.Remove(untiered);
        Assert.Null(pack.FindTier(character, untiered));

        var state = new RunState
        {
            Character = character, Act = 1, Hp = 80, MaxHp = 80,
            Deck = Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_IRONCLAD")), 5)
                .Concat(Enumerable.Repeat(new DeckCard(new CardRef("CARD.DEFEND_IRONCLAD")), 4)).ToArray(),
        };
        var eval = new RecommendationEngine(pack).EvaluateOffer(state, [new CardRef(patched), new CardRef(untiered), new CardRef(plain)]);
        var p = eval.Cards[0];
        var u = eval.Cards[1];
        var k = eval.Cards[2];

        // Changed by a patch: still "추정", at the pack's confidence, though the model knows the card.
        Assert.True(p.KnownToModel);
        Assert.True(p.IsEstimate);
        Assert.Equal(pack.FindTier(character, patched)!.Confidence, p.Confidence, 9);

        // No tier entry: "추정", placeholder confidence, "?" letter; but the model rates it, so it is not "unjudged".
        Assert.True(u.KnownToModel);
        Assert.False(u.IsRated);
        Assert.True(u.IsEstimate);
        Assert.Equal(pack.Weights.UnknownConfidence, u.Confidence, 9);
        Assert.Equal("?", u.TierText);
        Assert.False(u.Unjudged);

        // A normal card the model knows: as before.
        Assert.True(k.KnownToModel);
        Assert.False(k.IsEstimate);
        Assert.Equal(1.0, k.Confidence, 9);
    }
}
