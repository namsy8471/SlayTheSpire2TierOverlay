using Sts2Overlay.Core.Model;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>One play seen in a combat: who played it, which card, and its combat id.</summary>
internal readonly record struct CombatPlay(ulong Owner, string CardId, int Index);

/// <summary>
/// Every card play logged during one fight (see <see cref="Log.CardPlayedEvent"/>), and whether I had visited a merchant
/// before it (my own purchases are never logged, so an unexplained card may be one).
/// </summary>
internal sealed record CombatObservation(IReadOnlyList<CombatPlay> Plays, bool AfterMerchant = false);

/// <summary>
/// A deck being rebuilt step by step from log patches. Besides the cards it keeps, per card, whether the log is
/// unsure about it, and three counters for things the log proves happened but cannot place:
/// unidentified cards (random transform results / random adds), unplaced upgrades, unplaced removals.
///
/// Every "chose cards" line names cards that are IN the deck at that moment, so an operation on a card we don't
/// have is evidence we missed it earlier: the card is added back (marked <see cref="UncertainReason.Inferred"/>)
/// and, if an unidentified card was pending, that pending card is now identified.
/// </summary>
internal sealed class DeckReplay
{
    private sealed class Slot(DeckCard card, UncertainReason? reason)
    {
        public DeckCard Card = card;
        public UncertainReason? Reason = reason;
        /// <summary>Added only because a fight showed it (not counted when sizing the next fight's deck range).</summary>
        public bool FromObservation;
        /// <summary>Fights survived in the deck (Guilty leaves after 5).</summary>
        public int Combats;
        /// <summary>
        /// Made from one of several identical copies (enchant): the log doesn't say which copy, so this card
        /// may sit where any of these peers sits. Only deck ORDER is in doubt, which matters to NextItem-in-deck-order picks.
        /// </summary>
        public List<Slot>? Peers;
    }

    private const string Guilty = "CARD.GUILTY";
    private const int GuiltyCombats = 5; // Guilty.cs maxCombats

    /// <summary>Bing Bong (relic): every card that joins the deck is copied once (BingBong.AfterCardChangedPiles).</summary>
    public bool DuplicateAdds { get; set; }

    /// <summary>Card types upgraded when they join the deck (Frozen / Molten / Toxic Egg: Power / Attack / Skill).</summary>
    public IReadOnlySet<string> UpgradeAddedTypes { get; set; } = new HashSet<string>();

    private readonly List<Slot> _slots;

    /// <summary>The run's game generation: which embedded card table answers rarity / type / upgrade questions.</summary>
    private readonly RngScheme _scheme;

    public DeckReplay(IEnumerable<DeckCard> deck, RngScheme scheme = RngSeeds.Default)
    {
        _slots = deck.Select(c => new Slot(c, null)).ToList();
        _scheme = scheme;
    }

    public int Count => _slots.Count;
    public int Unidentified { get; private set; }
    public int UnknownUpgrades { get; private set; }
    public int UnknownRemovals { get; private set; }

    public IReadOnlyList<DeckCard> Cards => _slots.Select(s => s.Card).ToList();

    public IReadOnlyList<UncertainCard> Uncertain =>
        _slots.Select((s, i) => (s, i)).Where(x => x.s.Reason is not null)
              .Select(x => new UncertainCard(x.i, x.s.Card.Id, x.s.Reason!.Value)).ToList();

    /// <param name="upgrade">Upgrade level the card arrives with (a reward card's replayed upgrade roll).</param>
    public void Add(string id, int floor, UncertainReason? reason = null, int upgrade = 0)
    {
        int max = CardRarities.MaxUpgrade(id, _scheme);
        // An egg upgrades a card of its type as it joins the deck, if it can still be upgraded.
        if (CardRarities.TypeOf(id, _scheme) is { } type && UpgradeAddedTypes.Contains(type) && upgrade < max) upgrade++;
        var card = new DeckCard(new CardRef(id, upgrade), FloorAdded: floor);
        if (upgrade >= max && reason == UncertainReason.UpgradeRoll) reason = null; // already at its upgraded form
        _slots.Add(new Slot(card, reason));
        if (DuplicateAdds) _slots.Add(new Slot(card, reason));
    }

