using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.History;

/// <summary>
/// Reconstructs one player's deck at every map point by undoing each node's recorded changes
/// (gains, removals, transforms, upgrades) backwards from the final deck.
/// Changes the run file does not record (some relic/event effects) show up as <see cref="Warnings"/>.
/// </summary>
public sealed class DeckTimeline
{
    private DeckTimeline() { }

    public long PlayerId { get; private init; }
    public string Character { get; private init; } = "";

    /// <summary>Deck before the first map point.</summary>
    public IReadOnlyList<DeckCard> StartingDeck => throw new NotImplementedException();

    /// <summary>Human-readable notes about undo steps that could not be matched.</summary>
    public IReadOnlyList<string> Warnings => throw new NotImplementedException();

    public static DeckTimeline Build(RunHistory run, long playerId) => throw new NotImplementedException();

    /// <summary>Deck right before the node at <paramref name="nodeIndex"/> (0-based index into <see cref="RunHistory.Nodes"/>).</summary>
    public IReadOnlyList<DeckCard> DeckBefore(int nodeIndex) => throw new NotImplementedException();

    /// <summary>Deck right after the node at <paramref name="nodeIndex"/>.</summary>
    public IReadOnlyList<DeckCard> DeckAfter(int nodeIndex) => throw new NotImplementedException();

    /// <summary>
    /// The engine input right before the node: deck, act, floor, HP/gold after the previous node,
    /// relics with <c>floor_added_to_deck</c> before this floor, removals bought so far.
    /// </summary>
    public RunState StateBefore(int nodeIndex) => throw new NotImplementedException();
}
