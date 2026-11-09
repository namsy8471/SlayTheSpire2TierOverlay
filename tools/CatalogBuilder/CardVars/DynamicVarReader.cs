using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CatalogBuilder.CardVars;

// ---------------------------------------------------------------------------------------------
// The DynamicVar machinery shared by --card-vars and --relic-vars.
//
// Cards (CardModel) and relics (RelicModel) both declare their description numbers the same way:
//     protected override IEnumerable<DynamicVar> CanonicalVars => new DamageVar(6m, ValueProp.Move);
// The template name of a var is DynamicVar.Name. DamageVar passes "Damage" to the DynamicVar base
// constructor, PowerVar<StrengthPower> passes typeof(T).Name, `new DynamicVar("Gold", 20m)` passes
// "Gold". We don't hard-code those: we run each var class's constructor on IlMachine until it calls
// DynamicVar::.ctor(string name, decimal baseValue) and read both arguments there.
// ---------------------------------------------------------------------------------------------

/// <summary>A DynamicVar being built by CanonicalVars.</summary>
internal sealed class VarObj(TypeReference type)
{
    public TypeReference Type { get; } = type;
    public string? Name { get; set; }
    public decimal? Value { get; set; }
    public string? SavedProperty { get; set; }
    public bool IsCalculated { get; set; }
    public string? BaseVarName { get; set; }
    public string? ExtraVarName { get; set; }
    /// <summary>For a StringVar: the second constructor argument (its text), as the machine saw it.</summary>
    public bool IsString { get; set; }
    public object? StringArg { get; set; }
}

internal sealed class DynamicVarReader
{
    private readonly TypeDefinition _modelBase;
    private readonly TypeDefinition _dynamicVar;
    private readonly TypeDefinition _calculatedVar;
    private readonly TypeDefinition _stringVar;
    private readonly CallHook? _extraHook;
    private TypeDefinition? _currentModel;

    public TypeDefinition DynamicVarSet { get; }

    /// <param name="modelBase">CardModel or RelicModel: where base-chain lookups stop.</param>
    /// <param name="extraHook">Tried before the built-in var hook while CanonicalVars runs (e.g. to follow ModelDb lookups).</param>
    public DynamicVarReader(TypeDefinition modelBase, CallHook? extraHook = null)
    {
        _modelBase = modelBase;
        _extraHook = extraHook;
        var types = modelBase.Module.GetTypes().ToDictionary(t => t.FullName);
        const string ns = "MegaCrit.Sts2.Core.Localization.DynamicVars.";
        _dynamicVar = types[ns + "DynamicVar"];
        DynamicVarSet = types[ns + "DynamicVarSet"];
        _calculatedVar = types[ns + "CalculatedVar"];
        _stringVar = types[ns + "StringVar"];
    }

    /// <summary>
    /// Runs the model's CanonicalVars getter (model class or an intermediate base; the model base's own returns
    /// Array.Empty) and returns the vars it builds, in order.
    /// </summary>
    public List<VarObj> ReadCanonicalVars(TypeDefinition model, string id, List<string> warnings)
    {
        _currentModel = model;
        var built = new List<VarObj>();
        if (FindInChain(model, "get_CanonicalVars") is not { } getter) return built;
        MachineResult r = IlMachine.Run(getter, [ThisRef.Value], CanonicalVarsHook);
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
        return built;
    }

    private HookAction CanonicalVarsHook(MethodReference method, object?[] args, bool isNewobj, out object? result)
    {
        result = null;
        if (_extraHook != null)
        {
            HookAction a = _extraHook(method, args, isNewobj, out result);
            if (a != HookAction.NotHandled) return a;
        }
        return VarConstructionHook(method, args, isNewobj, out result);
    }

