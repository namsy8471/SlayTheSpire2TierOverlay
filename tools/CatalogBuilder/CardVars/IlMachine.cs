using System.Globalization;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CatalogBuilder.CardVars;

// ---------------------------------------------------------------------------------------------
// A small symbolic IL interpreter for card dynamic vars.
//
// Resources/Catalog/IlEvaluator only knows integers and straight-line code. Card vars need more:
//   - decimals:  `new DamageVar(6m, ...)` compiles to  ldc.i4.6; newobj Decimal::.ctor(int32)
//                and 1.5m to  newobj Decimal::.ctor(int32 lo, int32 mid, int32 hi, bool neg, uint8 scale)
//   - lambdas:   `.WithMultiplier((card, _) => ...)` uses the compiler's cached-delegate pattern
//                  ldsfld <>9__x; dup; brtrue.s L; pop; ...build delegate...; dup; stsfld <>9__x; L:
//                which has a branch. We know the cache is "empty" the first time, so we fall through.
//   - generics:  PowerVar<T>(amount) names itself `typeof(T).Name`.
// Anything we cannot know is `Unknown`. Real data-dependent branches are counted in AmbiguousBranches
// so the caller can flag the result instead of silently trusting it.
// ---------------------------------------------------------------------------------------------

internal sealed class Unknown
{
    public static readonly Unknown Value = new();
    public override string ToString() => "?";
}

/// <summary>`this` of the method being evaluated (the card).</summary>
internal sealed class ThisRef
{
    public static readonly ThisRef Value = new();
}

/// <summary>A value read from a compiler-generated lambda cache field (<c>&lt;&gt;9__...</c>): "not created yet".</summary>
internal sealed class LambdaCache
{
    public static readonly LambdaCache Value = new();
}

/// <summary>
/// A number read from a card property backed by a field with a constant initializer
/// (GeneticAlgorithm: <c>new BlockVar(CurrentBlock, ...)</c> with <c>_currentBlock = 1</c>). The value is the fresh
/// card's; the property is [SavedProperty] state that grows during a run.
/// </summary>
internal sealed record PropertyValue(decimal Value, string Property);

/// <summary>Result of <c>typeof(T)</c>.</summary>
internal sealed record TypeValue(TypeReference Type);

/// <summary>An array or list being built (new DynamicVar[3] { ... }, ReadOnlyArray, single-element list).</summary>
internal sealed class Collection
{
    public List<object?> Items { get; } = new();
}

/// <summary>What a call hook decided.</summary>
internal enum HookAction { NotHandled, Handled, Stop }

internal delegate HookAction CallHook(MethodReference method, object?[] args, bool isNewobj, out object? result);

internal sealed record MachineResult(bool Ok, object? ReturnValue, string? Failure, int AmbiguousBranches);

internal static class IlMachine
{
    private const int StepLimit = 20_000;

