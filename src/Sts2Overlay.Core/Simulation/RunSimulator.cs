using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Forecast;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.Core.Simulation;

/// <summary>Settings of a simulated run. Game numbers come from the code-confirmed odds (<see cref="ForecastOdds"/>, <see cref="Weights.Shop"/>).</summary>
public sealed record SimOptions
{
    /// <summary>Ascension (7+ halves rare odds and the pity step, like the game). 10 matches the top-player reference.</summary>
    public int Ascension { get; init; } = 10;

    /// <summary>Co-op card pool (co-op-only cards in, single-player-only cards out).</summary>
    public bool Multiplayer { get; init; }

    public int StartGold { get; init; } = 99;

    /// <summary>[추정] Gold after a boss fight (not measured; StS2 bosses drop roughly this much).</summary>
    public int BossGold { get; init; } = 75;

    /// <summary>Chance that a "?" room is a fight / a shop; the rest are events (same defaults as the forecaster).</summary>
    public double UnknownFightChance { get; init; } = ForecastOptions.Default.UnknownFightChance;

    public double UnknownShopChance { get; init; } = ForecastOptions.Default.UnknownShopChance;

    /// <summary>Chance that an event adds one random character card (events, transforms) without a choice.</summary>
    public double EventCardChance { get; init; } = ForecastOptions.Default.EventCardChance;

    /// <summary>Visit shops (buy cards and removals the policy chooses). Off → card rewards only.</summary>
    public bool Shops { get; init; } = true;

    /// <summary>Keep every decision's state in the result (for diagnosis). Off saves memory on large batches.</summary>
    public bool KeepDecisions { get; init; } = true;

    public static SimOptions Default { get; } = new();
}

/// <summary>One choice the simulated player made.</summary>
public sealed record SimDecision
{
    public required int Act { get; init; }
    public required int Floor { get; init; }
    public required OfferContext Context { get; init; }
    public required IReadOnlyList<CardRef> Offered { get; init; }

    /// <summary>Cards taken (reward: 0 or 1; shop: any number).</summary>
    public required IReadOnlyList<CardRef> Picked { get; init; }

    /// <summary>The run state the policy saw.</summary>
    public required RunState StateBefore { get; init; }
}

/// <summary>A finished simulated run: the deck at the end of each act and the decisions on the way.</summary>
public sealed record SimRun
{
    public required string Character { get; init; }
    public required int Seed { get; init; }

    /// <summary>Index 0..2 = deck after act 1..3's boss reward.</summary>
    public required IReadOnlyList<IReadOnlyList<DeckCard>> ActEndDecks { get; init; }

    public required IReadOnlyList<string> Relics { get; init; }
    public required IReadOnlyList<SimDecision> Decisions { get; init; }
    public int RewardScreens { get; init; }
    public int RewardSkips { get; init; }
    public int CardsBought { get; init; }
    public int Removals { get; init; }
}

/// <summary>What a policy buys in a shop.</summary>
public sealed record ShopChoice(IReadOnlyList<CardRef> Cards, bool Remove);

/// <summary>The simulated player's decision rule.</summary>
public interface ISimPolicy
{
    string Name { get; }

    /// <summary>The card to take from a reward screen, or null to skip.</summary>
    CardRef? PickReward(RunState state, IReadOnlyList<CardRef> offer, OfferContext context);

    /// <summary>Cards to buy and whether to buy the card removal.</summary>
    ShopChoice Shop(RunState state, ShopOffer offer);
}

/// <summary>
/// Plays the card side of a whole run: the typical act layout (<see cref="RewardScheduler.TypicalActs"/>, from the user's runs),
/// card rewards rolled with the game's rarity odds and rare pity offset, reward upgrades by act, shops (2 attacks, 2 skills,
/// 1 power, 2 colorless, one card on sale, card removal), events that add a random card, gold from the path weights.
/// The policy picks. Not modelled [추정]: HP/death, relics after the start, potions, rest-site upgrades, ancients' options,
/// transforms that replace cards. Deterministic for a seed.
/// </summary>
public sealed class RunSimulator
{
    private readonly DataPack _pack;
    private readonly ForecastCardPool _pool;
    private readonly Dictionary<(string, bool), CharacterPool> _pools = new();
    private readonly Lock _gate = new();

