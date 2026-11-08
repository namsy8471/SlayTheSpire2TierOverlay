using System.Text.RegularExpressions;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Events;

/// <summary>Event option texts and score labels in English (<see cref="Loc"/>): no Korean may leak into any of them.</summary>
public class EventScorerEnglishTests
{
    private static readonly Regex Hangul = new("[가-힣]", RegexOptions.Compiled);

    private static readonly string PackDir = Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1");
    private static readonly EventCatalog RealCatalog = EventCatalog.LoadDirectory(PackDir);
    private static readonly DataPack RealPack = DataPackLoader.LoadDirectory(PackDir);

    private static RunState State(int hp, int maxHp = 80, int gold = 99, int curses = 0) => new()
    {
        Character = "CHARACTER.IRONCLAD",
        Act = 2,
        Floor = 20,
        Hp = hp,
        MaxHp = maxHp,
        Gold = gold,
        Deck = Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_IRONCLAD")), 5)
            .Concat(Enumerable.Repeat(new DeckCard(new CardRef("CARD.DEFEND_IRONCLAD")), 4))
            .Append(new DeckCard(new CardRef("CARD.BASH")))
            .Concat(Enumerable.Repeat(new DeckCard(new CardRef("CARD.DOUBT")), curses))
            .ToArray(),
    };

    /// <summary>Every user-facing string of an evaluation: option texts, component labels and non-id details.</summary>
    private static IEnumerable<string> Texts(EventEvaluation eval)
        => eval.Options.Concat(eval.NotOffered).SelectMany(o => o.EffectTexts
            .Concat(o.Score.Components.Select(c => c.Label))
            .Concat(o.Score.Components.Select(c => c.Detail).OfType<string>()));

    [Fact]
    public void Every_event_of_the_pack_scores_without_Korean_in_English()
    {
        RunState[] states = [State(80), State(6, gold: 0), State(40, gold: 400, curses: 2), State(79, maxHp: 79, gold: 999)];
        var texts = new List<string>();
        var scorer = new EventScorer(RealPack, RealCatalog);
        using (Loc.Use(Lang.Eng))
        {
            foreach (var state in states)
            {
                foreach (var ev in RealCatalog.Events)
                {
                    foreach (var page in ev.Pages)
                    {
                        if (scorer.Evaluate(state, ev.Id, page: page.Page) is { } byPage) texts.AddRange(Texts(byPage));
                        // As logged: the page's keys, which also scores an ancient's not-offered pool.
                        var keys = page.Options.Select(o => o.Key).Take(3).ToArray();
                        if (keys.Length > 0 && scorer.Evaluate(state, ev.Id, keys) is { } byKeys) texts.AddRange(Texts(byKeys));
                    }
                }
            }
        }

        Assert.NotEmpty(texts);
        var korean = texts.Where(t => Hangul.IsMatch(t)).Distinct().ToList();
        Assert.True(korean.Count == 0, "Korean in English event texts: " + string.Join(" | ", korean.Take(10)));
        // The sweep reaches the common effect kinds.
        Assert.Contains(texts, t => t.StartsWith("Lose ", StringComparison.Ordinal) && t.Contains(" HP", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Remove ", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Upgrade ", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("Gold", StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_sweep_in_Korean_still_speaks_Korean()
    {
        var scorer = new EventScorer(RealPack, RealCatalog);
        var eval = scorer.Evaluate(State(40), "EVENT.ABYSSAL_BATHS")!;
        Assert.Contains("최대 체력 +2", eval.Options[0].EffectTexts);
        Assert.Contains("체력 3 잃음", eval.Options[0].EffectTexts);
    }

    private const string Json = """
    {
      "events": [
        {
          "id": "EVENT.TEST_MIX",
          "pages": [
            { "page": "INITIAL", "options": [
              { "key": "TEST_MIX.pages.INITIAL.options.A", "effects": [ { "type": "maxHp", "amount": 2 }, { "type": "hpLoss", "amount": 3 } ], "next": "NEXT" },
              { "key": "TEST_MIX.pages.INITIAL.options.B", "effects": [ { "type": "heal", "amount": 10 }, { "type": "gold", "amount": 50, "chance": 0.5 } ] },
              { "key": "TEST_MIX.pages.INITIAL.options.C", "effects": [ { "type": "cardRemove", "count": 2, "how": "choose" }, { "type": "gold", "amount": -100 } ] },
              { "key": "TEST_MIX.pages.INITIAL.options.D", "effects": [ { "type": "relic" }, { "type": "curse", "count": 2 }, { "type": "potion", "count": 2 } ] },
              { "key": "TEST_MIX.pages.INITIAL.options.E", "effects": [ { "type": "cardUpgrade", "count": 2, "how": "random" }, { "type": "cardTransform", "count": 1, "how": "choose" } ] },
              { "key": "TEST_MIX.pages.INITIAL.options.LEAVE", "effects": [], "leave": true }
            ]},
            { "page": "NEXT", "options": [
              { "key": "TEST_MIX.pages.NEXT.options.FIGHT", "effects": [ { "type": "fight" } ] }
            ]}
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Effect_one_liners_read_naturally_in_English()
    {
        var scorer = new EventScorer(new DataPack(), EventCatalog.Parse(Json));
        using var _ = Loc.Use(Lang.Eng);

        var eval = scorer.Evaluate(State(40, gold: 150), "EVENT.TEST_MIX")!;
        var o = eval.Options.ToDictionary(x => x.Option.Key[(x.Option.Key.LastIndexOf('.') + 1)..], x => x.EffectTexts);

        Assert.Equal(["Max HP +2", "Lose 3 HP", "More choices follow"], o["A"]);
        Assert.Equal(["Heal 10 HP", "Gain 50 Gold (50% chance)"], o["B"]);
        Assert.Equal(["Remove 2 cards", "Pay 100 Gold"], o["C"]);
        Assert.Equal(["Random Relic", "Gain 2 Curses", "2 Potions"], o["D"]);
        Assert.Equal(["Upgrade 2 random cards", "Transform 1 card"], o["E"]);
        Assert.Equal(["Nothing happens"], o["LEAVE"]);

        // The removal's detail names what it took (two basic cards: this pack knows no curses).
        var removal = eval.Options.Single(x => x.Option.Key.EndsWith(".C", StringComparison.Ordinal))
            .Score.Components.Single(c => c.Key == EventComponentKeys.Deck);
        Assert.Equal("Remove 2 cards", removal.Label);
        Assert.Equal("Basic 2", removal.Detail);
    }
}