    /// <summary>
    /// True when nothing about the deck is in doubt: every card known, no unplaced upgrade/removal, no flag. Only then can
    /// a replayed game RNG pick the same cards the game picked (the candidate list must match the game's exactly).
    /// </summary>
    public bool IsExact => Unidentified == 0 && UnknownUpgrades == 0 && UnknownRemovals == 0 && _slots.All(s => s.Reason is null);

    /// <summary>Every card that can still be upgraded gets +1; afterwards no earlier random upgrade is left unplaced.</summary>
    public void UpgradeAll()
    {
        foreach (Slot s in _slots)
            if (s.Card.Upgrade < CardRarities.MaxUpgrade(s.Card.Id, _scheme))
                s.Card = s.Card with { Card = s.Card.Card with { Upgrade = s.Card.Upgrade + 1 } };
        UnknownUpgrades = 0;
    }

    /// <summary>A fight ended: cards that count fights do so (Guilty removes itself after 5).</summary>
    public void CombatEnded()
    {
        foreach (Slot s in _slots) s.Combats++;
        _slots.RemoveAll(s => s.Card.Id == Guilty && s.Combats >= GuiltyCombats);
    }

    /// <summary>Removes the plainest copy. Returns false if the card was not known (it still left the deck).</summary>
    public bool Remove(string id)
    {
        int i = Find(id, plainest: true);
        if (i >= 0) { _slots.RemoveAt(i); return true; }
        Identify(); // we missed this card earlier; whatever it was, it's gone now
        return false;
    }

    public void Upgrade(string id, int floor)
    {
        int i = Require(id, floor, plainest: false);
        _slots[i].Card = _slots[i].Card with { Card = _slots[i].Card.Card with { Upgrade = _slots[i].Card.Upgrade + 1 } };
    }

    public void Enchant(string id, string? enchantment, int floor)
    {
        int i = Require(id, floor, plainest: true, unenchanted: true);
        List<Slot> peers = PeersOf(_slots[i]);
        if (enchantment != null) _slots[i].Card = _slots[i].Card with { EnchantmentId = enchantment, EnchantmentAmount = 1 };
        if (peers.Count > 0) _slots[i].Peers = [.. _slots[i].Peers ?? [], .. peers];
    }

    public void Duplicate(string id, int floor)
    {
        int i = Require(id, floor, plainest: false);
        _slots.Add(new Slot(_slots[i].Card, _slots[i].Reason));
    }

    /// <summary>
    /// Transform: the chosen card leaves; a fixed target joins (keeping nothing), or an unidentified card. In the deck
    /// the new card goes to the END (CardCmd.Transform: <c>pile.AddInternal(replacement)</c> without the old index —
    /// only combat piles keep the position).
    /// </summary>
    public void Transform(string id, string? target, int floor)
    {
        Remove(id);
        if (target != null) Add(target, floor);
        else Unidentified++;
    }

    /// <summary>A card-select whose meaning is unknown (unlisted event, second pick in a shop ...): keep the card, flag it.</summary>
    public void MarkUnknown(string id, int floor)
    {
        int i = Require(id, floor, plainest: false);
        _slots[i].Reason ??= UncertainReason.UnknownCardSelect;
    }

    /// <summary>
    /// <paramref name="n"/> random upgradable cards (of <paramref name="type"/>, or any) get +1, chosen by an RNG we
    /// can't replay. Still exact when the choice doesn't matter: n or fewer candidates (all of them are upgraded), or
    /// every candidate is the same card. Otherwise counted as unplaced upgrades.
    /// </summary>
    public void RandomUpgrades(int n, string? type = null)
    {
        if (IsExact)
        {
            List<Slot> c = Candidates(type);
            if (c.Count <= n) { foreach (Slot s in c) UpgradeSlot(s); return; }
            if (c.Select(s => (s.Card.Id, s.Card.Upgrade, s.Card.EnchantmentId)).Distinct().Count() == 1)
            {
                foreach (Slot s in c.Take(n)) UpgradeSlot(s);
                return;
            }
        }
        UnknownUpgrades += n;
    }

