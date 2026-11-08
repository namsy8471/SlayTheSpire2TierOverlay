using System.Text.Json;

namespace Sts2Overlay.Core.Combat;

/// <summary>How faithfully a card/relic/potion is described by its ops.</summary>
public enum CombatModel
{
    /// <summary>Every damage-relevant effect is expressed exactly (or it has none).</summary>
    Full,

    /// <summary>Expressed with a simplification (random or conditional effects).</summary>
    Approx,

    /// <summary>Affects damage in a way the ops can't express; simulated as doing nothing.</summary>
    None,
}

/// <summary>A card's combat behaviour at one upgrade level.</summary>
public sealed record CardCombat
{
    public required string Id { get; init; }
    public CombatModel Model { get; init; }
    public bool Upgraded { get; init; }

    /// <summary>Energy cost; −1 = unplayable.</summary>
    public int Cost { get; init; }
    public bool CostX { get; init; }

    /// <summary>A formula that replaces <see cref="Cost"/> when the card is played (Stomp-like cards).</summary>
    public CombatAmount? CostExpr { get; init; }

    public IReadOnlyList<CombatOp> Play { get; init; } = [];
    public IReadOnlyList<CombatOp> OnDraw { get; init; } = [];
    public IReadOnlyList<CombatOp> OnExhaust { get; init; } = [];
    public IReadOnlyList<CombatOp> EndOfTurnInHand { get; init; } = [];

    /// <summary>The card can only be played while this is &gt; 0.</summary>
    public CombatAmount? PlayableIf { get; init; }

    /// <summary>Catalog keywords at this level (Exhaust, Ethereal, Innate, Retain, Sly, Unplayable, …).</summary>
    public IReadOnlySet<string> Keywords { get; init; } = new HashSet<string>();

    /// <summary>Catalog type: Attack, Skill, Power, Status, Curse, Quest.</summary>
    public string Type { get; init; } = "";

    /// <summary>Catalog tags (e.g. Strike).</summary>
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();

    /// <summary>Catalog pool (<c>CARD_POOL.IRONCLAD_CARD_POOL</c>) and rarity, for random-card effects.</summary>
    public string Pool { get; init; } = "";
    public string Rarity { get; init; } = "";

    /// <summary>Regent star cost (−1 = none).</summary>
    public int StarCost { get; init; } = -1;

    /// <summary>Spends every star; X = stars spent.</summary>
    public bool StarCostX { get; init; }

    /// <summary>While in hand, at most this many cards can be played per turn (Normality).</summary>
    public int? InHandMaxCardsPerTurn { get; init; }

    /// <summary>While in hand, no other card can be played (Enthralled).</summary>
    public bool MustPlayFirst { get; init; }

    public string? Note { get; init; }

    public bool IsAttack => Type == "Attack";

    public bool Has(string keyword) => Keywords.Contains(keyword);
    public bool Unplayable => (Cost < 0 && !CostX) || Has("Unplayable");
}

/// <summary>A relic's effects in the first turns of a fight.</summary>
public sealed record RelicCombat
{
    public required string Id { get; init; }
    public CombatModel Model { get; init; }

    /// <summary>Runs before the turn-1 draw; persistent effects (triggers, modifiers) are registered here.</summary>
    public IReadOnlyList<CombatOp> CombatStart { get; init; } = [];

    /// <summary>Runs after the hand draw of <see cref="Turns"/>.</summary>
    public IReadOnlyList<CombatOp> TurnStart { get; init; } = [];

    /// <summary>Turns <see cref="TurnStart"/> runs on (1-based); empty = every turn.</summary>
    public IReadOnlyList<int> Turns { get; init; } = [];

    public IReadOnlyList<CombatOp> TurnEnd { get; init; } = [];
    public IReadOnlyList<int> TurnEndTurns { get; init; } = [];

    public int DrawPerTurn { get; init; }
    public int EnergyPerTurn { get; init; }
    public string? Note { get; init; }
}

public sealed record PotionCombat
{
    public required string Id { get; init; }
    public CombatModel Model { get; init; }
    public IReadOnlyList<CombatOp> Use { get; init; } = [];
    public string? Note { get; init; }
}

/// <summary>A character's turn economy at combat start.</summary>
public sealed record CharacterCombat
{
    public required string Id { get; init; }
    public int Energy { get; init; } = 3;
    public int Draw { get; init; } = 5;
    public int OrbSlots { get; init; }

