namespace Sts2Overlay.Core.Model;

/// <summary>
/// Everything the recommendation engine knows about "my" run at a decision point.
/// In co-op this is always the local player's view (PLAN §1.2, D4).
/// </summary>
public sealed record RunState
{
    /// <summary>Character model id, e.g. <c>CHARACTER.DEFECT</c>.</summary>
    public required string Character { get; init; }

    public int Ascension { get; init; }
    /// <summary>Schema recorded by a live save; offline evaluations can select a pack without a save.</summary>
    public int? SaveSchemaVersion { get; init; }
    public bool IsStandardRun { get; init; } = true;

    /// <summary>Act number, 1-based (1..3).</summary>
    public int Act { get; init; } = 1;

    /// <summary>Current act's publicly visible map boss. No later act or hidden second boss.</summary>
    public string? ActBoss { get; init; }

    /// <summary>Global floor number, 1-based, counted across acts like <c>floor_added_to_deck</c>.</summary>
    public int Floor { get; init; } = 1;

    public int Hp { get; init; }
    public int MaxHp { get; init; }
    public int Gold { get; init; }

    /// <summary>Empty slots on the local player's potion belt; null when its capacity or contents are unknown.</summary>
    public int? EmptyPotionSlots { get; init; }

    /// <summary>
    /// False when <see cref="Hp"/>, <see cref="MaxHp"/> and <see cref="Gold"/> are placeholders, not the real values: a run
    /// rebuilt from the game log alone (co-op guest, or no save matched the run) starts from the character's starting HP and
    /// gold and never sees damage, healing or spending. Advisors must not give verdicts that rest on them (rest heal vs smith,
    /// lethal HP loss, "not enough gold"), and the UI shows them as unknown.
    /// </summary>
    public bool VitalsKnown { get; init; } = true;

    public IReadOnlyList<DeckCard> Deck { get; init; } = [];

    /// <summary>Relic model ids, e.g. <c>RELIC.CRACKED_CORE</c>.</summary>
    public IReadOnlyList<string> Relics { get; init; } = [];

    public bool IsMultiplayer { get; init; }

    /// <summary>How many card removals were already bought at shops this run (drives removal price).</summary>
    public int CardRemovalsBought { get; init; }

    public double HpRatio => MaxHp <= 0 ? 1.0 : Math.Clamp((double)Hp / MaxHp, 0.0, 1.0);
}
