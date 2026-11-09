using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sts2Overlay.Core.Archetypes;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Forecast;

/// <summary>
/// PLAN §7.5 v2 / R5 "which deck is likely to come together": Monte Carlo over the run's remaining card rewards.
/// </summary>
/// <remarks>
/// One simulation = sample the remaining reward chances (<see cref="RewardScheduler"/>), roll each reward's cards with the
/// game's rarity odds (<see cref="ForecastOdds"/>, rare offset included), let the pick policy choose (or skip), and add the
/// card to the deck. After N simulations, "P(archetype completes)" is simply the share of simulations in which it did.
/// <para>Not modelled [추정]: event/ancient cards, removals, transforms, upgrades, relics gained later, gold, combos.</para>
/// Thread-safe: one instance can serve several threads (prepared per-character data is cached in a concurrent map).
/// </remarks>
public sealed class ArchetypeForecaster
{
    private const double Epsilon = 1e-9;
    private static readonly string[] RoleOrder = [ArchetypeRoles.Core, ArchetypeRoles.Payoff, ArchetypeRoles.Enabler, ArchetypeRoles.Support];

    private readonly ConcurrentDictionary<(string Character, bool Multiplayer), CharacterModel> _models = new();
    private readonly ForecastCache _cache;
    private readonly RecommendationEngine _engine;

    /// <param name="pack">Data pack (tiers, archetypes, weights).</param>
    /// <param name="pool">Reward card pool; null → from the pack.</param>
    /// <param name="cacheCapacity">Results kept in the LRU cache (0 = off).</param>
    /// <param name="calibration">Raw → calibrated probability maps; null → <see cref="ForecastCalibration.Default"/>.</param>
    public ArchetypeForecaster(DataPack pack, ForecastCardPool? pool = null, int cacheCapacity = 32, ForecastCalibration? calibration = null)
    {
        Pack = pack;
        Pool = pool ?? ForecastCardPool.FromPack(pack);
        Calibration = calibration ?? ForecastCalibration.Default;
        _cache = new ForecastCache(cacheCapacity);
        _engine = new RecommendationEngine(pack);
    }

    public DataPack Pack { get; }
    public ForecastCardPool Pool { get; }
    public ForecastCalibration Calibration { get; }

    /// <summary>Runs (or returns the cached) forecast for <paramref name="request"/>.</summary>
    public ForecastResult Forecast(ForecastRequest request, ForecastOptions? options = null, CancellationToken cancel = default)
    {
        options ??= ForecastOptions.Default;
        var key = KeyOf(request, options);
        if (_cache.TryGet(key, out var cached)) return cached with { FromCache = true };

        var result = Run(request, options, key, cancel);
        _cache.Add(key, result);
        return result;
    }

    /// <summary>Stable text key of everything the result depends on (also the cache key and, by hash, the default seed).</summary>
    public static string KeyOf(ForecastRequest request, ForecastOptions options)
    {
        var s = request.State;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256);
        sb.Append(s.Character).Append('|').Append(s.Ascension).Append('|').Append(s.Act).Append('|').Append(s.Floor).Append('|').Append(s.IsMultiplayer ? 'm' : 's');
        sb.Append("|d:");
        foreach (var g in s.Deck.GroupBy(c => c.Id, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append(g.Key).Append('*').Append(g.Count()).Append('+').Append(g.Count(c => c.Upgrade > 0)).Append(',');
        }
        sb.Append("|r:").AppendJoin(',', s.Relics.Order(StringComparer.Ordinal));
        if (request.Map is { } map) sb.Append("|m:").Append(map.ActId).Append('@').Append(map.Current?.ToString() ?? "start").Append('#').Append(map.Nodes.Count);
        if (request.RarityOffset is { } o) sb.Append("|o:").Append(o.ToString("0.###", inv));
        if (request.FixedSchedule is { } fixedSchedule)
        {
            sb.Append("|f:");
            foreach (var e in fixedSchedule) sb.Append((int)e.Kind).Append(e.Act);
        }
        sb.Append("|n:").Append(options.Simulations).Append("|p:").Append(options.Policy).Append("|z:").Append(options.PickNoise.ToString("0.###", inv))
          .Append("|b:").Append(options.ShopBuyChance.ToString("0.###", inv)).Append("|u:").Append(options.UnknownFightChance.ToString("0.###", inv))
          .Append('/').Append(options.UnknownShopChance.ToString("0.###", inv)).Append("|c:").Append(options.CardsPerReward)
          .Append("|e:").Append(options.EventCardChance.ToString("0.####", inv)).Append("|h:")
          .AppendJoin(',', (options.ActDeathChance ?? []).Select(h => h.ToString("0.####", inv)));
        if (options.Seed is { } seed) sb.Append("|s:").Append(seed);
        return sb.ToString();
    }