    /// <summary>The character's card pool, for "random card of your class" effects.</summary>
    public string? Pool { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// Combat numbers for the 3-turn damage simulator, extracted from the game's card/relic/potion classes
/// (<c>data/packs/&lt;ver&gt;/combat/*.json</c>). Ids only, no game text.
/// </summary>
public sealed class CombatData
{
    private readonly Dictionary<string, (CardCombat Base, CardCombat Up)> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RelicCombat> _relics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PotionCombat> _potions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CharacterCombat> _characters = new(StringComparer.Ordinal);
    private Dictionary<string, CardCombat[]>? _byPool;

    public int CardCount => _cards.Count;
    public IEnumerable<string> CardIds => _cards.Keys;
    public IEnumerable<RelicCombat> Relics => _relics.Values;
    public IEnumerable<PotionCombat> Potions => _potions.Values;
    public IEnumerable<CharacterCombat> Characters => _characters.Values;

    /// <summary>The card at <paramref name="upgrade"/> level (0 = base); null when the card is unknown.</summary>
    public CardCombat? Card(string id, int upgrade = 0)
        => _cards.TryGetValue(id, out var c) ? (upgrade > 0 ? c.Up : c.Base) : null;

    public RelicCombat? Relic(string id) => _relics.GetValueOrDefault(id);
    public PotionCombat? Potion(string id) => _potions.GetValueOrDefault(id);

    public CharacterCombat Character(string? id)
        => id is not null && _characters.TryGetValue(id, out var c) ? c : new CharacterCombat { Id = id ?? "" };

    /// <summary>
    /// Base-level cards of a pool that random-card effects can create (Common/Uncommon/Rare, playable).
    /// </summary>
    public IReadOnlyList<CardCombat> RandomPool(string pool)
    {
        _byPool ??= _cards.Values
            .Select(c => c.Base)
            .Where(c => c.Rarity is "Common" or "Uncommon" or "Rare" && !c.Unplayable)
            .GroupBy(c => c.Pool, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return _byPool.TryGetValue(pool, out var list) ? list : [];
    }

    public const string DirectoryName = "combat";
    public const string ColorlessPool = "CARD_POOL.COLORLESS_CARD_POOL";

    /// <summary>Loads <c>&lt;packDir&gt;/combat</c>; null when the folder is missing.</summary>
    public static CombatData? LoadPack(string packDir)
    {
        var dir = Path.Combine(packDir, DirectoryName);
        return Directory.Exists(dir) ? LoadDirectory(dir) : null;
    }

    public static CombatData LoadDirectory(string dir)
    {
        var data = new CombatData();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                data.Add(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{file}: {ex.Message}", ex);
            }
        }
        return data;
    }

    public static CombatData Parse(params string[] jsons)
    {
        var data = new CombatData();
        foreach (var json in jsons)
        {
            using var doc = JsonDocument.Parse(json);
            data.Add(doc.RootElement);
        }
        return data;
    }

    /// <summary>
    /// Adds one file's content: <c>{"cards": {...}}</c>, <c>{"relics": {...}}</c>, <c>{"potions": {...}}</c>,
    /// <c>{"characters": {...}}</c> (any combination). Keys starting with <c>_</c> are comments.
    /// </summary>
    private void Add(JsonElement root)
    {
        _byPool = null;
        if (root.TryGetProperty("cards", out var cards))
        {
            foreach (var p in cards.EnumerateObject())
            {
                if (!p.Name.StartsWith('_')) AddCard(p.Name, p.Value);
            }
        }
        if (root.TryGetProperty("relics", out var relics))
        {
            foreach (var p in relics.EnumerateObject())
            {
                if (!p.Name.StartsWith('_')) _relics[p.Name] = ParseRelic(p.Name, p.Value);
            }
        }
        if (root.TryGetProperty("potions", out var potions))
        {
            foreach (var p in potions.EnumerateObject())
            {
                if (p.Name.StartsWith('_')) continue;
                _potions[p.Name] = new PotionCombat
                {
                    Id = p.Name,
                    Model = ParseModel(p.Value),
                    Use = CombatOp.ParseList(CombatOp.Get(p.Value, "use")),
                    Note = CombatOp.Str(p.Value, "note"),
                };
            }
        }
        if (root.TryGetProperty("characters", out var chars))
        {
            foreach (var p in chars.EnumerateObject())
            {
                if (p.Name.StartsWith('_')) continue;
                var v = p.Value;
                _characters[p.Name] = new CharacterCombat
                {
                    Id = p.Name,
                    Energy = Int(v, "energy") ?? 3,
                    Draw = Int(v, "draw") ?? 5,
                    OrbSlots = Int(v, "orbSlots") ?? 0,
                    Pool = CombatOp.Str(v, "pool"),
                    Note = CombatOp.Str(v, "note"),
                };
            }
        }
    }

    private void AddCard(string id, JsonElement v)
    {
        var (cost, x) = ParseCost(CombatOp.Get(v, "cost"));
        var (costUp, xUp) = CombatOp.Get(v, "costUp") is { } cu ? ParseCost(cu) : (cost, x);
        var keywords = StringSet(CombatOp.Get(v, "keywords"));
        var play = CombatOp.ParseList(CombatOp.Get(v, "play"));
        var onDraw = CombatOp.ParseList(CombatOp.Get(v, "onDraw"));
        var onExhaust = CombatOp.ParseList(CombatOp.Get(v, "onExhaust"));
        var endInHand = CombatOp.ParseList(CombatOp.Get(v, "endOfTurnInHand"));
        var costExpr = Amount(v, "costExpr");
        var b = new CardCombat
        {
            Id = id,
            Model = ParseModel(v),
            Cost = cost,
            CostX = x,
            CostExpr = costExpr,
            Play = play,
            OnDraw = onDraw,
            OnExhaust = onExhaust,
            EndOfTurnInHand = endInHand,
            PlayableIf = Amount(v, "playableIf"),
            Keywords = keywords,
            Type = CombatOp.Str(v, "type") ?? "",
            Tags = StringSet(CombatOp.Get(v, "tags")),
            Pool = CombatOp.Str(v, "pool") ?? "",
            Rarity = CombatOp.Str(v, "rarity") ?? "",
            StarCost = Int(v, "starCost") ?? -1,
            StarCostX = CombatOp.Get(v, "starCostX") is { ValueKind: JsonValueKind.True },
            InHandMaxCardsPerTurn = Int(v, "inHandMaxCardsPerTurn"),
            MustPlayFirst = CombatOp.Get(v, "mustPlayFirst") is { ValueKind: JsonValueKind.True },
            Note = CombatOp.Str(v, "note"),
        };
        var u = b with
        {
            Upgraded = true,
            Cost = costUp,
            CostX = xUp,
            CostExpr = Amount(v, "costExprUp") ?? costExpr,
            Play = UpList(v, "playUp", play),
            OnDraw = UpList(v, "onDrawUp", onDraw),
            OnExhaust = UpList(v, "onExhaustUp", onExhaust),
            EndOfTurnInHand = UpList(v, "endOfTurnInHandUp", endInHand),
            Keywords = CombatOp.Get(v, "keywordsUp") is { ValueKind: JsonValueKind.Array } ku ? StringSet(ku) : keywords,
            StarCost = Int(v, "starCostUp") ?? b.StarCost,
        };
        _cards[id] = (b, u);
    }

    private static IReadOnlyList<CombatOp> UpList(JsonElement v, string name, IReadOnlyList<CombatOp> fallback)
        => CombatOp.Get(v, name) is { ValueKind: JsonValueKind.Array } arr ? CombatOp.ParseList(arr) : fallback;

    private static RelicCombat ParseRelic(string id, JsonElement v) => new()
    {
        Id = id,
        Model = ParseModel(v),
        CombatStart = CombatOp.ParseList(CombatOp.Get(v, "combatStart")),
        TurnStart = CombatOp.ParseList(CombatOp.Get(v, "turnStart")),
        Turns = IntList(CombatOp.Get(v, "turns")),
        TurnEnd = CombatOp.ParseList(CombatOp.Get(v, "turnEnd")),
        TurnEndTurns = IntList(CombatOp.Get(v, "turnEndTurns")),
        DrawPerTurn = Int(v, "drawPerTurn") ?? 0,
        EnergyPerTurn = Int(v, "energyPerTurn") ?? 0,
        Note = CombatOp.Str(v, "note"),
    };

    private static int[] IntList(JsonElement? el)
        => el is { ValueKind: JsonValueKind.Array } t
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToArray()
            : [];

    private static CombatAmount? Amount(JsonElement v, string name)
        => CombatOp.Get(v, name) is { } el ? CombatAmount.Parse(el, CombatAmount.Zero) : null;

    private static CombatModel ParseModel(JsonElement v) => CombatOp.Str(v, "model") switch
    {
        "full" => CombatModel.Full,
        "approx" => CombatModel.Approx,
        _ => CombatModel.None,
    };

    private static (int Cost, bool X) ParseCost(JsonElement? el) => el switch
    {
        { ValueKind: JsonValueKind.Number } n => ((int)n.GetDouble(), false),
        { ValueKind: JsonValueKind.String } s when s.GetString() is "X" or "x" => (0, true),
        _ => (-1, false),
    };

    private static int? Int(JsonElement v, string name)
        => CombatOp.Get(v, name) is { ValueKind: JsonValueKind.Number } n ? (int)n.GetDouble() : null;

    private static HashSet<string> StringSet(JsonElement? el)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (el is { ValueKind: JsonValueKind.Array } arr)
        {
            foreach (var s in arr.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.String && s.GetString() is { } str) set.Add(str);
            }
        }
        return set;
    }
}
