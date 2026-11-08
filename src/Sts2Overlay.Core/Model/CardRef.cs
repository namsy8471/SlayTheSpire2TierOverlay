namespace Sts2Overlay.Core.Model;

/// <summary>
/// A card identified by its game model id (e.g. <c>CARD.BALL_LIGHTNING</c>) and upgrade level.
/// </summary>
public readonly record struct CardRef(string Id, int Upgrade = 0)
{
    public bool IsUpgraded => Upgrade > 0;

    public override string ToString() => Upgrade > 0 ? $"{Id}+{Upgrade}" : Id;
}

/// <summary>One card instance in a deck.</summary>
public sealed record DeckCard(CardRef Card, int FloorAdded = 0, string? EnchantmentId = null, int EnchantmentAmount = 0)
{
    public string Id => Card.Id;
    public int Upgrade => Card.Upgrade;
}
