using Sts2Overlay.Core.Combat;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Events;

/// <summary>The training dummy (BATTLEWORN_DUMMY) options with a known damage forecast, on the real events.json.</summary>
public class DummyFightScoringTests
{
    private const string Setting1 = "BATTLEWORN_DUMMY.pages.INITIAL.options.SETTING_1";
    private const string Setting2 = "BATTLEWORN_DUMMY.pages.INITIAL.options.SETTING_2";
    private const string Setting3 = "BATTLEWORN_DUMMY.pages.INITIAL.options.SETTING_3";

    private static readonly EventScorer Scorer = new(new DataPack(),
        EventCatalog.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1")));

    // The dummy shows up in act 3 (ACT.GLORY).
    private static readonly RunState State = new()
    {
        Character = "CHARACTER.IRONCLAD",
        Act = 3,
        Hp = 60,
        MaxHp = 80,
        Gold = 100,
        Deck = Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_IRONCLAD")), 5)
            .Concat(Enumerable.Repeat(new DeckCard(new CardRef("CARD.DEFEND_IRONCLAD")), 5))
            .ToArray(),
    };

    /// <summary>Every simulated fight deals exactly <paramref name="total"/> damage.</summary>
    private static DamageForecast Always(double total)
        => new(Enumerable.Repeat(total, 100).ToArray(), [total / 3, total / 3, total / 3], 0, new ForecastCoverage());

    private static EventEvaluation Evaluate(EventContext? context)
        => Scorer.Evaluate(State, DummyFight.EventId, context: context)!;

    private static EventOptionEvaluation Option(EventEvaluation e, string key) => e.Options.Single(o => o.Option.Key == key);

    [Fact]
    public void SinglePlayer_KillChanceFollowsTheForecast_AndTheKillableSettingWins()
    {
        // 100 damage: the 75 HP setting always dies, 150 and 300 never do.
        var e = Evaluate(new EventContext { Forecast = Always(100) });

        Assert.Equal(75, Option(e, Setting1).DummyHp);
        Assert.Equal(150, Option(e, Setting2).DummyHp);
        Assert.Equal(300, Option(e, Setting3).DummyHp);
        Assert.Equal(1, Option(e, Setting1).WinChance);
        Assert.Equal(0, Option(e, Setting2).WinChance);
        Assert.Equal(0, Option(e, Setting3).WinChance);
        Assert.Equal(Setting1, e.Best!.Option.Key);
        Assert.Contains(Option(e, Setting1).EffectTexts, t => t.Contains("체력 75 인형을 3턴 안에 처치 · 예상 확률 100%"));
        Assert.All(e.Options, o => Assert.False(o.HasUnmodelled));
        Assert.NotNull(e.Forecast);
    }

    [Fact]
    public void Coop_ScalesTheHp_SoTheSameDamageMayNotBeEnough()
    {
        // Two players in act 3: 75 → 180. 150 damage no longer kills even the first setting.
        var e = Evaluate(new EventContext { PlayerCount = 2, Forecast = Always(150) });

        Assert.Equal(180, Option(e, Setting1).DummyHp);
        Assert.Equal(360, Option(e, Setting2).DummyHp);
        Assert.Equal(720, Option(e, Setting3).DummyHp);
        Assert.All(e.Options, o => Assert.Equal(0, o.WinChance));
    }

    [Fact]
    public void BiggerRewardWins_WhenEverySettingDies()
    {
        var weak = Evaluate(new EventContext { Forecast = Always(100) });
        var strong = Evaluate(new EventContext { Forecast = Always(1000) });

        Assert.All(strong.Options, o => Assert.Equal(1, o.WinChance));
        // A sure kill is worth more than a missed one for the same setting.
        Assert.True(Option(strong, Setting3).Total > Option(weak, Setting3).Total);
        Assert.NotEqual(Setting1, strong.Best!.Option.Key);
    }

    [Fact]
    public void WithoutAForecast_TheFightIsACoinFlip_AndMarkedUnmodelled()
    {
        var e = Evaluate(null);

        Assert.All(e.Options, o =>
        {
            Assert.Null(o.WinChance);
            Assert.True(o.HasUnmodelled);
        });
        Assert.Null(e.Forecast);
        Assert.Equal(75, Option(e, Setting1).DummyHp);
    }

    [Fact]
    public void PartialChance_WeightsTheReward()
    {
        // Half of the fights reach 75.
        var totals = Enumerable.Repeat(50.0, 50).Concat(Enumerable.Repeat(80.0, 50)).ToArray();
        var half = new DamageForecast(totals, [20, 20, 25], 0, new ForecastCoverage());
        var e = Evaluate(new EventContext { Forecast = half });
        Assert.Equal(0.5, Option(e, Setting1).WinChance!.Value, 6);

        var sure = Evaluate(new EventContext { Forecast = Always(80) });
        Assert.True(Option(sure, Setting1).Total > Option(e, Setting1).Total);
    }
}
