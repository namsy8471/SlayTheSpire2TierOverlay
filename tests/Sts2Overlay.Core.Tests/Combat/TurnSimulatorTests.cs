using System.Reflection;
using System.Text.Json;
using Sts2Overlay.Core.Combat;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Model;
using Xunit.Abstractions;

namespace Sts2Overlay.Core.Tests.Combat;

/// <summary>
/// The simulator's rules on small decks (checked by hand) with the v0.107.1 combat data, and on synthetic data.
/// The checks of the shipped combat data themselves run per version in <see cref="CombatPackTests"/>.
/// </summary>
public class TurnSimulatorTests
{
    private static string PackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1");
    private static readonly Lazy<CombatData> Real = new(() => CombatData.LoadPack(PackDir)!);

    internal static SimPlayer Deck(string character, params string[] cards) => new()
    {
        Character = character,
        Deck = cards.Select(c => new DeckCard(c.EndsWith('+') ? new CardRef(c[..^1], 1) : new CardRef(c))).ToArray(),
    };

    private static string[] Many(string id, int n) => Enumerable.Repeat(id, n).ToArray();

    private static DamageForecast Run(SimPlayer p, CombatData? data = null, int iterations = 200)
        => new TurnSimulator(data ?? Real.Value).Forecast(p, new SimOptions { Iterations = iterations });

    // ---- exact small decks (the rules, checked by hand) ------------------------------------------------------------

    [Fact]
    public void TenStrikes_ThreeEachTurn()
    {
        var f = Run(Deck("CHARACTER.IRONCLAD", Many("CARD.STRIKE_IRONCLAD", 10)));
        Assert.Equal([18.0, 18.0, 18.0], f.MeanPerTurn);
        Assert.Equal(1, f.ProbAtLeast(54));
        Assert.Equal(0, f.ProbAtLeast(55));
    }

    [Fact]
    public void Bash_VulnerableLastsUntilTheEnemyTurnEnds()
    {
        // Turn 1: 8 + Vulnerable 2. Enemy turn: 2 → 1. Turn 2: floor(8×1.5)=12, +2 → 3, enemy turn → 2. Turn 3: 12.
        var f = Run(Deck("CHARACTER.IRONCLAD", Many("CARD.BASH", 10)));
        Assert.Equal([8.0, 12.0, 12.0], f.MeanPerTurn);
    }

    [Fact]
    public void DeadlyPoison_TicksOnTheEnemyTurnAndDecays()
    {
        // 3×5 poison a turn: 15 tick → 14, +15 = 29 tick → 28, +15 = 43 tick.
        var f = Run(Deck("CHARACTER.SILENT", Many("CARD.DEADLY_POISON", 10)));
        Assert.Equal([15.0, 29.0, 43.0], f.MeanPerTurn);
        Assert.Equal(87, f.Mean, 6);
    }

    [Fact]
    public void Anger_CopiesItself_WithoutTheValueLoopingForever()
    {
        // Anger (0): 6 damage and a copy into the discard pile. Five in hand each turn, the draw pile runs dry on turn 3
        // and the shuffled discard (originals + copies) refills it: 5 × 6 every turn.
        var f = Run(Deck("CHARACTER.IRONCLAD", Many("CARD.ANGER", 10)));
        Assert.Equal([30.0, 30.0, 30.0], f.MeanPerTurn);
    }

    [Fact]
    public void MultiCast_EvokesTheFrontOrbXPlusOneTimes()
    {
        // Zap+ (0): channel Lightning. Multi-Cast+ with X = 3: evoke it 4 times, 8 each. Both cards come back every turn.
        var f = Run(Deck("CHARACTER.DEFECT", "CARD.ZAP+", "CARD.MULTI_CAST+"));
        Assert.Equal([32.0, 32.0, 32.0], f.MeanPerTurn);
    }

    private static SimPlayer Enchanted(string character, string card, int n, string enchantment, int amount = 1, int enchantedCount = -1) => new()
    {
        Character = character,
        Deck = Enumerable.Range(0, n)
            .Select(k => k < (enchantedCount < 0 ? n : enchantedCount)
                ? new DeckCard(new CardRef(card), EnchantmentId: enchantment, EnchantmentAmount: amount)
                : new DeckCard(new CardRef(card)))
            .ToArray(),
    };

    [Theory]
    [InlineData("ENCHANTMENT.SHARP", 2, 24.0)]     // 3 Strikes a turn, 6 + 2 each
    [InlineData("ENCHANTMENT.CORRUPTED", 1, 27.0)] // 6 × 1.5
    [InlineData("ENCHANTMENT.INSTINCT", 1, 36.0)]  // 6 × 2
    [InlineData("ENCHANTMENT.SPIRAL", 1, 36.0)]    // every play is played twice
    public void Enchantments_ChangeTheStrikesDamage(string enchantment, int amount, double perTurn)
    {
        var f = Run(Enchanted("CHARACTER.IRONCLAD", "CARD.STRIKE_IRONCLAD", 10, enchantment, amount));
        Assert.Equal([perTurn, perTurn, perTurn], f.MeanPerTurn);
    }

