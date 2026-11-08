using System.IO.Compression;
using System.Text.Json;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Save;
using Sts2Overlay.GameState.Tracking;
using Xunit.Abstractions;

namespace Sts2Overlay.GameState.Tests;

/// <summary>
/// <see cref="EventPrediction"/> against what really happened, two ways:
/// <list type="bullet">
/// <item>Room-entry saves (the exact file the live overlay predicts from) must give the event the game pulled.</item>
/// <item>Every event room in a save's <c>map_point_history</c> is replayed through the same pull loop, from the act's
/// <c>event_ids</c>, with the run state of that moment rebuilt from the history; the final index must equal the save's
/// <c>events_visited</c>.</item>
/// </list>
/// Fixtures (<c>Fixtures/event_saves</c>) are the user's single-player saves of 2026-11-09 (gzip, compact JSON). Single
/// player saves carry no Steam id (<c>net_id</c> 1) and no paths, so they are stored unchanged.
/// </summary>
public class EventPredictionTests(ITestOutputHelper output)
{
    private static readonly RunState Empty = new() { Character = "CHARACTER.IRONCLAD", Hp = 80, MaxHp = 80, Gold = 99 };

    private static EventPrediction.Facts Facts(int act, int gold = 99, int hp = 80, int maxHp = 80, int floor = 1, RunState? state = null, IReadOnlyList<string>? potions = null)
        => new(act, gold, hp, maxHp, floor, state ?? Empty, potions ?? []);

    private static RunState WithDeck(params string[] ids) => Empty with { Deck = ids.Select(id => new DeckCard(new CardRef(id), 1)).ToList() };

    private static EventPrediction.Allowed Check(string eventId, EventPrediction.Facts f) => EventPrediction.IsAllowed(eventId, f);

    [Fact]
    public void Pull_skips_seen_and_disallowed_events_and_stops_on_the_first_allowed_one()
    {
        // Act 1 (index 0): Fake Merchant and Ranwid need act 2+, so the pull walks past them.
        string[] ids = ["EVENT.ABYSSAL_BATHS", "EVENT.FAKE_MERCHANT", "EVENT.RANWID_THE_ELDER", "EVENT.SELF_HELP_BOOK", "EVENT.ROOM_FULL_OF_CHEESE"];
        var g = EventPrediction.Pull(ids, 1, new HashSet<string>(), Facts(0), out int next);
        Assert.Equal("EVENT.SELF_HELP_BOOK", g.EventId);
        Assert.True(g.Certain);
        Assert.Equal(4, next);

        g = EventPrediction.Pull(ids, 4, new HashSet<string> { "EVENT.SELF_HELP_BOOK" }, Facts(0), out next);
        Assert.Equal("EVENT.ROOM_FULL_OF_CHEESE", g.EventId);
        Assert.Equal(5, next);

        // Wraps around past the end: Abyssal Baths at index 0.
        g = EventPrediction.Pull(ids, 5, new HashSet<string> { "EVENT.SELF_HELP_BOOK", "EVENT.ROOM_FULL_OF_CHEESE" }, Facts(0), out next);
        Assert.Equal("EVENT.ABYSSAL_BATHS", g.EventId);
        Assert.Equal(6, next);
    }

