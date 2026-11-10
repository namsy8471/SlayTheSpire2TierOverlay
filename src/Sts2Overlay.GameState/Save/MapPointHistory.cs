using System.Text.Json;

namespace Sts2Overlay.GameState.Save;

/// <summary>
/// Helpers over <c>map_point_history</c>: an array per act, one entry per room entered, each with
/// <c>rooms[]</c> (what the room really was) and <c>player_stats[]</c> (per-player results).
/// The same shape is used in <c>current_run.save</c> and in history <c>.run</c> files.
/// </summary>
public static class MapPointHistory
{
    /// <summary>
    /// Total floors = number of entries over all acts. This is exactly the game's
    /// <c>RunState.TotalFloor</c> (RunState.cs:158), the number written into <c>floor_added_to_deck</c>.
    /// </summary>
    public static int TotalFloors(JsonElement history)
    {
        if (history.ValueKind != JsonValueKind.Array) return 0;
        int n = 0;
        foreach (JsonElement act in history.EnumerateArray())
            if (act.ValueKind == JsonValueKind.Array) n += act.GetArrayLength();
        return n;
    }

    /// <summary>Entries in one act (0-based), or 0 if the act has no history yet.</summary>
    public static int FloorsInAct(JsonElement history, int actIndex) =>
        history.ValueKind == JsonValueKind.Array && actIndex < history.GetArrayLength()
        && history[actIndex].ValueKind == JsonValueKind.Array
            ? history[actIndex].GetArrayLength()
            : 0;

    /// <summary>
    /// Cards a player removed while in a shop room. Checks <c>rooms[].room_type == "shop"</c>, not
    /// <c>map_point_type</c>, because a "?" node (<c>unknown</c>) can roll into a shop.
    /// Fallback for saves without <c>extra_fields.card_shop_removals_used</c> (e.g. history files).
    /// </summary>
    public static int CountShopRemovals(JsonElement history, ulong playerId)
    {
        int n = 0;
        foreach (JsonElement entry in Entries(history))
        {
            if (!IsShop(entry)) continue;
            if (PlayerStats(entry, playerId) is { } stats
                && stats.TryGetProperty("cards_removed", out JsonElement removed)
                && removed.ValueKind == JsonValueKind.Array)
            {
                n += removed.GetArrayLength();
            }
        }
        return n;
    }

    /// <summary>All entries of all acts in order.</summary>
    public static IEnumerable<JsonElement> Entries(JsonElement history)
    {
        if (history.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement act in history.EnumerateArray())
        {
            if (act.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement entry in act.EnumerateArray()) yield return entry;
        }
    }

    private static bool IsShop(JsonElement entry) =>
        entry.TryGetProperty("rooms", out JsonElement rooms) && rooms.ValueKind == JsonValueKind.Array
        && rooms.EnumerateArray().Any(r => r.TryGetProperty("room_type", out JsonElement t) && t.ValueEquals("shop"));

    /// <summary><c>player_stats[]</c> element whose <c>player_id</c> matches (single player uses 1).</summary>
    public static JsonElement? PlayerStats(JsonElement entry, ulong playerId)
    {
        if (!entry.TryGetProperty("player_stats", out JsonElement stats) || stats.ValueKind != JsonValueKind.Array)
            return null;
        foreach (JsonElement s in stats.EnumerateArray())
            if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("player_id", out JsonElement id)
                && id.ValueKind == JsonValueKind.Number && id.TryGetUInt64(out ulong v) && v == playerId)
                return s;
        return null;
    }
}
