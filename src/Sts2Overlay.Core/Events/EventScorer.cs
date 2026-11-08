using System.Globalization;
using System.Text.Json;
using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Events;

/// <summary>
/// Scores event options from what they do (<see cref="EventCatalog"/>) and the run state:
/// <list type="bullet">
/// <item><b>HP</b> is priced like the path advisor: losing x% of max HP costs <c>HpValue × x × (1 + boost × (1 − hp ratio))</c>,
/// so the same 10 HP hurts more at low HP. Healing is worth only the HP actually missing.</item>
/// <item><b>Deck changes</b> look at the deck: removing/transforming is worth a lot with curses or basic Strikes/Defends,
/// little once they are gone; a specific card is scored by <see cref="CardScorer"/> against skipping it (like the shop).</item>
/// <item><b>Gold</b> has a per-gold value that halves above <see cref="EventWeights.GoldSaturation"/>; an option that costs more
/// gold than the player has is marked unaffordable and never recommended.</item>
/// <item>An option whose HP loss would kill the player costs <see cref="EventWeights.DeathCost"/>.</item>
/// <item>An option that leads to another page adds the best option of that page (looked ahead with the changed HP/gold).</item>
/// <item><b>Measured choices</b> (<see cref="DataPack.EventStats"/>, optional): an option of the shown page that top players
/// chose more (or less) often than at random gets <c>MeasuredBlend × MeasuredScale × (rate − 1/k)</c> points
/// (ancients: <c>1/offered</c>). A relic with a stable measured rate (<see cref="DataPack.RelicStats"/>) is scaled by
/// <see cref="RelicStats.ValueMultiplier"/>.</item>
/// </list>
/// Like card scores, <see cref="ScoreBreakdown.Total"/> is exactly the sum of the shown components.
/// </summary>
public sealed class EventScorer(DataPack pack, EventCatalog catalog, EventWeights? weights = null)
{
    public DataPack Pack { get; } = pack;
    public EventCatalog Catalog { get; } = catalog;

    /// <summary>The given weights, else the pack's (<c>weights.json</c> "events").</summary>
    public EventWeights Weights { get; } = weights ?? pack.Weights.Events;

    /// <summary>
    /// Scores the options of <paramref name="eventId"/>. With <paramref name="offeredKeys"/> (exact text keys from the log) only
    /// those are scored, in that order; otherwise every option of <paramref name="page"/> (default: the first page).
    /// Null when the event is not in the catalog and no keys were given.
    /// <paramref name="possibleKeys"/> limits <see cref="EventEvaluation.NotOffered"/> to options this run could have drawn
    /// (e.g. no co-op-only relic in single player); null = the whole pool.
    /// </summary>
    public EventEvaluation? Evaluate(RunState state, string eventId, IReadOnlyList<string>? offeredKeys = null, string? page = null,
        IReadOnlyCollection<string>? possibleKeys = null)
        => Evaluate(state, DeckAnalyzer.Analyze(state, Pack), eventId, offeredKeys, page, possibleKeys);

