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
    /// <remarks>
    /// A card a monster stole and that was taken back after the fight is listed among the choices as well
    /// (and as a removal at the same node); it is not an offer, so it is left out before grouping.
    /// The context comes from the rooms: a shop room → Shop, else boss/elite/monster room → its reward,
    /// else Event (so an unknown point that turned into a fight counts as a card reward).
    /// For a node with several groups, later groups see the earlier groups' picks in their deck.
    /// </remarks>
    /// <exception cref="ArgumentException">No player with that id is in the run.</exception>
    public static IReadOnlyList<CardDecision> Extract(RunHistory run, long playerId) =>
        Extract(run, playerId, DeckTimeline.Build(run, playerId));

    /// <summary>Same as <see cref="Extract(RunHistory, long)"/> reusing an already built timeline for that player.</summary>
    public static IReadOnlyList<CardDecision> Extract(RunHistory run, long playerId, DeckTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.PlayerId != playerId)
        {
            throw new ArgumentException("The timeline belongs to another player.", nameof(timeline));
        }

        var decisions = new List<CardDecision>();
        for (var i = 0; i < run.Nodes.Count; i++)
        {
            var node = run.Nodes[i];
            var stats = node.StatsFor(playerId);
            if (stats is null || stats.CardChoices.Count == 0)
            {
                continue;
            }

            var state = timeline.StateBefore(i);
            var context = ContextOf(node);
            if (context == OfferContext.Shop)
            {
                decisions.Add(Make(node, i, 0, context, stats.CardChoices, state));
                continue;
            }

            var choices = WithoutReturnedCards(node, stats);
            if (choices.Count == 0)
            {
                continue;
            }

            var groups = choices.Count % 3 == 0 ? choices.Chunk(3).ToArray() : [choices.ToArray()];
            var deck = state.Deck.ToList();
            for (var g = 0; g < groups.Length; g++)
            {
                var decision = Make(node, i, g, context, groups[g], state with { Deck = deck.ToArray() });
                decisions.Add(decision);
                deck.AddRange(decision.Picked.Select(c => new DeckCard(c, node.Floor)));
            }
        }

        return decisions;
    }

    /// <summary>The offer context of a map point, from its rooms (falls back to the map point type).</summary>
    public static OfferContext ContextOf(RunNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        bool Has(string type) => node.Rooms.Any(r => r.RoomType == type) || (node.Rooms.Count == 0 && node.MapPointType == type);

        if (Has("shop"))
        {
            return OfferContext.Shop;
        }

        if (Has("boss"))
        {
            return OfferContext.BossReward;
        }

        if (Has("elite"))
        {
            return OfferContext.EliteReward;
        }

        return Has("monster") ? OfferContext.CardReward : OfferContext.Event;
    }

    private static CardDecision Make(RunNode node, int index, int group, OfferContext context, IReadOnlyList<CardChoice> choices, RunState state) => new()
    {
        Node = node,
        NodeIndex = index,
        GroupIndex = group,
        Context = context,
        Offered = choices.Select(c => c.Card.Card).ToArray(),
        Picked = choices.Where(c => c.WasPicked).Select(c => c.Card.Card).ToArray(),
        StateBefore = state,
    };

    /// <summary>
    /// Drops stolen-and-returned cards: a choice whose recorded floor is before the node, or, when the count
    /// is not a multiple of 3, a choice whose id was also removed at this node (the theft).
    /// </summary>
    private static List<CardChoice> WithoutReturnedCards(RunNode node, PlayerNodeStats stats)
    {
        var stolen = stats.CardsRemoved.Select(c => c.Id).ToList();
        var result = new List<CardChoice>(stats.CardChoices.Count);
        foreach (var c in stats.CardChoices)
        {
            if (c.Card.FloorAdded > 0 && c.Card.FloorAdded < node.Floor)
            {
                stolen.Remove(c.Card.Id);
                continue;
            }

            result.Add(c);
        }

        for (var i = 0; i < result.Count && result.Count % 3 != 0; i++)
        {
            if (stolen.Remove(result[i].Card.Id))
            {
                result.RemoveAt(i);
                i--;
            }
        }

        return result;
    }
}
