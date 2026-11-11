using System.Text.Json;
using Sts2Overlay.GameState.Tracking;
using Xunit.Abstractions;

namespace Sts2Overlay.GameState.Tests;

public class AncientOptionsTests(ITestOutputHelper output)
{
    private sealed record Visit(string Run, string Event, int Slot, int Players, AncientInputs Inputs, string[] Truth);

    /// <summary>tests/fixtures/ancient-visits.v0.107.1.json: every act-start ancient of the user's v0.107.1 history (no ids).</summary>
    private static readonly Lazy<IReadOnlyList<Visit>> s_visits = new(() =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "ancient-visits.v0.107.1.json")));
        return doc.RootElement.GetProperty("visits").EnumerateArray().Select(v => new Visit(
            v.GetProperty("run").GetString()!,
            v.GetProperty("event").GetString()!,
            v.GetProperty("slot").GetInt32(),
            v.GetProperty("players").GetInt32(),
            new AncientInputs
            {
                RunSeed = v.GetProperty("seed").GetString()!,
                Slot = v.GetProperty("slot").GetInt32(),
                ActIndex = v.GetProperty("act").GetInt32(),
                Character = v.GetProperty("character").GetString()!,
                Deck = v.GetProperty("deck").EnumerateArray().Select(c => c.GetString()!.Split('|'))
                    .Select(p => new AncientDeckCard(p[0], p.Length > 1 ? p[1] : null)).ToArray(),
                Relics = v.GetProperty("relics").EnumerateArray().Select(r => r.GetString()!).ToArray(),
                Scheme = RngScheme.Legacy32, // recorded on v0.107.1
            },
            v.GetProperty("truth").EnumerateArray().Select(t => t.GetString()!).ToArray())).ToList();
    });

    /// <summary>History notation: Sea Glass is listed by its character's entry (<c>SILENT</c>).</summary>
    private static string[] Replay(Visit v, AncientInputs? inputs = null)
    {
        var draw = AncientOptions.Draw(v.Event, inputs ?? v.Inputs)!;
        return draw.Options.Select(o => o == AncientOptions.SeaGlass ? draw.SeaGlassCharacter!["CHARACTER.".Length..] : o).ToArray();
    }

    [Fact]
    public void Replays_every_recorded_ancient_visit_exactly()
    {
        var visits = s_visits.Value;
        foreach (var g in visits.GroupBy(v => v.Event).OrderBy(g => g.Key))
            output.WriteLine($"{g.Key,-16} {g.Count(v => Replay(v).SequenceEqual(v.Truth))}/{g.Count()}");
        Assert.Equal(42, visits.Count);
        Assert.Equal(7, visits.Select(v => v.Event).Distinct().Count()); // every act-start ancient is covered
        Assert.All(visits, v => Assert.True(Replay(v).SequenceEqual(v.Truth),
            $"{v.Run} {v.Event} slot {v.Slot}: game [{string.Join(", ", v.Truth)}] replay [{string.Join(", ", Replay(v))}]"));
    }

    [Fact]
    public void The_deck_and_relics_decide_some_real_draws()
    {
        // Same seeds with an empty deck and no relics: the conditional options (Claw, Tooth, Legion, Soup, Tri-Boomerang,
        // Bracelet, Touch/Tooth) change real draws, so the inputs are needed, not decoration.
        var changed = s_visits.Value.Where(v => !Replay(v, v.Inputs with { Deck = [], Relics = [] }).SequenceEqual(v.Truth)).ToList();
        foreach (var v in changed) output.WriteLine($"{v.Run} {v.Event} slot {v.Slot}");
        Assert.True(changed.Count >= 10, $"{changed.Count} draws depend on the deck/relics");
        Assert.Contains(changed, v => v.Event == "EVENT.PAEL");
        Assert.Contains(changed, v => v.Event == "EVENT.TANX");
        Assert.Contains(changed, v => v.Event == "EVENT.OROBAS");
    }

    private static AncientInputs Inputs(string seed, params string[] deck) => new()
    {
        RunSeed = seed,
        ActIndex = 1,
        Character = "CHARACTER.IRONCLAD",
        Deck = deck.Select(d => new AncientDeckCard(d)).ToArray(),
        Relics = ["RELIC.BURNING_BLOOD"],
        Scheme = RngScheme.Legacy32,
    };

    private static IEnumerable<string> Seeds(int n)
    {
        const string chars = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var r = new Random(11);
        for (int i = 0; i < n; i++) yield return new string(Enumerable.Range(0, 10).Select(_ => chars[r.Next(chars.Length)]).ToArray());
    }

    private static readonly string[] IroncladStart =
        ["CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD", "CARD.STRIKE_IRONCLAD",
         "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.DEFEND_IRONCLAD", "CARD.BASH"];

    [Fact]
    public void Conditional_options_appear_only_when_the_deck_allows_them()
    {
        foreach (string seed in Seeds(1500))
        {
            var bare = Inputs(seed) with { Relics = [] };
            var full = Inputs(seed, IroncladStart);
            Assert.DoesNotContain("PAELS_CLAW", AncientOptions.Options("EVENT.PAEL", bare)!);
            Assert.DoesNotContain("PAELS_TOOTH", AncientOptions.Options("EVENT.PAEL", bare)!);
            Assert.DoesNotContain("NUTRITIOUS_SOUP", AncientOptions.Options("EVENT.TEZCATARA", bare)!);
            Assert.DoesNotContain("TRI_BOOMERANG", AncientOptions.Options("EVENT.TANX", bare)!);
            Assert.DoesNotContain("BEAUTIFUL_BRACELET", AncientOptions.Options("EVENT.NONUPEIPE", bare)!);
            Assert.Equal(AncientOptions.OrobasLocked, AncientOptions.Options("EVENT.OROBAS", bare)![2]);

            // A pet blocks Pael's Legion; Defends enchanted already don't count for Goopy (Claw).
            Assert.DoesNotContain("PAELS_LEGION", AncientOptions.Options("EVENT.PAEL", full with { Relics = ["RELIC.BYRDPIP"] })!);
            var goopy = full with { Deck = full.Deck.Select(c => c.Id.StartsWith("CARD.DEFEND") ? c with { EnchantmentId = "ENCHANTMENT.GOOPY" } : c).ToArray() };
            Assert.DoesNotContain("PAELS_CLAW", AncientOptions.Options("EVENT.PAEL", goopy)!);

            foreach (string ev in new[] { "EVENT.PAEL", "EVENT.TEZCATARA", "EVENT.OROBAS", "EVENT.NONUPEIPE", "EVENT.TANX", "EVENT.VAKUU", "EVENT.DARV" })
            {
                var o = AncientOptions.Options(ev, full)!;
                Assert.Equal(3, o.Count);
                Assert.Equal(3, o.Distinct().Count());
                Assert.All(o, x => Assert.Contains(x, AncientOptions.AllOptions(ev)));
            }
        }
        // With the starting deck the conditional options do show up for some seeds.
        Assert.Contains(Seeds(400), s => AncientOptions.Options("EVENT.PAEL", Inputs(s, IroncladStart))!.Contains("PAELS_CLAW"));
        Assert.Contains(Seeds(400), s => AncientOptions.Options("EVENT.TANX", Inputs(s, IroncladStart))!.Contains("TRI_BOOMERANG"));
        Assert.Contains(Seeds(400), s => AncientOptions.Options("EVENT.NONUPEIPE", Inputs(s, IroncladStart))!.Contains("BEAUTIFUL_BRACELET"));
        Assert.Contains(Seeds(400), s => AncientOptions.Options("EVENT.TEZCATARA", Inputs(s, IroncladStart))!.Contains("NUTRITIOUS_SOUP"));
    }

    [Fact]
    public void Offered_options_always_come_from_the_possible_pool()
    {
        // PossibleKeys is what the panel ranks below the offered three: it must hold every option a draw can give.
        var inputs = s_visits.Value.Select(v => (v.Event, v.Inputs)).ToList();
        foreach (string seed in Seeds(300))
            foreach (string ev in new[] { "EVENT.PAEL", "EVENT.TEZCATARA", "EVENT.OROBAS", "EVENT.NONUPEIPE", "EVENT.TANX", "EVENT.VAKUU", "EVENT.DARV" })
            {
                inputs.Add((ev, Inputs(seed, IroncladStart)));
                inputs.Add((ev, Inputs(seed) with { Relics = [], ActIndex = 2 }));
                foreach (int act in new[] { 1, 2 }) inputs.Add((ev, Inputs(seed, IroncladStart) with { ActIndex = act, Scheme = RngScheme.Xx64 }));
            }
        foreach (var (ev, p) in inputs)
        {
            var possible = AncientOptions.PossibleKeys(ev, p)!;
            Assert.All(AncientOptions.Keys(ev, p)!, k => Assert.Contains(k, possible));
        }
        // Conditions the deck rules out leave the pool too.
        Assert.DoesNotContain("PAEL.pages.INITIAL.options.PAELS_CLAW", AncientOptions.PossibleKeys("EVENT.PAEL", Inputs("X"))!);
        Assert.Contains("OROBAS.pages.INITIAL.options.OPTION_POOL_3_LOCKED", AncientOptions.PossibleKeys("EVENT.OROBAS", Inputs("X") with { Relics = [] })!);
    }

    [Fact]
    public void Darv_offers_the_act_relics_only_in_their_act()
    {
        foreach (string seed in Seeds(1500))
        {
            var act2 = AncientOptions.Options("EVENT.DARV", Inputs(seed) with { ActIndex = 1 })!;
            var act3 = AncientOptions.Options("EVENT.DARV", Inputs(seed) with { ActIndex = 2 })!;
            Assert.DoesNotContain("PHILOSOPHERS_STONE", act2);
            Assert.DoesNotContain("VELVET_CHOKER", act2);
            Assert.DoesNotContain("ECTOPLASM", act3);
            Assert.DoesNotContain("SOZU", act3);
            Assert.DoesNotContain("PANDORAS_BOX", AncientOptions.Options("EVENT.DARV", Inputs(seed) with { DeckClearingModifier = true })!);
        }
    }

    [Fact]
    public void From_v0109_darv_and_orobas_use_the_new_pools()
    {
        // Darv (v0.111.0 source): Ectoplasm and Sozu each their own act-2 set; Philosopher's Stone and Velvet Choker from act 2 on.
        // Orobas: Sand Castle moved from the first pool to the second.
        bool stoneInAct2 = false, sandCastleSecond = false;
        foreach (string seed in Seeds(1500))
        {
            var p = Inputs(seed, IroncladStart) with { Scheme = RngScheme.Xx64 };
            var act2 = AncientOptions.Options("EVENT.DARV", p with { ActIndex = 1 })!;
            var act3 = AncientOptions.Options("EVENT.DARV", p with { ActIndex = 2 })!;
            stoneInAct2 |= act2.Contains("PHILOSOPHERS_STONE") || act2.Contains("VELVET_CHOKER");
            Assert.DoesNotContain("ECTOPLASM", act3);
            Assert.DoesNotContain("SOZU", act3);
            var orobas = AncientOptions.Options("EVENT.OROBAS", p with { ActIndex = 1 })!;
            Assert.NotEqual("SAND_CASTLE", orobas[0]);
            sandCastleSecond |= orobas[1] == "SAND_CASTLE";
        }
        Assert.True(stoneInAct2);
        Assert.True(sandCastleSecond);
        Assert.Equal(AncientOptions.AllOptions("EVENT.DARV", RngScheme.Legacy32).Order(), AncientOptions.AllOptions("EVENT.DARV", RngScheme.Xx64).Order());
        Assert.Equal(AncientOptions.AllOptions("EVENT.OROBAS", RngScheme.Legacy32).Order(), AncientOptions.AllOptions("EVENT.OROBAS", RngScheme.Xx64).Order());
    }

    [Fact]
    public void Sea_glass_shows_another_unlocked_character()
    {
        int seaGlass = 0;
        foreach (string seed in Seeds(1500))
        {
            var only = AncientOptions.Draw("EVENT.OROBAS", Inputs(seed) with { UnlockedEpochs = ["SILENT1_EPOCH"] })!;
            if (only.SeaGlassCharacter is { } c) { seaGlass++; Assert.Equal("CHARACTER.SILENT", c); }
            var none = AncientOptions.Draw("EVENT.OROBAS", Inputs(seed) with { UnlockedEpochs = [] })!;
            if (none.SeaGlassCharacter is { } own) Assert.Equal("CHARACTER.IRONCLAD", own); // nobody else: own character
            var all = AncientOptions.Draw("EVENT.OROBAS", Inputs(seed))!;
            Assert.NotEqual("CHARACTER.IRONCLAD", all.SeaGlassCharacter);
            Assert.Equal(all.SeaGlassCharacter is not null, all.Options.Contains(AncientOptions.SeaGlass));
        }
        Assert.InRange(seaGlass, 150, 450); // slot 1 is Sea Glass about 1/4 × 2/3 of the time
    }

    [Fact]
    public void Keys_are_the_event_text_keys_and_the_stream_follows_slot_and_id()
    {
        var keys = AncientOptions.Keys("EVENT.VAKUU", Inputs("VRKW7CVYCB"))!;
        Assert.All(keys, k => Assert.StartsWith("VAKUU.pages.INITIAL.options.", k));
        Assert.Null(AncientOptions.Keys("EVENT.NEOW", Inputs("VRKW7CVYCB")));
        Assert.Null(AncientOptions.Keys("EVENT.BRAIN_LEECH", Inputs("VRKW7CVYCB")));
        foreach (RngScheme scheme in new[] { RngScheme.Legacy32, RngScheme.Xx64 })
        {
            Assert.Equal(AncientOptions.StreamSeed("VRKW7CVYCB", 0, "EVENT.PAEL", scheme) + 1, AncientOptions.StreamSeed("VRKW7CVYCB", 1, "EVENT.PAEL", scheme));
            Assert.Equal(NeowOptions.StreamSeed("VRKW7CVYCB", 1, scheme), AncientOptions.StreamSeed("VRKW7CVYCB", 1, "EVENT.NEOW", scheme));
        }
    }

    [Fact]
    public void Every_option_key_is_in_the_event_pack()
    {
        // The panel scores options by key (data/packs/v0.107.1/events.json): every replayable option must be there.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.107.1", "events.json")));
        foreach (string ev in new[] { "EVENT.PAEL", "EVENT.TEZCATARA", "EVENT.OROBAS", "EVENT.NONUPEIPE", "EVENT.TANX", "EVENT.VAKUU", "EVENT.DARV" })
        {
            var e = doc.RootElement.GetProperty("events").EnumerateArray().Single(x => x.GetProperty("id").GetString() == ev);
            var keys = e.GetProperty("pages").EnumerateArray().SelectMany(p => p.GetProperty("options").EnumerateArray())
                .Select(o => o.GetProperty("key").GetString()).ToHashSet();
            var mine = AncientOptions.AllOptions(ev).Append(ev == "EVENT.OROBAS" ? AncientOptions.OrobasLocked : null).OfType<string>()
                .Select(o => AncientOptions.KeyPrefix(ev) + o).ToList();
            Assert.All(mine, k => Assert.Contains(k, keys));
            Assert.Equal(keys.Count, mine.Count); // and nothing in the pack is missing from the replay's pool
        }
    }
}