    /// <summary>
    /// <c>cards.Where(IsUpgradable).ToList().StableShuffle(rng).Take(n)</c> replayed: sort by (id, upgrade) — the
    /// card comparer — then Fisher-Yates with the given stream. Needs an exact deck; otherwise counted as unplaced.
    /// </summary>
    public void ShuffleUpgrades(GameRng rng, int n, string? type = null)
    {
        List<Slot> c = Candidates(type);
        // Equal cards (same id + upgrade) are interchangeable, unless they differ by enchantment: then which one gets
        // the upgrade depends on sort details we don't model.
        bool mixedTies = c.GroupBy(s => (s.Card.Id, s.Card.Upgrade)).Any(g => g.Select(s => s.Card.EnchantmentId).Distinct().Count() > 1);
        if (!IsExact || mixedTies) { RandomUpgrades(n, type); return; }
        List<Slot> sorted = c.OrderBy(s => s.Card.Id, StringComparer.Ordinal).ThenBy(s => s.Card.Upgrade).ToList();
        rng.Shuffle(sorted);
        foreach (Slot s in sorted.Take(n)) UpgradeSlot(s);
    }

    /// <summary>
    /// <c>rng.NextItem(upgradable cards in deck order)</c> gets +1 (Tablet of Truth). Needs an exact deck in game order.
    /// Where a card's position is in doubt (<see cref="Slot.Peers"/>), every possible order must land on the same card.
    /// </summary>
    public void PickUpgrade(GameRng rng)
    {
        if (!IsExact || _slots.Any(s => s.FromObservation)) { RandomUpgrades(1); return; }
        int n = Candidates(null).Count;
        if (n == 0) return;
        int k = rng.NextInt(n);
        Slot? pick = null;
        foreach (List<Slot>? order in Orders())
        {
            if (order is null) { RandomUpgrades(1); return; } // too many possible orders to check
            Slot s = order.Where(Upgradable).ElementAt(k);
            if (pick is null) pick = s;
            else if (Identity(s) != Identity(pick)) { RandomUpgrades(1); return; } // the order decides: unknown
        }
        UpgradeSlot(pick!);
    }

    private const int MaxOrders = 4096;

    /// <summary>
    /// The deck orders the log allows: ours first, then each card of doubtful position swapped with each of its peers
    /// (all combinations). Yields a single null if there are more than <see cref="MaxOrders"/>.
    /// </summary>
    private IEnumerable<List<Slot>?> Orders()
    {
        var orders = new List<List<Slot>> { _slots };
        foreach (Slot f in _slots.Where(s => s.Peers is { Count: > 0 }).ToList())
        {
            var next = new List<List<Slot>>();
            foreach (List<Slot> o in orders)
            {
                next.Add(o);
                foreach (Slot p in f.Peers!.Where(o.Contains))
                {
                    var swapped = new List<Slot>(o);
                    int a = swapped.IndexOf(f), b = swapped.IndexOf(p);
                    (swapped[a], swapped[b]) = (swapped[b], swapped[a]);
                    next.Add(swapped);
                }
            }
            if (next.Count > MaxOrders) return [null];
            orders = next;
        }
        return orders;
    }

    private bool Upgradable(Slot s) => s.Card.Upgrade < CardRarities.MaxUpgrade(s.Card.Id, _scheme);
    private static (string, int, string?) Identity(Slot s) => (s.Card.Id, s.Card.Upgrade, s.Card.EnchantmentId);
    private List<Slot> PeersOf(Slot s) => _slots.Where(o => o != s && Identity(o) == Identity(s)).ToList();

    private List<Slot> Candidates(string? type) =>
        _slots.Where(s => Upgradable(s) && (type is null || CardRarities.TypeOf(s.Card.Id, _scheme) == type)).ToList();

    private static void UpgradeSlot(Slot s) =>
        s.Card = s.Card with { Card = s.Card.Card with { Upgrade = s.Card.Upgrade + 1 } };
    public void UnknownAdds(int n) => Unidentified += n;
    public void RandomRemoves(int n) => UnknownRemovals += n;

