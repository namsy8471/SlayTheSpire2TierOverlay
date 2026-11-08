using Mono.Cecil;
using Mono.Cecil.Cil;
using Sts2Overlay.Resources.Catalog;

namespace CatalogBuilder.CardVars;

// ---------------------------------------------------------------------------------------------
// Reads every card's description variables ("DynamicVars") out of sts2.dll, statically.
//
// In the game a card's text is a template, e.g. "Deal {Damage:diff()} damage.", and the numbers come
// from the card class:
//     protected override IEnumerable<DynamicVar> CanonicalVars => new DamageVar(6m, ValueProp.Move);
//     protected override void OnUpgrade() { DynamicVars.Damage.UpgradeValueBy(2m); }
// The template name of a var is DynamicVar.Name. DamageVar passes "Damage" to the DynamicVar base
// constructor, PowerVar<StrengthPower> passes typeof(T).Name, `new DynamicVar("Gold", 20m)` passes
// "Gold". We don't hard-code those: we run each var class's constructor on IlMachine until it calls
// DynamicVar::.ctor(string name, decimal baseValue) and read both arguments there.
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
    private readonly ModuleDefinition _module;
    private readonly TypeDefinition _cardModel;
    private readonly TypeDefinition _dynamicVar;
    private readonly TypeDefinition _dynamicVarSet;
    private readonly TypeDefinition _calculatedVar;
    private readonly EnumTable _keywords;

    public CardVarsExtractor(TypeDefinition cardModel)
    {
        _cardModel = cardModel;
        _module = cardModel.Module;
        var types = _module.GetTypes().ToDictionary(t => t.FullName);
        const string ns = "MegaCrit.Sts2.Core.Localization.DynamicVars.";
        _dynamicVar = types[ns + "DynamicVar"];
        _dynamicVarSet = types[ns + "DynamicVarSet"];
        _calculatedVar = types[ns + "CalculatedVar"];
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
        _currentCard = card;

        // 1) CanonicalVars getter (card class or an intermediate base; CardModel's own returns Array.Empty).
        List<VarObj> built = new();
        if (FindInChain(card, "get_CanonicalVars") is { } getter)
        {
            MachineResult r = IlMachine.Run(getter, [ThisRef.Value], VarConstructionHook);
            if (!r.Ok) warnings.Add($"{id}: CanonicalVars not evaluated ({r.Failure})");
            if (r.AmbiguousBranches > 0) warnings.Add($"{id}: CanonicalVars has {r.AmbiguousBranches} data-dependent branch(es)");
            switch (r.ReturnValue)
            {
                case Collection c:
                    foreach (object? item in c.Items)
                        if (item is VarObj v) built.Add(v);
                        else warnings.Add($"{id}: CanonicalVars item is not a var ({item})");
                    break;
                case VarObj single: built.Add(single); break;
                default:
                    if (r.Ok) warnings.Add($"{id}: CanonicalVars returned {r.ReturnValue ?? "null"}");
                    break;
            }
        }

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
        if (FindInChain(card, "OnUpgrade") is { } onUpgrade)
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

    /// <summary>Nearest definition of a method in the card or its bases, stopping before CardModel.</summary>
    private MethodDefinition? FindInChain(TypeDefinition card, string name)
    {
        for (TypeDefinition? t = card; t != null && t.FullName != _cardModel.FullName; t = t.BaseType?.Resolve())
            if (t.Methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == 0 && m.HasBody) is { } m)
                return m;
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // Building DynamicVar objects
    // ------------------------------------------------------------------------------------------

    /// <summary>A DynamicVar being built by CanonicalVars.</summary>
    private sealed class VarObj(TypeReference type)
    {
        public TypeReference Type { get; } = type;
        public string? Name { get; set; }
        public decimal? Value { get; set; }
        public string? SavedProperty { get; set; }
        public bool IsCalculated { get; set; }
        public string? BaseVarName { get; set; }
        public string? ExtraVarName { get; set; }
    }

    private bool IsDynamicVarType(TypeReference t)
    {
        for (TypeDefinition? d = t.Resolve(); d != null; d = d.BaseType?.Resolve())
            if (d.FullName == _dynamicVar.FullName) return true;
        return false;
    }

    private bool IsCalculatedVarType(TypeDefinition t)
    {
        for (TypeDefinition? d = t; d != null; d = d.BaseType?.Resolve())
            if (d.FullName == _calculatedVar.FullName) return true;
        return false;
    }

    private HookAction VarConstructionHook(MethodReference method, object?[] args, bool isNewobj, out object? result)
    {
        result = null;
        if (isNewobj && IsDynamicVarType(method.DeclaringType))
        {
            var v = new VarObj(method.DeclaringType);
            RunVarCtor(method, v, args);
            if (method.DeclaringType.Resolve() is { } def && IsCalculatedVarType(def))
            {
                v.IsCalculated = true;
                v.BaseVarName = VarSetGetterCalledBy(def, "GetBaseVar");
                v.ExtraVarName = VarSetGetterCalledBy(def, "GetExtraVar");
            }
            result = v;
            return HookAction.Handled;
        }
        // A card property read in CanonicalVars (GeneticAlgorithm's CurrentBlock): use its field's initial value.
        if (!isNewobj && args.Length == 1 && args[0] is ThisRef && method.Name.StartsWith("get_", StringComparison.Ordinal)
            && _currentCard != null && BackingFieldInitialValue(_currentCard, method) is { } initial)
        {
            result = new PropertyValue(initial, method.Name["get_".Length..]);
            return HookAction.Handled;
        }
        // Fluent setters on a var (WithMultiplier, FromOsty) return the var itself.
        if (!isNewobj && args.Length > 0 && args[0] is VarObj receiver && method.ReturnType.MetadataType != MetadataType.Void
            && IsDynamicVarType(method.ReturnType))
        {
            result = receiver;
            return HookAction.Handled;
        }
        return HookAction.NotHandled;
    }

    /// <summary>
    /// Runs a var constructor with the given arguments until it reaches DynamicVar::.ctor(string, decimal),
    /// following `: base(...)` and `: this(...)` chains.
    /// </summary>
    private void RunVarCtor(MethodReference ctorRef, VarObj v, object?[] args)
    {
        if (ctorRef.DeclaringType.FullName == _dynamicVar.FullName)
        {
            v.Name = args.Length > 0 ? args[0] as string : null;
            v.Value = args.Length > 1 ? IlMachine.ToDecimal(args[1]) : null;
            if (args.Length > 1 && args[1] is PropertyValue p) v.SavedProperty = p.Property;
            return;
        }
        MethodDefinition? ctor = ctorRef.Resolve();
        if (ctor == null) return;
        IList<TypeReference>? generic = (ctorRef.DeclaringType as GenericInstanceType)?.GenericArguments;
        var ctorArgs = new object?[args.Length + 1];
        ctorArgs[0] = v;
        Array.Copy(args, 0, ctorArgs, 1, args.Length);

        IlMachine.Run(ctor, ctorArgs, (MethodReference m, object?[] a, bool isNew, out object? res) =>
        {
            res = null;
            if (!isNew && m.Name == ".ctor" && a.Length > 0 && ReferenceEquals(a[0], v))
            {
                RunVarCtor(m, v, a[1..]);
                return HookAction.Stop;
            }
            return VarConstructionHook(m, a, isNew, out res);
        }, generic);
    }

    /// <summary>The var name a DynamicVarSet getter returns, e.g. get_Damage -> "Damage" (read from its ldstr).</summary>
    private static string? VarSetGetterName(MethodReference getter) =>
        getter.Resolve()?.Body?.Instructions.FirstOrDefault(i => i.OpCode.Code == Code.Ldstr)?.Operand as string;

    /// <summary>For CalculatedVar.GetBaseVar/GetExtraVar: which DynamicVarSet getter the nearest override calls.</summary>
    private string? VarSetGetterCalledBy(TypeDefinition varType, string method)
    {
        for (TypeDefinition? t = varType; t != null && t.FullName != _dynamicVar.FullName; t = t.BaseType?.Resolve())
        {
            if (t.Methods.FirstOrDefault(m => m.Name == method && m.HasBody) is not { } md) continue;
            foreach (Instruction ins in md.Body.Instructions)
                if (ins.Operand is MethodReference mr && mr.DeclaringType.FullName == _dynamicVarSet.FullName && mr.Name.StartsWith("get_"))
                    return VarSetGetterName(mr);
            return null;
        }
        return null;
    }

    private TypeDefinition? _currentCard;

    /// <summary>
    /// For a getter of the form <c>get { return _field; }</c> declared on the card, the constant the card's
    /// constructor stores in <c>_field</c> (a field initializer: <c>ldarg.0; ldc.i4 N; stfld _field</c>).
    /// </summary>
    private static decimal? BackingFieldInitialValue(TypeDefinition card, MethodReference getterRef)
    {
        MethodDefinition? getter = getterRef.Resolve();
        if (getter?.HasBody != true || getter.DeclaringType.FullName != card.FullName) return null;
        var body = getter.Body.Instructions.Where(i => i.OpCode.Code != Code.Nop).ToList();
        if (body.Count != 3 || body[0].OpCode.Code != Code.Ldarg_0 || body[1].OpCode.Code != Code.Ldfld || body[2].OpCode.Code != Code.Ret)
            return null;
        var field = (FieldReference)body[1].Operand;
        MethodDefinition? ctor = card.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        if (ctor == null) return null;
        decimal? value = 0m; // a field nobody initializes starts at 0
        var ins = ctor.Body.Instructions;
        for (int i = 2; i < ins.Count; i++)
            if (ins[i].OpCode.Code == Code.Stfld && ins[i].Operand is FieldReference f && f.FullName == field.FullName)
                value = ins[i - 2].OpCode.Code == Code.Ldarg_0 ? IntConstant(ins[i - 1]) : null;
        return value;
    }

    private static int? IntConstant(Instruction ins) => ins.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => -1, Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3,
        Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
        Code.Ldc_I4_S => (sbyte)ins.Operand,
        Code.Ldc_I4 => (int)ins.Operand,
        _ => null,
    };

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
                case var g when g.StartsWith("get_") && self is VarSetRef && m.DeclaringType.FullName == _dynamicVarSet.FullName:
                    res = new VarRef(VarSetGetterName(m)); return HookAction.Handled;
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
        if (FindInChainWithParams(card, "AddExtraArgsToDescription") is not { } m) yield break;
        foreach (string name in LocStringAddNames(m)) yield return name;
        foreach (Instruction ins in m.Body.Instructions)
            if (ins.Operand is GenericInstanceMethod { Name: "GetValues" } gim && gim.DeclaringType.FullName == "System.Enum"
                && gim.GenericArguments[0].Resolve() is { IsEnum: true } e)
                foreach (FieldDefinition f in e.Fields.Where(f => f.IsStatic && f.HasConstant))
                    yield return f.Name;
    }

    private MethodDefinition? FindInChainWithParams(TypeDefinition card, string name)
    {
        for (TypeDefinition? t = card; t != null && t.FullName != _cardModel.FullName; t = t.BaseType?.Resolve())
            if (t.Methods.FirstOrDefault(m => m.Name == name && m.HasBody) is { } m) return m;
        return null;
    }

    /// <summary>
    /// The first string literal before each <c>LocString.Add(name, value)</c> call, and the default name of each
    /// DynamicVar passed to <c>LocString.Add(DynamicVar)</c>. A linear scan, so branches don't matter.
    /// </summary>
    private List<string> LocStringAddNames(MethodDefinition m)
    {
        var names = new List<string>();
        string? firstString = null;
        string? pendingVarName = null;
        foreach (Instruction ins in m.Body.Instructions)
        {
            if (ins.OpCode.Code == Code.Ldstr) firstString ??= (string)ins.Operand;
            if (ins.OpCode.Code == Code.Newobj && ins.Operand is MethodReference ctor && IsDynamicVarType(ctor.DeclaringType))
            {
                var v = new VarObj(ctor.DeclaringType);
                RunVarCtor(ctor, v, ctor.Parameters.Select(_ => (object?)Unknown.Value).ToArray());
                pendingVarName = v.Name;
            }
            if (ins.Operand is MethodReference { Name: "GetFormattedText" } fmt && fmt.DeclaringType.Name == "LocString")
                break; // later Add calls fill other LocStrings (e.g. the REPLAY line), not the description
            if (ins.Operand is MethodReference call && call.DeclaringType.Name == "LocString" && call.Name is "Add" or "AddObj")
            {
                string? name = call.Parameters.Count == 1 ? pendingVarName : firstString;
                if (name != null && !names.Contains(name)) names.Add(name);
                firstString = null;
                pendingVarName = null;
            }
        }
        return names;
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
        return LocStringAddNames(m);
    }
}
