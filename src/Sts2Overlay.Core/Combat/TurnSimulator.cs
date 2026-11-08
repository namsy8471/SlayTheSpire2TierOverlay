using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Combat;

/// <summary>One player's side of a simulated fight.</summary>
public sealed record SimPlayer
{
    public required string Character { get; init; }

    /// <summary>The deck, enchantments included (<see cref="DeckCard.EnchantmentId"/>: Sharp, Swift, Imbued, ...).</summary>
    public required IReadOnlyList<DeckCard> Deck { get; init; }
    public IReadOnlyList<string> Relics { get; init; } = [];

    /// <summary>Potions in the belt; used on turn 1 when <see cref="SimOptions.UsePotions"/>.</summary>
    public IReadOnlyList<string> Potions { get; init; } = [];
}

public sealed record SimOptions
{
    public int Turns { get; init; } = 3;
    public int Iterations { get; init; } = 2000;

    /// <summary>Seed of the shuffles, so the same deck always gives the same forecast.</summary>
    public int Seed { get; init; } = 20261109;

    public bool UsePotions { get; init; }

    /// <summary>Look-ahead try-outs per card choice (<see cref="SimFight.Rollouts"/>); 0 = the plain greedy choice.</summary>
    public int Rollouts { get; init; }
}

/// <summary>
/// Plays a deck's first turns against one enemy that never acts (the training dummy), many times with different shuffles.
/// Each turn: energy and draw, then a greedy policy plays the card with the best damage value per energy until nothing
/// worth playing is left; end-of-turn effects; then the enemy turn (poison ticks, Doom check, debuffs tick down).
/// </summary>
/// <remarks>
/// The policy goes all-in on damage (no blocking: the dummy never attacks). It is greedy, not optimal, so a skilled
/// player can beat it a little; card effects the data can't express (<see cref="CombatModel.None"/>) do nothing.
/// </remarks>
public sealed class TurnSimulator(CombatData data)
{
    public CombatData Data { get; } = data;

    public DamageForecast Forecast(SimPlayer player, SimOptions? options = null)
    {
        var o = options ?? new SimOptions();
        var rng = new Random(o.Seed);
        var totals = new double[o.Iterations];
        var perTurn = new double[o.Turns];
        double doom = 0;
        var setup = new Setup(Data, player);
        for (var i = 0; i < o.Iterations; i++)
        {
            var f = new SimFight(setup, rng, o.Turns, o.UsePotions) { Rollouts = o.Rollouts };
            f.Run();
            totals[i] = f.Effective;
            for (var t = 0; t < o.Turns; t++) perTurn[t] += f.TurnDealt[t];
            doom += f.Doom;
        }
        for (var t = 0; t < o.Turns; t++) perTurn[t] /= Math.Max(1, o.Iterations);
        return new DamageForecast(totals, perTurn, doom / Math.Max(1, o.Iterations), setup.Coverage);
    }

    /// <summary>Per-player data resolved once per forecast: card levels, relics, deck statistics for the policy.</summary>
    internal sealed class Setup
    {
        public readonly CombatData Data;
        public readonly List<CardCombat> Cards = new();

        /// <summary>The enchantment of each card in <see cref="Cards"/> (same index), or null.</summary>
        public readonly List<Enchant?> Enchants = new();

        public readonly List<RelicCombat> Relics = new();
        public readonly List<PotionCombat> Potions = new();
        public readonly CharacterCombat Character;
        public readonly string CharacterPool;
        public readonly ForecastCoverage Coverage;
        public readonly int StrikesInDeck;

        // Rough per-card averages of the deck, used to price draw/energy/strength against the turns still to come.
        public readonly double AvgHitsPerCard;
        public readonly double AvgAttackDamagePerCard;
        public readonly double AvgValuePerCard;
        public readonly double DamagePerEnergy;
        public readonly double AttackShare;

        public Setup(CombatData data, SimPlayer p)
        {
            Data = data;
            Character = data.Character(p.Character);
            CharacterPool = Character.Pool ?? PoolOf(p.Character);
            var approx = new List<string>();
            var unknown = new List<string>();
            var full = 0;
            foreach (var c in p.Deck)
            {
                var cc = data.Card(c.Id, c.Upgrade);
                if (cc is null)
                {
                    unknown.Add(c.Id);
                    continue;
                }
                Cards.Add(cc);
                Enchants.Add(c.EnchantmentId is { } e ? new Enchant(e, c.EnchantmentAmount) : null);
                if (cc.Model == CombatModel.Full) full++;
                else if (cc.Model == CombatModel.Approx) approx.Add(c.Id);
                else unknown.Add(c.Id);
            }
            var approxRelics = new List<string>();
            foreach (var r in p.Relics)
            {
                if (data.Relic(r) is not { } rc) continue;
                Relics.Add(rc);
                if (rc.Model != CombatModel.Full) approxRelics.Add(r);
            }
            foreach (var id in p.Potions)
            {
                if (data.Potion(id) is { Use.Count: > 0 } pc) Potions.Add(pc);
            }
            Coverage = new ForecastCoverage
            {
                Cards = p.Deck.Count,
                FullCards = full,
                ApproxCards = approx.Distinct().ToArray(),
                UnknownCards = unknown.Distinct().ToArray(),
                ApproxRelics = approxRelics,
            };
            StrikesInDeck = Cards.Count(c => SimFight.HasTag(c, "Strike"));

            double hits = 0, dmg = 0, cost = 0, attacks = 0;
            foreach (var c in Cards)
            {
                foreach (var op in c.Play)
                {
                    if (op.Op is not (CombatOps.Attack or CombatOps.OstyAttack)) continue;
                    var h = op.Hits.IsConstant ? op.Hits.Constant : 1;
                    var d = op.Dmg.IsConstant ? op.Dmg.Constant : 6;
                    hits += h;
                    dmg += h * d;
                }
                if (c.IsAttack)
                {
                    attacks++;
                    cost += c.CostX ? 2 : Math.Max(0, c.Cost);
                }
            }
            var n = Math.Max(1, Cards.Count);
            AvgHitsPerCard = hits / n;
            AvgAttackDamagePerCard = dmg / n;
            DamagePerEnergy = cost > 0 ? Math.Max(3, dmg / cost) : 6;
            AvgValuePerCard = Math.Max(2, AvgAttackDamagePerCard);
            AttackShare = attacks / n;
        }

        private readonly Dictionary<CardCombat, Dictionary<string, CardCombat>> _enchanted = new(ReferenceEqualityComparer.Instance);

        /// <summary>The card with its enchantment's keyword/cost changes, built once per card level and enchantment.</summary>
        public CardCombat Enchanted(CardCombat card, Enchant? e)
        {
            if (e is null) return card;
            if (!_enchanted.TryGetValue(card, out var byId)) _enchanted[card] = byId = new Dictionary<string, CardCombat>(StringComparer.Ordinal);
            if (!byId.TryGetValue(e.Id, out var result)) byId[e.Id] = result = Enchantments.Apply(card, e);
            return result;
        }

        /// <summary><c>CHARACTER.IRONCLAD</c> → <c>CARD_POOL.IRONCLAD_CARD_POOL</c>.</summary>
        public static string PoolOf(string character)
        {
            var name = character.StartsWith("CHARACTER.", StringComparison.Ordinal) ? character["CHARACTER.".Length..] : character;
            return $"CARD_POOL.{name}_CARD_POOL";
        }
    }
}