    /// <summary>Neow's Talisman: the last basic Strike and last basic Defend get +1.</summary>
    public void UpgradeBasics()
    {
        foreach (Func<string, bool> isBasic in new Func<string, bool>[] { DeckEffects.IsBasicStrike, DeckEffects.IsBasicDefend })
        {
            int i = _slots.FindLastIndex(s => isBasic(s.Card.Id));
            if (i >= 0) _slots[i].Card = _slots[i].Card with { Card = _slots[i].Card.Card with { Upgrade = _slots[i].Card.Upgrade + 1 } };
        }
    }

    /// <summary>Pandora's Box: every basic Strike/Defend becomes a random card.</summary>
    public void TransformAllBasics()
    {
        int n = _slots.RemoveAll(s => DeckEffects.IsBasicStrike(s.Card.Id) || DeckEffects.IsBasicDefend(s.Card.Id));
        Unidentified += n;
    }

    /// <summary>Leafy Poultice: the first basic Strike and first basic Defend become random cards.</summary>
    public void TransformStrikeAndDefend()
    {
        foreach (Func<string, bool> isBasic in new Func<string, bool>[] { DeckEffects.IsBasicStrike, DeckEffects.IsBasicDefend })
        {
            int i = _slots.FindIndex(s => isBasic(s.Card.Id));
            if (i < 0) continue;
            _slots.RemoveAt(i);
            Unidentified++;
        }
    }

    /// <summary>Archaic Tooth: the starter card becomes its stronger form, keeping its upgrade.</summary>
    public void Transcend()
    {
        int i = _slots.FindIndex(s => DeckEffects.Transcendence.ContainsKey(s.Card.Id));
        if (i >= 0)
            _slots[i].Card = _slots[i].Card with { Card = _slots[i].Card.Card with { Id = DeckEffects.Transcendence[_slots[i].Card.Id] } };
    }

    /// <summary>Rest-site CLONE: every card with the Clone enchantment (Pael's Growth) is copied, upgrade and enchantment
    /// included (the copies can be cloned again at the next rest site).</summary>
    public void CloneEnchanted()
    {
        foreach (Slot s in _slots.Where(s => s.Card.EnchantmentId == "ENCHANTMENT.CLONE").ToList())
            _slots.Add(new Slot(s.Card, s.Reason));
    }

    /// <summary>
    /// Every deck card <paramref name="accepts"/> takes that has no enchantment yet gets <paramref name="enchantment"/>
    /// (EnchantmentModel.CanEnchant: no other enchantment, not Unplayable; Pael's Claw's Goopy needs the Defend tag).
    /// </summary>
    public void EnchantAll(Func<string, bool> accepts, string enchantment)
    {
        foreach (Slot s in _slots)
            if (s.Card.EnchantmentId == null && accepts(s.Card.Id))
                s.Card = s.Card with { EnchantmentId = enchantment, EnchantmentAmount = 1 };
    }

