using System.Text.Json;
using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.IO;

namespace Sts2Overlay.GameState.Save;

/// <summary>
/// Parses <c>current_run.save</c> / <c>current_run_mp.save</c> (schema 16, v0.107.1) into Core types.
/// Uses <see cref="JsonDocument"/> (a read-only DOM) instead of typed classes because we only need
/// a handful of fields from a ~80 KB file, and missing/extra fields must never throw.
/// </summary>
public static class RunSaveParser
{
    /// <summary>File names the game uses for an in-progress run (single player / co-op host).</summary>
    public static readonly IReadOnlyList<string> RunSaveFileNames = ["current_run.save", "current_run_mp.save"];

    /// <summary>Reads (sharing-safe, with retries) and parses a save file. Null if it does not exist.</summary>
    public static ParsedSave? ParseFile(string path, ulong? localNetId)
    {
        string? json = SharedFile.ReadAllText(path);
        return json == null ? null : Parse(json, localNetId);
    }

    /// <param name="json">The save file text.</param>
    /// <param name="localNetId">
    /// Our co-op <c>net_id</c> (= Steam ID). Ignored when the save has a single player (whose net_id is 1).
    /// </param>
    /// <exception cref="JsonException">The text is not valid JSON (e.g. read mid-write).</exception>
    public static ParsedSave Parse(string json, ulong? localNetId)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        // --- pick "my" player ---------------------------------------------------------------
        JsonElement players = Prop(root, "players");
        var playerList = players.ValueKind == JsonValueKind.Array ? players.EnumerateArray().ToList() : [];
        if (playerList.Count == 0) throw new JsonException("save has no players");

        var netIds = playerList.Select(p => Prop(p, "net_id").TryGetUInt64(out ulong id) ? id : 0UL).ToList();
        int myIndex = playerList.Count == 1 ? 0 : localNetId is { } want ? netIds.IndexOf(want) : -1;
        bool matched = myIndex >= 0;
        if (!matched) myIndex = 0; // unknown id in co-op: show the host's (first) player rather than nothing
        JsonElement me = playerList[myIndex];
        ulong myNetId = netIds[myIndex];

        // --- act, map, floor ------------------------------------------------------------------
        int actIndex = Int(root, "current_act_index") ?? 0;
        JsonElement acts = Prop(root, "acts");
        JsonElement act = acts.ValueKind == JsonValueKind.Array && actIndex < acts.GetArrayLength() ? acts[actIndex] : default;
        string? actId = Str(act, "id");

        List<MapCoord> visited = ReadCoords(Prop(root, "visited_map_coords"));
        MapGraph? map = Prop(act, "saved_map") is { ValueKind: JsonValueKind.Object } savedMap
            ? ParseMap(savedMap, actId ?? "", actIndex + 1, visited.Count > 0 ? visited[^1] : null)
            : null;

        JsonElement history = Prop(root, "map_point_history");
        int floorsBeforeAct = Enumerable.Range(0, actIndex).Sum(i => MapPointHistory.FloorsInAct(history, i));
        // The room-entry save is written BEFORE that room's history entry is appended (RunManager.cs
        // EnterMapPointInternal: SaveRun, then AppendToMapPointHistory), so history is one short there.
        // visited_map_coords already contains the new room, so take whichever is larger.
        int floor = Math.Max(
            floorsBeforeAct + MapPointHistory.FloorsInAct(history, actIndex),
            floorsBeforeAct + visited.Count);

        // --- deck, relics, potions -------------------------------------------------------------
        var deck = new List<DeckCard>();
        foreach (JsonElement c in Array(me, "deck"))
        {
            JsonElement ench = Prop(c, "enchantment");
            deck.Add(new DeckCard(
                new CardRef(Str(c, "id") ?? "?", Int(c, "current_upgrade_level") ?? 0),
                FloorAdded: Int(c, "floor_added_to_deck") ?? 0,
                EnchantmentId: Str(ench, "id"),
                EnchantmentAmount: Int(ench, "amount") ?? 0));
        }
        var relics = Array(me, "relics").Select(r => Str(r, "id")).OfType<string>().ToList();
        var potions = Array(me, "potions")
            .Select(p => (id: Str(p, "id"), slot: Int(p, "slot_index") ?? 0))
            .Where(p => p.id != null)
            .Select(p => new PotionSlot(p.id!, p.slot))
            .ToList();