    /// <summary>FNV-1a over the key: unlike <see cref="string.GetHashCode()"/> it is the same in every process.</summary>
    internal static int StableSeed(string key)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in key)
        {
            h ^= ch;
            h *= 1099511628211UL;
        }
        return (int)(h ^ (h >> 32));
    }

    // ================================================================================================
    // Simulation
    // ================================================================================================

    private ForecastResult Run(ForecastRequest request, ForecastOptions options, string key, CancellationToken cancel)
    {
        var sw = Stopwatch.StartNew();
        var state = request.State;
        var model = _models.GetOrAdd((state.Character, state.IsMultiplayer), k => CharacterModel.Build(Pack, Pool, k.Character, k.Multiplayer));
        var a = model.Archetypes.Length;
        if (a == 0) return ForecastResult.Empty with { Key = key, ElapsedMs = sw.Elapsed.TotalMilliseconds };

        var seed = options.Seed ?? StableSeed(key);
        var rng = new ForecastRng(unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 1));
        // Deaths draw from their own stream, so turning the death model on or off leaves the "if you survive" numbers identical.
        var deathRng = new ForecastRng(unchecked((ulong)seed * 0xD1B54A32D192ED03UL + 7));
        var start = SimState.Initial(model, state, Pack);
        var scheduler = new RewardScheduler(state, request.Map, options);
        var sims = Math.Max(1, options.Simulations);
        var curAct = Math.Clamp(state.Act, 1, RewardScheduler.LastAct);
        var scarcity = state.Ascension >= ForecastOdds.ScarcityAscension;
        var startOffset = request.RarityOffset ?? GuessOffset(state);

        var nextBoss = new Accumulator(model);
        var end = new Accumulator(model);
        var endAll = new Accumulator(model);
        // Share of the current act still ahead: only that part of its death chance is left. [추정]
        var deathFraction = (double)RewardScheduler.RemainingContentFloors(curAct, state.Floor)
                            / RewardScheduler.TypicalActs[curAct - 1].ContentFloors;
        var events = new List<RewardEvent>(64);
        var sim = start.Clone();
        var offered = new int[Math.Max(1, options.CardsPerReward)];
        var shopOffer = new int[7];
        double[] untilBoss = new double[4], untilEnd = new double[4];
        var engineDeck = options.Policy == PickPolicy.Engine ? new List<DeckCard>(64) : null;

        for (var n = 0; n < sims; n++)
        {
            if ((n & 63) == 0) cancel.ThrowIfCancellationRequested();
            start.CopyTo(sim);
            if (engineDeck is not null)
            {
                engineDeck.Clear();
                engineDeck.AddRange(state.Deck);
            }
            if (request.FixedSchedule is { } fixedSchedule)
            {
                events.Clear();
                events.AddRange(fixedSchedule);
            }
            else
            {
                scheduler.Sample(ref rng, events);
            }

            var offset = startOffset;
            var bossTaken = false;
            var deathAt = SampleDeath(ref deathRng, events, options, curAct, deathFraction);
            for (var e = 0; e < events.Count; e++)
            {
                var ev = events[e];
                if (e == deathAt) endAll.Add(model, sim); // the run ends here; the conditional future keeps going
                if (!bossTaken && (ev.Act > curAct || (ev.Act == curAct && ev.Kind is RewardKind.Boss or RewardKind.FinalBoss)))
                {
                    nextBoss.Add(model, sim);
                    bossTaken = true;
                }
                if (ev.Kind == RewardKind.FinalBoss) continue;
                if (ev.Kind == RewardKind.EventCard)
                {
                    // Event / transform card: one random character card, no choice (rarity like a normal reward slot). [추정]
                    var card = RollEventCard(model, ref rng, scarcity, offered);
                    if (card >= 0) Gain(model, sim, card, engineDeck);
                    continue;
                }
                var bucket = ev.Kind == RewardKind.Shop ? 3 : ev.Kind == RewardKind.Boss ? 2 : ev.Kind == RewardKind.Elite ? 1 : 0;
                untilEnd[bucket]++;
                if (!bossTaken) untilBoss[bucket]++;

                if (ev.Kind == RewardKind.Shop)
                {
                    RollShop(model, ref rng, scarcity, shopOffer);
                    var buy = rng.NextDouble() < options.ShopBuyChance;
                    var pick = Choose(model, sim, shopOffer, shopOffer.Length, ev, options, ref rng, state, engineDeck);
                    if (buy && pick >= 0) Gain(model, sim, pick, engineDeck);
                }
                else
                {
                    var count = RollReward(model, ref rng, ev.Kind, scarcity, ref offset, offered);
                    var pick = Choose(model, sim, offered, count, ev, options, ref rng, state, engineDeck);
                    if (pick >= 0) Gain(model, sim, pick, engineDeck);
                }
            }
            if (!bossTaken) nextBoss.Add(model, sim);
            end.Add(model, sim);
            if (deathAt < 0 || deathAt >= events.Count) endAll.Add(model, sim);
        }

        var forecasts = new ArchetypeForecast[a];
        for (var i = 0; i < a; i++)
        {
            var arch = model.Archetypes[i];
            var commitNow = start.Commit(model, i);
            var complete = commitNow >= arch.Threshold - Epsilon;
            var endOutcome = end.Outcome(i, sims);
            var allOutcome = endAll.Outcome(i, sims);
            var completion = start.CompletionPercent(model, i) / 100.0;
            forecasts[i] = new ArchetypeForecast
            {
                Archetype = arch.Archetype,
                CurrentCommit = commitNow,
                CurrentCompletionPercent = completion * 100.0,
                IsCompleteNow = complete,
                NextBoss = nextBoss.Outcome(i, sims),
                EndOfRun = endOutcome,
                EndOfRunAll = allOutcome,
                // Already complete stays complete (removing a build card is rare); otherwise map raw → observed frequency.
                CalibratedIfSurvive = complete ? 1.0 : Calibration.IfSurvive.Apply(endOutcome.PComplete, completion),
                CalibratedAll = complete ? 1.0 : Calibration.All.Apply(allOutcome.PComplete, completion),
            };
        }

        return new ForecastResult
        {
            Archetypes = forecasts
                .OrderByDescending(f => f.CalibratedIfSurvive)
                .ThenByDescending(f => f.EndOfRun.PComplete)
                .ThenByDescending(f => f.EndOfRun.PPrimary)
                .ThenBy(f => f.Archetype.Id, StringComparer.Ordinal)
                .ToArray(),
            Simulations = sims,
            Seed = seed,
            Policy = options.Policy,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            UsedMap = request.FixedSchedule is null && scheduler.UsesMap,
            UsedFixedSchedule = request.FixedSchedule is not null,
            RarityOffsetUsed = startOffset,
            ExpectedUntilNextBoss = Counts(untilBoss, sims),
            ExpectedUntilEnd = Counts(untilEnd, sims),
            PoolSize = model.CharacterPoolSize,
            Key = key,
        };
    }

    private static RewardCounts Counts(double[] c, int sims) => new(c[0] / sims, c[1] / sims, c[2] / sims, c[3] / sims);

    /// <summary>
    /// Index of the event before which the run ends (death or abandon), or −1 when it survives. Act k ends the run with
    /// <see cref="ForecastOptions.ActDeathChance"/>[k] (the current act scaled by the share still ahead); the point inside
    /// that act is uniform over its events, boss included.
    /// </summary>
    private static int SampleDeath(ref ForecastRng rng, List<RewardEvent> events, ForecastOptions options, int curAct, double fraction)
    {
        if (options.ActDeathChance is not { Count: > 0 } hazard || events.Count == 0) return -1;
        for (var act = curAct; act <= RewardScheduler.LastAct; act++)
        {
            var h = act - 1 < hazard.Count ? hazard[act - 1] : 0;
            if (act == curAct) h *= Math.Clamp(fraction, 0, 1);
            if (rng.NextDouble() >= h) continue;

            int first = -1, count = 0;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].Act != act) continue;
                if (first < 0) first = i;
                count++;
            }
            if (count == 0) continue;
            return first + (count == 1 ? 0 : rng.NextInt(count));
        }
        return -1;
    }

    /// <summary>A random character card for an event/transform (normal-reward rarity odds, offset untouched).</summary>
    private static int RollEventCard(CharacterModel m, ref ForecastRng rng, bool scarcity, int[] scratch)
    {
        var rare = scarcity ? ForecastOdds.NormalRareScarcity : ForecastOdds.NormalRare;
        var t = rng.NextDouble();
        var rarity = t < rare ? PoolRarity.Rare : t < rare + ForecastOdds.NormalUncommon ? PoolRarity.Uncommon : PoolRarity.Common;
        var card = DrawDistinct(m.ByRarity(rarity), ref rng, scratch, 0);
        return card >= 0 ? card : DrawDistinct(m.AnyCharacterCard, ref rng, scratch, 0);
    }

    /// <summary>Unknown offset: run start → the reset value; later → 0 (roughly the average after a few non-rare cards). [추정]</summary>
    private static double GuessOffset(RunState state) => state.Floor <= 1 ? ForecastOdds.OffsetReset : 0.0;

    /// <summary>
    /// One reward screen: per slot a rarity roll (offset applies to normal/elite and moves after each card; boss = rare),
    /// then a uniformly random card of that rarity not already in this reward. Returns the number of cards offered.
    /// </summary>
    /// <remarks>The offset shifts the roll, so it moves both the rare and the uncommon threshold (as in StS1). [추정 for uncommon]</remarks>
    private static int RollReward(CharacterModel m, ref ForecastRng rng, RewardKind kind, bool scarcity, ref double offset, int[] into)
    {
        var count = 0;
        for (var slot = 0; slot < into.Length; slot++)
        {
            PoolRarity rarity;
            if (kind == RewardKind.Boss)
            {
                rarity = PoolRarity.Rare;
                offset = ForecastOdds.OffsetReset;
            }
            else
            {
                var (rare, uncommon) = kind == RewardKind.Elite
                    ? (scarcity ? ForecastOdds.EliteRareScarcity : ForecastOdds.EliteRare, ForecastOdds.EliteUncommon)
                    : (scarcity ? ForecastOdds.NormalRareScarcity : ForecastOdds.NormalRare, ForecastOdds.NormalUncommon);
                var t = rng.NextDouble() - offset;
                rarity = t < rare ? PoolRarity.Rare : t < rare + uncommon ? PoolRarity.Uncommon : PoolRarity.Common;
                offset = rarity == PoolRarity.Rare
                    ? ForecastOdds.OffsetReset
                    : Math.Min(ForecastOdds.OffsetMax, offset + (scarcity ? ForecastOdds.OffsetStepScarcity : ForecastOdds.OffsetStep));
            }

            var card = DrawDistinct(m.ByRarity(rarity), ref rng, into, count);
            if (card < 0) card = DrawDistinct(m.AnyCharacterCard, ref rng, into, count);
            if (card >= 0) into[count++] = card;
        }
        return count;
    }

    /// <summary>Shop: attack, attack, skill, skill, power (shop odds, offset untouched) + colorless uncommon and rare.</summary>
    private static void RollShop(CharacterModel m, ref ForecastRng rng, bool scarcity, int[] into)
    {
        var rare = scarcity ? ForecastOdds.ShopRareScarcity : ForecastOdds.ShopRare;
        ReadOnlySpan<int> types = [0, 0, 1, 1, 2];
        var count = 0;
        foreach (var type in types)
        {
            var t = rng.NextDouble();
            var rarity = t < rare ? PoolRarity.Rare : t < rare + ForecastOdds.ShopUncommon ? PoolRarity.Uncommon : PoolRarity.Common;
            var card = DrawDistinct(m.ByTypeRarity(type, rarity), ref rng, into, count);
            if (card < 0) card = DrawDistinct(m.ByTypeAny(type), ref rng, into, count);
            if (card < 0) card = DrawDistinct(m.AnyCharacterCard, ref rng, into, count);
            into[count++] = card;
        }
        // Compute first, then store: "into[count++] = Draw(..., count)" would pass the already incremented count.
        var colorlessUncommon = DrawDistinct(m.ColorlessByRarity(PoolRarity.Uncommon), ref rng, into, count);
        into[count++] = colorlessUncommon;
        var colorlessRare = DrawDistinct(m.ColorlessByRarity(PoolRarity.Rare), ref rng, into, count);
        into[count] = colorlessRare;
    }

    private static int DrawDistinct(int[] pool, ref ForecastRng rng, int[] taken, int takenCount)
    {
        if (pool.Length == 0) return -1;
        var start = pool.Length == 1 ? 0 : rng.NextInt(pool.Length);
        if (Array.IndexOf(taken, pool[start], 0, takenCount) < 0) return pool[start];
        // Card already in this reward: the game rerolls until distinct, which is the same as a uniform pick among the
        // cards not taken yet. Picking the k-th free card does that in one pass, even for a pool of 3.
        var free = 0;
        foreach (var c in pool) if (Array.IndexOf(taken, c, 0, takenCount) < 0) free++;
        if (free == 0) return -1;
        var k = rng.NextInt(free);
        foreach (var c in pool)
        {
            if (Array.IndexOf(taken, c, 0, takenCount) >= 0) continue;
            if (k-- == 0) return c;
        }
        return -1;
    }

    /// <summary>Index of the card to take, or −1 for skip. Ignores −1 entries (empty pool slots).</summary>
    private int Choose(CharacterModel m, SimState s, int[] offer, int count, RewardEvent ev, ForecastOptions options, ref ForecastRng rng, RunState state, List<DeckCard>? engineDeck)
        => engineDeck is null ? ChooseGreedy(m, s, offer.AsSpan(0, count), ev, options, ref rng) : ChooseEngine(m, offer.AsSpan(0, count).ToArray(), ev, state, engineDeck);

    /// <summary>
    /// The engine's main score terms on flat arrays (PLAN §7.1): base tier + build (capped, weak cap) + need − dilution −
    /// duplicates + boss bonus − off-build, against skip = T0 + size + commit (PLAN §7.2). Low-confidence cards can't beat skip.
    /// Combos, the requires gate and saturation are left out to stay fast.
    /// </summary>
    private int ChooseGreedy(CharacterModel m, SimState s, ReadOnlySpan<int> offer, RewardEvent ev, ForecastOptions options, ref ForecastRng rng)
    {
        var w = Pack.Weights;
        var act = Math.Clamp(ev.Act, 1, RewardScheduler.LastAct) - 1;
        var a = m.Archetypes.Length;
        var r = m.RoleCount;

        // Per-decision deck facts.
        Span<double> gapUrg = stackalloc double[r];
        var weak = false;
        for (var k = 0; k < r; k++)
        {
            var target = m.Target[act][k];
            if (target > 0)
            {
                gapUrg[k] = w.WNeed * Math.Max(0, target - s.Supply[k]) / target * m.Urgency[act][k];
                if (m.IsWeakRole[k] && s.Supply[k] < w.WeakThreshold * target) weak = true;
            }
            else gapUrg[k] = 0;
        }
        var maxCommit = 0.0;
        var anyCommitted = false;
        for (var i = 0; i < a; i++)
        {
            var c = s.Commit(m, i);
            if (c > maxCommit) maxCommit = c;
            if (c >= w.CommitThreshold) anyCommitted = true;
        }
        var over = Math.Max(0, s.DeckSize - m.IdealSize[act]);
        var skip = m.SkipBase[act] + w.Skip.LambdaSize * over + (maxCommit >= w.CommitThreshold ? w.Skip.LambdaCommit : 0);
        var cap = weak ? w.BuildCapWeak : w.BuildCap;

        var best = -1;
        var bestValue = double.NegativeInfinity;
        foreach (var card in offer)
        {
            if (card < 0) continue;
            var raw = 0.0;
            var memberCommit = 0.0;
            var inCommitted = false;
            var row = card * a;
            for (var i = 0; i < a; i++)
            {
                var weight = m.ArchWeight[row + i];
                if (weight <= 0) continue;
                var c = s.Commit(m, i);
                raw += c * weight;
                if (c > memberCommit) memberCommit = c;
                if (c >= w.CommitThreshold) inCommitted = true;
            }
            var need = 0.0;
            var roleRow = card * r;
            for (var k = 0; k < r; k++) need += gapUrg[k] * m.RoleStrength[roleRow + k];

            var value = m.TierValue[card * 3 + act]
                        + Math.Min(cap, w.WArch * raw)
                        + need
                        - w.LambdaDilution * over * (1 - m.Cycle[card])
                        - w.RhoDuplicate * s.Copies[card] * m.DupFactor[card];
            if (ev.Kind == RewardKind.Boss && memberCommit > 0) value += w.Situation.BossRewardBuildBonus * memberCommit;
            if (anyCommitted && !inCommitted && need <= 0) value -= w.OffBuildPenalty;
            if (options.PickNoise > 0) value += options.PickNoise * rng.NextGaussian();

            if (value > bestValue && m.CanBeatSkip[card])
            {
                bestValue = value;
                best = card;
            }
        }
        return best >= 0 && bestValue >= skip + w.Skip.Margin ? best : -1;
    }

    private int ChooseEngine(CharacterModel m, int[] offer, RewardEvent ev, RunState state, List<DeckCard> deck)
    {
        var cards = offer.Where(c => c >= 0).Select(c => new CardRef(m.Ids[c])).ToArray();
        if (cards.Length == 0) return -1;
        var context = ev.Kind switch
        {
            RewardKind.Elite => OfferContext.EliteReward,
            RewardKind.Boss => OfferContext.BossReward,
            RewardKind.Shop => OfferContext.Shop,
            _ => OfferContext.CardReward,
        };
        var now = state with { Act = ev.Act, Deck = deck.ToArray() };
        var eval = _engine.EvaluateOffer(now, cards, context);
        return eval.Best is { } best && m.Index.TryGetValue(best.Id, out var idx) ? idx : -1;
    }

    private static void Gain(CharacterModel m, SimState s, int card, List<DeckCard>? engineDeck)
    {
        s.Add(m, card);
        engineDeck?.Add(new DeckCard(new CardRef(m.Ids[card])));
    }

    // ================================================================================================
    // Prepared data (structure of arrays: the hot loop reads plain double[] / int[] instead of dictionaries)
    // ================================================================================================

    internal sealed class ArchetypeModel
    {
        public required Archetype Archetype { get; init; }
        public double FullWeight { get; init; }
        public double Threshold { get; init; }
        public double CompletionTotal { get; init; }
        public int CoreTotal { get; init; }
    }

    internal sealed class CharacterModel
    {
        public ArchetypeModel[] Archetypes = [];
        public string[] Ids = [];
        public Dictionary<string, int> Index = new(StringComparer.Ordinal);
        public int RoleCount;
        public bool[] IsWeakRole = [];

        // Per card (flattened [card * width + j]).
        public double[] TierValue = [];   // width 3 (act 1..3)
        public double[] ArchWeight = [];  // width A: role weight in archetype, 0 = not a member
        public double[] ArchCompletion = []; // width A: completion weight (core/payoff 100, others 56)
        public bool[] ArchCore = [];      // width A: core or payoff
        public double[] RoleStrength = []; // width R
        public double[] Cycle = [];
        public double[] DupFactor = [];
        public bool[] CanBeatSkip = [];

        // Per act (index 0..2).
        public double[][] Target = [];
        public double[][] Urgency = [];
        public int[] IdealSize = [];
        public double[] SkipBase = [];

        private int[][] _byRarity = [];
        private int[][] _byTypeRarity = []; // [type * 3 + rarity], type 0 attack, 1 skill, 2 power
        private int[][] _byType = [];
        private int[][] _colorless = [];
        public int[] AnyCharacterCard = [];
        public int CharacterPoolSize => AnyCharacterCard.Length;

        public int[] ByRarity(PoolRarity r) => _byRarity[(int)r];
        public int[] ByTypeRarity(int type, PoolRarity r) => _byTypeRarity[type * 3 + (int)r];
        public int[] ByTypeAny(int type) => _byType[type];
        public int[] ColorlessByRarity(PoolRarity r) => _colorless[(int)r];

        public static CharacterModel Build(DataPack pack, ForecastCardPool pool, string character, bool multiplayer)
        {
            var w = pack.Weights;
            var m = new CharacterModel();
            var archetypes = pack.ArchetypesFor(character).ToArray();
            var charCards = pool.CharacterCards(character, multiplayer).ToArray();
            var colorless = pool.ColorlessCards(multiplayer).ToArray();

            // Card index space: pool cards first, then archetype cards that aren't in the pool (e.g. basics, event cards).
            var ids = new List<string>();
            int IndexOf(string raw)
            {
                var id = pack.Canonical(raw);
                if (m.Index.TryGetValue(id, out var i)) return i;
                m.Index[id] = ids.Count;
                ids.Add(id);
                return ids.Count - 1;
            }
            var charIdx = charCards.Select(c => IndexOf(c.Id)).ToArray();
            var colorIdx = colorless.Select(c => IndexOf(c.Id)).ToArray();

            // Archetype membership: a card counts once, in the first role of core → payoff → enabler → support.
            var a = archetypes.Length;
            var membership = new List<(int Card, int Arch, double Weight, double Completion, bool Core)>();
            m.Archetypes = new ArchetypeModel[a];
            for (var i = 0; i < a; i++)
            {
                var arch = archetypes[i];
                var seen = new HashSet<string>(StringComparer.Ordinal);
                double completionTotal = 0;
                var coreTotal = 0;
                foreach (var role in RoleOrder)
                {
                    foreach (var raw in ListFor(arch.Cards, role))
                    {
                        var id = pack.Canonical(raw);
                        if (!seen.Add(id)) continue;
                        var isCore = role is ArchetypeRoles.Core or ArchetypeRoles.Payoff;
                        var completion = isCore ? w.Completion.CoreWeight : w.Completion.SupportWeight;
                        completionTotal += completion;
                        if (isCore) coreTotal++;
                        membership.Add((IndexOf(id), i, RoleWeight(arch, role), completion, isCore));
                    }
                }
                m.Archetypes[i] = new ArchetypeModel
                {
                    Archetype = arch,
                    FullWeight = arch.FullWeight,
                    Threshold = arch.CommitThreshold,
                    CompletionTotal = completionTotal,
                    CoreTotal = coreTotal,
                };
            }

            var n = ids.Count;
            m.Ids = ids.ToArray();
            var roles = Roles.All;
            var r = m.RoleCount = roles.Count;
            m.IsWeakRole = roles.Select(role => w.WeakRoles.Contains(role)).ToArray();
            m.TierValue = new double[n * 3];
            m.ArchWeight = new double[n * a];
            m.ArchCompletion = new double[n * a];
            m.ArchCore = new bool[n * a];
            m.RoleStrength = new double[n * r];
            m.Cycle = new double[n];
            m.DupFactor = new double[n];
            m.CanBeatSkip = new bool[n];

            for (var c = 0; c < n; c++)
            {
                var entry = pack.FindTier(character, m.Ids[c]);
                for (var act = 1; act <= 3; act++)
                {
                    var tier = entry is null ? w.UnknownTier
                        : entry.ActTier is not null && entry.ActTier.TryGetValue(act, out var t) ? t : entry.Tier;
                    m.TierValue[c * 3 + act - 1] = Finite(w.TierValue.GetValueOrDefault(tier) + (entry?.BaseAdjust ?? 0));
                }
                if (entry is not null)
                {
                    for (var k = 0; k < r; k++) m.RoleStrength[c * r + k] = Finite(entry.Roles.GetValueOrDefault(roles[k]));
                }
                m.Cycle[c] = Finite(entry?.Cycle ?? 0);
                m.DupFactor[c] = Finite(entry?.DupFactor ?? 1.0);
                m.CanBeatSkip[c] = (entry?.Confidence ?? w.UnknownConfidence) >= w.Skip.LowConfidence;
            }
            foreach (var (card, arch, weight, completion, core) in membership)
            {
                m.ArchWeight[card * a + arch] = weight;
                m.ArchCompletion[card * a + arch] = completion;
                m.ArchCore[card * a + arch] = core;
            }

            m.Target = new double[3][];
            m.Urgency = new double[3][];
            m.IdealSize = new int[3];
            m.SkipBase = new double[3];
            var emptyRoles = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var act = 1; act <= 3; act++)
            {
                var targets = DeckAnalyzer.TargetsFor(new RunState { Character = character, Act = act }, w);
                var urgency = Weights.ForAct(w.Urgency, act, emptyRoles);
                m.Target[act - 1] = roles.Select(role => targets.TryGetValue(role, out var t) && double.IsFinite(t) ? t : 0).ToArray();
                m.Urgency[act - 1] = roles.Select(role => urgency.TryGetValue(role, out var u) ? u : 1.0).ToArray();
                m.IdealSize[act - 1] = Weights.ForAct(w.IdealDeckSize, act, int.MaxValue);
                m.SkipBase[act - 1] = Weights.ForAct(w.Skip.T0, act, 0.0);
            }

            int[] Pick(IEnumerable<(PoolCard Card, int Index)> source) => source.Select(x => x.Index).Distinct().ToArray();
            var charPairs = charCards.Zip(charIdx).ToArray();
            var colorPairs = colorless.Zip(colorIdx).ToArray();
            m._byRarity = Enum.GetValues<PoolRarity>().Select(rar => Pick(charPairs.Where(p => p.First.Rarity == rar))).ToArray();
            string[] types = [CardTypes.Attack, CardTypes.Skill, CardTypes.Power];
            m._byTypeRarity = types.SelectMany(type => Enum.GetValues<PoolRarity>()
                .Select(rar => Pick(charPairs.Where(p => p.First.Type == type && p.First.Rarity == rar)))).ToArray();
            m._byType = types.Select(type => Pick(charPairs.Where(p => p.First.Type == type))).ToArray();
            m._colorless = Enum.GetValues<PoolRarity>().Select(rar => Pick(colorPairs.Where(p => p.First.Rarity == rar))).ToArray();
            m.AnyCharacterCard = Pick(charPairs);
            return m;
        }

        private static double RoleWeight(Archetype a, string role)
            => a.RoleWeight.TryGetValue(role, out var w) ? w : new Archetype().RoleWeight.GetValueOrDefault(role);

        private static List<string> ListFor(ArchetypeCards cards, string role) => role switch
        {
            ArchetypeRoles.Core => cards.Core,
            ArchetypeRoles.Payoff => cards.Payoff,
            ArchetypeRoles.Enabler => cards.Enabler,
            _ => cards.Support,
        };

        private static double Finite(double v) => double.IsFinite(v) ? v : 0.0;
    }

    /// <summary>The mutable deck summary one simulation works on.</summary>
    internal sealed class SimState
    {
        public int[] Copies = [];
        public double[] WeightSum = [];
        public double[] CompletionOwned = [];
        public int[] CoreOwned = [];
        public double[] Supply = [];
        public int DeckSize;

        public double Commit(CharacterModel m, int a)
        {
            var full = m.Archetypes[a].FullWeight;
            return full > 0 ? Math.Clamp(WeightSum[a] / full, 0.0, 1.0) : 0.0;
        }

        public double CompletionPercent(CharacterModel m, int a)
        {
            var total = m.Archetypes[a].CompletionTotal;
            return total > 0 ? Math.Clamp(CompletionOwned[a] / total * 100.0, 0.0, 100.0) : 0.0;
        }

        /// <summary>
        /// Adds one copy. Same rules as <see cref="ArchetypeDetector"/>: the first copy adds the full role weight, the second
        /// <see cref="ArchetypeDetector.SecondCopyFactor"/> of it, later copies nothing; completion counts distinct cards.
        /// </summary>
        public void Add(CharacterModel m, int card)
        {
            var n = ++Copies[card];
            DeckSize++;
            var a = m.Archetypes.Length;
            var row = card * a;
            for (var i = 0; i < a; i++)
            {
                var weight = m.ArchWeight[row + i];
                if (weight <= 0) continue;
                if (n == 1)
                {
                    WeightSum[i] += weight;
                    CompletionOwned[i] += m.ArchCompletion[row + i];
                    if (m.ArchCore[row + i]) CoreOwned[i]++;
                }
                else if (n == 2)
                {
                    WeightSum[i] += weight * ArchetypeDetector.SecondCopyFactor;
                }
            }
            var r = m.RoleCount;
            for (var k = 0; k < r; k++) Supply[k] += m.RoleStrength[card * r + k];
        }

        public static SimState Initial(CharacterModel m, RunState state, DataPack pack)
        {
            var s = new SimState
            {
                Copies = new int[m.Ids.Length],
                WeightSum = new double[m.Archetypes.Length],
                CompletionOwned = new double[m.Archetypes.Length],
                CoreOwned = new int[m.Archetypes.Length],
                Supply = new double[m.RoleCount],
            };
            foreach (var dc in state.Deck)
            {
                if (m.Index.TryGetValue(pack.Canonical(dc.Id), out var idx)) s.Add(m, idx);
                else s.DeckSize++;
            }

            // Role supply exactly as the engine computes it (upgrades ×1.2, cards outside the pool included).
            var profile = DeckAnalyzer.AnalyzeDeck(state, pack);
            for (var k = 0; k < m.RoleCount; k++) s.Supply[k] = profile.RoleSupply.GetValueOrDefault(Roles.All[k]);
            s.DeckSize = state.Deck.Count;

            // Key relics owned now add their weight once (relics gained later are not simulated).
            var relics = state.Relics.Select(pack.Canonical).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < m.Archetypes.Length; i++)
            {
                var counted = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kr in m.Archetypes[i].Archetype.KeyRelics)
                {
                    var id = pack.Canonical(kr.Id);
                    if (relics.Contains(id) && counted.Add(id)) s.WeightSum[i] += kr.W;
                }
            }
            return s;
        }

        public SimState Clone()
        {
            var c = new SimState
            {
                Copies = new int[Copies.Length],
                WeightSum = new double[WeightSum.Length],
                CompletionOwned = new double[CompletionOwned.Length],
                CoreOwned = new int[CoreOwned.Length],
                Supply = new double[Supply.Length],
            };
            CopyTo(c);
            return c;
        }

        public void CopyTo(SimState t)
        {
            Array.Copy(Copies, t.Copies, Copies.Length);
            Array.Copy(WeightSum, t.WeightSum, WeightSum.Length);
            Array.Copy(CompletionOwned, t.CompletionOwned, CompletionOwned.Length);
            Array.Copy(CoreOwned, t.CoreOwned, CoreOwned.Length);
            Array.Copy(Supply, t.Supply, Supply.Length);
            t.DeckSize = DeckSize;
        }
    }

    /// <summary>Counts per archetype over simulations, turned into probabilities at the end.</summary>
    private sealed class Accumulator(CharacterModel m)
    {
        private readonly int[] _complete = new int[m.Archetypes.Length];
        private readonly int[] _primary = new int[m.Archetypes.Length];
        private readonly int[] _allCore = new int[m.Archetypes.Length];
        private readonly int[,] _stage = new int[m.Archetypes.Length, 6];
        private readonly double[] _commit = new double[m.Archetypes.Length];
        private readonly double[] _completion = new double[m.Archetypes.Length];

        public void Add(CharacterModel model, SimState s)
        {
            var best = -1;
            var bestCommit = 0.0;
            for (var i = 0; i < model.Archetypes.Length; i++)
            {
                var arch = model.Archetypes[i];
                var commit = s.Commit(model, i);
                var pct = s.CompletionPercent(model, i);
                if (commit >= arch.Threshold - Epsilon) _complete[i]++;
                if (arch.CoreTotal > 0 && s.CoreOwned[i] >= arch.CoreTotal) _allCore[i]++;
                _stage[i, ArchetypeDetector.StageFor(pct)]++;
                _commit[i] += commit;
                _completion[i] += pct;
                if (commit > bestCommit + Epsilon)
                {
                    bestCommit = commit;
                    best = i;
                }
            }
            if (best >= 0) _primary[best]++;
        }

        public HorizonOutcome Outcome(int i, int sims)
        {
            var atLeast = new double[6];
            var cumulative = 0;
            for (var k = 5; k >= 0; k--)
            {
                cumulative += _stage[i, k];
                atLeast[k] = (double)cumulative / sims;
            }
            return new HorizonOutcome
            {
                PComplete = (double)_complete[i] / sims,
                PPrimary = (double)_primary[i] / sims,
                PAllCore = (double)_allCore[i] / sims,
                PStageAtLeast = atLeast,
                MeanCommit = _commit[i] / sims,
                MeanCompletionPercent = _completion[i] / sims,
            };
        }
    }
}

/// <summary>A small thread-safe LRU cache of forecast results keyed by <see cref="ArchetypeForecaster.KeyOf"/>.</summary>
public sealed class ForecastCache(int capacity)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<(string Key, ForecastResult Value)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ForecastResult Value)>> _map = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate) return _map.Count;
        }
    }

    public bool TryGet(string key, out ForecastResult value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = ForecastResult.Empty;
        return false;
    }

    public void Add(string key, ForecastResult value)
    {
        if (capacity <= 0) return;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing)) _order.Remove(existing);
            var node = _order.AddFirst((key, value));
            _map[key] = node;
            while (_map.Count > capacity && _order.Last is { } last)
            {
                _map.Remove(last.Value.Key);
                _order.RemoveLast();
            }
        }
    }
}