    /// <summary>
    /// Uses one fight's card plays as a partial deck listing. At combat start the game numbers every player's deck in
    /// player order (NetCombatCardDb.StartCombat): player 0 gets 0..n0-1, player 1 gets n0..n0+n1-1, and cards created
    /// during the fight get ids after ALL decks. So a play of mine whose id is below the next player's lowest id (or,
    /// for a later seat, below a bound derived from the earlier seats) is a card of my deck. If the fight shows more
    /// copies of a card than we have, the missing copies are added (<see cref="UncertainReason.Inferred"/>: identity
    /// certain, upgrade level unknown); copies that were flagged by an unknown card-select are confirmed.
    /// </summary>
    public void Observe(CombatObservation obs, IReadOnlyList<ulong> order, ulong me, int floor)
    {
        int seat = order.ToList().IndexOf(me);
        if (seat < 0) return;
        var seatOf = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var mine = obs.Plays.Where(p => p.Owner == me).GroupBy(p => p.Index).Select(g => g.First()).ToList();
        // A token / status played by anyone was made during the fight, so every deck id is below it.
        var made = obs.Plays.Where(p => CardRarities.IsCombatOnly(p.CardId, _scheme)).Select(p => p.Index).ToList();
        mine = mine.Where(p => !CardRarities.IsCombatOnly(p.CardId, _scheme)).ToList();
        if (mine.Count == 0) return;
        int myMin = mine.Min(p => p.Index);
        // Size used for bounds: cards added only from earlier fights don't widen the range (no runaway on one mistake),
        // but cards we know exist without knowing what they are (random transform results) do take ids.
        int size = _slots.Count(s => !s.FromObservation) + Unidentified;
        var earlier = obs.Plays.Where(p => seatOf.TryGetValue(p.Owner, out int s) && s < seat).Select(p => p.Index).ToList();
        var later = obs.Plays.Where(p => seatOf.TryGetValue(p.Owner, out int s) && s > seat).Select(p => p.Index).ToList();

        // Upper bound (exclusive) of my deck ids, and the lowest id my deck can start at.
        // Bounds seen in this fight (independent of what we think our deck size is) ...
        var observed = new List<int>(made);
        observed.AddRange(later);                            // later seats' decks start after mine
        observed.AddRange(earlier.Where(i => i > myMin));    // an earlier seat's id above mine = a card made in the fight
        // ... plus one derived from our own deck size.
        var bounds = new List<int>(observed);
        int startLow = 0;
        if (seat == 0)
        {
            if (later.Count == 0) bounds.Add(size);          // nobody else played: only trust what fits our size
        }
        else
        {
            var below = earlier.Where(i => i < myMin).ToList();
            if (below.Count == 0 && bounds.Count == 0) return; // no way to tell my deck from created cards
            if (below.Count > 0)
            {
                startLow = below.Max() + 1;
                bounds.Add(startLow + size);                 // my deck starts after the earlier seats' cards
            }
            else startLow = -1;                              // start unknown
        }
        int upper = bounds.Min();
        var deckPlays = mine.Where(p => p.Index < upper).ToList();
        if (deckPlays.Count == 0) return;

        // Exact deck size: when my lowest play sits right after the previous seat's highest (or I'm seat 0) and my
        // highest deck play sits right before a bound seen in this fight, my deck is exactly [start, end). A size above
        // what we track means cards we never saw join the deck (e.g. a local shop purchase, which the game doesn't log).
        int? exact = null;
        int start = seat == 0 ? 0 : startLow;
        int end = deckPlays.Max(p => p.Index) + 1;
        if (start >= 0 && (seat == 0 || myMin == start) && observed.Count > 0 && end == observed.Min())
            exact = end - start;
        int missing = exact is { } n ? n - (_slots.Count + Unidentified) : 0;

        foreach (var g in deckPlays.GroupBy(p => p.CardId))
        {
            int seen = g.Count();
            var have = _slots.Where(s => s.Card.Id == g.Key).ToList();
            foreach (Slot s in have.Take(seen))
                if (s.Reason == UncertainReason.UnknownCardSelect) s.Reason = null;
            for (int k = have.Count; k < seen; k++)
            {
                // Either a card we missed entirely (local shop purchase ...) or a pending unidentified card (random
                // transform result ...) that is now known. An exact deck size tells which; otherwise prefer the latter.
                bool counted = missing > 0;
                if (counted) missing--;
                bool identifies = !counted && Unidentified > 0;
                if (!counted) Identify();
                // Every unlogged way a card joins the deck that we know of — my own shop purchase (the merchant's upgrade
                // roll has a -999999999 base: never), a random transform's result — makes a plain card, which only an
                // egg upgrades (Hook.ModifyCardBeingAddedToDeck). So once I've been to a merchant, a card a merchant
                // sells is taken as plain; anything else keeps its upgrade in doubt.
                UncertainReason? reason = obs.AfterMerchant && CardRarities.Of(g.Key, _scheme) is "Common" or "Uncommon" or "Rare"
                    ? null : UncertainReason.Inferred;
                Add(g.Key, floor, reason);
                if (DuplicateAdds) _slots.RemoveAt(_slots.Count - 1); // a copy Bing Bong made would show up on its own
                _slots[^1].FromObservation = !counted && !identifies;
            }
        }
        if (missing > 0) Unidentified += missing; // cards that exist but never showed themselves
    }

