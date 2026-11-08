using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Data;

/// <summary>
/// Names-free card facts (ids and stats) extracted from the game, e.g. <c>data/catalog/cards.v0.107.1.json</c>
/// produced by <c>tools/CatalogBuilder</c> (static IL of sts2.dll). See docs/DATA_SCHEMA.md §8.
/// </summary>
/// <remarks>
/// Values are kept as written in the file (the real catalog uses PascalCase such as <c>Attack</c> / <c>Rare</c>);
/// every consumer compares type and rarity case-insensitively (<see cref="DataPack.CardType"/> and
/// <see cref="DataPack.CardRarity"/> return lower case).
/// </remarks>
public sealed class CardCatalog
{
    public string? GameVersion { get; set; }

    /// <summary>Game build commit (older dumps).</summary>
    public string? Commit { get; set; }

    /// <summary>Tool that produced the file.</summary>
    public string? Source { get; set; }

    public string? Note { get; set; }

    /// <summary>Card count declared by the file (informational; <see cref="Cards"/> is authoritative).</summary>
    public int? CardCount { get; set; }

    public List<CatalogCard> Cards { get; set; } = [];

    /// <summary>Cards whose <see cref="CatalogCard.PoolOwner"/> is <paramref name="owner"/> (<c>CHARACTER.X</c> or <see cref="DataPack.Colorless"/>).</summary>
    public IEnumerable<CatalogCard> CardsOf(string owner)
        => Cards.Where(c => string.Equals(c.PoolOwner, owner, StringComparison.Ordinal));

    /// <summary>
    /// Lower-case rarity → number of cards of <paramref name="owner"/>'s pool that can be offered in this game mode:
    /// <see cref="CatalogCard.IsAvailable"/> (multiplayer-only cards are left out of single player, single-player-only cards out of
    /// multiplayer, and cards hidden from the library are left out). Every rarity is counted (basic and ancient too); callers
    /// decide which rarities a source can roll.
    /// </summary>
    public IReadOnlyDictionary<string, int> PoolSizes(string owner, bool isMultiplayer)
        => CardsOf(owner)
            .Where(c => c.IsAvailable(isMultiplayer) && !string.IsNullOrEmpty(c.Rarity))
            .GroupBy(c => c.Rarity!.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}

/// <summary>One card of the <see cref="CardCatalog"/>.</summary>
public sealed class CatalogCard
{
    /// <summary>Value of <see cref="Multiplayer"/> for cards that exist only in multiplayer runs.</summary>
    public const string MultiplayerOnlyMode = "MultiplayerOnly";

    /// <summary>Value of <see cref="Multiplayer"/> for cards that exist only in single-player runs.</summary>
    public const string SingleplayerOnlyMode = "SingleplayerOnly";

    private const string PoolPrefix = "CARD_POOL.";
    private const string PoolSuffix = "_CARD_POOL";
    private const string CharacterPrefix = "CHARACTER.";

    /// <summary>Game pools that are not a character's or the colorless pool (no reward/shop owner).</summary>
    private static readonly HashSet<string> OwnerlessPools = new(StringComparer.Ordinal)
    {
        "EVENT", "CURSE", "TOKEN", "STATUS", "QUEST", "DEPRECATED",
    };

    private string? _character;
    private bool _multiplayerOnly;
    private bool _singleplayerOnly;

    public string Id { get; set; } = "";

    /// <summary>
    /// Card pool as written: the game's <c>CARD_POOL.DEFECT_CARD_POOL</c> / <c>CARD_POOL.COLORLESS_CARD_POOL</c> / <c>CARD_POOL.EVENT_CARD_POOL</c> ...
    /// (older dumps: <c>CHARACTER.DEFECT</c> or <c>COLORLESS</c>). Use <see cref="PoolOwner"/> to compare with a character.
    /// </summary>
    public string? Pool { get; set; }

