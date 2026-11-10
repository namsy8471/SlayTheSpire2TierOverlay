using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Events;

/// <summary>
/// A log-only run (co-op guest) has placeholder HP and gold (<see cref="RunState.VitalsKnown"/> false): HP loss is never
/// called lethal (but carries a caution), and a gold cost is never called unaffordable.
/// </summary>
public class EventScorerUnknownVitalsTests
{
    private const string Json = """
    {
      "events": [
        {
          "id": "EVENT.TEST_SHRINE",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "TEST_SHRINE.pages.INITIAL.options.PAY", "effects": [ { "type": "gold", "amount": -100 }, { "type": "relic", "tier": "rare" } ] },
              { "key": "TEST_SHRINE.pages.INITIAL.options.BLEED", "effects": [ { "type": "hpLoss", "amount": 40 }, { "type": "gold", "amount": 200 } ] },
              { "key": "TEST_SHRINE.pages.INITIAL.options.DEEPER", "effects": [], "next": "DEEP" },
              { "key": "TEST_SHRINE.pages.INITIAL.options.LEAVE", "effects": [], "leave": true }
            ]},
            { "page": "DEEP", "options": [
              { "key": "TEST_SHRINE.pages.DEEP.options.CUT", "effects": [ { "type": "hpLoss", "amount": 5 }, { "type": "gold", "amount": 300 } ] }
            ]}
          ]
        }
      ]
    }
    """;

    private static readonly EventScorer Scorer = new(new DataPack(), EventCatalog.Parse(Json));

    private static RunState State(int hp, int gold, bool known) => new()
    {
        Character = "CHARACTER.IRONCLAD",
        Hp = hp,
        MaxHp = 80,
        Gold = gold,
        VitalsKnown = known,
        Deck = Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_IRONCLAD")), 5).ToArray(),
    };

    private static EventOptionEvaluation Option(EventEvaluation eval, string name) => eval.Options.Single(o => o.Option.ShortName == name);

    [Fact]
    public void Hp_loss_with_unknown_hp_is_not_lethal_but_carries_a_caution()
    {
        // With known HP 30, losing 40 is lethal; with unknown HP the same placeholder must not decide that.
        var known = Scorer.Evaluate(State(30, 0, known: true), "EVENT.TEST_SHRINE")!;
        Assert.True(Option(known, "BLEED").IsLethal);
        Assert.False(Option(known, "BLEED").HpLossUnknown);

        var unknown = Scorer.Evaluate(State(30, 0, known: false), "EVENT.TEST_SHRINE")!;
        var bleed = Option(unknown, "BLEED");
        Assert.False(bleed.IsLethal);
        Assert.True(bleed.HpLossUnknown);
        Assert.DoesNotContain(bleed.Score.Components, c => c.Key == EventComponentKeys.Death);
        Assert.Contains(unknown.Options, o => o.Option.ShortName == "BLEED"); // never dropped
        Assert.False(Option(unknown, "LEAVE").HpLossUnknown);
        Assert.False(Option(unknown, "PAY").HpLossUnknown);
    }

    [Fact]
    public void A_follow_up_page_that_costs_hp_passes_the_caution_on()
    {
        var unknown = Scorer.Evaluate(State(80, 0, known: false), "EVENT.TEST_SHRINE")!;
        Assert.True(Option(unknown, "DEEPER").HpLossUnknown);
    }

    [Fact]
    public void Unknown_gold_never_marks_a_gold_cost_unaffordable()
    {
        Assert.True(Option(Scorer.Evaluate(State(80, 50, known: true), "EVENT.TEST_SHRINE")!, "PAY").IsUnaffordable);
        Assert.False(Option(Scorer.Evaluate(State(80, 50, known: false), "EVENT.TEST_SHRINE")!, "PAY").IsUnaffordable);
    }

    [Fact]
    public void A_locked_button_stays_unaffordable_with_unknown_gold()
    {
        // "_LOCKED" comes from the game itself (the log names the greyed-out variant), not from our gold arithmetic.
        var keys = new[] { "TEST_SHRINE.pages.INITIAL.options.PAY_LOCKED", "TEST_SHRINE.pages.INITIAL.options.LEAVE" };
        var eval = Scorer.Evaluate(State(80, 500, known: false), "EVENT.TEST_SHRINE", keys)!;
        Assert.True(eval.Options[0].IsUnaffordable);
    }
}