    public RunSimulator(DataPack pack, ForecastCardPool? pool = null)
    {
        _pack = pack;
        _pool = pool ?? ForecastCardPool.FromPack(pack);
    }

    public SimRun Run(string character, IReadOnlyList<DeckCard> startingDeck, IReadOnlyList<string> startingRelics,
        ISimPolicy policy, int seed, SimOptions? options = null)
    {
        options ??= SimOptions.Default;
        var cp = PoolFor(character, options.Multiplayer);
        var rng = new ForecastRng(unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0x51ED));
        var scarcity = options.Ascension >= ForecastOdds.ScarcityAscension;
        var odds = _pack.Weights.CardOdds;
        var upgradePerAct = options.Ascension >= odds.HighAscension ? odds.UpgradeChancePerActHighAscension : odds.UpgradeChancePerAct;
        var path = _pack.Weights.Path;

        var deck = startingDeck.ToList();
        var gold = options.StartGold;
        var removals = 0;
        var offset = ForecastOdds.OffsetReset;
        var decisions = new List<SimDecision>();
        var actEnd = new List<IReadOnlyList<DeckCard>>();
        int screens = 0, skips = 0, bought = 0;

        RunState State(int act, int floor) => new()
        {
            Character = character,
            Ascension = options.Ascension,
            Act = act,
            Floor = floor,
            Hp = 1,
            MaxHp = 1,
            Gold = gold,
            Deck = deck.ToArray(),
            Relics = startingRelics,
            IsMultiplayer = options.Multiplayer,
            CardRemovalsBought = removals,
        };

        void Reward(int act, int floor, OfferContext context)
        {
            var offer = RollReward(cp, ref rng, context, scarcity, ref offset, act, upgradePerAct);
            var state = State(act, floor);
            var pick = policy.PickReward(state, offer, context);
            screens++;
            if (pick is { } p) deck.Add(new DeckCard(p, floor));
            else skips++;
            if (options.KeepDecisions)
            {
                decisions.Add(new SimDecision
                {
                    Act = act, Floor = floor, Context = context, Offered = offer, StateBefore = state,
                    Picked = pick is { } q ? [q] : [],
                });
            }
        }

        void Shop(int act, int floor)
        {
            var offer = RollShop(cp, ref rng, scarcity, character);
            var state = State(act, floor);
            var choice = policy.Shop(state, offer);
            var taken = new List<CardRef>();
            foreach (var card in choice.Cards)
            {
                var sale = offer.Cards.FirstOrDefault(c => c.Card == card);
                if (sale is null || sale.Price > gold) continue;
                gold -= sale.Price;
                deck.Add(new DeckCard(card, floor));
                taken.Add(card);
                bought++;
            }
            if (choice.Remove)
            {
                var price = ShopScorer.RemovalPrice(State(act, floor), _pack.Weights);
                var target = new ShopScorer(_pack).RemovalTarget(State(act, floor), out _);
                if (target is not null && price <= gold)
                {
                    gold -= price;
                    deck.RemoveAt(deck.FindIndex(d => d.Id == target));
                    removals++;
                }
            }
            if (options.KeepDecisions)
            {
                decisions.Add(new SimDecision
                {
                    Act = act, Floor = floor, Context = OfferContext.Shop, StateBefore = state,
                    Offered = offer.Cards.Select(c => c.Card).ToArray(), Picked = taken,
                });
            }
        }