        // Removal price = 75 + 25 x removals (MerchantCardRemovalEntry.cs:32). The game stores the count in
        // extra_fields; older saves don't, so fall back to counting shop removals in the history.
        int removals = Int(Prop(me, "extra_fields"), "card_shop_removals_used")
                       ?? MapPointHistory.CountShopRemovals(history, myNetId);

        var state = new RunState
        {
            Character = Str(me, "character_id") ?? "CHARACTER.UNKNOWN",
            Ascension = Int(root, "ascension") ?? 0,
            Act = actIndex + 1,
            Floor = Math.Max(floor, 1),
            Hp = Int(me, "current_hp") ?? 0,
            MaxHp = Int(me, "max_hp") ?? 0,
            Gold = Int(me, "gold") ?? 0,
            Deck = deck,
            Relics = relics,
            IsMultiplayer = playerList.Count > 1,
            CardRemovalsBought = removals,
        };

        JsonElement playerOdds = Prop(me, "odds");
        var mapOdds = new Dictionary<string, double>(StringComparer.Ordinal);
        if (Prop(root, "odds") is { ValueKind: JsonValueKind.Object } runOdds)
            foreach (JsonProperty p in runOdds.EnumerateObject())
                if (p.Value.TryGetDouble(out double v)) mapOdds[p.Name] = v;

        return new ParsedSave
        {
            State = state,
            Map = map,
            Seed = Str(Prop(root, "rng"), "seed"),
            SchemaVersion = Int(root, "schema_version") ?? 0,
            MyNetId = myNetId,
            MyPlayerMatched = matched,
            PlayerNetIds = netIds,
            Potions = potions,
            MaxPotionSlots = Int(me, "max_potion_slot_count") ?? 3,
            CardRarityOffset = Double(playerOdds, "card_rarity_odds_value"),
            PotionRewardOdds = Double(playerOdds, "potion_reward_odds_value"),
            UnknownMapPointOdds = mapOdds,
            SaveTime = Long(root, "save_time") is { } t and > 0 ? DateTimeOffset.FromUnixTimeSeconds(t) : null,
            ActId = actId,
            GameMode = Str(root, "game_mode"),
        };
    }

    /// <summary>
    /// <c>saved_map</c> → <see cref="MapGraph"/>. Nodes come from <c>start</c>, <c>points[]</c>, <c>boss</c> and,
    /// with the double-boss ascension, <c>second_boss</c>. Edges are each node's <c>children</c>.
    /// </summary>
    internal static MapGraph ParseMap(JsonElement savedMap, string actId, int actNumber, MapCoord? current)
    {
        var nodes = new Dictionary<MapCoord, MapNode>();
        void Add(JsonElement point)
        {
            if (point.ValueKind != JsonValueKind.Object) return;
            if (ReadCoord(Prop(point, "coord")) is not { } coord) return;
            nodes[coord] = new MapNode(coord, MapGraph.ParseType(Str(point, "type")), ReadCoords(Prop(point, "children")));
        }

        Add(Prop(savedMap, "start"));
        foreach (JsonElement p in Array(savedMap, "points")) Add(p);
        Add(Prop(savedMap, "boss"));
        Add(Prop(savedMap, "second_boss"));

        MapCoord start = ReadCoord(Prop(Prop(savedMap, "start"), "coord")) ?? new MapCoord(0, 0);
        MapCoord boss = ReadCoord(Prop(Prop(savedMap, "boss"), "coord")) ?? start;
        return new MapGraph
        {
            ActId = actId,
            Act = actNumber,
            Width = Int(savedMap, "width") ?? 0,
            Height = Int(savedMap, "height") ?? 0,
            Nodes = nodes,
            Start = start,
            Boss = boss,
            SecondBoss = ReadCoord(Prop(Prop(savedMap, "second_boss"), "coord")),
            Current = current,
        };
    }

    // --- tiny null-tolerant JSON accessors --------------------------------------------------------
    // TryGetProperty throws on non-objects, so every accessor checks ValueKind first. A missing value is
    // returned as `default(JsonElement)` (ValueKind.Undefined), which the other helpers treat as "absent".

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) ? v : default;

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out int v) ? v : null;

    private static long? Long(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out long v) ? v : null;

    private static double? Double(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out double v) ? v : null;

    private static MapCoord? ReadCoord(JsonElement e) =>
        Int(e, "col") is { } col && Int(e, "row") is { } row ? new MapCoord(col, row) : null;

    private static List<MapCoord> ReadCoords(JsonElement arr) =>
        arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(ReadCoord).OfType<MapCoord>().ToList()
            : [];
}
