using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Overlay.Core.Data;

namespace Sts2Overlay.Core.Events;

/// <summary>
/// What every event option does, read from the decompiled game code (<c>data/packs/&lt;ver&gt;/events.json</c>).
/// Names-free: only model ids, localization keys and numbers. Titles come from the local pck extraction at runtime.
/// </summary>
public sealed class EventCatalog
{
    public const string FileName = "events.json";

    public string? GameVersion { get; init; }

    public IReadOnlyList<EventDef> Events { get; init; } = [];

    private Dictionary<string, EventDef>? _byId;
    private Dictionary<string, (EventDef Event, EventPage Page, EventOptionDef Option)>? _byKey;

    public static EventCatalog Empty { get; } = new();

    public EventDef? Find(string eventId)
    {
        _byId ??= Events.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return _byId.TryGetValue(eventId, out var e) ? e : null;
    }

    /// <summary>The option with this exact text key (e.g. <c>ABYSSAL_BATHS.pages.INITIAL.options.IMMERSE</c>), with its event and page.</summary>
    public (EventDef Event, EventPage Page, EventOptionDef Option)? FindOption(string textKey)
    {
        if (_byKey is null)
        {
            var map = new Dictionary<string, (EventDef, EventPage, EventOptionDef)>(StringComparer.Ordinal);
            foreach (var e in Events)
            {
                foreach (var p in e.Pages)
                {
                    foreach (var o in p.Options) map.TryAdd(o.Key, (e, p, o));
                }
            }
            _byKey = map;
        }
        return _byKey.TryGetValue(textKey, out var hit) ? hit : null;
    }

    /// <summary>Loads <c>events.json</c> from a pack directory; <see cref="Empty"/> when the file does not exist.</summary>
    /// <exception cref="InvalidDataException">The file exists but is malformed (message starts with the path).</exception>
    public static EventCatalog LoadDirectory(string packDirectory)
    {
        var path = Path.Combine(packDirectory, FileName);
        return File.Exists(path) ? LoadFile(path) : Empty;
    }

    public static EventCatalog LoadFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    public static EventCatalog Parse(string json)
        => JsonSerializer.Deserialize<EventCatalog>(json, DataPackLoader.JsonOptions) ?? Empty;
}

public sealed class EventDef
{
    /// <summary><c>EVENT.ABYSSAL_BATHS</c>.</summary>
    public required string Id { get; init; }

    /// <summary><c>event</c> or <c>ancient</c> (Neow and the other act-start ancients).</summary>
    public string Kind { get; init; } = "event";

    public bool Shared { get; init; }

    /// <summary>Acts whose event pool holds it (<c>ACT.OVERGROWTH</c> ...); empty = shared / unknown.</summary>
    public IReadOnlyList<string> Acts { get; init; } = [];

    /// <summary>Short note of the game's <c>IsAllowed</c> conditions.</summary>
    public string? Allowed { get; init; }

    /// <summary>Base values of the event's dynamic variables (<c>Damage</c>, <c>Gold</c> ...), usable in formulas.</summary>
    public IReadOnlyDictionary<string, double> Vars { get; init; } = new Dictionary<string, double>();

    public string? Notes { get; init; }

    public IReadOnlyList<EventPage> Pages { get; init; } = [];

    public bool IsAncient => string.Equals(Kind, "ancient", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first page (<c>INITIAL</c> for almost every event).</summary>
    public EventPage? InitialPage => Pages.FirstOrDefault(p => p.Page == "INITIAL") ?? Pages.FirstOrDefault();

    public EventPage? FindPage(string? page) => page is null ? null : Pages.FirstOrDefault(p => string.Equals(p.Page, page, StringComparison.Ordinal));
}

public sealed class EventPage
{
    public required string Page { get; init; }
    public IReadOnlyList<EventOptionDef> Options { get; init; } = [];
}

public sealed class EventOptionDef
{
    /// <summary>Exact text key the game passes to <c>EventOption</c>; the localization keys add <c>.title</c>/<c>.description</c>.</summary>
    public required string Key { get; init; }

    /// <summary>When the option is present or locked (free text from the code reading).</summary>
    public string? Condition { get; init; }

    public IReadOnlyList<EventEffect> Effects { get; init; } = [];

    /// <summary>Page shown after choosing it when that page offers more options; null when the event ends.</summary>
    public string? Next { get; init; }

    public bool Leave { get; init; }

    /// <summary>Last key segment (<c>IMMERSE</c>).</summary>
    [JsonIgnore]
    public string ShortName => Key[(Key.LastIndexOf('.') + 1)..];
}

/// <summary>One thing an option does to the player. Which fields are used depends on <see cref="Type"/> (see <see cref="EventEffectTypes"/>).</summary>
public sealed class EventEffect
{
    public required string Type { get; init; }

    /// <summary>A number, or a formula string (<see cref="EventFormula"/>), or absent.</summary>
    public JsonElement? Amount { get; init; }

    public JsonElement? Count { get; init; }

    /// <summary><c>choose</c> | <c>random</c> | <c>all</c> | <c>fixed</c>.</summary>
    public string? How { get; init; }

    public string? Filter { get; init; }
    public string? Card { get; init; }
    public string? Pool { get; init; }
    public string? Rarity { get; init; }
    public int? Options { get; init; }
    public string? Enchantment { get; init; }
    public string? Relic { get; init; }
    public string? Tier { get; init; }
    public string? Potion { get; init; }
    public string? Encounter { get; init; }
    public string? Rewards { get; init; }
    public string? Note { get; init; }

    /// <summary>Probability the effect happens (1 when absent).</summary>
    public double? Chance { get; init; }
}

/// <summary>The effect type vocabulary of <c>events.json</c>.</summary>
public static class EventEffectTypes
{
    public const string HpLoss = "hpLoss";
    public const string Heal = "heal";
    public const string MaxHp = "maxHp";
    public const string Gold = "gold";
    public const string CardRemove = "cardRemove";
    public const string CardTransform = "cardTransform";
    public const string CardUpgrade = "cardUpgrade";
    public const string CardAdd = "cardAdd";
    public const string Curse = "curse";
    public const string CardReward = "cardReward";
    public const string Enchant = "enchant";
    public const string Relic = "relic";
    public const string RelicLose = "relicLose";
    public const string Potion = "potion";
    public const string PotionLose = "potionLose";
    public const string Fight = "fight";
    public const string Duplicate = "duplicate";
    public const string Other = "other";
}
