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

    /// <summary>Act number, 1-based (1..3).</summary>
    public int Act { get; init; } = 1;

    /// <summary>Global floor number, 1-based, counted across acts like <c>floor_added_to_deck</c>.</summary>
    public int Floor { get; init; } = 1;

    public int Hp { get; init; }
    public int MaxHp { get; init; }
    public int Gold { get; init; }

    public IReadOnlyList<DeckCard> Deck { get; init; } = [];

    /// <summary>Relic model ids, e.g. <c>RELIC.CRACKED_CORE</c>.</summary>
    public IReadOnlyList<string> Relics { get; init; } = [];

    public bool IsMultiplayer { get; init; }

    /// <summary>How many card removals were already bought at shops this run (drives removal price).</summary>
    public int CardRemovalsBought { get; init; }

    public double HpRatio => MaxHp <= 0 ? 1.0 : Math.Clamp((double)Hp / MaxHp, 0.0, 1.0);
}
