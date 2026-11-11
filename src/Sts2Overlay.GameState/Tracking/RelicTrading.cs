using System.Text.Json;
using Sts2Overlay.GameState.Save;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// <c>RelicModel.IsTradable</c> (v0.107.1; unchanged in v0.111.0), which the Relic Trader and Ranwid events count in <c>IsAllowed</c>:
/// <code>
/// !IsUsedUp &amp;&amp; !HasUponPickupEffect &amp;&amp; !IsMelted &amp;&amp; !SpawnsPets &amp;&amp; Rarity not in { Starter, Event, Ancient }
/// </code>
/// The constant parts come from <c>data/catalog/relics.&lt;version&gt;.json</c> (tools/CatalogBuilder --relics, embedded; the
/// table is picked by the run's <see cref="RngScheme"/>, see <see cref="GameDataVersion"/>). The rest is the relic's saved
/// state: <c>IsMelted</c> is a saved bool on every relic, and <c>IsUsedUp</c> is overridden by ten relics, of which only
/// Lizard Tail has a tradable rarity (its saved <c>WasUsed</c>; the same in both versions).
/// </summary>
public static class RelicTrading
{
    private sealed record Info(string Rarity, bool UponPickup, bool Pets, bool UsedUpOverride, bool AddsPet, bool AllowedOverride);

    private static readonly Lazy<Dictionary<string, Info>> s_v0107 = new(() => Load(RngScheme.Legacy32));
    private static readonly Lazy<Dictionary<string, Info>> s_v0111 = new(() => Load(RngScheme.Xx64));

    private static Dictionary<string, Info> Table(RngScheme scheme) => (scheme == RngScheme.Legacy32 ? s_v0107 : s_v0111).Value;

    private static Dictionary<string, Info> Load(RngScheme scheme)
    {
        using Stream? s = typeof(RelicTrading).Assembly.GetManifestResourceStream(GameDataVersion.Resource("relics", scheme));
        var map = new Dictionary<string, Info>(StringComparer.Ordinal);
        if (s == null) return map;
        using JsonDocument doc = JsonDocument.Parse(s);
        foreach (JsonElement r in doc.RootElement.GetProperty("relics").EnumerateArray())
        {
            static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            map[r.GetProperty("id").GetString()!] = new Info(
                r.TryGetProperty("rarity", out var ra) ? ra.GetString() ?? "" : "",
                B(r, "hasUponPickupEffect"), B(r, "spawnsPets"), B(r, "usedUpOverride"), B(r, "addsPet"), B(r, "allowedOverride"));
        }
        return map;
    }

    /// <summary>The saved <c>IsUsedUp</c> flag of relics with a tradable rarity: relic id → saved bool that means "used up".</summary>
    private static readonly Dictionary<string, string> s_usedUpBool = new(StringComparer.Ordinal)
    {
        ["RELIC.LIZARD_TAIL"] = "WasUsed", // LizardTail.IsUsedUp => _wasUsed
    };

    /// <summary>Counts tradable relics. <paramref name="exact"/> is false when a relic is unknown or its used-up state can't be read.</summary>
    public static int CountTradable(IEnumerable<SavedRelic> relics, out bool exact, RngScheme scheme = RngSeeds.Default)
    {
        exact = true;
        int n = 0;
        var table = Table(scheme);
        foreach (var r in relics)
        {
            if (!table.TryGetValue(r.Id, out var info)) { exact = false; continue; }
            if (info.Rarity is "Starter" or "Event" or "Ancient" || info.UponPickup || info.Pets) continue;
            if (r.TrueBools.Contains("IsMelted")) continue;
            if (info.UsedUpOverride)
            {
                if (!s_usedUpBool.TryGetValue(r.Id, out var flag)) { exact = false; continue; }
                if (r.TrueBools.Contains(flag)) continue;
            }
            n++;
        }
        return n;
    }

    /// <summary>True when the embedded table knows the relic (tests: the table must cover every relic in real saves).</summary>
    public static bool Knows(string relicId, RngScheme scheme = RngSeeds.Default) => Table(scheme).ContainsKey(relicId);

    /// <summary>The relic's rarity (<c>Starter</c>, <c>Common</c> ... <c>Ancient</c>), or null for an unknown id.</summary>
    public static string? RarityOf(string relicId, RngScheme scheme = RngSeeds.Default) => Table(scheme).GetValueOrDefault(relicId)?.Rarity;

    /// <summary><c>RelicModel.AddsPet</c> (Byrdpip, Pael's Legion): <c>Player.HasEventPet</c> blocks the Byrdonis Nest event.</summary>
    public static bool AddsPet(string relicId, RngScheme scheme = RngSeeds.Default) => Table(scheme).GetValueOrDefault(relicId)?.AddsPet ?? false;

    /// <summary>
    /// True when a relic in the player's grab bag can surely be pulled: known and without its own <c>IsAllowed</c> rule
    /// (those rules read act/treasure-room state, e.g. eggs only before the act-3 chest).
    /// </summary>
    public static bool AlwaysPullable(string relicId, RngScheme scheme = RngSeeds.Default) => Table(scheme).GetValueOrDefault(relicId) is { AllowedOverride: false };
}