    /// <param name="method">Method to run.</param>
    /// <param name="args">ldarg values (for instance methods args[0] is `this`).</param>
    /// <param name="hook">Called for every call/callvirt/newobj before the built-in simulation.</param>
    /// <param name="genericArgs">Values for the declaring type's generic parameters (PowerVar&lt;T&gt; -> T).</param>
    public static MachineResult Run(MethodDefinition method, object?[] args, CallHook? hook, IList<TypeReference>? genericArgs = null)
    {
        if (!method.HasBody) return new(false, null, "no body", 0);

        var instructions = method.Body.Instructions;
        var indexOf = new Dictionary<Instruction, int>();
        for (int i = 0; i < instructions.Count; i++) indexOf[instructions[i]] = i;

        var stack = new Stack<object?>();
        var locals = new Dictionary<int, object?>();
        int ambiguous = 0;

        object? Pop() => stack.Count > 0 ? stack.Pop() : Unknown.Value;
        object? Arg(int i) => i < args.Length ? args[i] : Unknown.Value;

        int pc = 0;
        for (int steps = 0; steps < StepLimit; steps++)
        {
            if (pc >= instructions.Count) return new(false, null, "fell off end of method", ambiguous);
            Instruction ins = instructions[pc];
            int next = pc + 1;

            switch (ins.OpCode.Code)
            {
                case Code.Nop: break;

                // ---- constants ----------------------------------------------------------------
                case Code.Ldc_I4_M1: stack.Push(-1); break;
                case Code.Ldc_I4_0: stack.Push(0); break;
                case Code.Ldc_I4_1: stack.Push(1); break;
                case Code.Ldc_I4_2: stack.Push(2); break;
                case Code.Ldc_I4_3: stack.Push(3); break;
                case Code.Ldc_I4_4: stack.Push(4); break;
                case Code.Ldc_I4_5: stack.Push(5); break;
                case Code.Ldc_I4_6: stack.Push(6); break;
                case Code.Ldc_I4_7: stack.Push(7); break;
                case Code.Ldc_I4_8: stack.Push(8); break;
                case Code.Ldc_I4_S: stack.Push((int)(sbyte)ins.Operand); break;
                case Code.Ldc_I4: stack.Push((int)ins.Operand); break;
                case Code.Ldc_I8: stack.Push((long)ins.Operand); break;
                case Code.Ldc_R4: stack.Push((double)(float)ins.Operand); break;
                case Code.Ldc_R8: stack.Push((double)ins.Operand); break;
                case Code.Ldstr: stack.Push((string)ins.Operand); break;
                case Code.Ldnull: stack.Push(null); break;
                case Code.Ldtoken:
                    stack.Push(ins.Operand is TypeReference tr ? new TypeValue(Substitute(tr, genericArgs)) : ins.Operand);
                    break;

                // ---- arguments and locals -------------------------------------------------------
                case Code.Ldarg_0: stack.Push(Arg(0)); break;
                case Code.Ldarg_1: stack.Push(Arg(1)); break;
                case Code.Ldarg_2: stack.Push(Arg(2)); break;
                case Code.Ldarg_3: stack.Push(Arg(3)); break;
                case Code.Ldarg_S:
                case Code.Ldarg:
                    stack.Push(Arg(((ParameterDefinition)ins.Operand).Index + (method.HasThis ? 1 : 0)));
                    break;
                case Code.Starg_S:
                case Code.Starg:
                    Pop(); // we don't track writes to arguments
                    break;
                case Code.Stloc_0: locals[0] = Pop(); break;
                case Code.Stloc_1: locals[1] = Pop(); break;
                case Code.Stloc_2: locals[2] = Pop(); break;
                case Code.Stloc_3: locals[3] = Pop(); break;
                case Code.Stloc_S:
                case Code.Stloc: locals[((VariableDefinition)ins.Operand).Index] = Pop(); break;
                case Code.Ldloc_0: stack.Push(locals.GetValueOrDefault(0, Unknown.Value)); break;
                case Code.Ldloc_1: stack.Push(locals.GetValueOrDefault(1, Unknown.Value)); break;
                case Code.Ldloc_2: stack.Push(locals.GetValueOrDefault(2, Unknown.Value)); break;
                case Code.Ldloc_3: stack.Push(locals.GetValueOrDefault(3, Unknown.Value)); break;
                case Code.Ldloc_S:
                case Code.Ldloc: stack.Push(locals.GetValueOrDefault(((VariableDefinition)ins.Operand).Index, Unknown.Value)); break;
                case Code.Ldloca_S:
                case Code.Ldloca:
                case Code.Ldarga_S:
                case Code.Ldarga:
                    stack.Push(Unknown.Value);
                    break;

                // ---- stack shuffling and conversions ----------------------------------------------
                case Code.Dup: { object? top = Pop(); stack.Push(top); stack.Push(top); break; }
                case Code.Pop: Pop(); break;
                case Code.Conv_I1: case Code.Conv_I2: case Code.Conv_I4: case Code.Conv_U1:
                case Code.Conv_U2: case Code.Conv_U4: case Code.Conv_I: case Code.Conv_U:
                {
                    object? v = Pop();
                    stack.Push(v switch { long l => (int)l, double d => (int)d, _ => v });
                    break;
                }
                case Code.Conv_I8: case Code.Conv_U8:
                {
                    object? v = Pop();
                    stack.Push(v switch { int i => (long)i, double d => (long)d, _ => v });
                    break;
                }
                case Code.Conv_R4: case Code.Conv_R8: case Code.Conv_R_Un:
                {
                    object? v = Pop();
                    stack.Push(v switch { int i => (double)i, long l => (double)l, _ => v });
                    break;
                }

                // ---- simple arithmetic on known integers ------------------------------------------
                case Code.Add: case Code.Sub: case Code.Mul:
                {
                    object? b = Pop(), a = Pop();
                    stack.Push(a is int x && b is int y
                        ? ins.OpCode.Code switch { Code.Add => x + y, Code.Sub => x - y, _ => x * y }
                        : Unknown.Value);
                    break;
                }
                case Code.Neg: { object? a = Pop(); stack.Push(a is int x ? -x : Unknown.Value); break; }
                case Code.Ceq: case Code.Cgt: case Code.Clt: case Code.Cgt_Un: case Code.Clt_Un:
                {
                    object? b = Pop(), a = Pop();
                    if (a is int x && b is int y)
                        stack.Push(ins.OpCode.Code switch { Code.Ceq => x == y, Code.Cgt or Code.Cgt_Un => x > y, _ => x < y } ? 1 : 0);
                    else stack.Push(Unknown.Value);
                    break;
                }

                // ---- fields: values unknown, except decimal constants and lambda caches -----------
                case Code.Ldsfld:
                {
                    var f = (FieldReference)ins.Operand;
                    if (f.DeclaringType.FullName == "System.Decimal")
                        stack.Push(f.Name switch { "Zero" => 0m, "One" => 1m, "MinusOne" => -1m, _ => (object)Unknown.Value });
                    else if (f.Name.StartsWith("<>9__", StringComparison.Ordinal))
                        stack.Push(LambdaCache.Value);
                    else
                        stack.Push(Unknown.Value);
                    break;
                }
                case Code.Ldfld: Pop(); stack.Push(Unknown.Value); break;
                case Code.Ldflda: Pop(); stack.Push(Unknown.Value); break;
                case Code.Ldsflda: stack.Push(Unknown.Value); break;
                case Code.Stfld: Pop(); Pop(); break;
                case Code.Stsfld: Pop(); break;
                case Code.Ldftn: stack.Push(Unknown.Value); break;
                case Code.Ldvirtftn: Pop(); stack.Push(Unknown.Value); break;
                case Code.Box: case Code.Unbox_Any: case Code.Castclass: case Code.Isinst: break;
                case Code.Initobj: Pop(); break;

                // ---- arrays -----------------------------------------------------------------------
                case Code.Newarr:
                {
                    object? count = Pop();
                    if (count is int n)
                    {
                        var arr = new Collection();
                        for (int i = 0; i < n; i++) arr.Items.Add(null);
                        stack.Push(arr);
                    }
                    else stack.Push(Unknown.Value);
                    break;
                }
                case Code.Stelem_I: case Code.Stelem_I1: case Code.Stelem_I2: case Code.Stelem_I4:
                case Code.Stelem_I8: case Code.Stelem_Ref: case Code.Stelem_Any:
                {
                    object? value = Pop(), index = Pop(), array = Pop();
                    if (array is Collection c && index is int i && i >= 0 && i < c.Items.Count) c.Items[i] = value;
                    break;
                }

                // ---- branches (forward and backward, bounded by StepLimit) ------------------------
                case Code.Br: case Code.Br_S: case Code.Leave: case Code.Leave_S:
                    next = indexOf[(Instruction)ins.Operand];
                    break;
                case Code.Brtrue: case Code.Brtrue_S: case Code.Brfalse: case Code.Brfalse_S:
                {
                    object? v = Pop();
                    bool onTrue = ins.OpCode.Code is Code.Brtrue or Code.Brtrue_S;
                    bool? truth = v switch
                    {
                        LambdaCache => false,          // cache not filled yet: build the delegate (fall through)
                        null => false,
                        int i => i != 0,
                        long l => l != 0,
                        bool b => b,
                        Unknown => null,
                        _ => true,                     // any object reference we built is non-null
                    };
                    if (truth is null) ambiguous++;    // data-dependent: fall through and let the caller flag it
                    else if (truth == onTrue) next = indexOf[(Instruction)ins.Operand];
                    break;
                }
                case Code.Beq: case Code.Beq_S: case Code.Bne_Un: case Code.Bne_Un_S:
                case Code.Bge: case Code.Bge_S: case Code.Bgt: case Code.Bgt_S: case Code.Ble: case Code.Ble_S:
                case Code.Blt: case Code.Blt_S: case Code.Bge_Un: case Code.Bge_Un_S: case Code.Bgt_Un:
                case Code.Bgt_Un_S: case Code.Ble_Un: case Code.Ble_Un_S: case Code.Blt_Un: case Code.Blt_Un_S:
                {
                    object? b = Pop(), a = Pop();
                    if (a is int x && b is int y)
                    {
                        bool take = ins.OpCode.Code switch
                        {
                            Code.Beq or Code.Beq_S => x == y,
                            Code.Bne_Un or Code.Bne_Un_S => x != y,
                            Code.Bge or Code.Bge_S or Code.Bge_Un or Code.Bge_Un_S => x >= y,
                            Code.Bgt or Code.Bgt_S or Code.Bgt_Un or Code.Bgt_Un_S => x > y,
                            Code.Ble or Code.Ble_S or Code.Ble_Un or Code.Ble_Un_S => x <= y,
                            _ => x < y,
                        };
                        if (take) next = indexOf[(Instruction)ins.Operand];
                    }
                    else ambiguous++;
                    break;
                }

                // ---- calls ------------------------------------------------------------------------
                case Code.Call:
                case Code.Callvirt:
                case Code.Newobj:
                {
                    var target = (MethodReference)ins.Operand;
                    bool isNewobj = ins.OpCode.Code == Code.Newobj;
                    int argCount = target.Parameters.Count + (target.HasThis && !isNewobj ? 1 : 0);
                    var callArgs = new object?[argCount];
                    for (int i = argCount - 1; i >= 0; i--) callArgs[i] = Pop();

                    bool pushes = isNewobj || target.ReturnType.MetadataType != MetadataType.Void;
                    object? result = Unknown.Value;
                    HookAction action = hook?.Invoke(target, callArgs, isNewobj, out result) ?? HookAction.NotHandled;
                    if (action == HookAction.Stop) return new(true, null, null, ambiguous);
                    if (action == HookAction.NotHandled) result = Simulate(target, callArgs, isNewobj);
                    if (pushes) stack.Push(result);
                    break;
                }

                case Code.Ret:
                    return new(true, stack.Count > 0 ? stack.Pop() : null, null, ambiguous);

                default:
                    return new(false, null, $"unsupported opcode {ins.OpCode.Name} at IL_{ins.Offset:x4}", ambiguous);
            }
            pc = next;
        }
        return new(false, null, "step limit (loop?)", ambiguous);
    }

