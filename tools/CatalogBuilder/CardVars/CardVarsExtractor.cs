using Mono.Cecil;
using Mono.Cecil.Cil;
using Sts2Overlay.Resources.Catalog;

namespace CatalogBuilder.CardVars;

// ---------------------------------------------------------------------------------------------
// Reads every card's description variables ("DynamicVars") out of sts2.dll, statically.
//
// In the game a card's text is a template, e.g. "Deal {Damage:diff()} damage.", and the numbers come
// from the card class (CanonicalVars, read by DynamicVarReader) and OnUpgrade:
//     protected override void OnUpgrade() { DynamicVars.Damage.UpgradeValueBy(2m); }
//
// CardModel.UpgradeInternal() = CurrentUpgradeLevel++; OnUpgrade(); DynamicVars.RecalculateForUpgradeOrEnchant().
// The last step copies CalculationBase into every CalculatedVar, so a calculated var's displayed value
// and upgrade follow its base var (out of combat the runtime multiplier is 0).
// ---------------------------------------------------------------------------------------------

/// <summary>One card in card-vars.json.</summary>
internal sealed record CardVarsRow(
    string Id,
    Dictionary<string, decimal> Vars,
    CardUpgradeRow Upgrade,
    Dictionary<string, CalculatedVarRow>? Calculated,
    Dictionary<string, string>? SavedProperties,
    List<string> Unresolved);

/// <summary>What one upgrade (OnUpgrade) changes. Vars are deltas added to the base values.</summary>
internal sealed record CardUpgradeRow(
    Dictionary<string, decimal> Vars,
    int CostDelta,
    List<string> AddKeywords,
    List<string> RemoveKeywords,
    int? StarCostDelta,
    List<string>? Other);

/// <summary>
/// A CalculatedVar (e.g. CalculatedDamage): value = vars[Base] + vars[Extra] × (runtime multiplier, 0 out of combat).
/// </summary>
internal sealed record CalculatedVarRow(string Base, string Extra);

internal sealed record CardVarsFile(
    string GameVersion,
    string Source,
    string Note,
    Dictionary<string, string> ContextVars,
    List<CardVarsRow> Cards);

internal sealed class CardVarsExtractor
{
    private readonly TypeDefinition _cardModel;
    private readonly DynamicVarReader _vars;
    private readonly EnumTable _keywords;

    public CardVarsExtractor(TypeDefinition cardModel)
    {
        _cardModel = cardModel;
        _vars = new DynamicVarReader(cardModel);
        MethodDefinition addKeyword = cardModel.Methods.First(m => m.Name == "AddKeyword" && m.Parameters.Count == 1);
        _keywords = EnumTable.From(addKeyword.Parameters[0].ParameterType);
    }

    // ------------------------------------------------------------------------------------------
    // Card -> row
    // ------------------------------------------------------------------------------------------

