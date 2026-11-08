using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Data;

/// <summary><c>archetypes/&lt;character&gt;.json</c> entries (PLAN §6.3).</summary>
public sealed class Archetype
{
    public string Id { get; set; } = "";
    public string Character { get; set; } = "";
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.Ordinal);
    public string? Color { get; set; }
    public ArchetypeCards Cards { get; set; } = new();

    /// <summary>Weight per role in this archetype (core/enabler/payoff/support).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, double> RoleWeight { get; set; } = new(StringComparer.Ordinal)
    {
        [ArchetypeRoles.Core] = 1.0,
        [ArchetypeRoles.Enabler] = 0.7,
        [ArchetypeRoles.Payoff] = 0.9,
        [ArchetypeRoles.Support] = 0.5,
    };

    /// <summary>Sum of owned role weights (plus relic weights) at which commit reaches 1.0.</summary>
    public double FullWeight { get; set; } = 4.0;

    public double CommitThreshold { get; set; } = 0.7;

    /// <summary>
    /// Distinct core cards the build needs before it works (default 1). Until the deck owns this many, the scorer pushes the
    /// missing core cards (<c>core_priority</c>) and gates the build's other cards by <c>ownedCore / coreTarget</c> (<c>core_missing</c>).
    /// Clamped to the number of core cards, so an archetype without core cards is always "core secured".
    /// </summary>
    public int CoreTarget { get; set; } = 1;

    /// <summary>
    /// Distinct essential (payoff + enabler) cards the build needs before its support cards count fully; null uses
    /// <see cref="CorePriorityWeights.EssentialTarget"/>. Clamped to the number of essential cards.
    /// </summary>
    public int? EssentialTarget { get; set; }

    public List<KeyRelic> KeyRelics { get; set; } = [];
    public List<ArchetypeNeed> Needs { get; set; } = [];
    public string? Desc { get; set; }
    public string? GuideHeroCard { get; set; }

    [JsonIgnore]
    public string DisplayName => Names.TryGetValue("kor", out var k) ? k : Names.Values.FirstOrDefault() ?? Id;

    /// <summary>Returns the archetype role of a card ("core", "enabler", "payoff", "support") or null.</summary>
    public string? RoleOf(string cardId)
    {
        if (Cards.Core.Contains(cardId)) return ArchetypeRoles.Core;
        if (Cards.Payoff.Contains(cardId)) return ArchetypeRoles.Payoff;
        if (Cards.Enabler.Contains(cardId)) return ArchetypeRoles.Enabler;
        if (Cards.Support.Contains(cardId)) return ArchetypeRoles.Support;
        return null;
    }

    /// <summary>Role weight of the card in this archetype, 0 when it is not part of it.</summary>
    public double WeightOf(string cardId)
    {
        var role = RoleOf(cardId);
        return role is not null && RoleWeight.TryGetValue(role, out var w) ? w : 0.0;
    }
}

public static class ArchetypeRoles
{
    public const string Core = "core";
    public const string Enabler = "enabler";
    public const string Payoff = "payoff";
    public const string Support = "support";
}

public sealed class ArchetypeCards
{
    public List<string> Core { get; set; } = [];
    public List<string> Enabler { get; set; } = [];
    public List<string> Payoff { get; set; } = [];
    public List<string> Support { get; set; } = [];

    [JsonIgnore]
    public IEnumerable<string> All => Core.Concat(Enabler).Concat(Payoff).Concat(Support);
}

public sealed class KeyRelic
{
    public string Id { get; set; } = "";
    public double W { get; set; } = 1.0;
}

/// <summary>A requirement such as "at least 2 block cards" (<c>tag</c>) or "draw supply ≥ 2" (<c>role</c>).</summary>
public sealed class ArchetypeNeed
{
    /// <summary>Axis tag from <see cref="CardTierEntry.Provides"/>.</summary>
    public string? Tag { get; set; }

    /// <summary>Role key from <see cref="Roles"/>; compared against role supply.</summary>
    public string? Role { get; set; }

    public double Min { get; set; } = 1;
}

/// <summary><c>combos/&lt;character&gt;.json</c> entries (PLAN §6.4).</summary>
public sealed class Combo
{
    public string? Character { get; set; }
    public List<string> Cards { get; set; } = [];
    public string Name { get; set; } = "";
    public string? Why { get; set; }
    public double W { get; set; } = 6;
}