    /// <summary>`!0` inside PowerVar&lt;T&gt; -> the concrete T of the instance being built.</summary>
    private static TypeReference Substitute(TypeReference t, IList<TypeReference>? genericArgs) =>
        t is GenericParameter gp && genericArgs != null && gp.Position < genericArgs.Count ? genericArgs[gp.Position] : t;

    /// <summary>Converts a stack value to decimal the way C# implicit conversions would.</summary>
    public static decimal? ToDecimal(object? v) => v switch
    {
        decimal d => d,
        int i => i,
        long l => l,
        double d => (decimal)d,
        bool b => b ? 1m : 0m,
        PropertyValue p => p.Value,
        _ => null,
    };

    /// <summary>A handful of BCL calls that matter for constant building; everything else is Unknown.</summary>
    private static object? Simulate(MethodReference target, object?[] args, bool isNewobj)
    {
        string type = target.DeclaringType.FullName;
        string name = target.Name;

        if (type == "System.Decimal")
        {
            if (args.Length == 1 && args[0] is PropertyValue) return args[0]; // int -> decimal keeps where it came from
            if (isNewobj && args.Length == 1) return ToDecimal(args[0]) ?? (object)Unknown.Value;
            if (isNewobj && args.Length == 5 && args[0] is int lo && args[1] is int mid && args[2] is int hi && args[4] is int scale)
                return new decimal(lo, mid, hi, args[3] is int neg && neg != 0, (byte)scale);
            if ((name is "op_Implicit" or "op_Explicit") && args.Length == 1) return ToDecimal(args[0]) ?? (object)Unknown.Value;
            return Unknown.Value;
        }
        if (type == "System.Convert" && name == "ToDecimal" && args.Length == 1)
            return ToDecimal(args[0]) ?? (object)Unknown.Value;

