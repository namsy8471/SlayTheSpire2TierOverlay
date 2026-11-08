using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.History;

/// <summary>One recorded card decision: what was offered, what was picked, and the state before it.</summary>
public sealed record CardDecision
{
    public required RunNode Node { get; init; }

    /// <summary>0-based index of the node in <see cref="RunHistory.Nodes"/>.</summary>
    public int NodeIndex { get; init; }

    /// <summary>0-based index of this group within the node (a node can hold several reward groups).</summary>
    public int GroupIndex { get; init; }

    public OfferContext Context { get; init; }
    public required IReadOnlyList<CardRef> Offered { get; init; }

    /// <summary>All picked cards (shops can buy several; rewards pick 0 or 1).</summary>
    public required IReadOnlyList<CardRef> Picked { get; init; }

    public bool Skipped => Picked.Count == 0;
    public required RunState StateBefore { get; init; }
}

/// <summary>Turns a run history into a list of card decisions for one player.</summary>
public static class DecisionExtractor
{
    /// <summary>
    /// Shop points → one <see cref="OfferContext.Shop"/> decision with every card offered.
    /// Other points: <c>card_choices</c> split into groups of 3 when the count is a multiple of 3
    /// (e.g. 6 = two reward screens), else one group. Context from map point type
    /// (monster → CardReward, elite → EliteReward, boss → BossReward, ancient/unknown event → Event).
    /// </summary>
    public static IReadOnlyList<CardDecision> Extract(RunHistory run, long playerId) => throw new NotImplementedException();
}
