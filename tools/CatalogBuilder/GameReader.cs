using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CatalogBuilder;

/// <summary>Everything we statically decoded about one card class.</summary>
public sealed class DecodedCard
{
    public required TypeDefinition Type { get; init; }
    public required string Id { get; init; }
    public string? Type_ { get; set; }          // CardType name ("Attack"...)
    public string? Rarity { get; set; }
    public string? Target { get; set; }
    public int? Cost { get; set; }
    public bool? CostX { get; set; }
    public int? StarCost { get; set; }
    public bool? StarCostX { get; set; }
    public bool? GainsBlock { get; set; }
    public int? MaxUpgrade { get; set; }
    public string? Multiplayer { get; set; }
    public bool? ShowInLibrary { get; set; }
    public List<string>? Keywords { get; set; }
    public List<string>? Tags { get; set; }

    /// <summary>Fields we could not decode at all.</summary>
    public List<string> Unresolved { get; } = new();

    /// <summary>Fields whose getter has runtime logic (if/else); we report the constructor's canonical value.</summary>
    public List<string> Dynamic { get; } = new();
}

/// <summary>
/// Reads sts2.dll with Mono.Cecil. Cecil parses the file as metadata + IL bytes only;
/// no game code is ever executed, and the file is opened read-only.
/// </summary>
public sealed class GameReader : IDisposable
{
    private readonly ModuleDefinition _module;
    private readonly Dictionary<string, TypeDefinition> _typesByFullName;

    public TypeDefinition AbstractModel { get; }
    public TypeDefinition CardModel { get; }
    public TypeDefinition CardPoolModel { get; }
    public TypeDefinition CharacterModel { get; }
    public TypeDefinition ModelDb { get; }

    // Enum value -> name tables, read from the enum definitions inside the assembly.
    private readonly EnumTable _cardType, _rarity, _target, _multiplayer, _keyword, _tag;

