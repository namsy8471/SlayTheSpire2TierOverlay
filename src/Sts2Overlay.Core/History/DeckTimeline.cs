using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.History;

/// <summary>
/// Reconstructs one player's deck at every map point by undoing each node's recorded changes
/// (gains, removals, transforms, upgrades) backwards from the final deck.
/// Changes the run file does not record (some relic/event effects) show up as <see cref="Warnings"/>.
/// </summary>
/// <remarks>
/// Per node the changes are undone in this order: enchantments, upgrades, transforms, gains, removals
/// (the reverse of the order they usually happen in). A card stolen in combat and taken back is recorded
/// as a removal plus a gain at the same node, so it cancels out naturally.
/// </remarks>
public sealed class DeckTimeline
{
    private DeckTimeline() { }

    private RunHistory _run = null!;
    private DeckCard[][] _before = [];
    private DeckCard[][] _after = [];
    private DeckCard[] _final = [];
    private string[] _warnings = [];
    private HeldRelic[] _relics = [];

    public long PlayerId { get; private init; }
    public string Character { get; private init; } = "";

    /// <summary>Deck before the first map point.</summary>
    public IReadOnlyList<DeckCard> StartingDeck => _before.Length > 0 ? _before[0] : _final;

    /// <summary>The player's final deck as recorded in the run file.</summary>
    public IReadOnlyList<DeckCard> FinalDeck => _final;

    /// <summary>Human-readable notes about undo steps that could not be matched.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Number of individual undo steps attempted (each recorded gain, removal, transform, upgrade, enchantment).</summary>
    public int UndoOperationCount { get; private init; }

    /// <summary>Builds the timeline for <paramref name="playerId"/>.</summary>
    /// <exception cref="ArgumentException">No player with that id is in the run.</exception>
    public static DeckTimeline Build(RunHistory run, long playerId)
    {
        ArgumentNullException.ThrowIfNull(run);
        var player = run.FindPlayer(playerId)
            ?? throw new ArgumentException($"Player {playerId} is not part of this run.", nameof(playerId));

        var n = run.Nodes.Count;
        var before = new DeckCard[n][];
        var after = new DeckCard[n][];
        var warnings = new List<string>();
        var ops = 0;
        var deck = new List<DeckCard>(player.Deck);
        var gainedUpgrades = GainedUpgrades(run, playerId);

        for (var i = n - 1; i >= 0; i--)
        {
            after[i] = deck.ToArray();
            var node = run.Nodes[i];
            var stats = node.StatsFor(playerId);
            if (stats is not null)
            {
                ops += UndoNode(deck, node.Floor, stats, gainedUpgrades, warnings);
            }

            before[i] = deck.ToArray();
        }

        // Warnings were collected walking backwards; present them in floor order.
        warnings.Reverse();

        return new DeckTimeline
        {
            _run = run,
            _before = before,
            _after = after,
            _final = player.Deck.ToArray(),
            _warnings = warnings.ToArray(),
            _relics = BuildRelics(run, player),
            PlayerId = playerId,
            Character = player.Character,
            UndoOperationCount = ops,
        };
    }