    public CardVarsRow Extract(TypeDefinition card, string id, int? baseCost, List<string> warnings)
    {
        var unresolved = new List<string>();
        var vars = new Dictionary<string, decimal>();
        var calculated = new Dictionary<string, CalculatedVarRow>();
        var saved = new Dictionary<string, string>();

        // 1) CanonicalVars getter (card class or an intermediate base; CardModel's own returns Array.Empty).
        List<VarObj> built = _vars.ReadCanonicalVars(card, id, warnings);

        foreach (VarObj v in built)
        {
            if (v.Name is null) { warnings.Add($"{id}: var of {v.Type.Name} has no static name"); continue; }
            if (v.Value is { } value) vars[v.Name] = IlMachine.Normalize(value);
            else if (!v.IsCalculated) unresolved.Add(v.Name);
            if (v.SavedProperty != null) saved[v.Name] = v.SavedProperty;
            if (v.IsCalculated) calculated[v.Name] = new CalculatedVarRow(v.BaseVarName ?? "?", v.ExtraVarName ?? "?");
        }

        // 2) OnUpgrade.
        var upgrade = new UpgradeState();
        if (_vars.FindInChain(card, "OnUpgrade") is { } onUpgrade)
            RunUpgrade(onUpgrade, upgrade, id, warnings);

        // 3) CalculatedVars: base value and upgrade follow their base var (RecalculateForUpgradeOrEnchant).
        foreach (var (name, calc) in calculated)
        {
            if (vars.TryGetValue(calc.Base, out decimal b)) vars[name] = b;
            else unresolved.Add(name);
            if (upgrade.VarDeltas.TryGetValue(calc.Base, out decimal d)) upgrade.VarDeltas[name] = d;
        }

        // Absolute sets (BaseValue = x) become deltas against the canonical value.
        foreach (var (name, set) in upgrade.VarSets)
        {
            if (vars.TryGetValue(name, out decimal b)) upgrade.VarDeltas[name] = set - b;
            else upgrade.Other.Add($"set {name}");
        }
        int costDelta = upgrade.CostDelta;
        if (upgrade.CostSet is int newCost)
        {
            if (baseCost is int c) costDelta = newCost - c;
            else upgrade.Other.Add("SetCustomBaseCost");
        }
        foreach (string name in upgrade.VarDeltas.Keys)
            if (!vars.ContainsKey(name) && !unresolved.Contains(name))
                warnings.Add($"{id}: OnUpgrade changes {name}, which is not in CanonicalVars");

        // 4) Values the card adds to its description itself (AddExtraArgsToDescription override): runtime only.
        foreach (string extra in ExtraDescriptionArgs(card))
            if (!vars.ContainsKey(extra) && !unresolved.Contains(extra)) unresolved.Add(extra);

        return new CardVarsRow(
            id,
            vars,
            new CardUpgradeRow(
                upgrade.VarDeltas.ToDictionary(kv => kv.Key, kv => IlMachine.Normalize(kv.Value)),
                costDelta,
                upgrade.AddKeywords,
                upgrade.RemoveKeywords,
                upgrade.StarCostDelta == 0 ? null : upgrade.StarCostDelta,
                upgrade.Other.Count == 0 ? null : upgrade.Other),
            calculated.Count == 0 ? null : calculated,
            saved.Count == 0 ? null : saved,
            unresolved);
    }

    // ------------------------------------------------------------------------------------------
    // OnUpgrade
    // ------------------------------------------------------------------------------------------

    private sealed class UpgradeState
    {
        public Dictionary<string, decimal> VarDeltas { get; } = new();
        public Dictionary<string, decimal> VarSets { get; } = new();
        public int CostDelta { get; set; }
        public int? CostSet { get; set; }
        public int StarCostDelta { get; set; }
        public List<string> AddKeywords { get; } = new();
        public List<string> RemoveKeywords { get; } = new();
        public List<string> Other { get; } = new();
    }

    private sealed class VarSetRef { public static readonly VarSetRef Value = new(); }
    private sealed record VarRef(string? Name);
    private sealed class EnergyCostRef { public static readonly EnergyCostRef Value = new(); }