    public GameReader(string dllPath)
    {
        // The resolver lets Cecil follow references into other DLLs next to sts2.dll (GodotSharp etc.)
        // when we call .Resolve(). It only reads those files too.
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(dllPath)!);
        _module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadWrite = false,
            InMemory = true, // read the whole file into memory and close it immediately
        });

        // GetTypes() also returns nested types, which plain .Types does not.
        _typesByFullName = _module.GetTypes().ToDictionary(t => t.FullName);

        AbstractModel = FindType("MegaCrit.Sts2.Core.Models.AbstractModel");
        CardModel = FindType("MegaCrit.Sts2.Core.Models.CardModel");
        CardPoolModel = FindType("MegaCrit.Sts2.Core.Models.CardPoolModel");
        CharacterModel = FindType("MegaCrit.Sts2.Core.Models.CharacterModel");
        ModelDb = FindType("MegaCrit.Sts2.Core.Models.ModelDb");

        // Rather than hard-coding enum namespaces, ask CardModel's properties what type they return.
        _cardType = EnumTable.From(GetterOf(CardModel, "get_Type")!.ReturnType);
        _rarity = EnumTable.From(GetterOf(CardModel, "get_Rarity")!.ReturnType);
        _target = EnumTable.From(GetterOf(CardModel, "get_TargetType")!.ReturnType);
        _multiplayer = EnumTable.From(GetterOf(CardModel, "get_MultiplayerConstraint")!.ReturnType);
        // IEnumerable<CardKeyword> / HashSet<CardTag>: the enum is the generic argument.
        _keyword = EnumTable.From(((GenericInstanceType)GetterOf(CardModel, "get_CanonicalKeywords")!.ReturnType).GenericArguments[0]);
        _tag = EnumTable.From(((GenericInstanceType)GetterOf(CardModel, "get_CanonicalTags")!.ReturnType).GenericArguments[0]);
    }

    public void Dispose() => _module.Dispose();

    private TypeDefinition FindType(string fullName) =>
        _typesByFullName.TryGetValue(fullName, out var t)
            ? t
            : throw new InvalidOperationException($"Type {fullName} not found in assembly - game update renamed it?");

    private static MethodDefinition? GetterOf(TypeDefinition type, string getterName) =>
        type.Methods.FirstOrDefault(m => m.Name == getterName && m.Parameters.Count == 0);

    // ------------------------------------------------------------------------------------------
    // Type hierarchy helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>Base classes of a type, nearest first (stops when a base can't be resolved).</summary>
    private static IEnumerable<TypeDefinition> BaseChain(TypeDefinition type)
    {
        for (TypeReference? b = type.BaseType; b != null; )
        {
            TypeDefinition? def = b.Resolve();
            if (def == null) yield break;
            yield return def;
            b = def.BaseType;
        }
    }

    private static bool InheritsFrom(TypeDefinition type, TypeDefinition baseType) =>
        BaseChain(type).Any(b => b.FullName == baseType.FullName);

    /// <summary>All concrete (non-abstract) subclasses of a model base class.</summary>
    public IEnumerable<TypeDefinition> ConcreteSubclassesOf(TypeDefinition baseType) =>
        _module.GetTypes().Where(t => t.IsClass && !t.IsAbstract && InheritsFrom(t, baseType));

    /// <summary>
    /// Mirrors ModelDb.GetId(type): category = slug of the class directly under AbstractModel
    /// (CardModel -> "CARD"), entry = slug of the class name.
    /// </summary>
    public string ModelIdOf(TypeDefinition type)
    {
        TypeDefinition categoryType = type;
        foreach (TypeDefinition b in BaseChain(type))
        {
            if (b.FullName == AbstractModel.FullName) break;
            categoryType = b;
        }
        return $"{Slug.SlugifyCategory(categoryType.Name)}.{Slug.Slugify(type.Name)}";
    }

    // ------------------------------------------------------------------------------------------
    // ModelDb.Card<T>() / CardPool<T>() / Character<T>() reference scanning
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds every `ModelDb.{methodName}&lt;T&gt;()` call in a method body and returns the T's.
    /// Pools, characters and starting decks are all written as lists of such calls, e.g.
    ///   return new CardModel[88] { ModelDb.Card&lt;AdaptiveStrike&gt;(), ... };
    /// This is a plain scan (order preserved, duplicates kept) so branches don't matter.
    /// </summary>
    public List<TypeDefinition> ModelDbGenericCalls(MethodDefinition? method, string methodName)
    {
        var result = new List<TypeDefinition>();
        if (method?.HasBody != true) return result;
        foreach (Instruction ins in method.Body.Instructions)
        {
            if (ins.OpCode.Code is Code.Call or Code.Callvirt
                && ins.Operand is GenericInstanceMethod gim
                && gim.Name == methodName
                && gim.DeclaringType.FullName == ModelDb.FullName
                && gim.GenericArguments[0].Resolve() is { } t)
            {
                result.Add(t);
            }
        }
        return result;
    }

    public MethodDefinition? FindMethod(TypeDefinition type, string name) =>
        type.Methods.FirstOrDefault(m => m.Name == name);

    // ------------------------------------------------------------------------------------------
    // Card decoding
    // ------------------------------------------------------------------------------------------

    public DecodedCard DecodeCard(TypeDefinition card)
    {
        var d = new DecodedCard { Type = card, Id = ModelIdOf(card) };

        // 1) Constructor: `: base(cost, CardType.X, CardRarity.Y, TargetType.Z [, showInLibrary])`
        Dictionary<string, object?>? ctorArgs = DecodeBaseCtorArgs(card);

        // 2) Each field: an override getter in the card class (or an intermediate base) wins,
        //    then the constructor argument, then CardModel's own default getter.
        d.Type_ = _cardType.Name(ResolveInt(d, card, "type", "get_Type", ctorArgs, "type"));
        d.Rarity = _rarity.Name(ResolveInt(d, card, "rarity", "get_Rarity", ctorArgs, "rarity"));
        d.Target = _target.Name(ResolveInt(d, card, "target", "get_TargetType", ctorArgs, "targetType"));
        d.Cost = ResolveInt(d, card, "cost", "get_CanonicalEnergyCost", ctorArgs, "canonicalEnergyCost");
        d.CostX = AsBool(ResolveInt(d, card, "costX", "get_HasEnergyCostX", null, null));
        d.StarCost = ResolveInt(d, card, "starCost", "get_CanonicalStarCost", null, null);
        d.StarCostX = AsBool(ResolveInt(d, card, "starCostX", "get_HasStarCostX", null, null));
        d.GainsBlock = AsBool(ResolveInt(d, card, "gainsBlock", "get_GainsBlock", null, null));
        d.MaxUpgrade = ResolveInt(d, card, "maxUpgrade", "get_MaxUpgradeLevel", null, null);
        d.Multiplayer = _multiplayer.Name(ResolveInt(d, card, "multiplayer", "get_MultiplayerConstraint", null, null));

        // shouldShowInCardLibrary is only a constructor parameter (default true).
        if (ctorArgs == null) d.Unresolved.Add("showInLibrary");
        else d.ShowInLibrary = ctorArgs.GetValueOrDefault("shouldShowInCardLibrary") is int s ? s != 0 : true;

        d.Keywords = ResolveEnumList(d, card, "keywords", "get_CanonicalKeywords", _keyword);
        d.Tags = ResolveEnumList(d, card, "tags", "get_CanonicalTags", _tag);
        return d;
    }

    private static bool? AsBool(int? v) => v is null ? null : v != 0;

    /// <summary>Finds an override of a getter in the card or any base class *below* CardModel.</summary>
    private MethodDefinition? FindOverride(TypeDefinition card, string getterName)
    {
        foreach (TypeDefinition t in new[] { card }.Concat(BaseChain(card)))
        {
            if (t.FullName == CardModel.FullName) return null;
            if (GetterOf(t, getterName) is { } m) return m;
        }
        return null;
    }

    private int? ResolveInt(DecodedCard d, TypeDefinition card, string field, string getterName,
                            Dictionary<string, object?>? ctorArgs, string? ctorParam)
    {
        object? ctorValue = ctorParam != null ? ctorArgs?.GetValueOrDefault(ctorParam) : null;

        if (FindOverride(card, getterName) is { } getter)
        {
            EvalResult r = IlEvaluator.Evaluate(getter);
            if (r.Ok && r.ReturnValue is int v) return v; // the common `ldc.i4.X; ret` case

            // Getter has logic (e.g. MadScience's Type depends on a runtime choice).
            if (ctorValue is int cv) { d.Dynamic.Add(field); return cv; }
            d.Unresolved.Add(field);
            return null;
        }

        if (ctorParam != null)
        {
            if (ctorValue is int cv) return cv;
            d.Unresolved.Add(field);
            return null;
        }

        // No override: use CardModel's own default (e.g. MaxUpgradeLevel => 1), read from IL as well.
        EvalResult baseResult = IlEvaluator.Evaluate(GetterOf(CardModel, getterName)!);
        if (baseResult.Ok && baseResult.ReturnValue is int bv) return bv;
        d.Unresolved.Add(field);
        return null;
    }

    private List<string>? ResolveEnumList(DecodedCard d, TypeDefinition card, string field, string getterName, EnumTable table)
    {
        MethodDefinition getter = FindOverride(card, getterName) ?? GetterOf(CardModel, getterName)!;
        EvalResult r = IlEvaluator.Evaluate(getter);
        if (r.Ok && r.ReturnValue is CollectionValue c && c.Items.All(i => i is int))
            return c.Items.Select(i => table.Name((int)i!)!).ToList();
        d.Unresolved.Add(field);
        return null;
    }

    /// <summary>
    /// Evaluates the card's parameterless constructor until it calls its base constructor, and
    /// returns that call's arguments by parameter name. If the base is an intermediate class (not
    /// CardModel itself), we evaluate *its* constructor with those arguments, recursively.
    /// </summary>
    private Dictionary<string, object?>? DecodeBaseCtorArgs(TypeDefinition card)
    {
        MethodDefinition? ctor = card.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        return ctor == null ? null : CaptureBaseCall(card, ctor, null);
    }

    private Dictionary<string, object?>? CaptureBaseCall(TypeDefinition owner, MethodDefinition ctor, object?[]? args)
    {
        Dictionary<string, object?>? captured = null;

        IlEvaluator.Evaluate(ctor, args, (method, callArgs) =>
        {
            // We are looking for `call BaseType::.ctor(...)` on `this`.
            bool isBaseCtor = method.Name == ".ctor"
                              && callArgs.Length > 0 && callArgs[0] is ThisRef
                              && method.DeclaringType.FullName == owner.BaseType?.FullName;
            if (!isBaseCtor) return false;

            MethodDefinition? baseCtor = method.Resolve();
            if (baseCtor == null) return true;

            if (baseCtor.DeclaringType.FullName == CardModel.FullName)
            {
                // Reached CardModel(int canonicalEnergyCost, CardType type, CardRarity rarity, TargetType targetType, bool shouldShowInCardLibrary)
                captured = new Dictionary<string, object?>();
                for (int i = 0; i < baseCtor.Parameters.Count; i++)
                    captured[baseCtor.Parameters[i].Name] = callArgs[i + 1]; // +1 skips `this`
            }
            else
            {
                // Intermediate base class: keep walking up with the concrete argument values.
                captured = CaptureBaseCall(baseCtor.DeclaringType, baseCtor, callArgs);
            }
            return true; // stop evaluating the rest of the constructor
        });

        return captured;
    }
}