    /// <summary>Owning character, e.g. <c>CHARACTER.DEFECT</c>; null (or the string <c>"None"</c> in the file) for shared pools.</summary>
    public string? Character
    {
        get => _character;
        set => _character = string.IsNullOrWhiteSpace(value) || string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    /// <summary>Local display names only; never committed (the repo catalog is names-free).</summary>
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Attack | Skill | Power | Status | Curse | Quest (any case).</summary>
    public string? Type { get; set; }

    /// <summary>Basic | Common | Uncommon | Rare | Ancient | Event | Curse | Status | Token | Quest (any case).</summary>
    public string? Rarity { get; set; }

    public int? Cost { get; set; }

    [JsonConverter(typeof(NullAsFalseConverter))]
    public bool CostX { get; set; }

    /// <summary>Star cost (Regent); -1 when the card has none.</summary>
    public int StarCost { get; set; } = -1;

    /// <summary>Self | AnyEnemy | AllEnemies | None | RandomEnemy | AnyAlly | AllAllies.</summary>
    public string? Target { get; set; }

    /// <summary>Whether the card gains block. The extractor writes null when it could not tell (2 cards in v0.107.1); read as false.</summary>
    [JsonConverter(typeof(NullAsFalseConverter))]
    public bool GainsBlock { get; set; }

    /// <summary>Highest upgrade level (usually 1).</summary>
    public int? MaxUpgrade { get; set; }

    /// <summary>Game-mode restriction as written: <c>None</c>, <see cref="MultiplayerOnlyMode"/> or <see cref="SingleplayerOnlyMode"/>.</summary>
    public string? Multiplayer { get; set; }

    /// <summary>
    /// True when <see cref="Multiplayer"/> is <c>MultiplayerOnly</c>, or when an older catalog sets <c>"multiplayerOnly": true</c>.
    /// </summary>
    public bool MultiplayerOnly
    {
        get => _multiplayerOnly || string.Equals(Multiplayer, MultiplayerOnlyMode, StringComparison.OrdinalIgnoreCase);
        set => _multiplayerOnly = value;
    }

    /// <summary>True when <see cref="Multiplayer"/> is <c>SingleplayerOnly</c> (or <c>"singleplayerOnly": true</c>).</summary>
    public bool SingleplayerOnly
    {
        get => _singleplayerOnly || string.Equals(Multiplayer, SingleplayerOnlyMode, StringComparison.OrdinalIgnoreCase);
        set => _singleplayerOnly = value;
    }

    public List<string> Keywords { get; set; } = [];
    public List<string> Tags { get; set; } = [];

    /// <summary>False for cards the game hides from the card library (deprecated and a few event cards).</summary>
    public bool ShowInLibrary { get; set; } = true;

    /// <summary>
    /// Whose card pool this card belongs to: <see cref="Character"/> when present, else the pool mapped
    /// <c>CARD_POOL.X_CARD_POOL</c> → <c>CHARACTER.X</c>, colorless → <see cref="DataPack.Colorless"/>.
    /// Null for event, curse, token, status, quest and deprecated pools.
    /// </summary>
    [JsonIgnore]
    public string? PoolOwner => Character ?? PoolOwnerOf(Pool);

    /// <summary>
    /// Can be generated in a run of this mode: not excluded by <see cref="MultiplayerOnly"/> / <see cref="SingleplayerOnly"/>,
    /// and shown in the library.
    /// </summary>
    public bool IsAvailable(bool isMultiplayer)
        => ShowInLibrary && (isMultiplayer ? !SingleplayerOnly : !MultiplayerOnly);

    /// <summary>
    /// Maps a pool string to its owner: <c>CARD_POOL.DEFECT_CARD_POOL</c> → <c>CHARACTER.DEFECT</c>,
    /// <c>CARD_POOL.COLORLESS_CARD_POOL</c> → <see cref="DataPack.Colorless"/>, shared pools (event, curse, token, status, quest,
    /// deprecated) → null. Old-format values (<c>CHARACTER.X</c>, <c>COLORLESS</c>) are returned unchanged.
    /// </summary>
    public static string? PoolOwnerOf(string? pool)
    {
        if (string.IsNullOrWhiteSpace(pool)) return null;
        if (pool == DataPack.Colorless || pool.StartsWith(CharacterPrefix, StringComparison.Ordinal)) return pool;
        if (!pool.StartsWith(PoolPrefix, StringComparison.Ordinal) || !pool.EndsWith(PoolSuffix, StringComparison.Ordinal)
            || pool.Length <= PoolPrefix.Length + PoolSuffix.Length)
        {
            return null;
        }
        var name = pool[PoolPrefix.Length..^PoolSuffix.Length];
        if (name == DataPack.Colorless) return DataPack.Colorless;
        return OwnerlessPools.Contains(name) ? null : CharacterPrefix + name;
    }
}

/// <summary>Reads a JSON boolean, treating <c>null</c> as false (catalog fields the extractor could not determine).</summary>
internal sealed class NullAsFalseConverter : JsonConverter<bool>
{
    public override bool Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        => reader.TokenType switch
        {
            System.Text.Json.JsonTokenType.Null => false,
            System.Text.Json.JsonTokenType.True => true,
            System.Text.Json.JsonTokenType.False => false,
            _ => throw new System.Text.Json.JsonException($"Expected a boolean or null but found {reader.TokenType}."),
        };

    public override void Write(System.Text.Json.Utf8JsonWriter writer, bool value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