    /// <summary>
    /// Deck right before the node at <paramref name="nodeIndex"/> (0-based index into <see cref="RunHistory.Nodes"/>).
    /// <paramref name="nodeIndex"/> may equal the node count, which returns the final deck.
    /// </summary>
    public IReadOnlyList<DeckCard> DeckBefore(int nodeIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nodeIndex, _before.Length);
        return nodeIndex == _before.Length ? _final : _before[nodeIndex];
    }

    /// <summary>Deck right after the node at <paramref name="nodeIndex"/>.</summary>
    public IReadOnlyList<DeckCard> DeckAfter(int nodeIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(nodeIndex, _after.Length);
        return _after[nodeIndex];
    }

    /// <summary>
    /// The engine input right before the node: deck, act, floor, HP/gold after the previous node,
    /// relics with <c>floor_added_to_deck</c> before this floor, removals bought so far.
    /// </summary>
    public RunState StateBefore(int nodeIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(nodeIndex, _before.Length);

        var node = _run.Nodes[nodeIndex];
        var floor = node.Floor;
        var (hp, maxHp, gold) = VitalsBefore(nodeIndex);

        var removals = 0;
        for (var j = 0; j < nodeIndex; j++)
        {
            var prev = _run.Nodes[j];
            if (prev.HasShop)
            {
                removals += prev.StatsFor(PlayerId)?.CardsRemoved.Count ?? 0;
            }
        }

        return new RunState
        {
            Character = Character,
            Ascension = _run.Ascension,
            Act = node.Act,
            Floor = floor,
            Hp = hp,
            MaxHp = maxHp,
            Gold = gold,
            Deck = _before[nodeIndex],
            Relics = _relics.Where(r => r.HeldBefore(floor)).Select(r => r.Id).ToArray(),
            IsMultiplayer = _run.IsMultiplayer,
            CardRemovalsBought = removals,
        };
    }

    private (int Hp, int MaxHp, int Gold) VitalsBefore(int nodeIndex)
    {
        for (var j = nodeIndex - 1; j >= 0; j--)
        {
            var s = _run.Nodes[j].StatsFor(PlayerId);
            if (s is not null)
            {
                return (s.CurrentHp, s.MaxHp, s.CurrentGold);
            }
        }

        // First recorded node: reverse its own deltas.
        var first = _run.Nodes[nodeIndex].StatsFor(PlayerId);
        if (first is null)
        {
            return (0, 0, 0);
        }

        var maxHp = Math.Max(1, first.MaxHp - first.MaxHpGained + first.MaxHpLost);
        var hp = first.CurrentHp + first.DamageTaken - first.HpHealed;
        if (hp <= 0)
        {
            // The run start is logged as a heal from 0 to the starting HP.
            hp = first.CurrentHp + first.DamageTaken;
        }

        hp = Math.Clamp(hp, 1, maxHp);
        var gold = Math.Max(0, first.CurrentGold - first.GoldGained + first.GoldSpent + first.GoldLost + first.GoldStolen);
        return (hp, maxHp, gold);
    }

    private static string Describe(DeckCard c) => c.EnchantmentId is null ? c.Card.ToString() : $"{c.Card} [{c.EnchantmentId}]";

    /// <summary>Upgrade level each card had when it entered the deck, keyed by id and floor (from gain/transform records).</summary>
    private static Dictionary<(string Id, int Floor), int> GainedUpgrades(RunHistory run, long playerId)
    {
        var map = new Dictionary<(string, int), int>();
        foreach (var node in run.Nodes)
        {
            var stats = node.StatsFor(playerId);
            if (stats is null)
            {
                continue;
            }

            foreach (var c in stats.CardsGained.Concat(stats.CardsTransformed.Select(t => t.Final)))
            {
                var key = (c.Id, node.Floor);
                map[key] = map.TryGetValue(key, out var known) ? Math.Min(known, c.Upgrade) : c.Upgrade;
            }
        }

        return map;
    }

    private static int UndoNode(List<DeckCard> deck, int floor, PlayerNodeStats stats, Dictionary<(string Id, int Floor), int> gainedUpgrades, List<string> warnings)
    {
        var ops = 0;

        // Collected per node then reversed so the final list (also reversed) reads in record order.
        var local = new List<string>();

        foreach (var e in stats.CardsEnchanted.Reverse())
        {
            ops++;
            var idx = BestMatch(deck, e.Id, c =>
                c.EnchantmentId is null ? -1 :
                (c.EnchantmentId == e.EnchantmentId ? 4 : 0) + (e.FloorAdded > 0 && c.FloorAdded == e.FloorAdded ? 2 : 0) + (c.Upgrade == e.Upgrade ? 1 : 0));
            if (idx < 0)
            {
                local.Add($"floor {floor}: enchanted {e.Id} ({e.EnchantmentId}) not found in deck");
                continue;
            }

            deck[idx] = deck[idx] with { EnchantmentId = null, EnchantmentAmount = 0 };
        }

        foreach (var id in stats.UpgradedCards.Reverse())
        {
            ops++;
            // Prefer a copy that is above the level it was obtained at and that already existed before this node.
            var idx = BestMatch(deck, id, c =>
                c.Upgrade <= 0 ? -1 :
                (c.Upgrade > gainedUpgrades.GetValueOrDefault((c.Id, c.FloorAdded)) ? 4 : 0) + (c.FloorAdded < floor ? 2 : 0));
            if (idx < 0)
            {
                local.Add($"floor {floor}: upgraded {id} has no upgraded copy in deck");
                continue;
            }

            deck[idx] = deck[idx] with { Card = deck[idx].Card with { Upgrade = deck[idx].Upgrade - 1 } };
        }

        foreach (var t in stats.CardsTransformed.Reverse())
        {
            ops++;
            var final = t.Final;
            var idx = BestMatch(deck, final.Id, c => Similarity(c, final, floor));
            if (idx < 0)
            {
                local.Add($"floor {floor}: transformed {t.Original.Id} -> {final.Id} but {final.Id} not found in deck");
            }
            else
            {
                if (deck[idx].Upgrade != final.Upgrade || deck[idx].EnchantmentId != final.EnchantmentId)
                {
                    local.Add($"floor {floor}: transformed into {Describe(final)} but closest copy in deck is {Describe(deck[idx])}");
                }

                deck.RemoveAt(idx);
            }

            Insert(deck, t.Original);
        }

        foreach (var g in stats.CardsGained.Reverse())
        {
            ops++;
            var idx = BestMatch(deck, g.Id, c => Similarity(c, g, floor));
            if (idx < 0)
            {
                local.Add($"floor {floor}: gained {g.Id} not found in deck");
                continue;
            }

            if (deck[idx].FloorAdded != floor || deck[idx].Upgrade != g.Upgrade || deck[idx].EnchantmentId != g.EnchantmentId)
            {
                local.Add($"floor {floor}: gained {Describe(g)} but closest copy in deck is {Describe(deck[idx])} (floor {deck[idx].FloorAdded})");
            }

            deck.RemoveAt(idx);
        }

        foreach (var r in stats.CardsRemoved)
        {
            ops++;
            Insert(deck, r);
        }

        local.Reverse();
        warnings.AddRange(local);
        return ops;
    }

    /// <summary>
    /// How well a deck card matches a recorded card at a node: the node floor first (records often omit
    /// or misreport <c>floor_added_to_deck</c>), then the recorded floor, upgrade and enchantment.
    /// </summary>
    private static int Similarity(DeckCard deckCard, DeckCard record, int nodeFloor) =>
        (deckCard.FloorAdded == nodeFloor ? 8 : 0)
        + (record.FloorAdded > 0 && deckCard.FloorAdded == record.FloorAdded ? 4 : 0)
        + (deckCard.Upgrade == record.Upgrade ? 2 : 0)
        + (deckCard.EnchantmentId == record.EnchantmentId ? 1 : 0);

    /// <summary>Index of the highest scoring card with <paramref name="id"/> (negative scores excluded; ties → latest), or -1.</summary>
    private static int BestMatch(List<DeckCard> deck, string id, Func<DeckCard, int> score)
    {
        var best = -1;
        var bestScore = int.MinValue;
        for (var i = 0; i < deck.Count; i++)
        {
            if (!string.Equals(deck[i].Id, id, StringComparison.Ordinal))
            {
                continue;
            }

            var s = score(deck[i]);
            if (s >= 0 && s >= bestScore)
            {
                best = i;
                bestScore = s;
            }
        }

        return best;
    }

    /// <summary>Inserts keeping the deck roughly ordered by floor added.</summary>
    private static void Insert(List<DeckCard> deck, DeckCard card)
    {
        var at = deck.Count;
        while (at > 0 && deck[at - 1].FloorAdded > card.FloorAdded)
        {
            at--;
        }

        deck.Insert(at, card);
    }

    private static HeldRelic[] BuildRelics(RunHistory run, RunPlayer player)
    {
        var nodes = run.Nodes;
        var pickedAtStart = new HashSet<string>(StringComparer.Ordinal);
        if (nodes.Count > 0 && nodes[0].StatsFor(player.Id) is { } s0)
        {
            pickedAtStart.UnionWith(PickedRelics(s0));
        }

        var held = player.Relics
            .Select(r => new HeldRelic(r.Id, r.FloorAdded, null, pickedAtStart.Contains(r.Id)))
            .ToList();

        // Relics lost during the run are absent from the final list; add them back with a best-effort acquisition floor.
        for (var i = 0; i < nodes.Count; i++)
        {
            var stats = nodes[i].StatsFor(player.Id);
            if (stats is null)
            {
                continue;
            }

            foreach (var id in stats.RelicsRemoved)
            {
                var acquired = 1;
                var atStart = false;
                for (var k = i - 1; k >= 0; k--)
                {
                    if (nodes[k].StatsFor(player.Id) is { } sk && PickedRelics(sk).Contains(id))
                    {
                        acquired = nodes[k].Floor;
                        atStart = k == 0;
                        break;
                    }
                }

                held.Add(new HeldRelic(id, acquired, nodes[i].Floor, atStart));
            }
        }

        return held.ToArray();
    }

    private static IEnumerable<string> PickedRelics(PlayerNodeStats s) =>
        s.RelicChoices.Where(c => c.WasPicked).Select(c => c.Id).Concat(s.BoughtRelics);

    /// <param name="Id">Relic id.</param>
    /// <param name="FloorAdded">Floor it was obtained on.</param>
    /// <param name="LostAtFloor">Floor of the node that removed it, if it was lost.</param>
    /// <param name="PickedAtFirstNode">Obtained at the first map point (e.g. Neow), so not held before it.</param>
    private sealed record HeldRelic(string Id, int FloorAdded, int? LostAtFloor, bool PickedAtFirstNode)
    {
        public bool HeldBefore(int floor)
        {
            if (LostAtFloor is { } lost && floor > lost)
            {
                return false;
            }

            if (floor <= 1)
            {
                return FloorAdded <= 1 && !PickedAtFirstNode;
            }

            return FloorAdded < floor;
        }
    }
}