        if (type == "System.Type" && name == "GetTypeFromHandle" && args.Length == 1 && args[0] is TypeValue tv)
            return tv;
        if (name == "get_Name" && args.Length == 1 && args[0] is TypeValue named)
            return named.Type.Name; // typeof(T).Name: the bare class name, e.g. "StrengthPower"

        if (type == "System.Array" && name == "Empty") return new Collection();

        string typeName = target.DeclaringType.Name;
        if (isNewobj && IsCollectionType(typeName))
        {
            var coll = new Collection();
            if (args.Length == 1)
            {
                if (args[0] is Collection src) coll.Items.AddRange(src.Items);
                else if (target.Parameters[0].ParameterType.IsGenericParameter) coll.Items.Add(args[0]);
                else if (target.Parameters[0].ParameterType.MetadataType != MetadataType.Int32) return Unknown.Value;
            }
            else if (args.Length > 1) return Unknown.Value;
            return coll;
        }
        if (!isNewobj && name == "Add" && args.Length == 2 && args[0] is Collection addTo)
        {
            addTo.Items.Add(args[1]);
            return 1;
        }
        return Unknown.Value;
    }

    private static bool IsCollectionType(string typeName) =>
        typeName.StartsWith("List`", StringComparison.Ordinal) ||
        typeName.StartsWith("HashSet`", StringComparison.Ordinal) ||
        typeName.Contains("ReadOnlyArray`", StringComparison.Ordinal) ||
        typeName.Contains("ReadOnlySingleElementList`", StringComparison.Ordinal) ||
        typeName.Contains("ReadOnlyList`", StringComparison.Ordinal);

    /// <summary>Trailing zeros off (1.50 -> 1.5) so JSON shows the number the way the source wrote it.</summary>
    public static decimal Normalize(decimal d) =>
        decimal.Parse(d.ToString("0.############################", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
