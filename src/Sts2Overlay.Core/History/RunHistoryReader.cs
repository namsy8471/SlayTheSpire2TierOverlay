using System.Text.Json;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.History;

/// <summary>Parses <c>.run</c> history files (PLAN §3.3, §7.7).</summary>
/// <remarks>
/// Parsing is tolerant: missing fields and fields of an unexpected JSON kind become defaults,
/// unknown fields are ignored. Only malformed JSON (or a non-object root) throws.
/// </remarks>
public static class RunHistoryReader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    /// <summary>Parses the JSON text of a run file.</summary>
    /// <exception cref="JsonException">The text is not valid JSON or the root is not an object.</exception>
    public static RunHistory Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json, DocumentOptions);
        return FromRoot(doc.RootElement);
    }

    /// <summary>Parses a run file from a stream (UTF-8).</summary>
    /// <exception cref="JsonException">The content is not valid JSON or the root is not an object.</exception>
    public static RunHistory Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var doc = JsonDocument.Parse(stream, DocumentOptions);
        return FromRoot(doc.RootElement);
    }

    /// <summary>Reads and parses a run file from disk.</summary>
    public static RunHistory ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    private static RunHistory FromRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Run history root must be a JSON object.");
        }

        return new RunHistory
        {
            SchemaVersion = Json.Int(root, "schema_version"),
            BuildId = Json.Str(root, "build_id") ?? "",
            Seed = Json.Str(root, "seed") ?? "",
            GameMode = Json.Str(root, "game_mode") ?? "",
            Ascension = Json.Int(root, "ascension"),
            Win = Json.Bool(root, "win"),
            WasAbandoned = Json.Bool(root, "was_abandoned"),
            StartTime = Json.Long(root, "start_time"),
            RunTime = Json.Int(root, "run_time"),
            KilledByEncounter = Json.Str(root, "killed_by_encounter"),
            KilledByEvent = Json.Str(root, "killed_by_event"),
            Acts = Json.Array(root, "acts").Select(Json.AsString).OfType<string>().ToArray(),
            Players = Json.Array(root, "players").Where(Json.IsObject).Select(ParsePlayer).ToArray(),
            Nodes = ParseNodes(root),
        };
    }

    private static RunPlayer ParsePlayer(JsonElement p) => new()
    {
        Id = Json.Long(p, "id"),
        Character = Json.Str(p, "character") ?? "",
        Deck = Json.Array(p, "deck").Where(Json.IsObject).Select(ParseCard).ToArray(),
        Relics = Json.Array(p, "relics")
            .Where(Json.IsObject)
            .Select(r => new RelicEntry(Json.Str(r, "id") ?? "", Json.Int(r, "floor_added_to_deck")))
            .Where(r => r.Id.Length > 0)
            .ToArray(),
        Potions = Json.Array(p, "potions")
            .Select(x => x.ValueKind == JsonValueKind.Object ? Json.Str(x, "id") : Json.AsString(x))
            .OfType<string>()
            .ToArray(),
    };

    private static RunNode[] ParseNodes(JsonElement root)
    {
        var nodes = new List<RunNode>();
        var actIndex = 0;
        foreach (var act in Json.Array(root, "map_point_history"))
        {
            var indexInAct = 0;
            if (act.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in act.EnumerateArray())
                {
                    if (!Json.IsObject(node))
                    {
                        continue;
                    }

                    nodes.Add(ParseNode(node, actIndex, indexInAct, nodes.Count + 1));
                    indexInAct++;
                }
            }

            actIndex++;
        }

        return nodes.ToArray();
    }

    private static RunNode ParseNode(JsonElement n, int actIndex, int indexInAct, int floor) => new()
    {
        ActIndex = actIndex,
        IndexInAct = indexInAct,
        Floor = floor,
        MapPointType = Json.Str(n, "map_point_type") ?? "",
        Rooms = Json.Array(n, "rooms").Where(Json.IsObject).Select(r => new RunRoom(
            Json.Str(r, "room_type") ?? "",
            Json.Str(r, "model_id"),
            Json.Array(r, "monster_ids").Select(Json.AsString).OfType<string>().ToArray(),
            Json.Int(r, "turns_taken"))).ToArray(),
        Stats = Json.Array(n, "player_stats").Where(Json.IsObject).Select(ParseStats).ToArray(),
    };

    private static PlayerNodeStats ParseStats(JsonElement s) => new()
    {
        PlayerId = Json.Long(s, "player_id"),
        CardChoices = Json.Array(s, "card_choices")
            .Where(Json.IsObject)
            .Select(c => (Card: Json.Obj(c, "card"), Picked: Json.Bool(c, "was_picked")))
            .Where(c => c.Card is not null)
            .Select(c => new CardChoice(ParseCard(c.Card!.Value), c.Picked))
            .Where(c => c.Card.Id.Length > 0)
            .ToArray(),
        CardsGained = ParseCards(s, "cards_gained"),
        CardsRemoved = ParseCards(s, "cards_removed"),
        CardsTransformed = Json.Array(s, "cards_transformed")
            .Where(Json.IsObject)
            .Select(t => (Original: Json.Obj(t, "original_card"), Final: Json.Obj(t, "final_card")))
            .Where(t => t.Original is not null && t.Final is not null)
            .Select(t => new CardTransform(ParseCard(t.Original!.Value), ParseCard(t.Final!.Value)))
            .ToArray(),
        UpgradedCards = Json.Array(s, "upgraded_cards")
            .Select(x => x.ValueKind == JsonValueKind.Object ? Json.Str(x, "id") : Json.AsString(x))
            .OfType<string>()
            .ToArray(),
        CardsEnchanted = Json.Array(s, "cards_enchanted").Where(Json.IsObject).Select(ParseEnchanted).OfType<DeckCard>().ToArray(),
        RestSiteChoices = Json.Array(s, "rest_site_choices").Select(Json.AsString).OfType<string>().ToArray(),
        RelicChoices = ParsePickChoices(s, "relic_choices"),
        PotionChoices = ParsePickChoices(s, "potion_choices"),
        BoughtRelics = Json.Array(s, "bought_relics").Select(Json.AsString).OfType<string>().ToArray(),
        BoughtPotions = Json.Array(s, "bought_potions").Select(Json.AsString).OfType<string>().ToArray(),
        RelicsRemoved = Json.Array(s, "relics_removed").Select(Json.AsString).OfType<string>().ToArray(),
        CurrentHp = Json.Int(s, "current_hp"),
        MaxHp = Json.Int(s, "max_hp"),
        CurrentGold = Json.Int(s, "current_gold"),
        DamageTaken = Json.Int(s, "damage_taken"),
        HpHealed = Json.Int(s, "hp_healed"),
        MaxHpGained = Json.Int(s, "max_hp_gained"),
        MaxHpLost = Json.Int(s, "max_hp_lost"),
        GoldGained = Json.Int(s, "gold_gained"),
        GoldSpent = Json.Int(s, "gold_spent"),
        GoldLost = Json.Int(s, "gold_lost"),
        GoldStolen = Json.Int(s, "gold_stolen"),
    };

    private static DeckCard[] ParseCards(JsonElement s, string name) =>
        Json.Array(s, name).Where(Json.IsObject).Select(ParseCard).Where(c => c.Id.Length > 0).ToArray();

    private static PickChoice[] ParsePickChoices(JsonElement s, string name) =>
        Json.Array(s, name)
            .Where(Json.IsObject)
            .Select(c => new PickChoice(Json.Str(c, "choice") ?? "", Json.Bool(c, "was_picked")))
            .Where(c => c.Id.Length > 0)
            .ToArray();

    /// <summary><c>{card, enchantment}</c>: the card as it is after enchanting; the id-only enchantment fills gaps.</summary>
    private static DeckCard? ParseEnchanted(JsonElement e)
    {
        var cardEl = Json.Obj(e, "card");
        if (cardEl is null)
        {
            return null;
        }

        var card = ParseCard(cardEl.Value);
        if (card.Id.Length == 0)
        {
            return null;
        }

        if (card.EnchantmentId is null)
        {
            var (id, amount) = ParseEnchantment(e);
            if (id is not null)
            {
                card = card with { EnchantmentId = id, EnchantmentAmount = amount };
            }
        }

        return card;
    }

    private static DeckCard ParseCard(JsonElement c)
    {
        var (enchantmentId, amount) = ParseEnchantment(c);
        return new DeckCard(
            new CardRef(Json.Str(c, "id") ?? "", Math.Max(0, Json.Int(c, "current_upgrade_level"))),
            Json.Int(c, "floor_added_to_deck"),
            enchantmentId,
            amount);
    }

    /// <summary>Reads <c>enchantment</c> given either as <c>{id, amount}</c> or as a bare id string.</summary>
    private static (string? Id, int Amount) ParseEnchantment(JsonElement owner)
    {
        if (!owner.TryGetProperty("enchantment", out var e))
        {
            return (null, 0);
        }

        return e.ValueKind switch
        {
            JsonValueKind.Object => (Json.Str(e, "id"), Json.Int(e, "amount")),
            JsonValueKind.String => (e.GetString(), 0),
            _ => (null, 0),
        };
    }

    /// <summary>Lenient accessors over <see cref="JsonElement"/>.</summary>
    private static class Json
    {
        public static bool IsObject(JsonElement e) => e.ValueKind == JsonValueKind.Object;

        public static string? AsString(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

        public static IEnumerable<JsonElement> Array(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

        public static JsonElement? Obj(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        public static string? Str(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out var v) ? AsString(v) : null;

        public static bool Bool(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        public static long Long(JsonElement owner, string name)
        {
            if (!owner.TryGetProperty(name, out var v))
            {
                return 0;
            }

            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt64(out var l) => l,
                JsonValueKind.String when long.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s) => s,
                _ => 0,
            };
        }

        public static int Int(JsonElement owner, string name) => (int)Math.Clamp(Long(owner, name), int.MinValue, int.MaxValue);
    }
}

/// <summary>Which player is "me" in a run.</summary>
public static class LocalPlayer
{
    /// <summary>The anonymized id used for the user in test fixtures.</summary>
    public const long FixtureSelfId = 76561190000000001;

    /// <summary>
    /// Single player: the only player. Multiplayer: the player whose id equals <paramref name="steamId"/>
    /// (default <see cref="FixtureSelfId"/>), else the first player.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run has no players.</exception>
    public static RunPlayer Resolve(RunHistory run, long? steamId = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Players.Count == 0)
        {
            throw new InvalidOperationException("The run has no players.");
        }

        if (run.Players.Count == 1)
        {
            return run.Players[0];
        }

        return run.FindPlayer(steamId ?? FixtureSelfId) ?? run.Players[0];
    }
}
