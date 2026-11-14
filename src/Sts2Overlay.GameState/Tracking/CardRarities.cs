using System.Text.Json;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// Card id → rarity / type / keywords from the names-free catalogs embedded into this assembly
/// (<c>data/catalog/cards.&lt;version&gt;.json</c>, one per <see cref="GameDataVersion"/>). The tracker needs to know which
/// cards are created during a fight and never live in a deck, and which cards leave a fight when played (powers,
/// Exhaust) for the Thieving Hopper rule.
/// <para>
/// The run save or log version selects the table, including the Fool cards introduced in v0.113.
/// </para>
/// </summary>
public static class CardRarities
{
    private sealed record Info(string Rarity, string Type, bool Exhaust, int MaxUpgrade, string Multiplayer, IReadOnlySet<string> Keywords, IReadOnlySet<string> Tags);

    private static readonly EmbeddedCatalog<Dictionary<string, Info>> s_tables = new(Load);

    private static Dictionary<string, Info> Table(GameDataVersion version) => s_tables.For(version);

    private static Info? Find(string cardId, GameDataVersion version) => Table(version).GetValueOrDefault(cardId);

    private static Dictionary<string, Info> Load(GameDataVersion version)
    {
        using Stream? s = typeof(CardRarities).Assembly.GetManifestResourceStream(version.Resource("cards"));
        var map = new Dictionary<string, Info>(StringComparer.Ordinal);
        if (s == null) return map;
        using JsonDocument doc = JsonDocument.Parse(s);
        if (!doc.RootElement.TryGetProperty("cards", out JsonElement cards) || cards.ValueKind != JsonValueKind.Array) return map;
        foreach (JsonElement c in cards.EnumerateArray())
        {
            if (!c.TryGetProperty("id", out JsonElement id) || id.GetString() is not { } i) continue;
            string rarity = c.TryGetProperty("rarity", out JsonElement r) ? r.GetString() ?? "" : "";
            string type = c.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
            var keywords = c.TryGetProperty("keywords", out JsonElement k) && k.ValueKind == JsonValueKind.Array
                ? k.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var tags = c.TryGetProperty("tags", out JsonElement tg) && tg.ValueKind == JsonValueKind.Array
                ? tg.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            bool exhaust = keywords.Contains("Exhaust");
            int max = c.TryGetProperty("maxUpgrade", out JsonElement mu) && mu.TryGetInt32(out int m) ? m : 1;
            string mp = c.TryGetProperty("multiplayer", out JsonElement mpe) ? mpe.GetString() ?? "None" : "None";
            map[i] = new Info(rarity, type, exhaust, max, mp, keywords, tags);
        }
        // A renamed id (an old log line or history file) reads as the card the game migrated it to.
        foreach (var (old, now) in GameDataVersion.Renames(version.Scheme))
            if (!map.ContainsKey(old) && map.TryGetValue(now, out Info? info)) map[old] = info;
        return map;
    }

    public static string? Of(string cardId, GameDataVersion version = default) => Find(cardId, version)?.Rarity;

    /// <summary>True when the card id is in the catalog.</summary>
    public static bool Knows(string cardId, GameDataVersion version = default) => Table(version).ContainsKey(cardId);

    /// <summary>The card's canonical keyword (CardKeyword name, e.g. <c>Eternal</c>, <c>Exhaust</c>), before upgrades and enchantments.</summary>
    public static bool HasCanonicalKeyword(string cardId, string keyword, GameDataVersion version = default) =>
        Find(cardId, version)?.Keywords.Contains(keyword) ?? false;

    /// <summary>The card's CardTag (<c>Strike</c>, <c>Defend</c>, <c>Shiv</c> ...), from the catalog.</summary>
    public static bool HasTag(string cardId, string tag, GameDataVersion version = default) =>
        Find(cardId, version)?.Tags.Contains(tag) ?? false;

    /// <summary>Attack / Skill / Power / Status / Curse, or null for an unknown id.</summary>
    public static string? TypeOf(string cardId, GameDataVersion version = default) => Find(cardId, version)?.Type;

    /// <summary>How many times the card can be upgraded (0 for curses ...; 1 if the id is unknown).</summary>
    public static int MaxUpgrade(string cardId, GameDataVersion version = default) => Find(cardId, version)?.MaxUpgrade ?? 1;

    /// <summary>
    /// Whether the card can show up in a run with this many players (CardMultiplayerConstraint: some cards exist only in
    /// single player, some only in co-op).
    /// </summary>
    public static bool AllowedWithPlayers(string cardId, bool multiplayer, GameDataVersion version = default) =>
        Find(cardId, version)?.Multiplayer switch
        {
            "SingleplayerOnly" => !multiplayer,
            "MultiplayerOnly" => multiplayer,
            _ => true,
        };

    /// <summary>Tokens (Shiv, Soul ...) and statuses (Slimed, Wound ...) are made during a fight; a deck never holds them.</summary>
    public static bool IsCombatOnly(string cardId, GameDataVersion version = default) => Of(cardId, version) is "Token" or "Status";

    /// <summary>Played, this card leaves the fight (a power, or an Exhaust card) instead of going to the discard pile.</summary>
    public static bool LeavesCombatWhenPlayed(string cardId, GameDataVersion version = default) =>
        Find(cardId, version) is { } c && (c.Type == "Power" || c.Exhaust);

    /// <summary>
    /// Thieving Hopper's steal order (ThievingHopper._stealPriorities): 0 Uncommon, 1 Common/Rare/Event, 2 Basic/Quest,
    /// 3 Ancient or Imbued-enchanted; null = never stolen (curses ...). Lower wins.
    /// </summary>
    public static int? StealPriority(string cardId, string? enchantmentId, GameDataVersion version = default)
    {
        string? rarity = Of(cardId, version);
        if (rarity == "Ancient" || enchantmentId == "ENCHANTMENT.IMBUED") return 3;
        return rarity switch
        {
            "Uncommon" => 0,
            "Common" or "Rare" or "Event" => 1,
            "Basic" or "Quest" => 2,
            _ => null,
        };
    }
}
