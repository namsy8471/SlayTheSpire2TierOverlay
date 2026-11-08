using System.Globalization;
using System.Text.Json;
using Sts2Overlay.Core.Events;

namespace Sts2Overlay.Core.Combat;

/// <summary>
/// A number in the combat data: either a constant or a small formula over the simulator's state
/// (e.g. <c>"6+2*strikesInDeck"</c>, <c>"X"</c>). Formulas use <see cref="EventFormula"/>'s grammar.
/// </summary>
public readonly record struct CombatAmount(double Constant, string? Formula)
{
    public static readonly CombatAmount Zero = new(0, null);
    public static readonly CombatAmount One = new(1, null);
    public static readonly CombatAmount All = new(double.PositiveInfinity, null);

    public bool IsConstant => Formula is null;
    public bool IsAll => Formula is null && double.IsPositiveInfinity(Constant);

    /// <summary>The value for the given state variables; an unknown variable or a parse error gives 0.</summary>
    public double Evaluate(IReadOnlyDictionary<string, double> vars)
        => Formula is null ? Constant : EventFormula.TryEvaluate(Formula, vars, out var v) ? v : 0;

    public static CombatAmount Parse(JsonElement? el, CombatAmount fallback)
    {
        if (el is not { } e) return fallback;
        return e.ValueKind switch
        {
            JsonValueKind.Number => new(e.GetDouble(), null),
            JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => new(d, null),
            JsonValueKind.String when e.GetString() is "all" => All,
            JsonValueKind.String => new(0, e.GetString()),
            _ => fallback,
        };
    }

    public override string ToString() => Formula ?? Constant.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One effect of a card, relic or potion in the 3-turn damage simulator's vocabulary
/// (the spec lives in <c>data/packs/&lt;ver&gt;/combat/README.md</c>).
/// </summary>
public sealed record CombatOp
{
    public required string Op { get; init; }

    /// <summary>Damage per hit (<c>attack</c>, <c>ostyAttack</c>).</summary>
    public CombatAmount Dmg { get; init; } = CombatAmount.Zero;

    public CombatAmount Hits { get; init; } = CombatAmount.One;

    /// <summary>The amount of every other op (cards drawn, energy, strength, poison, …).</summary>
    public CombatAmount N { get; init; } = CombatAmount.Zero;

    /// <summary>How many times strength counts per hit (Heavy-Blade-like cards); 1 normally.</summary>
    public double StrMult { get; init; } = 1;

    public string? Card { get; init; }
    public bool Upgraded { get; init; }
    public bool Free { get; init; }
    public bool Exhaust { get; init; }
    public bool ExcludeTurnDraw { get; init; }

    /// <summary>
    /// A relic's turn-start <c>draw</c> that changes the hand draw itself (<c>ModifyHandDraw</c>: Ring of the Snake, Bag of
    /// Preparation…) rather than drawing more cards after it.
    /// </summary>
    public bool HandDraw { get; init; }
    public string? To { get; init; }
    public string? Orb { get; init; }
    public string? Filter { get; init; }
    public string? When { get; init; }
    public string? Power { get; init; }
    public string? From { get; init; }
    public string? Duration { get; init; }
    public string? Pick { get; init; }
    public int? LimitPerTurn { get; init; }

    /// <summary><c>addCard</c>: a random card instead of <see cref="Card"/> (<c>class|attack</c>, <c>colorless|any</c>, …).</summary>
    public string? Random { get; init; }

    /// <summary>Pick the best of this many random options (<c>addCard</c> random, <c>fetch</c>).</summary>
    public int? Choose { get; init; }

    /// <summary><c>addCard</c>: the copies cost this much for the rest of combat.</summary>
    public int? SetCost { get; init; }

    /// <summary>
    /// <c>evoke</c>: evoke the same orb this many times (0 = nothing: Multi-Cast with X = 0); <c>playFromHand</c>: play the
    /// card this many times. A formula like <c>"X+1"</c> is allowed.
    /// </summary>
    public CombatAmount Times { get; init; } = CombatAmount.One;

    /// <summary><c>triggerOrbPassives</c>: front | all | lightning | dark | frost | glass.</summary>
    public string? Which { get; init; }

    /// <summary><c>costMod</c>: only ever lowers a cost.</summary>
    public bool LowerOnly { get; init; }

    /// <summary>Triggers: the <see cref="Every"/> counter restarts each turn.</summary>
    public bool ResetEachTurn { get; init; }

    /// <summary><c>costMod</c>: the cost is set to this value; otherwise <see cref="Delta"/> is added.</summary>
    public double? Set { get; init; }
    public double Delta { get; init; }

    /// <summary><c>costMod</c>: how many matching cards it applies to (next / eachTurn).</summary>
    public int? Count { get; init; }

    /// <summary>Triggers: every Nth match.</summary>
    public int? Every { get; init; }

    /// <summary>Nested ops of triggers / <c>perTurn</c> / <c>nextTurn</c>.</summary>
    public IReadOnlyList<CombatOp> Effects { get; init; } = [];

    public static IReadOnlyList<CombatOp> ParseList(JsonElement? el)
    {
        if (el is not { ValueKind: JsonValueKind.Array } arr) return [];
        var list = new List<CombatOp>();
        foreach (var item in arr.EnumerateArray())
        {
            if (Parse(item) is { } op) list.Add(op);
        }
        return list;
    }

    public static CombatOp? Parse(JsonElement e, string? defaultOp = null)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var op = Str(e, "op") ?? defaultOp;
        if (op is null) return null;
        return new CombatOp
        {
            Op = op,
            Dmg = CombatAmount.Parse(Get(e, "dmg"), CombatAmount.Zero),
            Hits = CombatAmount.Parse(Get(e, "hits"), CombatAmount.One),
            N = CombatAmount.Parse(Get(e, "n"), CombatAmount.Zero),
            StrMult = Get(e, "strMult") is { ValueKind: JsonValueKind.Number } sm ? sm.GetDouble() : 1,
            Card = Str(e, "card"),
            Upgraded = Get(e, "upgraded") is { ValueKind: JsonValueKind.True },
            Free = Get(e, "free") is { ValueKind: JsonValueKind.True },
            Exhaust = Get(e, "exhaust") is { ValueKind: JsonValueKind.True },
            ExcludeTurnDraw = Get(e, "excludeTurnDraw") is { ValueKind: JsonValueKind.True },
            HandDraw = Get(e, "handDraw") is { ValueKind: JsonValueKind.True },
            To = Str(e, "to"),
            Orb = Str(e, "orb"),
            Filter = Str(e, "filter"),
            When = Str(e, "when"),
            Power = Str(e, "power"),
            From = Str(e, "from"),
            Duration = Str(e, "duration"),
            Pick = Str(e, "pick"),
            LimitPerTurn = Int(e, "limitPerTurn"),
            Random = Str(e, "random"),
            Choose = Int(e, "choose"),
            SetCost = Int(e, "setCost"),
            Times = CombatAmount.Parse(Get(e, "times"), CombatAmount.One),
            Which = Str(e, "which"),
            LowerOnly = Get(e, "lowerOnly") is { ValueKind: JsonValueKind.True },
            ResetEachTurn = Get(e, "resetEachTurn") is { ValueKind: JsonValueKind.True },
            Set = Get(e, "set") is { ValueKind: JsonValueKind.Number } s ? s.GetDouble() : null,
            Delta = Get(e, "delta") is { ValueKind: JsonValueKind.Number } d ? d.GetDouble() : 0,
            Count = Int(e, "count"),
            Every = Int(e, "every"),
            Effects = ParseList(Get(e, "effects")),
        };
    }

    private static int? Int(JsonElement e, string name)
        => Get(e, name) is { ValueKind: JsonValueKind.Number } v ? (int)v.GetDouble() : null;

    internal static JsonElement? Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v : null;

    internal static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Op names the simulator understands.</summary>
public static class CombatOps
{
    public const string Attack = "attack";
    public const string Damage = "damage";
    public const string Draw = "draw";
    public const string Energy = "energy";
    public const string Strength = "strength";
    public const string TempStrength = "tempStrength";
    public const string LoseStrength = "loseStrength";
    public const string Vulnerable = "vulnerable";
    public const string Weak = "weak";
    public const string Poison = "poison";
    public const string Doom = "doom";
    public const string Accelerant = "accelerant";
    public const string AddCard = "addCard";
    public const string DoomCheck = "doomCheck";
    public const string AutoPlayTop = "autoPlayTop";
    public const string PlayFromDiscard = "playFromDiscard";
    public const string UpgradeCards = "upgradeCards";
    public const string Fetch = "fetch";
    public const string ReturnFromDiscard = "returnFromDiscard";
    public const string ReturnNextTurn = "returnNextTurn";
    public const string RetainHand = "retainHand";
    public const string RedrawHand = "redrawHand";
    public const string NextToDrawTop = "nextToDrawTop";
    public const string CopyInHand = "copyInHand";
    public const string SelfWeak = "selfWeak";
    public const string TempFocus = "tempFocus";
    public const string TriggerOrbPassives = "triggerOrbPassives";
    public const string KillOsty = "killOsty";
    public const string PerTurn = "perTurn";
    public const string NextTurn = "nextTurn";
    public const string ReplayNext = "replayNext";
    public const string Vigor = "vigor";
    public const string AttackBonus = "attackBonus";
    public const string DamageMultiplier = "damageMultiplier";
    public const string VulnerableBonus = "vulnerableBonus";
    public const string CostMod = "costMod";
    public const string PlayTop = "playTop";
    public const string PlayFromHand = "playFromHand";
    public const string PlayDrawn = "playDrawn";
    public const string UpgradeInHand = "upgradeInHand";
    public const string TransformInHand = "transformInHand";
    public const string NoDraw = "noDraw";
    public const string NoEnergyGain = "noEnergyGain";
    public const string LoseHp = "loseHp";
    public const string GainBlock = "gainBlock";
    public const string Channel = "channel";
    public const string Evoke = "evoke";
    public const string Focus = "focus";
    public const string OrbSlots = "orbSlots";
    public const string Stars = "stars";
    public const string Forge = "forge";
    public const string Summon = "summon";
    public const string OstyAttack = "ostyAttack";
    public const string ExhaustCards = "exhaustCards";
    public const string DiscardCards = "discardCards";
    public const string GainPower = "gainPower";

    // Triggers (persistent, like powers).
    public const string OnPlay = "onPlay";
    public const string OnDraw = "onDraw";
    public const string OnExhaust = "onExhaust";
    public const string OnApply = "onApply";
    public const string OnHit = "onHit";
    public const string OnLoseHp = "onLoseHp";
    public const string OnGainBlock = "onGainBlock";
    public const string OnOstyLoseHp = "onOstyLoseHp";
    public const string OnStatusCreated = "onStatusCreated";
    public const string OnEvoke = "onEvoke";
    public const string OnChannel = "onChannel";
    public const string OnShuffle = "onShuffle";
    public const string OnDiscard = "onDiscard";
    public const string OnHandEmpty = "onHandEmpty";
    public const string OnStarsSpent = "onStarsSpent";
    public const string OnPotionUsed = "onPotionUsed";

    public static bool IsTrigger(string op) => op is OnPlay or OnDraw or OnExhaust or OnApply or OnHit or OnLoseHp or OnGainBlock
        or OnOstyLoseHp or OnStatusCreated or OnEvoke or OnChannel or OnShuffle or OnDiscard or OnHandEmpty or OnStarsSpent or OnPotionUsed;
}