    [Fact]
    public void Imbued_IsPlayedForFreeOnTurnOne()
    {
        // The Imbued Strike starts at the bottom of the draw pile and is played for free after the turn-1 draw: 6 + 3 × 6.
        var f = Run(Enchanted("CHARACTER.IRONCLAD", "CARD.STRIKE_IRONCLAD", 10, "ENCHANTMENT.IMBUED", enchantedCount: 1));
        Assert.Equal([24.0, 18.0, 18.0], f.MeanPerTurn);
    }

    [Fact]
    public void JeweledMask_TheFreePowerStaysFreeOnTurnOne()
    {
        // Jeweled Mask puts Inflame (the only Power) into the hand at 0 cost for turn 1: Strength 2, then 3 Strikes of 8.
        var p = Deck("CHARACTER.IRONCLAD", ["CARD.INFLAME", .. Many("CARD.STRIKE_IRONCLAD", 9)]) with { Relics = ["RELIC.JEWELED_MASK"] };
        var f = Run(p);
        Assert.Equal([24.0, 24.0, 24.0], f.MeanPerTurn);
    }

    private const string Synthetic = """
    {
      "cards": {
        "TEST.DOOM": { "model": "full", "cost": 1, "type": "Skill", "play": [ { "op": "doom", "n": 10 } ] },
        "TEST.HIT": { "model": "full", "cost": 1, "type": "Attack", "play": [ { "op": "attack", "dmg": 5 } ] },
        "TEST.FORM": { "model": "full", "cost": 3, "type": "Power", "keywords": ["Innate"],
                       "play": [ { "op": "perTurn", "when": "start", "effects": [ { "op": "strength", "n": 2 } ] } ] },
        "TEST.X": { "model": "full", "cost": "X", "type": "Attack", "play": [ { "op": "attack", "dmg": 4, "hits": "X" } ] },
        "TEST.DOUBLE": { "model": "full", "cost": 0, "type": "Skill", "keywords": ["Innate"],
                         "play": [ { "op": "replayNext", "filter": "attack", "n": 1 } ] },
        "TEST.ECHO": { "model": "full", "cost": 0, "type": "Power", "keywords": ["Innate"],
                       "play": [ { "op": "onPlay", "filter": "any", "effects": [ { "op": "damage", "n": 1 } ] } ] },
        "TEST.AGAIN": { "model": "full", "cost": 0, "type": "Skill", "keywords": ["Innate"],
                        "play": [ { "op": "damage", "n": 2 }, { "op": "replayNext", "filter": "any", "n": 1 } ] }
      },
      "characters": { "CHARACTER.TEST": { "energy": 3, "draw": 5 } }
    }
    """;

    private static readonly CombatData SyntheticData = CombatData.Parse(Synthetic);

    [Fact]
    public void Doom_KillsAtTheDoomCheckWhenItReachesTheRemainingHp()
    {
        // 30 Doom a turn, never damage: the best Doom check is 90 after the third enemy turn.
        var f = Run(Deck("CHARACTER.TEST", Many("TEST.DOOM", 10)), SyntheticData);
        Assert.Equal(1, f.ProbAtLeast(90));
        Assert.Equal(0, f.ProbAtLeast(91));
        Assert.Equal([0.0, 0.0, 0.0], f.MeanPerTurn);
    }

    [Fact]
    public void PerTurnStrength_StartsOnTheNextTurn()
    {
        // Turn 1: the innate power uses all 3 energy. Turn 2: +2 Strength, 3 hits of 7. Turn 3: +2 more, 3 hits of 9.
        var f = Run(Deck("CHARACTER.TEST", ["TEST.FORM", .. Many("TEST.HIT", 9)]), SyntheticData);
        Assert.Equal([0.0, 21.0, 27.0], f.MeanPerTurn);
    }

    [Fact]
    public void XCost_SpendsAllEnergy_AndReplayPlaysTheNextAttackTwice()
    {
        // Innate replay (0) then the X attack with X = 3: 3 hits of 4, twice.
        var f = Run(Deck("CHARACTER.TEST", "TEST.DOUBLE", "TEST.X"), SyntheticData);
        Assert.Equal(24, f.MeanPerTurn[0], 6);
    }

    [Fact]
    public void APower_DoesNotFireOnTheCardThatMadeIt()
    {
        // Echo (0, Innate): 1 damage after each card played. It is played first; only the 3 Hits after it trigger it.
        var f = Run(Deck("CHARACTER.TEST", "TEST.ECHO", "TEST.HIT", "TEST.HIT", "TEST.HIT"), SyntheticData);
        Assert.Equal(18, f.MeanPerTurn[0], 6);
    }

