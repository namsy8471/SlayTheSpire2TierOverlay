using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.History;

/// <summary>
/// A parsed <c>history/&lt;unix&gt;.run</c> file (schema 8 = v0.98.x, schema 9 = v0.107.x).
/// Unknown fields are ignored; missing optional fields become empty collections / zero.
/// </summary>
public sealed record RunHistory
{
    public int SchemaVersion { get; init; }
    public string BuildId { get; init; } = "";
    public string Seed { get; init; } = "";
    public string GameMode { get; init; } = "";
    public int Ascension { get; init; }
    public bool Win { get; init; }
    public bool WasAbandoned { get; init; }
    public long StartTime { get; init; }
    public int RunTime { get; init; }
    public string? KilledByEncounter { get; init; }
    public string? KilledByEvent { get; init; }

    /// <summary>Act model ids in order, e.g. <c>ACT.OVERGROWTH</c>.</summary>
    public IReadOnlyList<string> Acts { get; init; } = [];

    public IReadOnlyList<RunPlayer> Players { get; init; } = [];

    /// <summary>All visited map points flattened in visit order. <c>Nodes[i].Floor == i + 1</c>.</summary>
    public IReadOnlyList<RunNode> Nodes { get; init; } = [];

    public bool IsMultiplayer => Players.Count > 1;

    public RunPlayer? FindPlayer(long id) => Players.FirstOrDefault(p => p.Id == id);
}

public sealed record RunPlayer
{
    /// <summary>Steam id in multiplayer, 1 in single player.</summary>
    public long Id { get; init; }

    public string Character { get; init; } = "";

    /// <summary>Final deck.</summary>
    public IReadOnlyList<DeckCard> Deck { get; init; } = [];

    public IReadOnlyList<RelicEntry> Relics { get; init; } = [];
    public IReadOnlyList<string> Potions { get; init; } = [];
}

public sealed record RelicEntry(string Id, int FloorAdded);

public sealed record RunNode
{
    /// <summary>0-based act index.</summary>
    public int ActIndex { get; init; }

    public int Act => ActIndex + 1;

    /// <summary>0-based index inside the act.</summary>
    public int IndexInAct { get; init; }

    /// <summary>Global 1-based floor (same numbering as <c>floor_added_to_deck</c>).</summary>
    public int Floor { get; init; }

    /// <summary>ancient | monster | elite | rest_site | shop | treasure | unknown | boss</summary>
    public string MapPointType { get; init; } = "";

    public IReadOnlyList<RunRoom> Rooms { get; init; } = [];
    public IReadOnlyList<PlayerNodeStats> Stats { get; init; } = [];

    public PlayerNodeStats? StatsFor(long playerId) => Stats.FirstOrDefault(s => s.PlayerId == playerId);

    /// <summary>True when any room at this point was a shop.</summary>
    public bool HasShop => Rooms.Any(r => r.RoomType == "shop");
}

public sealed record RunRoom(string RoomType, string? ModelId, IReadOnlyList<string> MonsterIds, int TurnsTaken);

public sealed record CardChoice(DeckCard Card, bool WasPicked);
public sealed record CardTransform(DeckCard Original, DeckCard Final);
public sealed record PickChoice(string Id, bool WasPicked);

/// <summary>One player's <c>player_stats</c> entry at one map point.</summary>
public sealed record PlayerNodeStats
{
    public long PlayerId { get; init; }
    public IReadOnlyList<CardChoice> CardChoices { get; init; } = [];
    public IReadOnlyList<DeckCard> CardsGained { get; init; } = [];
    public IReadOnlyList<DeckCard> CardsRemoved { get; init; } = [];
    public IReadOnlyList<CardTransform> CardsTransformed { get; init; } = [];

    /// <summary>Card ids upgraded at this point (rest site smith, events…).</summary>
    public IReadOnlyList<string> UpgradedCards { get; init; } = [];

    /// <summary>Cards that received an enchantment here (the card as it is after enchanting).</summary>
    public IReadOnlyList<DeckCard> CardsEnchanted { get; init; } = [];

    public IReadOnlyList<string> RestSiteChoices { get; init; } = [];
    public IReadOnlyList<PickChoice> RelicChoices { get; init; } = [];
    public IReadOnlyList<PickChoice> PotionChoices { get; init; } = [];
    public IReadOnlyList<string> BoughtRelics { get; init; } = [];
    public IReadOnlyList<string> BoughtPotions { get; init; } = [];
    public IReadOnlyList<string> RelicsRemoved { get; init; } = [];

    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }
    public int CurrentGold { get; init; }
    public int DamageTaken { get; init; }
    public int HpHealed { get; init; }
    public int MaxHpGained { get; init; }
    public int MaxHpLost { get; init; }
    public int GoldGained { get; init; }
    public int GoldSpent { get; init; }
    public int GoldLost { get; init; }
    public int GoldStolen { get; init; }
}
