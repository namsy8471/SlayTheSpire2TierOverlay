using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Tests.Data;
using Xunit.Abstractions;

namespace Sts2Overlay.Core.Tests.Integration;

/// <summary>
/// "초반엔 어떤 카드든 타격·수비보다 낫다": with the real v0.107.1 pack, over every card-reward/event decision of the 24 fixture runs,
/// act 1 never recommends skip while the deck is at least half basic cards and some offered card has a base of D or better without
/// "엔진 없음". The basic share here is computed straight from the catalog rarity, independent of <see cref="DeckProfile.BasicCards"/>.
/// Skip counts per act and act-1 skips with their cause are reported (match rate vs the recorded picks as info only).
/// </summary>
public class EarlySkipReplayTests(ITestOutputHelper output)
{
    private static readonly Lazy<DataPack> RealPack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1"), RealCatalog.Instance));

    private sealed record Row(string Run, int Floor, int Act, double BasicShare, CardDecision Decision, OfferEvaluation Offer);

    private static readonly Lazy<IReadOnlyList<Row>> Rows = new(() =>
    {
        var pack = RealPack.Value;
        var engine = new RecommendationEngine(pack);
        var rows = new List<Row>();
        foreach (var file in TestPaths.AllRuns)
        {
            var run = RunHistoryReader.ReadFile(file);
            var player = LocalPlayer.Resolve(run);
            foreach (var d in DecisionExtractor.Extract(run, player.Id))
            {
                if (d.Context == OfferContext.Shop) continue;
                var s = d.StateBefore;
                var basics = s.Deck.Count(c => pack.CardRarity(s.Character, c.Id) == "basic");
                var share = s.Deck.Count == 0 ? 0 : (double)basics / s.Deck.Count;
                rows.Add(new Row(Path.GetFileNameWithoutExtension(file), d.Node.Floor, s.Act, share, d,
                    engine.EvaluateOffer(s, d.Offered, d.Context)));
            }
        }
        return rows;
    });

    /// <summary>An offered card that beats a basic card on its own: base ≥ D, no requires-gate penalty.</summary>
    private static bool Pickable(CardEvaluation e, Weights w)
        => e.Score.ValueOf(ComponentKeys.Base) >= w.TierValue[Tier.D] - 1e-9 && e.Score.ValueOf(ComponentKeys.EngineMissing) == 0;

    [Fact]
    public void Act1_BasicHeavyDeck_NeverSkipsAPickableCard()
    {
        var w = RealPack.Value.Weights;
        var scope = Rows.Value.Where(r => r.Act == 1 && r.BasicShare >= 0.5 - 1e-9 && r.Offer.Cards.Any(c => Pickable(c, w))).ToList();
        var bad = scope.Where(r => r.Offer.SkipRecommended).ToList();
        output.WriteLine($"invariant: {scope.Count} act-1 offers with basic share ≥ 0.5 and a pickable card, skip recommended in {bad.Count}");
        foreach (var r in bad)
        {
            output.WriteLine($"  BAD {r.Run} f{r.Floor} share {r.BasicShare:0.00} skip {r.Offer.Skip} | "
                + string.Join(" | ", r.Offer.Cards.Select(c => $"{c.Card.Id} {c.Score} conf {c.Confidence:0.##}")));
        }
        Assert.True(scope.Count >= 50, $"only {scope.Count} offers in scope");
        Assert.Empty(bad);
    }

    [Fact]
    public void Report_SkipsPerAct()
    {
        var rows = Rows.Value;
        var w = RealPack.Value.Weights;
        output.WriteLine($"{rows.Count} card-reward/event decisions, skip recommended {rows.Count(r => r.Offer.SkipRecommended)}");
        foreach (var g in rows.GroupBy(r => r.Act).OrderBy(g => g.Key))
        {
            var match = g.Count(r => r.Offer.SkipRecommended ? r.Decision.Skipped
                : r.Decision.Picked.Any(p => RealPack.Value.Canonical(p.Id) == RealPack.Value.Canonical(r.Offer.Best!.Value.Id)));
            output.WriteLine($"  act {g.Key}: {g.Count()} decisions, skip recommended {g.Count(r => r.Offer.SkipRecommended)}, "
                + $"user skipped {g.Count(r => r.Decision.Skipped)}, recommendation matches user {match} ({100.0 * match / g.Count():0.#}%)");
        }
        foreach (var r in rows.Where(r => r.Act == 1 && r.Offer.SkipRecommended))
        {
            var why = r.Offer.Cards.Any(c => c.Confidence < w.Skip.LowConfidence && c.Total >= r.Offer.Skip.Total + w.Skip.Margin) ? "low confidence"
                : r.Offer.Cards.All(c => !Pickable(c, w)) ? "no pickable card (below D or engine missing)"
                : "scores below skip";
            output.WriteLine($"  act-1 skip {r.Run} f{r.Floor} share {r.BasicShare:0.00} [{why}] skip {r.Offer.Skip.Total:0.#} | "
                + string.Join(" | ", r.Offer.Cards.Select(c => $"{c.Card.Id} {c.Score}")));
        }
        Assert.NotEmpty(rows);
    }
}