        foreach (var layout in RewardScheduler.TypicalActs)
        {
            var act = layout.Act;
            var treasureAt = 1 + rng.NextInt(Math.Max(1, layout.ContentFloors - 1));
            for (var i = 0; i < layout.ContentFloors; i++)
            {
                var floor = layout.StartFloor + 1 + i;
                if (i == treasureAt)
                {
                    gold += path.TreasureGold;
                    continue;
                }
                var r = rng.NextDouble();
                if ((r -= layout.Monster) < 0)
                {
                    gold += path.MonsterGold;
                    Reward(act, floor, OfferContext.CardReward);
                }
                else if ((r -= layout.Elite) < 0)
                {
                    gold += path.EliteGold;
                    Reward(act, floor, OfferContext.EliteReward);
                }
                else if ((r -= layout.Unknown) < 0)
                {
                    var u = rng.NextDouble();
                    if (u < options.UnknownFightChance)
                    {
                        gold += path.MonsterGold;
                        Reward(act, floor, OfferContext.CardReward);
                    }
                    else if (u < options.UnknownFightChance + options.UnknownShopChance)
                    {
                        if (options.Shops) Shop(act, floor);
                    }
                    else
                    {
                        gold += path.UnknownGold;
                        if (rng.NextDouble() < options.EventCardChance)
                        {
                            var card = cp.AnyCard.Length == 0 ? -1 : cp.AnyCard[rng.NextInt(cp.AnyCard.Length)];
                            if (card >= 0) deck.Add(new DeckCard(new CardRef(cp.Ids[card]), floor));
                        }
                    }
                }
                else if ((r -= layout.Shop) < 0)
                {
                    if (options.Shops) Shop(act, floor);
                }
                // else: rest site (no card change: upgrades are not modelled)
            }
            var bossFloor = layout.StartFloor + layout.ContentFloors + 1;
            if (act < RewardScheduler.LastAct)
            {
                Reward(act, bossFloor, OfferContext.BossReward);
                gold += options.BossGold;
            }
            actEnd.Add(deck.ToArray());
        }

