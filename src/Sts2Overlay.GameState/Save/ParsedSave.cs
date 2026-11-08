using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.GameState.Save;

/// <summary>A potion in a belt slot (<c>players[].potions[]</c>).</summary>
public readonly record struct PotionSlot(string Id, int Slot);

/// <summary>
/// Everything we read from one <c>current_run.save</c> / <c>current_run_mp.save</c>.
/// <see cref="State"/> and <see cref="Map"/> are the Core types the engine consumes; the rest are extras.
/// </summary>
public sealed record ParsedSave
{
    /// <summary>"My" player's view of the run (co-op: the player whose <c>net_id</c> matched).</summary>
    public required RunState State { get; init; }

    /// <summary>Current act's map with our position, or null if the save has no <c>saved_map</c>.</summary>
    public MapGraph? Map { get; init; }

    /// <summary>Run seed string (<c>rng.seed</c>), e.g. <c>H6VRKY8PFE</c>. Same as the log's <c>Seed:</c>.</summary>
    public string? Seed { get; init; }

    public int SchemaVersion { get; init; }

    /// <summary><c>net_id</c> of the player we picked as "me" (1 in single player, Steam ID in co-op).</summary>
    public ulong MyNetId { get; init; }

    /// <summary>False when no player matched our id and we fell back to <c>players[0]</c>.</summary>
    public bool MyPlayerMatched { get; init; } = true;

    /// <summary>All players' <c>net_id</c>s in save order (= lobby slot order).</summary>
    public IReadOnlyList<ulong> PlayerNetIds { get; init; } = [];

    public IReadOnlyList<PotionSlot> Potions { get; init; } = [];
    public int MaxPotionSlots { get; init; } = 3;

    /// <summary>
    /// Rare-card pity offset (<c>players[].odds.card_rarity_odds_value</c>): starts at -0.05, +0.01 per non-rare
    /// reward card (A7+: +0.005), capped at 0.4, reset to -0.05 when a rare shows up (CardRarityOdds.cs).
    /// </summary>
    public double? CardRarityOffset { get; init; }

    /// <summary><c>players[].odds.potion_reward_odds_value</c>: chance that the next fight drops a potion.</summary>
    public double? PotionRewardOdds { get; init; }

    /// <summary>Run-level <c>odds.*</c> for "?" rooms, e.g. <c>unknown_map_point_shop_odds_value</c>.</summary>
    public IReadOnlyDictionary<string, double> UnknownMapPointOdds { get; init; } = new Dictionary<string, double>();

    /// <summary><c>save_time</c> (unix seconds) as UTC.</summary>
    public DateTimeOffset? SaveTime { get; init; }

    /// <summary>Act model id, e.g. <c>ACT.UNDERDOCKS</c>.</summary>
    public string? ActId { get; init; }

    public string? GameMode { get; init; }

    /// <summary>Number of run modifiers (custom/daily runs; they replace Neow's options). -1 when the save has no list.</summary>
    public int ModifierCount { get; init; } = -1;

    /// <summary>The current act's event bookkeeping, for <see cref="Tracking.EventPrediction"/>. Null when the save lacks it.</summary>
    public EventRoomState? Events { get; init; }
}

/// <summary>
/// What the game pulls the next event from (<c>acts[i].rooms</c>, <c>events_seen</c>, the "?" room odds stream) plus the
/// facts that tell a room-entry save apart from later saves of the same room.
/// </summary>
public sealed record EventRoomState
{
    public int ActIndex { get; init; }

    /// <summary><c>acts[cur].rooms.event_ids</c>: the act's events, shuffled once at run start.</summary>
    public IReadOnlyList<string> EventIds { get; init; } = [];

    /// <summary><c>acts[cur].rooms.events_visited</c>: index the next pull starts from (the act's ancient counts as one).</summary>
    public int EventsVisited { get; init; }

    public string? AncientId { get; init; }

    /// <summary>Run-wide <c>events_seen</c>: a seen event is skipped by later pulls.</summary>
    public IReadOnlyList<string> EventsSeen { get; init; } = [];

    /// <summary><c>visited_map_coords</c> count (this act) and <c>map_point_history[cur]</c> count. The room-entry save has one more visited coord.</summary>
    public int VisitedInAct { get; init; }
    public int HistoryInAct { get; init; }

    /// <summary>All map-history entries of the run (= RunState.TotalFloor at pull time on the entry save).</summary>
    public int TotalHistory { get; init; }

    /// <summary><c>pre_finished_room</c> is set (a reload into a room whose combat already ended).</summary>
    public bool PreFinishedRoom { get; init; }

    /// <summary><c>rng.counters.unknown_map_point</c>: calls already drawn from the "?" room stream.</summary>
    public int UnknownMapPointCounter { get; init; }

    /// <summary>The previous map point had a shop room (a "?" can't roll a shop right after one).</summary>
    public bool PreviousWasShop { get; init; }

    /// <summary><c>players[].unlock_state.number_of_runs</c> (the very first run forces its first "?" rooms).</summary>
    public int NumberOfRuns { get; init; } = -1;

    public bool IsRoomEntrySave => !PreFinishedRoom && VisitedInAct == HistoryInAct + 1;
}
