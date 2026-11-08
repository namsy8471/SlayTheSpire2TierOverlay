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
}
