using CatalogBuilder.CardVars;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sts2Overlay.Resources.Catalog;

namespace CatalogBuilder.RelicVars;

// ---------------------------------------------------------------------------------------------
// Reads every relic's description variables out of sts2.dll, statically.
//
// RelicModel.DynamicDescription = relics.json "<ENTRY>.description" + DynamicVars.AddTo(description)
// + energyPrefix + singleStarIcon. DynamicVars is built from CanonicalVars exactly like a card's, so the
// var machinery is the card one (DynamicVarReader). Relics have no upgrades; what they do have is code
// that rewrites a var at runtime (BoneTea's "Combats" counts down, ArchaicTooth fills "StarterCard" with a
// card title), which we find by scanning the relic's methods for DynamicVar setters.
// ---------------------------------------------------------------------------------------------

/// <summary>One relic in relic-vars.json.</summary>
internal sealed record RelicVarsRow(
    string Id,
    Dictionary<string, decimal> Vars,
    Dictionary<string, string>? Strings,
    List<string>? ChangedAtRuntime,
    List<string> Unresolved);

internal sealed record RelicVarsFile(
    string GameVersion,
    string Source,
    string Note,
    Dictionary<string, string> ContextVars,
    List<RelicVarsRow> Relics);

internal sealed class RelicVarsExtractor
{
    private readonly GameReader _game;
    private readonly TypeDefinition _relicModel;
    private readonly DynamicVarReader _vars;

    /// <summary>DynamicVar members that change what a var shows.</summary>
    private static readonly HashSet<string> Mutators = new(StringComparer.Ordinal)
    {
        "set_BaseValue", "UpgradeValueBy", "set_EnchantedValue", "set_StringValue",
    };

    public RelicVarsExtractor(GameReader game)
    {
        _game = game;
        _relicModel = game.RelicModel;
        _vars = new DynamicVarReader(_relicModel, TitleLookupHook);
    }

    // ------------------------------------------------------------------------------------------
    // StringVar("EnchantmentName", ModelDb.Enchantment<Goopy>().Title.GetFormattedText()):
    // follow the ModelDb lookup so the row can say "the title of ENCHANTMENT.GOOPY" without game text.
    // ------------------------------------------------------------------------------------------

    private sealed record ModelRef(string Id);
    private sealed record TitleRef(string Key);

    private HookAction TitleLookupHook(MethodReference m, object?[] args, bool isNewobj, out object? result)
    {
        result = null;
        if (!isNewobj && m is GenericInstanceMethod gim && gim.DeclaringType.FullName == _game.ModelDb.FullName
            && gim.GenericArguments.Count == 1 && gim.GenericArguments[0].Resolve() is { } modelType)
        {
            result = new ModelRef(_game.ModelIdOf(modelType));
            return HookAction.Handled;
        }
        if (!isNewobj && m.Name == "get_Title" && args is [ModelRef model])
        {
            result = new TitleRef(model.Id + ".title");
            return HookAction.Handled;
        }
        if (!isNewobj && m.Name == "GetFormattedText" && m.DeclaringType.Name == "LocString" && args is [TitleRef title])
        {
            result = title;
            return HookAction.Handled;
        }
        return HookAction.NotHandled;
    }

    // ------------------------------------------------------------------------------------------
    // Relic -> row
    // ------------------------------------------------------------------------------------------

    public RelicVarsRow Extract(TypeDefinition relic, string id, List<string> warnings)
    {
        var vars = new Dictionary<string, decimal>();
        var strings = new Dictionary<string, string>();
        var unresolved = new List<string>();
        var runtime = new List<string>();

        foreach (VarObj v in _vars.ReadCanonicalVars(relic, id, warnings))
        {
            if (v.Name is null) { warnings.Add($"{id}: var of {v.Type.Name} has no static name"); continue; }
            if (v.IsCalculated) warnings.Add($"{id}: {v.Name} is a CalculatedVar (only its base value is listed)");
            if (v.IsString)
            {
                // Text, not a number. A model title is named by its key; anything else ("" filled later) is runtime.
                if (v.StringArg is TitleRef t) strings[v.Name] = t.Key;
                else if (!unresolved.Contains(v.Name)) unresolved.Add(v.Name);
                continue;
            }
            if (v.Value is { } value) vars[v.Name] = IlMachine.Normalize(value);
            else if (!unresolved.Contains(v.Name)) unresolved.Add(v.Name);
            // new DynamicVar("Combats", CombatsLeft): the value is a [SavedProperty]'s initial value; it follows the run state.
            if (v.SavedProperty != null && !runtime.Contains(v.Name)) runtime.Add(v.Name);
        }

        foreach (string name in RuntimeMutatedVars(relic))
        {
            if (name == "?") { warnings.Add($"{id}: changes a var whose name could not be read"); continue; }
            if (!runtime.Contains(name)) runtime.Add(name);
            if (!vars.ContainsKey(name) && !strings.ContainsKey(name) && !unresolved.Contains(name))
                warnings.Add($"{id}: code changes {name}, which is not in CanonicalVars");
        }

        return new RelicVarsRow(
            id,
            vars,
            strings.Count == 0 ? null : strings,
            runtime.Count == 0 ? null : runtime,
            unresolved);
    }

    /// <summary>
    /// Var names the relic's own code (any method of the relic class, its bases up to RelicModel, and their
    /// compiler-generated nested types: async state machines, lambdas) passes to a DynamicVar setter. A linear
    /// scan per method: the var is the last <c>DynamicVars["Name"]</c> / <c>DynamicVars.Name</c> read before the setter.
    /// </summary>
    private List<string> RuntimeMutatedVars(TypeDefinition relic)
    {
        var names = new List<string>();
        for (TypeDefinition? t = relic; t != null && t.FullName != _relicModel.FullName; t = t.BaseType?.Resolve())
            foreach (TypeDefinition type in WithNested(t))
                foreach (MethodDefinition m in type.Methods.Where(m => m.HasBody && m.Name != "get_CanonicalVars"))
                {
                    string? lastString = null, lastVar = null;
                    foreach (Instruction ins in m.Body.Instructions)
                    {
                        if (ins.OpCode.Code == Code.Ldstr) lastString = (string)ins.Operand;
                        if (ins.Operand is not MethodReference call) continue;
                        if (call.DeclaringType.FullName == _vars.DynamicVarSet.FullName)
                        {
                            if (call.Name == "get_Item") lastVar = lastString;
                            else if (call.Name.StartsWith("get_", StringComparison.Ordinal) && _vars.IsDynamicVarType(call.ReturnType))
                                lastVar = DynamicVarReader.VarSetGetterName(call);
                        }
                        else if (Mutators.Contains(call.Name) && _vars.IsDynamicVarType(call.DeclaringType))
                        {
                            string name = lastVar ?? "?";
                            if (!names.Contains(name)) names.Add(name);
                        }
                    }
                }
        return names;
    }

    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition t)
    {
        yield return t;
        foreach (TypeDefinition n in t.NestedTypes)
            foreach (TypeDefinition d in WithNested(n))
                yield return d;
    }

    /// <summary>
    /// Variables RelicModel.DynamicDescription adds to every relic description besides the relic's own DynamicVars
    /// (energyPrefix, singleStarIcon), read from the IL so a game update can't silently add one.
    /// </summary>
    public List<string> DescriptionContextVars() =>
        _vars.LocStringAddNames(_relicModel.Methods.First(m => m.Name == "get_DynamicDescription" && m.HasBody));
}
