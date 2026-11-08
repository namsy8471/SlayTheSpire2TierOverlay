using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Forecast;

public class ForecastCalibrationTests
{
    private const string Char = "CHARACTER.TEST";
    private const string Key = "CARD.KEY";

    private static readonly Lazy<ForecastCardPool> RealPool = new(() => ForecastCardPool.FromCatalogJson(
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "catalog", "cards.v0.107.1.json"))));

    // ---- IsotonicMap --------------------------------------------------------------------------------------------

    [Fact]
    public void Isotonic_PoolsAdjacentViolators()
    {
        // 0.1→miss, 0.2→hit, 0.3→miss, 0.4→hit: the 0.2/0.3 pair violates "higher forecast, higher rate" and is pooled
        // into one point (0.25, 50%).
        var map = IsotonicMap.Fit([(0.1, false), (0.2, true), (0.3, false), (0.4, true)]);

        Assert.Equal([0.1, 0.25, 0.4], map.X);
        Assert.Equal([0.0, 0.5, 1.0], map.Y);
        Assert.Equal(0.5, map.Apply(0.25, 0), 9);
        Assert.Equal(0.25, map.Apply(0.175, 0), 9); // linear between (0.1, 0) and (0.25, 0.5)
        Assert.Equal(ProbabilityClamp.Floor, map.Apply(0.0, 0)); // never claims 0%
        Assert.Equal(ProbabilityClamp.Ceiling, map.Apply(0.9, 0)); // never claims 100%
    }

    [Fact]
    public void Isotonic_IsMonotoneOnNoisyData()
    {
        var rng = new Random(3);
        var samples = Enumerable.Range(0, 2000).Select(_ =>
        {
            var p = rng.NextDouble();
            return (p, rng.NextDouble() < 0.2 + 0.5 * p);
        }).ToList();

        var map = IsotonicMap.Fit(samples);

        for (var i = 1; i < map.Y.Count; i++) Assert.True(map.Y[i] >= map.Y[i - 1]);
        for (var i = 1; i < map.X.Count; i++) Assert.True(map.X[i] > map.X[i - 1]);
        Assert.InRange(map.Apply(0.5, 0), 0.35, 0.55); // true rate 45%
    }

    // ---- PlattMap -----------------------------------------------------------------------------------------------

    [Fact]
    public void Platt_RecoversKnownCoefficients()
    {
        // Outcomes drawn from σ(0.5·logit p − 0.3 + 2·completion): the fit should find (0.5, −0.3, 2).
        var rng = new Random(11);
        var samples = new List<(double, double, bool)>();
        for (var i = 0; i < 40000; i++)
        {
            var p = 0.02 + 0.96 * rng.NextDouble();
            var c = rng.NextDouble();
            var z = 0.5 * Math.Log(p / (1 - p)) - 0.3 + 2 * c;
            samples.Add((p, c, rng.NextDouble() < 1 / (1 + Math.Exp(-z))));
        }

        var map = PlattMap.Fit(samples);

        Assert.Equal(0.5, map.A, 1);
        Assert.Equal(-0.3, map.B, 1);
        Assert.Equal(2.0, map.C, 1);
        Assert.StartsWith("new PlattMap(", map.ToCSharp(), StringComparison.Ordinal);
    }

    [Fact]
    public void Platt_WithoutCompletion_IsPlainTwoNumberScaling()
    {
        var samples = new[] { (0.1, 0.9, false), (0.2, 0.9, true), (0.7, 0.1, true), (0.8, 0.1, false), (0.5, 0.5, true) };
        Assert.Equal(0.0, PlattMap.Fit(samples, useCompletion: false).C);
        Assert.Same(PlattMap.Identity, PlattMap.Fit([]));
    }

    [Fact]
    public void Platt_Identity_KeepsTheRawValue_InsideTheClamp()
    {
        Assert.Equal(0.3, PlattMap.Identity.Apply(0.3, 0.7), 9);
        Assert.Equal(ProbabilityClamp.Floor, PlattMap.Identity.Apply(0.0, 0));
        Assert.Equal(ProbabilityClamp.Ceiling, PlattMap.Identity.Apply(1.0, 0));
    }

    [Fact]
    public void Default_IsMonotoneInBothInputs()
    {
        foreach (var map in new[] { ForecastCalibration.Default.IfSurvive, ForecastCalibration.Default.All })
        {
            for (var p = 0.0; p < 0.95; p += 0.05)
            {
                Assert.True(map.Apply(p + 0.05, 0.3) >= map.Apply(p, 0.3));
                Assert.True(map.Apply(p, 0.6) >= map.Apply(p, 0.3));
            }
        }
    }

    // ---- Forecaster: event cards, deaths, calibration -------------------------------------------------------------

    private static (DataPack Pack, ForecastCardPool Pool) KeyPack(int commons)
    {
        // All skills: an attack-only Key would fill every shop attack slot by itself.
        var cards = new List<PoolCard> { new(Key, Char, PoolRarity.Common, "skill") };
        for (var i = 0; i < commons; i++) cards.Add(new PoolCard($"CARD.C{i}", Char, PoolRarity.Common, "skill"));
        var tiers = cards.ToDictionary(c => c.Id, c => new CardTierEntry { Tier = c.Id == Key ? Tier.S : Tier.D }, StringComparer.Ordinal);
        var pack = new DataPack
        {
            Tiers = new Dictionary<string, TierPack>(StringComparer.Ordinal) { [Char] = new TierPack { Character = Char, Cards = tiers } },
            Archetypes =
            [
                new Archetype
                {
                    Id = "test.key", Character = Char, Names = new() { ["kor"] = "열쇠 덱" }, FullWeight = 1.0,
                    Cards = new ArchetypeCards { Core = [Key] },
                },
            ],
        };
        return (pack, new ForecastCardPool(cards));
    }

    private static RunState State(params string[] deck) => new()
    {
        Character = Char,
        Act = 1,
        Floor = 2,
        Deck = deck.Select(id => new DeckCard(new CardRef(id))).ToArray(),
    };

    [Fact]
    public void Deaths_LowerTheUnconditionalNumber_AndNoDeathsChangeNothing()
    {
        var (pack, pool) = KeyPack(commons: 60);
        var f = new ArchetypeForecaster(pack, pool, cacheCapacity: 0, calibration: ForecastCalibration.Identity);
        var request = new ForecastRequest { State = State() };

        var deadly = f.Forecast(request, new ForecastOptions { Simulations = 4000, Seed = 5, ActDeathChance = [0.5, 0.5, 0.5] }).Archetypes[0];
        var safe = f.Forecast(request, new ForecastOptions { Simulations = 4000, Seed = 5, ActDeathChance = null }).Archetypes[0];

        Assert.True(deadly.EndOfRunAll.PComplete < deadly.EndOfRun.PComplete - 0.05,
            $"all {deadly.EndOfRunAll.PComplete:P1} vs survive {deadly.EndOfRun.PComplete:P1}");
        Assert.Equal(safe.EndOfRun.PComplete, safe.EndOfRunAll.PComplete, 9);
    }

    [Fact]
    public void EventCards_RaiseTheChance()
    {
        var (pack, pool) = KeyPack(commons: 400);
        var f = new ArchetypeForecaster(pack, pool, cacheCapacity: 0);
        var request = new ForecastRequest { State = State() };

        // Key is 1 card in 401: each extra random card adds ~0.25 %, and a run has several "?" events left.
        var none = f.Forecast(request, new ForecastOptions { Simulations = 8000, Seed = 8, EventCardChance = 0 }).Archetypes[0];
        var many = f.Forecast(request, new ForecastOptions { Simulations = 8000, Seed = 8, EventCardChance = 1 }).Archetypes[0];

        Assert.True(many.EndOfRun.PComplete > none.EndOfRun.PComplete + 0.008, $"{many.EndOfRun.PComplete:P1} vs {none.EndOfRun.PComplete:P1}");
        Assert.True(many.EndOfRun.PComplete < 1);
    }

    [Fact]
    public void Calibration_IsAppliedToBothNumbers_AndCompleteStaysCertain()
    {
        var (pack, pool) = KeyPack(commons: 60);
        var shift = new PlattMap(1, 1); // σ(logit p + 1)
        var f = new ArchetypeForecaster(pack, pool, cacheCapacity: 0, calibration: new ForecastCalibration(shift, PlattMap.Identity));
        var options = new ForecastOptions { Simulations = 2000, Seed = 4 };

        var open = f.Forecast(new ForecastRequest { State = State() }, options).Archetypes[0];
        Assert.Equal(shift.Apply(open.EndOfRun.PComplete, 0), open.CalibratedIfSurvive, 9);
        Assert.Equal(PlattMap.Identity.Apply(open.EndOfRunAll.PComplete, 0), open.CalibratedAll, 9);
        Assert.True(open.CalibratedIfSurvive > open.EndOfRun.PComplete);

        var done = f.Forecast(new ForecastRequest { State = State(Key) }, options).Archetypes[0];
        Assert.True(done.IsCompleteNow);
        Assert.Equal(1.0, done.CalibratedIfSurvive);
        Assert.Equal(1.0, done.CalibratedAll);
    }

    // ---- ForecastFit on the fixture runs --------------------------------------------------------------------------

    [Fact]
    public void Measure_FixtureRuns_GivesSaneRates()
    {
        var runs = TestPaths.AllRuns.Select(p => (RunHistoryReader.ReadFile(p), (long?)null)).ToList();
        var rates = ForecastFit.Measure(runs, RealPool.Value);

        Assert.True(rates.EventVisits > 0);
        Assert.InRange(rates.EventCardChance, 0.05, 1.5);
        Assert.Equal(3, rates.ActDeathChance.Count);
        Assert.All(rates.ActDeathChance, h => Assert.InRange(h, 0, 1));
        Assert.True(rates.Reached[0] >= rates.Reached[1] && rates.Reached[1] >= rates.Reached[2]);

        var options = ForecastOptions.Default.Apply(rates);
        Assert.Equal(Math.Min(1, rates.EventCardChance), options.EventCardChance);
        Assert.Same(rates.ActDeathChance, options.ActDeathChance);
    }
}