        return new SimRun
        {
            Character = character,
            Seed = seed,
            ActEndDecks = actEnd,
            Relics = startingRelics,
            Decisions = decisions,
            RewardScreens = screens,
            RewardSkips = skips,
            CardsBought = bought,
            Removals = removals,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Offers
    // ---------------------------------------------------------------------------------------------

    private IReadOnlyList<CardRef> RollReward(CharacterPool cp, ref ForecastRng rng, OfferContext context, bool scarcity, ref double offset,
        int act, double upgradePerAct)
    {
        var picked = new List<int>(3);
        var cards = new List<CardRef>(3);
        for (var slot = 0; slot < 3; slot++)
        {
            PoolRarity rarity;
            if (context == OfferContext.BossReward)
            {
                rarity = PoolRarity.Rare;
                offset = ForecastOdds.OffsetReset;
            }
            else
            {
                var (rare, uncommon) = context == OfferContext.EliteReward
                    ? (scarcity ? ForecastOdds.EliteRareScarcity : ForecastOdds.EliteRare, ForecastOdds.EliteUncommon)
                    : (scarcity ? ForecastOdds.NormalRareScarcity : ForecastOdds.NormalRare, ForecastOdds.NormalUncommon);
                var t = rng.NextDouble() - offset;
                rarity = t < rare ? PoolRarity.Rare : t < rare + uncommon ? PoolRarity.Uncommon : PoolRarity.Common;
                offset = rarity == PoolRarity.Rare
                    ? ForecastOdds.OffsetReset
                    : Math.Min(ForecastOdds.OffsetMax, offset + (scarcity ? ForecastOdds.OffsetStepScarcity : ForecastOdds.OffsetStep));
            }
            var card = Draw(cp.ByRarity[(int)rarity], ref rng, picked);
            if (card < 0) card = Draw(cp.AnyCard, ref rng, picked);
            if (card < 0) continue;
            picked.Add(card);
            var upgraded = rarity != PoolRarity.Rare && rng.NextDouble() < act * upgradePerAct;
            cards.Add(new CardRef(cp.Ids[card], upgraded ? 1 : 0));
        }
        return cards;
    }

    private ShopOffer RollShop(CharacterPool cp, ref ForecastRng rng, bool scarcity, string character)
    {
        var w = _pack.Weights.Shop;
        var rare = scarcity ? ForecastOdds.ShopRareScarcity : ForecastOdds.ShopRare;
        var picked = new List<int>(7);
        var cards = new List<(int Card, bool Colorless, string Rarity)>(7);
        ReadOnlySpan<int> types = [0, 0, 1, 1, 2];
        foreach (var type in types)
        {
            var t = rng.NextDouble();
            var rarity = t < rare ? PoolRarity.Rare : t < rare + ForecastOdds.ShopUncommon ? PoolRarity.Uncommon : PoolRarity.Common;
            var card = Draw(cp.ByTypeRarity[type * 3 + (int)rarity], ref rng, picked);
            if (card < 0) card = Draw(cp.ByType[type], ref rng, picked);
            if (card < 0) continue;
            picked.Add(card);
            cards.Add((card, false, rarity.ToString().ToLowerInvariant()));
        }
        foreach (var rarity in new[] { PoolRarity.Uncommon, PoolRarity.Rare })
        {
            var card = Draw(cp.Colorless[(int)rarity], ref rng, picked);
            if (card < 0) continue;
            picked.Add(card);
            cards.Add((card, true, rarity.ToString().ToLowerInvariant()));
        }
        var sale = cards.Count(c => !c.Colorless) is var n and > 0 ? rng.NextInt(n) : -1;
        var offers = new List<ShopCardOffer>(cards.Count);
        var characterIndex = 0;
        foreach (var (card, colorless, rarity) in cards)
        {
            var onSale = !colorless && characterIndex++ == sale;
            var basePrice = ShopPricing.BasePrice(rarity, colorless);
            var jitter = w.PriceJitterMin + rng.NextDouble() * (w.PriceJitterMax - w.PriceJitterMin);
            var price = (int)Math.Round(basePrice * jitter * (onSale ? w.SaleMultiplier : 1.0));
            offers.Add(new ShopCardOffer(new CardRef(cp.Ids[card]), price, PriceKnown: true, OnSale: onSale));
        }
        return new ShopOffer(offers);
    }

    private static int Draw(int[] pool, ref ForecastRng rng, List<int> taken)
    {
        var free = 0;
        foreach (var c in pool) if (!taken.Contains(c)) free++;
        if (free == 0) return -1;
        var k = rng.NextInt(free);
        foreach (var c in pool)
        {
            if (taken.Contains(c)) continue;
            if (k-- == 0) return c;
        }
        return -1;
    }

    private CharacterPool PoolFor(string character, bool multiplayer)
    {
        lock (_gate)
        {
            if (!_pools.TryGetValue((character, multiplayer), out var cp))
            {
                cp = CharacterPool.Build(_pool, character, multiplayer);
                _pools[(character, multiplayer)] = cp;
            }
            return cp;
        }
    }

    private sealed class CharacterPool
    {
        public string[] Ids = [];
        public int[][] ByRarity = [];
        public int[][] ByTypeRarity = [];
        public int[][] ByType = [];
        public int[][] Colorless = [];
        public int[] AnyCard = [];

        public static CharacterPool Build(ForecastCardPool pool, string character, bool multiplayer)
        {
            var chars = pool.CharacterCards(character, multiplayer).ToArray();
            var colorless = pool.ColorlessCards(multiplayer).ToArray();
            var all = chars.Concat(colorless).ToArray();
            var ids = all.Select(c => c.Id).ToArray();
            int[] Where(IEnumerable<int> idx) => idx.ToArray();
            var charIdx = Enumerable.Range(0, chars.Length);
            var colorIdx = Enumerable.Range(chars.Length, colorless.Length);
            string[] types = [CardTypes.Attack, CardTypes.Skill, CardTypes.Power];
            var rarities = Enum.GetValues<PoolRarity>();
            return new CharacterPool
            {
                Ids = ids,
                ByRarity = rarities.Select(r => Where(charIdx.Where(i => all[i].Rarity == r))).ToArray(),
                ByTypeRarity = types.SelectMany(t => rarities.Select(r => Where(charIdx.Where(i => all[i].Type == t && all[i].Rarity == r)))).ToArray(),
                ByType = types.Select(t => Where(charIdx.Where(i => all[i].Type == t))).ToArray(),
                Colorless = rarities.Select(r => Where(colorIdx.Where(i => all[i].Rarity == r))).ToArray(),
                AnyCard = Where(charIdx),
            };
        }
    }
}