    [Fact]
    public void AReplay_IsForTheNextCard_NotTheCardThatSetItUp()
    {
        // Again (0, Innate): 2 damage, then the next card is played twice. The play count is fixed before the card's own
        // effects run, so Again is not replayed by itself: 2 + Hit twice (10).
        var f = Run(Deck("CHARACTER.TEST", "TEST.AGAIN", "TEST.HIT"), SyntheticData);
        Assert.Equal(12, f.MeanPerTurn[0], 6);
    }

    [Fact]
    public void MysticLighter_AddsNineToEnchantedCardsHits()
    {
        // Clone does nothing in a fight; Mystic Lighter only cares that the card has an enchantment: 3 Strikes of 6 + 9.
        var p = Enchanted("CHARACTER.IRONCLAD", "CARD.STRIKE_IRONCLAD", 10, "ENCHANTMENT.CLONE") with { Relics = ["RELIC.MYSTIC_LIGHTER"] };
        Assert.Equal([45.0, 45.0, 45.0], Run(p).MeanPerTurn);
    }

    [Fact]
    public void Combine_AddsThePlayersSampleBySample()
    {
        var a = Run(Deck("CHARACTER.IRONCLAD", Many("CARD.STRIKE_IRONCLAD", 10)));
        var b = Run(Deck("CHARACTER.SILENT", Many("CARD.DEADLY_POISON", 10)));
        var both = DamageForecast.Combine([a, b]);
        Assert.Equal(54 + 87, both.Mean, 6);
        Assert.Equal(1, both.ProbAtLeast(141));
        Assert.Equal([33.0, 47.0, 61.0], both.MeanPerTurn);
    }

    // ---- multiplayer HP scaling (ScaleHpForMultiplayer; checked against run history) --------------------------------

    [Theory]
    [InlineData(75, 1, 2, 75)]
    [InlineData(300, 1, 2, 300)]
    [InlineData(75, 2, 2, 180)]
    [InlineData(150, 2, 2, 360)]
    [InlineData(300, 2, 2, 720)]
    [InlineData(75, 2, 0, 165)]
    public void DummyHp_ScalesWithPlayersAndAct(double baseHp, int players, int actIndex, int expected)
        => Assert.Equal(expected, DummyFight.ScaleHp(baseHp, players, actIndex));

    // ---- helpers ----------------------------------------------------------------------------------------------------

    internal static IEnumerable<(string Owner, CombatOp Op)> AllOps(CombatData data)
    {
        foreach (var id in data.CardIds)
        {
            foreach (var level in new[] { data.Card(id, 0)!, data.Card(id, 1)! })
            {
                foreach (var list in new[] { level.Play, level.OnDraw, level.OnExhaust, level.EndOfTurnInHand })
                {
                    foreach (var op in Flatten(list)) yield return (id, op);
                }
            }
        }
        foreach (var r in data.Relics)
        {
            foreach (var list in new[] { r.CombatStart, r.TurnStart, r.TurnEnd })
            {
                foreach (var op in Flatten(list)) yield return (r.Id, op);
            }
        }
        foreach (var p in data.Potions)
        {
            foreach (var op in Flatten(p.Use)) yield return (p.Id, op);
        }
    }

    private static IEnumerable<CombatOp> Flatten(IEnumerable<CombatOp> ops)
    {
        foreach (var op in ops)
        {
            yield return op;
            foreach (var inner in Flatten(op.Effects)) yield return inner;
        }
    }

    internal static IEnumerable<(string Owner, CombatAmount Amount)> AllAmounts(CombatData data)
    {
        foreach (var (owner, op) in AllOps(data))
        {
            yield return (owner, op.Dmg);
            yield return (owner, op.Hits);
            yield return (owner, op.N);
            yield return (owner, op.Times);
        }
        foreach (var id in data.CardIds)
        {
            foreach (var level in new[] { data.Card(id, 0)!, data.Card(id, 1)! })
            {
                if (level.CostExpr is { } c) yield return (id, c);
                if (level.PlayableIf is { } p) yield return (id, p);
            }
        }
    }
}