/// <summary>Maps the integer values of an enum to their names, as read from the enum's metadata.</summary>
public sealed class EnumTable
{
    private readonly Dictionary<long, string> _names = new();
    private readonly bool _isFlags;

    public static EnumTable From(TypeReference enumRef)
    {
        TypeDefinition def = enumRef.Resolve() ?? throw new InvalidOperationException($"Cannot resolve enum {enumRef}");
        var table = new EnumTable(def.CustomAttributes.Any(a => a.AttributeType.Name == "FlagsAttribute"));
        // An enum's members are `public static literal` fields with a Constant value.
        foreach (FieldDefinition f in def.Fields.Where(f => f.IsStatic && f.HasConstant))
            table._names.TryAdd(Convert.ToInt64(f.Constant), f.Name);
        return table;
    }

    private EnumTable(bool isFlags) => _isFlags = isFlags;

    public string? Name(int? value)
    {
        if (value is null) return null;
        if (_names.TryGetValue(value.Value, out var n)) return n;
        if (_isFlags) // e.g. 5 -> "A, C" like Enum.ToString() does
        {
            var parts = _names.Where(kv => kv.Key != 0 && (value.Value & kv.Key) == kv.Key).Select(kv => kv.Value).ToList();
            if (parts.Count > 0) return string.Join(", ", parts);
        }
        return value.Value.ToString(); // unknown value: keep the number so nothing is lost
    }
}