    private void RunUpgrade(MethodDefinition onUpgrade, UpgradeState s, string id, List<string> warnings)
    {
        MachineResult r = IlMachine.Run(onUpgrade, [ThisRef.Value], (MethodReference m, object?[] a, bool isNew, out object? res) =>
        {
            res = null;
            object? self = a.Length > 0 ? a[0] : null;
            switch (m.Name)
            {
                case "get_DynamicVars" when self is ThisRef:
                    res = VarSetRef.Value; return HookAction.Handled;
                case "get_Item" when self is VarSetRef:
                    res = new VarRef(a[1] as string); return HookAction.Handled;
                case var g when g.StartsWith("get_") && self is VarSetRef && m.DeclaringType.FullName == _vars.DynamicVarSet.FullName:
                    res = new VarRef(DynamicVarReader.VarSetGetterName(m)); return HookAction.Handled;
                case "UpgradeValueBy" when self is VarRef vr:
                    if (vr.Name != null && IlMachine.ToDecimal(a[1]) is { } delta)
                        s.VarDeltas[vr.Name] = s.VarDeltas.GetValueOrDefault(vr.Name) + delta;
                    else warnings.Add($"{id}: OnUpgrade UpgradeValueBy with unknown var/amount");
                    return HookAction.Handled;
                case "set_BaseValue" when self is VarRef vr:
                    if (vr.Name != null && IlMachine.ToDecimal(a[1]) is { } set) s.VarSets[vr.Name] = set;
                    else warnings.Add($"{id}: OnUpgrade sets BaseValue to an unknown value");
                    return HookAction.Handled;
                case "get_EnergyCost" when self is ThisRef:
                    res = EnergyCostRef.Value; return HookAction.Handled;
                case "UpgradeBy" when self is EnergyCostRef:
                    if (a[1] is int add) s.CostDelta += add; else warnings.Add($"{id}: EnergyCost.UpgradeBy(unknown)");
                    return HookAction.Handled;
                case "SetCustomBaseCost" when self is EnergyCostRef:
                    if (a[1] is int cost) s.CostSet = cost; else warnings.Add($"{id}: SetCustomBaseCost(unknown)");
                    return HookAction.Handled;
                case "UpgradeStarCostBy" when self is ThisRef:
                    if (a[1] is int star) s.StarCostDelta += star; else warnings.Add($"{id}: UpgradeStarCostBy(unknown)");
                    return HookAction.Handled;
                case "AddKeyword" when self is ThisRef:
                case "RemoveKeyword" when self is ThisRef:
                {
                    string kw = a[1] is int k ? _keywords.Name(k)! : "?";
                    (m.Name == "AddKeyword" ? s.AddKeywords : s.RemoveKeywords).Add(kw);
                    return HookAction.Handled;
                }
                case "OnUpgrade" when self is ThisRef && !isNew:
                    // base.OnUpgrade() of an intermediate card class; CardModel's own is empty.
                    if (m.DeclaringType.FullName != _cardModel.FullName && m.Resolve() is { } baseUpgrade)
                        RunUpgrade(baseUpgrade, s, id, warnings);
                    return HookAction.Handled;
            }
            if (!m.DeclaringType.Namespace.StartsWith("System", StringComparison.Ordinal)) // decimal ctor etc. are just constants
                s.Other.Add($"{m.DeclaringType.Name}.{m.Name}");
            return HookAction.NotHandled;
        });
        if (!r.Ok) warnings.Add($"{id}: OnUpgrade not evaluated ({r.Failure})");
        if (r.AmbiguousBranches > 0) warnings.Add($"{id}: OnUpgrade has {r.AmbiguousBranches} data-dependent branch(es)");
    }

    // ------------------------------------------------------------------------------------------
    // Description context
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Names a card adds in its own AddExtraArgsToDescription override: every `description.Add("Name", ...)`
    /// string, plus every member of an enum it loops over with Enum.GetValues&lt;T&gt;() (MadScience).
    /// </summary>
    private IEnumerable<string> ExtraDescriptionArgs(TypeDefinition card)
    {
        if (_vars.FindInChainWithParams(card, "AddExtraArgsToDescription") is not { } m) yield break;
        foreach (string name in _vars.LocStringAddNames(m)) yield return name;
        foreach (Instruction ins in m.Body.Instructions)
            if (ins.Operand is GenericInstanceMethod { Name: "GetValues" } gim && gim.DeclaringType.FullName == "System.Enum"
                && gim.GenericArguments[0].Resolve() is { IsEnum: true } e)
                foreach (FieldDefinition f in e.Fields.Where(f => f.IsStatic && f.HasConstant))
                    yield return f.Name;
    }

    /// <summary>
    /// Variables CardModel.GetDescriptionForPile adds to every card description besides the card's own DynamicVars
    /// (IfUpgraded, OnTable, InCombat, energyPrefix, ...), read from the IL so a game update can't silently add one.
    /// </summary>
    public List<string> DescriptionContextVars()
    {
        MethodDefinition m = _cardModel.Methods
            .Where(x => x.Name == "GetDescriptionForPile" && x.HasBody)
            .OrderByDescending(x => x.Parameters.Count)
            .First();
        return _vars.LocStringAddNames(m);
    }
}
