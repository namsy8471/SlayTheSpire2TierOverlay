using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Tests.Events;

/// <summary>Checks the committed <c>data/packs/v0.107.1/events.json</c> (66 events read from the decompiled game code).</summary>
public class EventCatalogTests
{
    private static readonly EventCatalog Catalog =
        EventCatalog.LoadDirectory(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1"));

    [Fact]
    public void HasEveryEventOfTheBuild()
    {
        Assert.Equal(66, Catalog.Events.Count);
        Assert.Equal(Catalog.Events.Count, Catalog.Events.Select(e => e.Id).Distinct().Count());
        Assert.All(Catalog.Events, e => Assert.StartsWith("EVENT.", e.Id));
        Assert.NotNull(Catalog.Find("EVENT.NEOW"));
        Assert.True(Catalog.Find("EVENT.NEOW")!.IsAncient);
    }

    [Fact]
    public void OptionKeysBelongToTheirEvent()
    {
        foreach (var e in Catalog.Events)
        {
            var entry = e.Id["EVENT.".Length..];
            // The generic "PROCEED" key is shared (EventModel's leave button).
            foreach (var o in e.Pages.SelectMany(p => p.Options).Where(o => o.Key != "PROCEED"))
            {
                Assert.True(o.Key.StartsWith(entry + ".", StringComparison.Ordinal), $"{e.Id}: {o.Key}");
            }
        }
    }

    [Fact]
    public void NextPagesExist()
    {
        foreach (var e in Catalog.Events)
        {
            foreach (var o in e.Pages.SelectMany(p => p.Options).Where(o => o.Next is not null))
            {
                // A few next pages are random branches named in the notes (e.g. Trial's scenario); the rest must exist.
                if (o.Next!.StartsWith("RANDOM", StringComparison.Ordinal)) continue;
                Assert.True(e.FindPage(o.Next) is not null, $"{e.Id}: {o.Key} → {o.Next}");
            }
        }
    }

    [Fact]
    public void EveryAmountFormulaEvaluates()
    {
        var state = new RunState
        {
            Character = "CHARACTER.IRONCLAD", Hp = 60, MaxHp = 80, Gold = 150, Act = 2, Floor = 20,
            Deck = Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_IRONCLAD")), 5).ToArray(),
        };
        var scorer = new EventScorer(new DataPack(), Catalog);
        foreach (var e in Catalog.Events)
        {
            foreach (var page in e.Pages)
            {
                var eval = scorer.Evaluate(state, e.Id, page: page.Page)!;
                foreach (var o in eval.Options)
                {
                    var unknownFormula = o.Option.Effects.Any(x => x.Type != EventEffectTypes.Other
                        && ((x.Amount is { ValueKind: JsonValueKind.String } a && !Evaluates(a, state, e))
                            || (x.Count is { ValueKind: JsonValueKind.String } c && !Evaluates(c, state, e))));
                    Assert.False(unknownFormula, $"{o.Option.Key}");
                    Assert.True(double.IsFinite(o.Total), o.Option.Key);
                }
            }
        }
    }

    private static bool Evaluates(JsonElement el, RunState s, EventDef e)
    {
        var vars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["hp"] = s.Hp, ["currentHp"] = s.Hp, ["maxHp"] = s.MaxHp, ["gold"] = s.Gold, ["act"] = s.Act, ["floor"] = s.Floor,
            ["ascension"] = 0, ["deckSize"] = s.Deck.Count, ["basicStrikes"] = 5, ["basicDefends"] = 4,
        };
        foreach (var (k, v) in e.Vars) { vars.TryAdd(k, v); vars[$"var.{k}"] = v; }
        return EventFormula.TryEvaluate(el.GetString()!, vars, out _);
    }
}