    /// <summary>
    /// Thieving Hopper stole one of my cards and it never came back (hopper escaped, or the "special card reward" was
    /// skipped). The log never names the stolen card, so it is worked out from the steal rule (ThievingHopper.ThieveryMove):
    /// the hopper acts after my turn, when my hand is already discarded, so it picks from (almost) my whole deck minus the
    /// cards that left the fight (powers / Exhaust played before the steal), taking the best class first
    /// (<see cref="CardRarities.StealPriority"/>). A copy played AFTER the steal was still in the fight, so it wasn't taken.
    /// If exactly one card id remains possible, that card is removed; otherwise an unplaced removal is counted.
    /// </summary>
    public void Stolen(IReadOnlyList<CombatPlay> mineBefore, IReadOnlyList<CombatPlay> mineAfter)
    {
        static Dictionary<string, int> Copies(IEnumerable<CombatPlay> plays) => plays
            .GroupBy(p => p.Index).Select(g => g.First().CardId)
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

        Dictionary<string, int> gone = Copies(mineBefore.Where(p => CardRarities.LeavesCombatWhenPlayed(p.CardId, _scheme)));
        Dictionary<string, int> stayed = Copies(mineAfter);
        var pool = new List<Slot>();
        foreach (var g in _slots.GroupBy(s => s.Card.Id))
            pool.AddRange(g.OrderBy(s => s.Card.Upgrade).Skip(gone.GetValueOrDefault(g.Key)));

        var classOf = pool.Select(s => (s, p: CardRarities.StealPriority(s.Card.Id, s.Card.EnchantmentId, _scheme)))
                          .Where(x => x.p is not null).ToList();
        if (classOf.Count == 0) { UnknownRemovals++; return; }
        int best = classOf.Min(x => x.p!.Value);
        var candidates = classOf.Where(x => x.p == best).GroupBy(x => x.s.Card.Id)
                                .Where(g => g.Count() > stayed.GetValueOrDefault(g.Key)).Select(g => g.Key).ToList();
        if (candidates.Count == 1) Remove(candidates[0]);
        else UnknownRemovals++;
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>A pending unidentified card turned out to be one we now know.</summary>
    private void Identify()
    {
        if (Unidentified > 0) Unidentified--;
    }

    /// <summary>Index of a copy of <paramref name="id"/>; if none, the card must exist (it was chosen from the deck): add it.</summary>
    private int Require(string id, int floor, bool plainest, bool unenchanted = false)
    {
        int i = Find(id, plainest, unenchanted);
        if (i >= 0) return i;
        Identify();
        Add(id, floor, UncertainReason.Inferred);
        return _slots.Count - 1;
    }

    /// <summary>
    /// A copy of <paramref name="id"/> to act on, -1 if none. Between otherwise equal copies the least doubtful wins
    /// (<see cref="Doubt"/>): a card a log line names surely exists, so a doubtful copy keeps its doubt (removing the
    /// flagged Strike would leave the other Strikes looking certain), and an unknown select flags a copy not flagged yet
    /// (each card one select names is a separate copy).
    /// </summary>
    private int Find(string id, bool plainest, bool unenchanted = false)
    {
        int best = -1;
        for (int i = 0; i < _slots.Count; i++)
        {
            DeckCard c = _slots[i].Card;
            if (c.Id != id) continue;
            if (best < 0) { best = i; continue; }
            DeckCard b = _slots[best].Card;
            if (unenchanted && (b.EnchantmentId != null) != (c.EnchantmentId != null)) { if (c.EnchantmentId == null) best = i; continue; }
            // plainest: lowest upgrade (removal keeps the good copy); otherwise also lowest upgrade (smith upgrades a plain copy).
            if (c.Upgrade < b.Upgrade || (plainest && c.Upgrade == b.Upgrade && c.EnchantmentId == null && b.EnchantmentId != null)) best = i;
            else if (c.Upgrade == b.Upgrade && (c.EnchantmentId == null) == (b.EnchantmentId == null)
                     && Doubt(_slots[i]) < Doubt(_slots[best])) best = i;
        }
        return best;
    }

    /// <summary>Tie-break between equal copies, lower first: certain, then upgrade in doubt, then maybe gone.</summary>
    private static int Doubt(Slot s) => s.Reason is null ? 0 : s.Reason == UncertainReason.UnknownCardSelect ? 2 : 1;
}
