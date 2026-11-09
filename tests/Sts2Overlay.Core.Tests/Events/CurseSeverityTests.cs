using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Events;

/// <summary>Ethereal curses (Clumsy) cost less than the flat curse penalty, on the real pack.</summary>
public class CurseSeverityTests
{
    private static readonly string Dir = Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1");
    private static readonly EventScorer Scorer = new(DataPackLoader.LoadDirectory(Dir), EventCatalog.LoadDirectory(Dir));

    private static RunState State(int act, int hp, int gold) => new()
    {
        Character = "CHARACTER.IRONCLAD",
        Act = act,
        Hp = hp,
        MaxHp = 80,
        Gold = gold,
        Deck = Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_IRONCLAD")), 5)
            .Concat(Enumerable.Repeat(new DeckCard(new CardRef("CARD.DEFEND_IRONCLAD")), 4))
            .ToArray(),
    };

    [Theory]
    [InlineData(1, 70, 99)]
    [InlineData(1, 40, 99)]
    [InlineData(2, 60, 150)]
    [InlineData(3, 60, 300)]
    public void ThisOrThat_RecommendsTheRelicWithClumsy(int act, int hp, int gold)
    {
        var e = Scorer.Evaluate(State(act, hp, gold), "EVENT.THIS_OR_THAT")!;
        Assert.Equal("ORNATE", e.Best!.Option.ShortName);
    }

    [Fact]
    public void CursePenalty_IsScaledPerCurse()
    {
        var w = new EventWeights();
        Assert.Equal(10, w.CursePenaltyFor("CARD.CLUMSY"), 6);
        Assert.Equal(25, w.CursePenaltyFor("CARD.DECAY"), 6);
        Assert.Equal(25, w.CursePenaltyFor(null), 6);
    }
}