    /// <summary>Nearest definition of a parameterless method in the model or its bases, stopping before the model base.</summary>
    public MethodDefinition? FindInChain(TypeDefinition model, string name)
    {
        for (TypeDefinition? t = model; t != null && t.FullName != _modelBase.FullName; t = t.BaseType?.Resolve())
            if (t.Methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == 0 && m.HasBody) is { } m)
                return m;
        return null;
    }

    /// <summary>Nearest definition of a method (any parameters) in the model or its bases, stopping before the model base.</summary>
    public MethodDefinition? FindInChainWithParams(TypeDefinition model, string name)
    {
        for (TypeDefinition? t = model; t != null && t.FullName != _modelBase.FullName; t = t.BaseType?.Resolve())
            if (t.Methods.FirstOrDefault(m => m.Name == name && m.HasBody) is { } m) return m;
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // Building DynamicVar objects
    // ------------------------------------------------------------------------------------------

    public bool IsDynamicVarType(TypeReference t) => InheritsFrom(t.Resolve(), _dynamicVar);

    private static bool InheritsFrom(TypeDefinition? t, TypeDefinition baseType)
    {
        for (TypeDefinition? d = t; d != null; d = d.BaseType?.Resolve())
            if (d.FullName == baseType.FullName) return true;
        return false;
    }

    private HookAction VarConstructionHook(MethodReference method, object?[] args, bool isNewobj, out object? result)
    {
        result = null;
        if (isNewobj && IsDynamicVarType(method.DeclaringType))
        {
            var v = new VarObj(method.DeclaringType);
            RunVarCtor(method, v, args);
            if (method.DeclaringType.Resolve() is { } def)
            {
                if (InheritsFrom(def, _calculatedVar))
                {
                    v.IsCalculated = true;
                    v.BaseVarName = VarSetGetterCalledBy(def, "GetBaseVar");
                    v.ExtraVarName = VarSetGetterCalledBy(def, "GetExtraVar");
                }
                if (InheritsFrom(def, _stringVar))
                {
                    v.IsString = true;
                    v.StringArg = args.Length > 1 ? args[1] : Unknown.Value;
                }
            }
            result = v;
            return HookAction.Handled;
        }
        // A model property read in CanonicalVars (GeneticAlgorithm's CurrentBlock): use its field's initial value.
        if (!isNewobj && args.Length == 1 && args[0] is ThisRef && method.Name.StartsWith("get_", StringComparison.Ordinal)
            && _currentModel != null && BackingFieldInitialValue(_currentModel, method) is { } initial)
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
    public static string? VarSetGetterName(MethodReference getter) =>
        getter.Resolve()?.Body?.Instructions.FirstOrDefault(i => i.OpCode.Code == Code.Ldstr)?.Operand as string;

    /// <summary>For CalculatedVar.GetBaseVar/GetExtraVar: which DynamicVarSet getter the nearest override calls.</summary>
    private string? VarSetGetterCalledBy(TypeDefinition varType, string method)
    {
        for (TypeDefinition? t = varType; t != null && t.FullName != _dynamicVar.FullName; t = t.BaseType?.Resolve())
        {
            if (t.Methods.FirstOrDefault(m => m.Name == method && m.HasBody) is not { } md) continue;
            foreach (Instruction ins in md.Body.Instructions)
                if (ins.Operand is MethodReference mr && mr.DeclaringType.FullName == DynamicVarSet.FullName && mr.Name.StartsWith("get_"))
                    return VarSetGetterName(mr);
            return null;
        }
        return null;
    }

    /// <summary>
    /// For a getter of the form <c>get { return _field; }</c> declared on the model, the constant the model's
    /// constructor stores in <c>_field</c> (a field initializer: <c>ldarg.0; ldc.i4 N; stfld _field</c>).
    /// </summary>
    private static decimal? BackingFieldInitialValue(TypeDefinition model, MethodReference getterRef)
    {
        MethodDefinition? getter = getterRef.Resolve();
        if (getter?.HasBody != true || getter.DeclaringType.FullName != model.FullName) return null;
        var body = getter.Body.Instructions.Where(i => i.OpCode.Code != Code.Nop).ToList();
        if (body.Count != 3 || body[0].OpCode.Code != Code.Ldarg_0 || body[1].OpCode.Code != Code.Ldfld || body[2].OpCode.Code != Code.Ret)
            return null;
        var field = (FieldReference)body[1].Operand;
        MethodDefinition? ctor = model.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
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
    // Description context
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The first string literal before each <c>LocString.Add(name, value)</c> call, and the default name of each
    /// DynamicVar passed to <c>LocString.Add(DynamicVar)</c>. A linear scan, so branches don't matter.
    /// Stops at the first <c>LocString.GetFormattedText</c> (later Add calls fill other LocStrings).
    /// </summary>
    public List<string> LocStringAddNames(MethodDefinition m)
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
}