    public EventEvaluation? Evaluate(RunState state, DeckProfile profile, string eventId, IReadOnlyList<string>? offeredKeys = null, string? page = null,
        IReadOnlyCollection<string>? possibleKeys = null)
    {
        var def = Catalog.Find(eventId);
        IReadOnlyList<EventOptionDef> options;
        string pageName;
        if (offeredKeys is { Count: > 0 })
        {
            options = offeredKeys.Select(ResolveOption).ToArray();
            pageName = PageOfKey(offeredKeys[0]) ?? page ?? "INITIAL";
            def ??= new EventDef { Id = eventId };
        }
        else
        {
            if (def is null) return null;
            var p = def.FindPage(page) ?? def.InitialPage;
            if (p is null) return null;
            options = EventSlots.Displayed(p); // the page's buttons: no greyed-out faces or lone fallbacks
            pageName = p.Page;
        }

        // Ancients show a few options of their pool; with the offered set unknown, EventStats assumes the usual count.
        int? offeredCount = offeredKeys is { Count: > 0 } ? offeredKeys.Count : null;
        var evals = options.Select(o =>
        {
            var e = WithMeasured(ScoreOption(state, profile, def, o, Weights.LookaheadDepth), state.Act, offeredCount);
            // "<KEY>_LOCKED" is the game's greyed-out variant of an option (e.g. Wongo's items without enough gold).
            return o.Key.EndsWith(LockedSuffix, StringComparison.Ordinal) ? e with { IsUnaffordable = true } : e;
        }).ToList();

        // Rank by total; unaffordable/lethal options can't be best unless nothing else is left.
        var order = Enumerable.Range(0, evals.Count).OrderByDescending(i => evals[i].Total).ThenBy(i => i).ToArray();
        var ranks = new int[evals.Count];
        for (var k = 0; k < order.Length; k++) ranks[order[k]] = k + 1;
        var eligible = order.Where(i => !evals[i].IsUnaffordable && !evals[i].IsLethal).ToList();
        if (eligible.Count == 0) eligible = order.Where(i => !evals[i].IsUnaffordable).ToList();
        int? best = eligible.Count > 0 ? eligible[0] : null;

        RecommendationConfidence confidence;
        if (best is int b && evals[b].HasUnmodelled) confidence = RecommendationConfidence.Estimate;
        else if (eligible.Count < 2) confidence = RecommendationConfidence.Sure;
        else
        {
            var gap = evals[eligible[0]].Total - evals[eligible[1]].Total;
            confidence = gap >= Weights.SureGap ? RecommendationConfidence.Sure
                : gap < Weights.CloseGap ? RecommendationConfidence.Close
                : RecommendationConfidence.Normal;
        }

        return new EventEvaluation
        {
            Event = def!,
            Page = pageName,
            Options = evals.Select((e, i) => e with { Rank = ranks[i], IsBest = best == i }).ToArray(),
            Confidence = confidence,
            OptionsExact = offeredKeys is { Count: > 0 },
            NotOffered = offeredKeys is { Count: > 0 } ? NotOfferedOptions(state, profile, def!, pageName, offeredKeys, possibleKeys) : [],
        };
    }

    /// <summary>
    /// An ancient's other options on the offered page (its pool: the game draws a few of them at random), scored the same
    /// way and ranked among themselves, best first. Regular events show every option they have, so they get none.
    /// </summary>
    private IReadOnlyList<EventOptionEvaluation> NotOfferedOptions(RunState state, DeckProfile profile, EventDef def, string pageName,
        IReadOnlyList<string> offeredKeys, IReadOnlyCollection<string>? possibleKeys)
    {
        if (!def.IsAncient || def.FindPage(pageName) is not { } page) return [];
        var offered = offeredKeys.ToHashSet(StringComparer.Ordinal);
        var rest = EventSlots.Displayed(page)
            .Where(o => !offered.Contains(o.Key) && !o.Key.EndsWith(LockedSuffix, StringComparison.Ordinal))
            .Where(o => possibleKeys is null || possibleKeys.Contains(o.Key))
            .Select(o => WithMeasured(ScoreOption(state, profile, def, o, Weights.LookaheadDepth), state.Act, offeredKeys.Count))
            .OrderByDescending(e => e.Total)
            .ToList();
        return rest.Select((e, k) => e with { Rank = k + 1 }).ToArray();
    }

    /// <summary>
    /// Adds the measured top-player choice rate of an option on the shown page:
    /// <c>MeasuredBlend × MeasuredScale × (rate − neutral)</c>, explained as "상위 플레이어 72% 선택".
    /// Unchanged (the very same evaluation) when the pack has no stats, the option has none, or the blend is 0.
    /// Applied to the shown page only, not to look-ahead pages: the rate says which button top players press here.
    /// </summary>
    private EventOptionEvaluation WithMeasured(EventOptionEvaluation e, int act, int? offeredCount)
    {
        if (Pack.EventStats is not { } stats || !(Weights.MeasuredBlend > 0)) return e;
        if (stats.Find(e.Option.Key, act, offeredCount) is not { } m) return e;
        var value = Weights.MeasuredBlend * Weights.MeasuredScale * (m.Rate - m.Neutral);
        var label = $"상위 플레이어 {m.Rate * 100:0}% 선택";
        var detail = m.FromAct ? $"{act}막 기준, 무작위면 {m.Neutral * 100:0}%" : $"무작위면 {m.Neutral * 100:0}%";
        return e with
        {
            Score = new ScoreBreakdown(e.Score.Components.Append(new ScoreComponent(EventComponentKeys.Measured, label, value, detail))),
            EffectTexts = [.. e.EffectTexts, m.FromAct ? $"{label} ({act}막)" : label],
        };
    }

