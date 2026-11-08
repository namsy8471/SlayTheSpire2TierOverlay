using System.Text.Json;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// Card id → rarity / type / keywords from the names-free catalog embedded into this assembly
/// (<c>data/catalog/cards.v0.107.1.json</c>). The tracker needs to know which cards are created during a fight and
/// never live in a deck, and which cards leave a fight when played (powers, Exhaust) for the Thieving Hopper rule.
/// </summary>
public static class CardRarities
{
    private sealed record Info(string Rarity, string Type, bool Exhaust, int MaxUpgrade, string Multiplayer, IReadOnlySet<string> Keywords);

    private static readonly Lazy<Dictionary<string, Info>> s_cards = new(() =>
    {
        using Stream? s = typeof(CardRarities).Assembly.GetManifestResourceStream("cards.json");
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
            bool exhaust = keywords.Contains("Exhaust");
            int max = c.TryGetProperty("maxUpgrade", out JsonElement mu) && mu.TryGetInt32(out int m) ? m : 1;
            string mp = c.TryGetProperty("multiplayer", out JsonElement mpe) ? mpe.GetString() ?? "None" : "None";
            map[i] = new Info(rarity, type, exhaust, max, mp, keywords);
        }
        return map;
    });

    public static string? Of(string cardId) => s_cards.Value.GetValueOrDefault(cardId)?.Rarity;

    /// <summary>True when the card id is in the catalog.</summary>
    public static bool Knows(string cardId) => s_cards.Value.ContainsKey(cardId);

    /// <summary>The card's canonical keyword (CardKeyword name, e.g. <c>Eternal</c>, <c>Exhaust</c>), before upgrades and enchantments.</summary>
    public static bool HasCanonicalKeyword(string cardId, string keyword) =>
        s_cards.Value.GetValueOrDefault(cardId)?.Keywords.Contains(keyword) ?? false;

    /// <summary>Attack / Skill / Power / Status / Curse, or null for an unknown id.</summary>
    public static string? TypeOf(string cardId) => s_cards.Value.GetValueOrDefault(cardId)?.Type;

    /// <summary>How many times the card can be upgraded (0 for curses ...; 1 if the id is unknown).</summary>
    public static int MaxUpgrade(string cardId) => s_cards.Value.GetValueOrDefault(cardId)?.MaxUpgrade ?? 1;

    /// <summary>
    /// Whether the card can show up in a run with this many players (CardMultiplayerConstraint: some cards exist only in
    /// single player, some only in co-op).
    /// </summary>
    public static bool AllowedWithPlayers(string cardId, bool multiplayer) =>
        s_cards.Value.GetValueOrDefault(cardId)?.Multiplayer switch
        {
            "SingleplayerOnly" => !multiplayer,
            "MultiplayerOnly" => multiplayer,
            _ => true,
        };

    /// <summary>Tokens (Shiv, Soul ...) and statuses (Slimed, Wound ...) are made during a fight; a deck never holds them.</summary>
    public static bool IsCombatOnly(string cardId) => Of(cardId) is "Token" or "Status";

    /// <summary>Played, this card leaves the fight (a power, or an Exhaust card) instead of going to the discard pile.</summary>
    public static bool LeavesCombatWhenPlayed(string cardId) =>
        s_cards.Value.GetValueOrDefault(cardId) is { } c && (c.Type == "Power" || c.Exhaust);

    /// <summary>
    /// Thieving Hopper's steal order (ThievingHopper._stealPriorities): 0 Uncommon, 1 Common/Rare/Event, 2 Basic/Quest,
    /// 3 Ancient or Imbued-enchanted; null = never stolen (curses ...). Lower wins.
    /// </summary>
    public static int? StealPriority(string cardId, string? enchantmentId)
    {
        string? rarity = Of(cardId);
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
