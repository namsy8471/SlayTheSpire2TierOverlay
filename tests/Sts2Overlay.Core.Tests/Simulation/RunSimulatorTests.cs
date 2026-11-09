using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Simulation;

namespace Sts2Overlay.Core.Tests.Simulation;

/// <summary>
/// The deck simulator behind tools/DeckSim (docs/research/DECK_SIM.md) with the real v0.107.1 pack and the committed catalog's pools:
/// a seed always plays the same run, cards come only from the character's pool and colorless cards, and the engine policy builds decks.
/// </summary>
public class RunSimulatorTests
{
    private const string Silent = "CHARACTER.SILENT";

    private static readonly Lazy<DataPack> Pack = new(() =>
        DataPackLoader.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1")));

    private static readonly Lazy<ForecastCardPool> Pool = new(() => ForecastCardPool.FromCatalogJson(
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json"))));

    private static IReadOnlyList<DeckCard> SilentStart()
        => Enumerable.Repeat("CARD.STRIKE_SILENT", 5).Concat(Enumerable.Repeat("CARD.DEFEND_SILENT", 5))
            .Concat(["CARD.NEUTRALIZE", "CARD.SURVIVOR"])
            .Select(id => new DeckCard(new CardRef(id))).ToArray();

    private static SimRun Play(ISimPolicy policy, int seed)
        => new RunSimulator(Pack.Value, Pool.Value).Run(Silent, SilentStart(), [], policy, seed);

    private static string Key(SimRun run)
        => string.Join("|", run.ActEndDecks.Select(d => string.Join(",", d.Select(c => $"{c.Id}+{c.Upgrade}"))));

    [Fact]
    public void A_seed_always_plays_the_same_run()
    {
        var policy = new EnginePolicy(Pack.Value);
        Assert.Equal(Key(Play(policy, 7)), Key(Play(policy, 7)));
        Assert.NotEqual(Key(Play(policy, 7)), Key(Play(policy, 8)));
    }

    [Fact]
    public void Offered_cards_come_from_the_characters_pool_or_colorless()
    {
        var pool = Pool.Value;
        var run = Play(new TierOnlyPolicy(Pack.Value), 3);
        var allowed = pool.All.Where(c => c.Character == Silent || c.IsColorless).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var d in run.Decisions)
        {
            Assert.All(d.Offered, c => Assert.Contains(Pack.Value.Canonical(c.Id), allowed));
        }
    }

    [Fact]
    public void Engine_policy_plays_three_acts_and_grows_the_deck()
    {
        var run = Play(new EnginePolicy(Pack.Value), 11);
        Assert.Equal(3, run.ActEndDecks.Count);
        Assert.True(run.RewardScreens >= 15, $"reward screens {run.RewardScreens}");
        Assert.True(run.ActEndDecks[2].Count > SilentStart().Count);
        // Act 1 card rewards on a starter deck are taken (early skip relief), so act 1 ends bigger than the start.
        Assert.True(run.ActEndDecks[0].Count > SilentStart().Count);
    }

    [Fact]
    public void Deck_metrics_split_chosen_cards_by_fit_to_the_main_build()
    {
        var pack = Pack.Value;
        var poison = pack.ArchetypesFor(Silent).Single(a => a.Id == "silent.poison");
        var core = poison.Cards.Core[0];
        var enabler = poison.Cards.Enabler[0];
        var deck = SilentStart().Concat(new[] { core, enabler, enabler }.Select(id => new DeckCard(new CardRef(id)))).ToArray();

        var m = DeckMetrics.Compute(pack, Silent, deck);

        Assert.Equal("silent.poison", m.Primary?.Archetype.Id);
        Assert.Equal(12, m.Fit.GetValueOrDefault(CardFit.Basic));
        Assert.Equal(3, m.Fit.GetValueOrDefault(CardFit.Build));
        Assert.Equal(3, m.ChosenCards);
        Assert.Equal(1.0, m.Coherence, 6);
        Assert.Equal(1, m.BuildExtraCopies);
        Assert.True(m.PrimaryCoreSecured);
        Assert.Equal(m.ChosenCards, m.CostBuckets.Sum());
    }
}