    private const string LockedSuffix = "_LOCKED";

    /// <summary>The option for a logged key; a <c>_LOCKED</c> key falls back to its unlocked option's effects.</summary>
    private EventOptionDef ResolveOption(string key)
    {
        if (Catalog.FindOption(key) is { } hit) return hit.Option;
        if (key.EndsWith(LockedSuffix, StringComparison.Ordinal) && Catalog.FindOption(key[..^LockedSuffix.Length]) is { } open)
        {
            return new EventOptionDef { Key = key, Effects = open.Option.Effects, Condition = open.Option.Condition };
        }
        return new EventOptionDef { Key = key };
    }

    /// <summary><c>X.pages.INITIAL.options.Y</c> → <c>INITIAL</c>.</summary>
    public static string? PageOfKey(string key)
    {
        const string pages = ".pages.";
        var i = key.IndexOf(pages, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + pages.Length;
        var end = key.IndexOf('.', start);
        return end < 0 ? null : key[start..end];
    }

    private EventOptionEvaluation ScoreOption(RunState state, DeckProfile profile, EventDef def, EventOptionDef option, int depth)
    {
        var acc = new List<ScoreComponent>();
        var texts = new List<string>();
        var sim = new Sim(state.Hp, Math.Max(1, state.MaxHp), state.Gold);
        bool lethal = false, unaffordable = false, unmodelled = false, random = false;
        var vars = Variables(state, def);
        // Deck pool the option works on; removals/transforms take from it so "remove 2" doesn't count the same curse twice.
        var pool = new DeckPool(Pack, state, profile);

        if (option.Effects.Count == 0 && !option.Leave && Catalog.FindOption(option.Key) is null && !option.Key.EndsWith(LockedSuffix, StringComparison.Ordinal))
        {
            unmodelled = true;
            texts.Add("효과 정보 없음");
        }

        foreach (var e in option.Effects)
        {
            var p = Math.Clamp(e.Chance ?? 1.0, 0, 1);
            if (p < 1) random = true;
            if (string.Equals(e.How, "random", StringComparison.OrdinalIgnoreCase)) random = true;
            var chanceText = p < 1 ? $" ({p * 100:0}% 확률)" : "";
            var count = Resolve(e.Count, vars, out var countKnown) is double c ? (int)Math.Max(0, Math.Round(c)) : 1;
            if (!countKnown) unmodelled = true;
            double? amount = Resolve(e.Amount, vars, out var amountKnown);
            if (!amountKnown) unmodelled = true;

            switch (e.Type)
            {
                case EventEffectTypes.HpLoss when amount is double a:
                {
                    if (p >= 1 && a >= sim.Hp) lethal = true;
                    var lost = Math.Min(a, sim.Hp);
                    var v = -HpPoints(lost, sim) * p;
                    acc.Add(new(EventComponentKeys.Hp, $"체력 −{a:0}", v));
                    texts.Add($"체력 {a:0} 잃음{chanceText}");
                    sim = sim with { Hp = sim.Hp - lost * p };
                    break;
                }
                case EventEffectTypes.Heal when amount is double a:
                {
                    var gained = Math.Clamp(a, 0, sim.MaxHp - sim.Hp);
                    var v = HpPoints(gained, sim) * p;
                    acc.Add(new(EventComponentKeys.Hp, $"체력 +{gained:0}", v));
                    texts.Add(a >= sim.MaxHp ? $"체력 모두 회복{chanceText}" : $"체력 {a:0} 회복{chanceText}");
                    sim = sim with { Hp = sim.Hp + gained * p };
                    break;
                }
                case EventEffectTypes.MaxHp when amount is double a:
                {
                    var hpw = HpWeight(sim);
                    var v = Path.HpValue * a / sim.MaxHp * Weights.MaxHpFactorFor(state.Act) * (a < 0 ? hpw : 1) * p;
                    acc.Add(new(EventComponentKeys.MaxHp, $"최대 체력 {a:+0;−0}", v));
                    texts.Add($"최대 체력 {a:+0;−0}{chanceText}");
                    var maxHp = Math.Max(1, sim.MaxHp + a * p);
                    // GainMaxHp heals by the same amount; LoseMaxHp only caps current HP.
                    var hp = a > 0 ? sim.Hp + a * p : Math.Min(sim.Hp, maxHp);
                    if (p >= 1 && a < 0 && sim.MaxHp + a <= 0) lethal = true;
                    sim = sim with { MaxHp = maxHp, Hp = hp };
                    break;
                }
                case EventEffectTypes.Gold when amount is double a:
                {
                    if (a < 0 && -a > sim.Gold + 1e-9 && p >= 1) unaffordable = true;
                    var v = GoldPoints(a, sim.Gold) * p;
                    acc.Add(new(EventComponentKeys.Gold, a >= 0 ? $"골드 +{a:0}" : $"골드 −{-a:0}", v));
                    texts.Add((a >= 0 ? $"골드 {a:0} 획득" : $"골드 {-a:0} 지불") + chanceText);
                    sim = sim with { Gold = Math.Max(0, sim.Gold + a * p) };
                    break;
                }
                case EventEffectTypes.CardRemove:
                {
                    var choose = !string.Equals(e.How, "random", StringComparison.OrdinalIgnoreCase);
                    var v = pool.Remove(count, choose, e.Filter, Weights) * p;
                    acc.Add(new(EventComponentKeys.Deck, choose ? $"카드 {count}장 제거" : $"무작위 카드 {count}장 제거", v, pool.LastDetail));
                    texts.Add((choose ? $"카드 {count}장 제거" : $"무작위 카드 {count}장 제거") + FilterText(e.Filter) + chanceText);
                    break;
                }
                case EventEffectTypes.CardTransform:
                {
                    var choose = !string.Equals(e.How, "random", StringComparison.OrdinalIgnoreCase);
                    var v = pool.Transform(count, choose, e.Filter, Weights) * p;
                    acc.Add(new(EventComponentKeys.Deck, choose ? $"카드 {count}장 변화" : $"무작위 카드 {count}장 변화", v, pool.LastDetail));
                    texts.Add((choose ? $"카드 {count}장 변화" : $"무작위 카드 {count}장 변화") + FilterText(e.Filter) + chanceText);
                    break;
                }
                case EventEffectTypes.CardUpgrade:
                {
                    var how = (e.How ?? "choose").ToLowerInvariant();
                    var upgradable = pool.Upgradable(e.Filter);
                    var n = how == "all" ? upgradable : Math.Min(count, upgradable);
                    var each = how == "choose" ? Weights.UpgradeChosen : Weights.UpgradeRandom;
                    var v = n * each * p;
                    var label = how switch { "all" => "모두 강화", "random" => $"무작위 {count}장 강화", _ => $"카드 {count}장 강화" };
                    acc.Add(new(EventComponentKeys.Upgrade, label, v));
                    texts.Add(label + FilterText(e.Filter) + chanceText);
                    break;
                }
                case EventEffectTypes.CardAdd:
                {
                    var (v, label, detail) = CardAddValue(state, profile, e, count);
                    if (string.Equals(e.How, "random", StringComparison.OrdinalIgnoreCase)) random = true;
                    acc.Add(new(EventComponentKeys.Card, label, v * p, detail));
                    texts.Add(label + chanceText);
                    break;
                }
                case EventEffectTypes.Curse:
                {
                    var n = Math.Max(1, count);
                    var v = -Weights.CursePenalty * n * p;
                    acc.Add(new(EventComponentKeys.Curse, n > 1 ? $"저주 {n}장" : "저주", v, e.Card));
                    texts.Add((n > 1 ? $"저주 {n}장 받음" : "저주 받음") + chanceText);
                    break;
                }
                case EventEffectTypes.CardReward:
                {
                    var rare = string.Equals(e.Rarity, "rare", StringComparison.OrdinalIgnoreCase);
                    var n = Math.Max(1, count);
                    var v = (rare ? Weights.RareCardReward : Weights.CardReward) * n * p;
                    var label = rare ? "희귀 카드 보상" : "카드 보상";
                    acc.Add(new(EventComponentKeys.Card, n > 1 ? $"{label} ×{n}" : label, v));
                    texts.Add((n > 1 ? $"{label} {n}번" : label) + chanceText);
                    break;
                }
                case EventEffectTypes.Enchant:
                {
                    var n = Math.Max(1, count);
                    var v = Weights.EnchantValue * n * p;
                    acc.Add(new(EventComponentKeys.Card, "카드 인챈트", v, e.Enchantment));
                    texts.Add($"카드 {n}장 인챈트" + FilterText(e.Filter) + chanceText);
                    break;
                }
                case EventEffectTypes.Relic:
                {
                    var n = Math.Max(1, count);
                    // Rarity value, scaled by the relic's measured pick rate when relics.json has a stable one.
                    var v = Weights.RelicValue(e.Tier, e.Relic, Pack.RelicStats) * n * p;
                    acc.Add(new(EventComponentKeys.Relic, "유물", v, e.Relic));
                    texts.Add((e.Relic is null ? "무작위 유물" : "유물") + (n > 1 ? $" {n}개" : "") + chanceText);
                    break;
                }
                case EventEffectTypes.RelicLose:
                {
                    var v = state.Relics.Count == 0 ? 0 : -Weights.RelicLoss * p;
                    acc.Add(new(EventComponentKeys.Relic, "유물 잃음", v, e.Relic));
                    texts.Add("유물 잃음" + chanceText);
                    break;
                }
                case EventEffectTypes.Potion:
                {
                    var n = Math.Max(1, count);
                    var v = Weights.PotionValue * n * p;
                    acc.Add(new(EventComponentKeys.Potion, "포션", v, e.Potion));
                    texts.Add($"포션 {n}개" + chanceText);
                    break;
                }
                case EventEffectTypes.PotionLose:
                {
                    var n = Math.Max(1, count);
                    var v = -Weights.PotionValue * n * p;
                    acc.Add(new(EventComponentKeys.Potion, "포션 잃음", v));
                    texts.Add($"포션 {n}개 잃음" + chanceText);
                    break;
                }
                case EventEffectTypes.Fight:
                {
                    var lost = Weights.FightHpLossFraction * sim.MaxHp;
                    var v = (-HpPoints(Math.Min(lost, sim.Hp), sim) + Weights.FightReward) * p;
                    acc.Add(new(EventComponentKeys.Fight, "전투", v, e.Encounter));
                    texts.Add("전투" + chanceText);
                    sim = sim with { Hp = Math.Max(1, sim.Hp - lost * p) };
                    break;
                }
                case EventEffectTypes.Duplicate:
                {
                    var n = Math.Max(1, count);
                    acc.Add(new(EventComponentKeys.Card, "카드 복제", Weights.DuplicateValue * n * p));
                    texts.Add($"카드 {n}장 복제" + chanceText);
                    break;
                }
                default:
                {
                    // "other", or a known type whose amount formula could not be evaluated.
                    unmodelled = true;
                    texts.Add((e.Note ?? e.Type) + chanceText);
                    break;
                }
            }
        }

        if (option.Leave && option.Effects.Count == 0) texts.Add("아무 일도 없음");

        if (lethal) acc.Add(new(EventComponentKeys.Death, "사망 위험", -Weights.DeathCost));

        // Follow-up page: add its best option, scored with the HP/gold this option leaves.
        if (!lethal && depth > 0 && def.FindPage(option.Next) is { Options.Count: > 0 } next)
        {
            var after = state with { Hp = (int)Math.Round(sim.Hp), MaxHp = (int)Math.Round(sim.MaxHp), Gold = (int)Math.Round(sim.Gold) };
            var nextEvals = next.Options.Select(o => ScoreOption(after, profile, def, o, depth - 1))
                .Where(o => !o.IsUnaffordable && !o.IsLethal).ToList();
            if (nextEvals.Count > 0)
            {
                var bestNext = nextEvals.MaxBy(o => o.Total)!;
                acc.Add(new(EventComponentKeys.Next, "다음 선택", bestNext.Total, bestNext.Option.Key));
                unmodelled |= bestNext.HasUnmodelled;
                texts.Add("이어서 선택지");
            }
        }

        return new EventOptionEvaluation
        {
            Option = option,
            Score = new ScoreBreakdown(acc),
            IsLethal = lethal,
            IsUnaffordable = unaffordable,
            HasUnmodelled = unmodelled,
            IsRandom = random,
            EffectTexts = texts,
        };
    }

    private Map.PathWeights Path => Pack.Weights.Path;

    private double HpWeight(Sim s) => 1 + Path.LowHpValueBoost * (1 - Math.Clamp(s.Hp / s.MaxHp, 0, 1));

    /// <summary>Points of <paramref name="hp"/> HP at the current HP ratio.</summary>
    private double HpPoints(double hp, Sim s) => Path.HpValue * hp / s.MaxHp * HpWeight(s);

    private double GoldPoints(double amount, double gold)
    {
        if (amount < 0) return Weights.GoldValue * amount;
        var full = Math.Max(0, Math.Min(amount, Weights.GoldSaturation - gold));
        return Weights.GoldValue * (full + 0.5 * (amount - full));
    }

    private (double Value, string Label, string? Detail) CardAddValue(RunState state, DeckProfile profile, EventEffect e, int count)
    {
        var n = Math.Max(1, count);
        if (e.Card is { } id)
        {
            if (DeckAnalyzer.IsCurse(Pack, state.Character, id) || string.Equals(e.Pool, "curse", StringComparison.OrdinalIgnoreCase))
            {
                return (-Weights.CursePenalty * n, "저주 카드", id);
            }
            var eval = new CardScorer(Pack).Score(state, profile, new CardRef(id), OfferContext.Event);
            var skip = new SkipScorer(Pack).Score(state, profile).Total;
            return ((eval.Total - skip) * n, n > 1 ? $"카드 추가 ×{n}" : "카드 추가", id);
        }
        if (string.Equals(e.Pool, "curse", StringComparison.OrdinalIgnoreCase))
        {
            return (-Weights.CursePenalty * n, "저주 카드", null);
        }
        if (string.Equals(e.How, "choose", StringComparison.OrdinalIgnoreCase))
        {
            return (Weights.CardReward * n, "카드 선택 추가", null);
        }
        var bonus = string.Equals(e.Rarity, "rare", StringComparison.OrdinalIgnoreCase) ? Weights.RareCardReward - Weights.CardReward : 0;
        return ((Weights.RandomCardAdd + bonus) * n, n > 1 ? $"무작위 카드 {n}장" : "무작위 카드", null);
    }

    private static string FilterText(string? filter) => string.IsNullOrWhiteSpace(filter) ? "" : $" ({filter})";

    private static Dictionary<string, double> Variables(RunState state, EventDef def)
    {
        var v = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["hp"] = state.Hp,
            ["currentHp"] = state.Hp,
            ["maxHp"] = state.MaxHp,
            ["gold"] = state.Gold,
            ["act"] = state.Act,
            ["floor"] = state.Floor,
            ["ascension"] = state.Ascension,
            ["deckSize"] = state.Deck.Count,
            ["basicStrikes"] = state.Deck.Count(d => CardIdHeuristics.IsBasicStrike(d.Id)),
            ["basicDefends"] = state.Deck.Count(d => CardIdHeuristics.IsBasicDefend(d.Id)),
        };
        // Event vars win over run-state names only when they don't collide (an event "MaxHp" var means "max HP gained").
        foreach (var (k, val) in def.Vars)
        {
            v.TryAdd(k, val);
            v[$"var.{k}"] = val;
        }
        return v;
    }

    private static double? Resolve(JsonElement? element, IReadOnlyDictionary<string, double> vars, out bool known)
    {
        known = true;
        if (element is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString() ?? "";
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            if (EventFormula.TryEvaluate(s, vars, out var f)) return f;
        }
        known = false;
        return null;
    }

    private readonly record struct Sim(double Hp, double MaxHp, double Gold);
}

/// <summary>Stable component keys of event option scores.</summary>
public static class EventComponentKeys
{
    public const string Hp = "ev_hp";
    public const string MaxHp = "ev_max_hp";
    public const string Gold = "ev_gold";
    public const string Deck = "ev_deck";
    public const string Upgrade = "ev_upgrade";
    public const string Card = "ev_card";
    public const string Curse = "ev_curse";
    public const string Relic = "ev_relic";
    public const string Potion = "ev_potion";
    public const string Fight = "ev_fight";
    public const string Death = "ev_death";
    public const string Next = "ev_next";

    /// <summary>The measured top-player choice rate (<see cref="Data.EventStats"/>).</summary>
    public const string Measured = "ev_measured";
}