    [Fact]
    public void Gold_and_hp_conditions_decide_exactly()
    {
        string[] ids = ["EVENT.TEA_MASTER", "EVENT.COLOSSAL_FLOWER", "EVENT.DENSE_VEGETATION"];
        Assert.Equal("EVENT.TEA_MASTER", EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1, gold: 150), out _).EventId);
        Assert.Equal("EVENT.COLOSSAL_FLOWER", EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1, gold: 149), out _).EventId);
        Assert.Equal("EVENT.DENSE_VEGETATION", EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1, gold: 149, hp: 18), out _).EventId);
    }

    [Fact]
    public void An_approximate_check_names_the_fallback()
    {
        // Relics the embedded table doesn't know (a later game version) make the Relic Trader check approximate.
        var state = Empty with { Relics = Enumerable.Range(0, 5).Select(i => $"RELIC.FUTURE_{i}").ToList() };
        string[] ids = ["EVENT.RELIC_TRADER", "EVENT.DENSE_VEGETATION"];
        var g = EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1, state: state), out _);
        Assert.Equal("EVENT.DENSE_VEGETATION", g.EventId);
        Assert.False(g.Certain);
        Assert.Equal(["EVENT.RELIC_TRADER"], g.Alternatives);
    }

    [Fact]
    public void Unknown_potions_make_potion_checks_approximate()
    {
        string[] ids = ["EVENT.THE_FUTURE_OF_POTIONS", "EVENT.DENSE_VEGETATION"];
        var known = EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1, potions: ["POTION.A"]), out _);
        Assert.Equal("EVENT.DENSE_VEGETATION", known.EventId);
        Assert.True(known.Certain);

        var unknown = EventPrediction.Pull(ids, 0, new HashSet<string>(), Facts(1) with { Potions = null }, out _);
        Assert.Equal("EVENT.THE_FUTURE_OF_POTIONS", unknown.EventId);
        Assert.False(unknown.Certain);
    }

    [Fact]
    public void Relic_trader_counts_only_tradable_relics()
    {
        // The 2026-11-09 miss: six relics, but the starter (Burning Blood) and two ancient pickup relics don't trade.
        string[] six = ["RELIC.BURNING_BLOOD", "RELIC.PRECARIOUS_SHEARS", "RELIC.AMETHYST_AUBERGINE", "RELIC.LASTING_CANDY", "RELIC.REGAL_PILLOW", "RELIC.PANDORAS_BOX"];
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.RELIC_TRADER", Facts(1, state: Empty with { Relics = six })));

        string[] five = ["RELIC.AKABEKO", "RELIC.ANCHOR", "RELIC.AMETHYST_AUBERGINE", "RELIC.LASTING_CANDY", "RELIC.REGAL_PILLOW"];
        Assert.Equal(EventPrediction.Allowed.Yes, Check("EVENT.RELIC_TRADER", Facts(1, state: Empty with { Relics = five })));

        // A used Lizard Tail (saved WasUsed) and a melted relic (saved IsMelted) don't trade either.
        var saved = five.Select(id => new SavedRelic(id, new HashSet<string>())).ToList();
        saved[0] = new SavedRelic("RELIC.AKABEKO", new HashSet<string> { "IsMelted" });
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.RELIC_TRADER", Facts(1) with { Relics = saved }));
        saved[0] = new SavedRelic("RELIC.LIZARD_TAIL", new HashSet<string> { "WasUsed" });
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.RELIC_TRADER", Facts(1) with { Relics = saved }));
        saved[0] = new SavedRelic("RELIC.LIZARD_TAIL", new HashSet<string>());
        Assert.Equal(EventPrediction.Allowed.Yes, Check("EVENT.RELIC_TRADER", Facts(1) with { Relics = saved }));
    }

    [Fact]
    public void Card_checks_follow_the_game_helpers()
    {
        // Wood Carvings: any removable Basic card, not only Strike/Defend (Bash counts).
        Assert.Equal(EventPrediction.Allowed.Yes, Check("EVENT.WOOD_CARVINGS", Facts(1, state: WithDeck("CARD.BASH", "CARD.ANGER"))));
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.WOOD_CARVINGS", Facts(1, state: WithDeck("CARD.ANGER"))));

        // Field of Man-Sized Holes: any unenchanted Attack/Skill/Power; curses never.
        Assert.Equal(EventPrediction.Allowed.Yes, Check("EVENT.FIELD_OF_MAN_SIZED_HOLES", Facts(1, state: WithDeck("CARD.ANGER"))));
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.FIELD_OF_MAN_SIZED_HOLES", Facts(1, state: WithDeck("CARD.ASCENDERS_BANE"))));

        // Slippery Bridge / Morphic Grove: Eternal curses can't be removed or transformed.
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.SLIPPERY_BRIDGE", Facts(1, floor: 7, state: WithDeck("CARD.ASCENDERS_BANE"))));
        Assert.Equal(EventPrediction.Allowed.Yes, Check("EVENT.SLIPPERY_BRIDGE", Facts(1, floor: 7, state: WithDeck("CARD.ASCENDERS_BANE", "CARD.ANGER"))));
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.MORPHIC_GROVE", Facts(1, gold: 100, state: WithDeck("CARD.ASCENDERS_BANE", "CARD.ANGER"))));

        // Grave of the Forgotten: Exhaust after upgrades (an upgraded Hologram loses it).
        var hologram = Empty with { Deck = [new DeckCard(new CardRef("CARD.HOLOGRAM", 1), 1)] };
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.GRAVE_OF_THE_FORGOTTEN", Facts(1, state: hologram)));
        Assert.Equal(EventPrediction.Allowed.Yes, Check("EVENT.GRAVE_OF_THE_FORGOTTEN", Facts(1, state: WithDeck("CARD.HOLOGRAM"))));

        // Byrdonis Nest: a pet relic blocks it.
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.BYRDONIS_NEST", Facts(1, state: Empty with { Relics = ["RELIC.BYRDPIP"] })));
        Assert.Equal(EventPrediction.Allowed.Yes, Check("EVENT.BYRDONIS_NEST", Facts(1)));

        // Colorful Philosophers: a second character's epoch.
        Assert.Equal(EventPrediction.Allowed.No, Check("EVENT.COLORFUL_PHILOSOPHERS", Facts(1) with { UnlockedEpochs = ["IRONCLAD2_EPOCH"] }));
        Assert.Equal(EventPrediction.Allowed.Yes, Check("EVENT.COLORFUL_PHILOSOPHERS", Facts(1) with { UnlockedEpochs = ["SILENT1_EPOCH"] }));
    }

    // --- real saves -------------------------------------------------------------------------------------------

    private static string EventSaves => TestPaths.Fixture("event_saves");

    private static string ReadSave(string path)
    {
        if (!path.EndsWith(".gz", StringComparison.Ordinal)) return File.ReadAllText(path);
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Room_entry_saves_predict_the_event_the_game_pulled()
    {
        var lines = File.ReadAllLines(Path.Combine(EventSaves, "room_entry", "truth.txt")).Where(l => l.Length > 0 && l[0] != '#').ToList();
        Assert.NotEmpty(lines);
        int exact = 0;
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            var save = RunSaveParser.Parse(ReadSave(Path.Combine(EventSaves, "room_entry", parts[0])), null);
            Assert.True(save.Events?.IsRoomEntrySave, $"{parts[0]} is not a room-entry save");
            var guess = EventPrediction.Predict(save);
            output.WriteLine($"{parts[0]}: {(guess?.EventId == parts[1] ? "ok" : "MISS")} guess {guess?.EventId}{(guess?.Certain == false ? "?" : "")} actual {parts[1]}");
            if (guess?.EventId == parts[1]) exact++;
        }
        Assert.Equal(lines.Count, exact);
    }

    [Fact]
    public void Relic_table_knows_every_relic_in_the_saves()
    {
        foreach (var path in Directory.GetFiles(EventSaves, "*.save.gz", SearchOption.AllDirectories))
        {
            var save = RunSaveParser.Parse(ReadSave(path), null);
            Assert.All(save.State.Relics, r => Assert.True(RelicTrading.Knows(r), $"{Path.GetFileName(path)}: {r}"));
            Assert.All(save.State.Deck, c => Assert.True(CardRarities.Knows(c.Id), $"{Path.GetFileName(path)}: {c.Id}"));
        }
    }

    /// <summary>Fixture runs (latest save of each run) plus the saves on this machine (frozen capture + the live run).</summary>
    private static IEnumerable<string> RealSaves()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(EventSaves, "runs"), "*.save.gz").Order(StringComparer.Ordinal)) yield return f;
        var frozen = Path.Combine(TestPaths.RepoRoot, "data", "local", "vision-robust", "frozen", "saves", "current_run.save");
        if (File.Exists(frozen)) yield return frozen;
        var saves = GamePaths.Discover(TestPaths.RealRoot).SavesDirectory;
        if (saves is null) yield break;
        foreach (var name in new[] { "current_run.save", "current_run.save.backup" })
        {
            var p = Path.Combine(saves, name);
            if (File.Exists(p)) yield return p;
        }
    }

    [Fact]
    public void Replaying_real_saves_gives_every_pulled_event()
    {
        int pulls = 0, exact = 0, files = 0;
        foreach (var path in RealSaves())
        {
            var json = ReadSave(path);
            var parsed = RunSaveParser.Parse(json, null);
            if (parsed.PlayerNetIds.Count > 1) continue; // co-op saves: the log names the event
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("events_seen", out _)) continue; // older schema
            files++;

            var r = Replay(root, parsed);
            foreach (var line in r.Lines) output.WriteLine($"{Path.GetFileName(path)}: {line}");
            pulls += r.Pulls;
            exact += r.Exact;
            Assert.True(r.Consistent, $"{path}: a pulled event contradicts the pull loop");
            Assert.True(r.IndexesMatch, $"{path}: replayed events_visited differs from the save");
        }
        output.WriteLine($"{files} saves, {pulls} event pulls, {exact} predicted exactly");
        Assert.True(files > 0);
        Assert.Equal(pulls, exact);
    }

    private sealed record ReplayResult(int Pulls, int Exact, bool Consistent, bool IndexesMatch, List<string> Lines);

    /// <summary>
    /// Walks <c>map_point_history</c>. What the room-entry save held at each event room is rebuilt from the history:
    /// gold/HP from the entry before; potions forward (picked, bought, used, discarded); deck and relics from the final
    /// save by <c>floor_added_to_deck</c>, with later removals, transforms, enchants and upgrades undone.
    /// </summary>
    private static ReplayResult Replay(JsonElement root, ParsedSave save)
    {
        var lines = new List<string>();
        var history = root.GetProperty("map_point_history");
        var acts = root.GetProperty("acts");
        var me = root.GetProperty("players")[0];
        var entries = history.EnumerateArray().SelectMany((act, a) => act.EnumerateArray().Select(e => (Act: a, Entry: e))).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var potions = new List<string>();
        int pulls = 0, exact = 0, k = 0;
        bool consistent = true, indexes = true;
        int gold = 99, hp = 80, maxHp = 80;

        for (int a = 0; a < history.GetArrayLength() && a < acts.GetArrayLength(); a++)
        {
            var rooms = acts[a].GetProperty("rooms");
            var ids = rooms.GetProperty("event_ids").EnumerateArray().Select(e => e.GetString()!).ToList();
            int idx = 0;
            foreach (var entry in history[a].EnumerateArray())
            {
                var type = entry.GetProperty("map_point_type").GetString();
                var room = entry.GetProperty("rooms").EnumerateArray().FirstOrDefault();
                if (type == "ancient") idx++;
                else if (type == "unknown" && room.ValueKind == JsonValueKind.Object && room.GetProperty("room_type").GetString() == "event")
                {
                    var actual = room.GetProperty("model_id").GetString()!;
                    var (deck, relics) = StateAt(me, entries, k);
                    var state = save.State with { Deck = deck, Relics = relics.Select(r => r.Id).ToList() };
                    var facts = new EventPrediction.Facts(a, gold, hp, maxHp, k, state, potions.ToList())
                    {
                        Relics = relics,
                        UnlockedEpochs = save.UnlockedEpochs,
                        RelicGrabBag = save.RelicGrabBag, // the bag only shrinks: a relic still in it now was in it then
                    };
                    var guess = EventPrediction.Pull(ids, idx, seen, facts, out _);
                    pulls++;
                    if (guess.EventId == actual) exact++;
                    else if (!guess.Alternatives.Contains(actual)) consistent = false;
                    // Where the game really took it from: the next position holding that event.
                    int pos = Enumerable.Range(idx, ids.Count).FirstOrDefault(i => ids[i % ids.Count] == actual, -1);
                    lines.Add($"act {a} floor {k}: {actual} at {pos} (guess {guess.EventId}{(guess.Certain ? "" : "?")}, gold {gold} hp {hp}/{maxHp}, potions {potions.Count})");
                    if (pos < 0) consistent = false; else idx = pos + 1;
                    seen.Add(actual);
                }
                if (entry.TryGetProperty("player_stats", out var ps) && ps.GetArrayLength() > 0)
                {
                    gold = ps[0].GetProperty("current_gold").GetInt32();
                    hp = ps[0].GetProperty("current_hp").GetInt32();
                    maxHp = ps[0].GetProperty("max_hp").GetInt32();
                    foreach (var c in Items(ps[0], "potion_choices"))
                        if (c.TryGetProperty("was_picked", out var picked) && picked.GetBoolean()) potions.Add(c.GetProperty("choice").GetString()!);
                    potions.AddRange(Items(ps[0], "bought_potions").Select(p => p.GetString()!));
                    foreach (var p in Items(ps[0], "potion_used").Concat(Items(ps[0], "potion_discarded")))
                        potions.Remove(p.GetString()!);
                }
                k++;
            }
            int stored = rooms.GetProperty("events_visited").GetInt32();
            if (history[a].GetArrayLength() > 0 && stored != idx)
            {
                indexes = false;
                lines.Add($"act {a}: replay ends at {idx}, save says {stored}");
            }
        }
        return new ReplayResult(pulls, exact, consistent, indexes, lines);
    }

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private sealed class Card(string id, int floor, int upgrade, string? enchantment)
    {
        public string Id = id; public int Floor = floor; public int Upgrade = upgrade; public string? Enchantment = enchantment;
        public static Card From(JsonElement c) => new(c.GetProperty("id").GetString()!,
            c.TryGetProperty("floor_added_to_deck", out var f) ? f.GetInt32() : 0,
            c.TryGetProperty("current_upgrade_level", out var u) ? u.GetInt32() : 0,
            c.TryGetProperty("enchantment", out var en) && en.ValueKind == JsonValueKind.Object ? en.GetProperty("id").GetString() : null);
    }

    /// <summary>
    /// Deck and relics when entering history entry <paramref name="k"/> (= floor k+1; an item added at entry j has
    /// <c>floor_added_to_deck</c> j+1, so it was held iff that is ≤ k). Changes at entries ≥ k are undone.
    /// </summary>
    private static (List<DeckCard> Deck, List<SavedRelic> Relics) StateAt(JsonElement me, List<(int Act, JsonElement Entry)> entries, int k)
    {
        var deck = me.GetProperty("deck").EnumerateArray().Select(Card.From).ToList();
        var relics = me.GetProperty("relics").EnumerateArray()
            .Select(r => (Id: r.GetProperty("id").GetString()!, Floor: r.TryGetProperty("floor_added_to_deck", out var f) ? f.GetInt32() : 0, El: r))
            .ToList();
        var removedRelics = new List<string>();
        for (int j = entries.Count - 1; j >= k; j--)
        {
            if (!entries[j].Entry.TryGetProperty("player_stats", out var ps) || ps.GetArrayLength() == 0) continue;
            var s = ps[0];
            foreach (var up in Items(s, "upgraded_cards"))
                if (deck.FirstOrDefault(c => c.Id == up.GetString() && c.Upgrade > 0) is { } c) c.Upgrade--;
            foreach (var en in Items(s, "cards_enchanted"))
            {
                var card = Card.From(en.GetProperty("card"));
                if (deck.FirstOrDefault(c => c.Id == card.Id && c.Floor == card.Floor && c.Enchantment != null) is { } c) c.Enchantment = null;
            }
            foreach (var t in Items(s, "cards_transformed")) deck.Add(Card.From(t.GetProperty("original_card")));
            foreach (var r in Items(s, "cards_removed")) deck.Add(Card.From(r));
            removedRelics.AddRange(Items(s, "relics_removed").Select(r => r.GetString()!));
        }
        var deckAt = deck.Where(c => c.Floor <= k)
            .Select(c => new DeckCard(new CardRef(c.Id, c.Upgrade), c.Floor, c.Enchantment)).ToList();
        var relicsAt = relics.Where(r => r.Floor <= k)
            .Select(r => new SavedRelic(r.Id, Items(r.El.TryGetProperty("props", out var p) ? p : default, "bools")
                .Where(b => b.GetProperty("value").ValueKind == JsonValueKind.True).Select(b => b.GetProperty("name").GetString()!).ToHashSet()))
            .Concat(removedRelics.Select(id => new SavedRelic(id, new HashSet<string>())))
            .ToList();
        return (deckAt, relicsAt);
    }
}