/// <summary>
/// Checks the shipped combat data (<c>data/packs/&lt;version&gt;/combat</c>) against the catalogs of the same version and
/// the simulator's vocabulary. One subclass per shipped data version.
/// </summary>
public abstract class CombatPackTests(ITestOutputHelper output)
{
    /// <summary>The pack and catalog version under test.</summary>
    protected abstract string Version { get; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<CombatData>> s_data = new(StringComparer.Ordinal);

    private string PackDir => Path.Combine(TestPaths.RepoRoot, "data", "packs", Version);

    /// <summary>Loaded once per version (xUnit makes a new instance per test).</summary>
    private CombatData Data => s_data.GetOrAdd(Version, _ => new Lazy<CombatData>(() => CombatData.LoadPack(PackDir)!)).Value;

    [Fact]
    public void HasEveryCardAndTheFiveCharacters()
    {
        var data = Data;
        var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "catalog", $"cards.{Version}.json")));
        var ids = catalog.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(ids.Length, data.CardCount);
        Assert.All(ids, id => Assert.NotNull(data.Card(id)));
        Assert.Equal(5, data.Characters.Count());
        Assert.Equal(3, data.Character("CHARACTER.DEFECT").OrbSlots);
        Assert.Equal(0, data.Character("CHARACTER.IRONCLAD").OrbSlots);
        Assert.NotEmpty(data.RandomPool("CARD_POOL.IRONCLAD_CARD_POOL"));
        Assert.Null(data.Relic("RELIC.BURNING_BLOOD")); // heals only: listed as irrelevant
        Assert.NotNull(data.Relic("RELIC.DIVINE_RIGHT"));
    }

    [Fact]
    public void EveryFormulaOnlyUsesKnownVariables()
    {
        var data = Data;
        var names = SimFight.VariableNames(new TurnSimulator.Setup(data, TurnSimulatorTests.Deck("CHARACTER.IRONCLAD", "CARD.STRIKE_IRONCLAD")));
        var vars = names.ToDictionary(n => n, _ => 2.0, StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        foreach (var (owner, amount) in TurnSimulatorTests.AllAmounts(data))
        {
            if (amount.Formula is { } f && !EventFormula.TryEvaluate(f, vars, out _)) bad.Add($"{owner}: {f}");
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void EveryOpIsOneTheSimulatorKnows()
    {
        var known = typeof(CombatOps).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        var unknown = TurnSimulatorTests.AllOps(Data).Where(x => !known.Contains(x.Op.Op)).Select(x => $"{x.Owner}: {x.Op.Op}").Distinct().ToList();
        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData("CHARACTER.IRONCLAD")]
    [InlineData("CHARACTER.SILENT")]
    [InlineData("CHARACTER.DEFECT")]
    [InlineData("CHARACTER.NECROBINDER")]
    [InlineData("CHARACTER.REGENT")]
    public void StarterDecks_DealPlausibleDamage(string character)
    {
        var decks = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "catalog", $"starting-decks.{Version}.json")));
        var entry = decks.RootElement.GetProperty("characters").EnumerateArray().First(c => c.GetProperty("character").GetString() == character);
        var player = new SimPlayer
        {
            Character = character,
            Deck = entry.GetProperty("deck").EnumerateArray().Select(c => new DeckCard(new CardRef(c.GetString()!))).ToArray(),
            Relics = entry.GetProperty("relics").EnumerateArray().Select(r => r.GetString()!).ToArray(),
        };
        var f = new TurnSimulator(Data).Forecast(player, new SimOptions { Iterations = 2000 });
        output.WriteLine($"{character}: mean {f.Mean:0.0} per turn {string.Join(" / ", f.MeanPerTurn.Select(t => t.ToString("0.0")))} P(75)={f.ProbAtLeast(75):P0}");
        Assert.Empty(f.Coverage.UnknownCards);
        Assert.InRange(f.Mean, 20, 75);
        Assert.All(f.MeanPerTurn, t => Assert.InRange(t, 3, 40));
    }

    [Fact]
    public void CountFieldsAreNumbers()
    {
        // These fields are read as plain integers: a formula there would be dropped without a word (it once hid
        // Multi-Cast's "times": "X"). Formulas belong in dmg / hits / n / times.
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackDir, "combat", "cards.json")));
        var intFields = new[] { "choose", "setCost", "count", "every", "limitPerTurn" };
        var bad = new List<string>();
        void Walk(string owner, JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray()) Walk(owner, item);
                return;
            }
            if (e.ValueKind != JsonValueKind.Object) return;
            foreach (var p in e.EnumerateObject())
            {
                if (intFields.Contains(p.Name) && p.Value.ValueKind != JsonValueKind.Number) bad.Add($"{owner}.{p.Name} = {p.Value}");
                Walk(owner, p.Value);
            }
        }
        foreach (var card in doc.RootElement.GetProperty("cards").EnumerateObject()) Walk(card.Name, card.Value);
        Assert.Empty(bad);
    }
}

public sealed class CombatPackTestsV0107(ITestOutputHelper output) : CombatPackTests(output)
{
    protected override string Version => "v0.107.1";
}

public sealed class CombatPackTestsV0111(ITestOutputHelper output) : CombatPackTests(output)
{
    protected override string Version => "v0.111.0";
}
