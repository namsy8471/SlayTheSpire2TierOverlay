using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CatalogBuilder;

// ---------------------------------------------------------------------------------------------
// A *tiny* symbolic IL interpreter.
//
// The C# compiler turns a property like
//     public override CardType Type => CardType.Skill;
// into IL like
//     ldc.i4.2      // push the integer 2 (enums are just ints in IL)
//     ret           // return the value on top of the stack
//
// Instead of pattern-matching dozens of IL shapes by hand, we "run" the method on a fake
// evaluation stack. We only understand straight-line code (no branches) and only a few kinds of
// values: integer constants, strings, simple collections (arrays / HashSet / List ...) and
// "Unknown". That is enough for constant getters, collection initializers and constructor calls
// like `: base(1, CardType.Attack, ...)`. Anything fancier (if/else, loops) makes evaluation
// fail, and the caller records the field as unresolved.
// ---------------------------------------------------------------------------------------------

/// <summary>Placeholder for "some value we cannot know statically".</summary>
public sealed class Unknown
{
    public static readonly Unknown Value = new();
    public override string ToString() => "?";
}

/// <summary>The `this` reference (ldarg.0 inside an instance method).</summary>
public sealed class ThisRef
{
    public static readonly ThisRef Value = new();
    public override string ToString() => "this";
}

/// <summary>An array or collection we are building up (new T[n], new HashSet&lt;T&gt; { ... }).</summary>
public sealed class CollectionValue
{
    public List<object?> Items { get; } = new();
}

/// <summary>Result of evaluating a method body.</summary>
public sealed record EvalResult(bool Ok, object? ReturnValue, string? Failure)
{
    public static EvalResult Fail(string why) => new(false, null, why);
}

public static class IlEvaluator
{
    /// <summary>
    /// Hook called for every call/callvirt. Receives the target method and the popped arguments
    /// (for instance methods, args[0] is the receiver). Return true to stop evaluation right there
    /// (used to capture the base-constructor call).
    /// </summary>
    public delegate bool CallHook(MethodReference method, object?[] args);

    /// <param name="method">Method whose body to evaluate.</param>
    /// <param name="args">Values for ldarg.N. If null, arg0 is `this` and the rest are Unknown.</param>
    /// <param name="onCall">Optional hook, see <see cref="CallHook"/>.</param>
    public static EvalResult Evaluate(MethodDefinition method, object?[]? args = null, CallHook? onCall = null)
    {
        if (!method.HasBody)
            return EvalResult.Fail("no body");

        var stack = new Stack<object?>();
        var locals = new Dictionary<int, object?>();

        object? Arg(int index)
        {
            if (args != null)
                return index < args.Length ? args[index] : Unknown.Value;
            return (method.HasThis && index == 0) ? ThisRef.Value : Unknown.Value;
        }

        object? Pop() => stack.Count > 0 ? stack.Pop() : Unknown.Value;

        foreach (Instruction ins in method.Body.Instructions)
        {
            OpCode op = ins.OpCode;
            switch (op.Code)
            {
                case Code.Nop:
                    break;

                // ---- constants ------------------------------------------------------------
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
                case Code.Ldc_I8:
                case Code.Ldc_R4:
                case Code.Ldc_R8: stack.Push(Unknown.Value); break;
                case Code.Ldstr: stack.Push((string)ins.Operand); break;
                case Code.Ldnull: stack.Push(null); break;
                case Code.Ldtoken: stack.Push(ins.Operand); break; // e.g. a FieldReference for InitializeArray

                // ---- arguments and locals -------------------------------------------------
                case Code.Ldarg_0: stack.Push(Arg(0)); break;
                case Code.Ldarg_1: stack.Push(Arg(1)); break;
                case Code.Ldarg_2: stack.Push(Arg(2)); break;
                case Code.Ldarg_3: stack.Push(Arg(3)); break;
                case Code.Ldarg_S:
                case Code.Ldarg:
                    stack.Push(Arg(((ParameterDefinition)ins.Operand).Index + (method.HasThis ? 1 : 0)));
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
                case Code.Ldloc:
                    stack.Push(locals.GetValueOrDefault(((VariableDefinition)ins.Operand).Index, Unknown.Value));
                    break;

                // ---- stack shuffling ------------------------------------------------------
                case Code.Dup:
                {
                    object? top = Pop();
                    stack.Push(top);
                    stack.Push(top); // collections are reference types, so both copies share Items
                    break;
                }
                case Code.Pop: Pop(); break;

                // Conversions do not change our (integer) constants in a way we care about.
                case Code.Conv_I1: case Code.Conv_I2: case Code.Conv_I4: case Code.Conv_U1:
                case Code.Conv_U2: case Code.Conv_U4: case Code.Conv_I8: case Code.Conv_U8:
                    break;

                // ---- fields: we never know field values, just keep the stack balanced -------
                case Code.Ldfld: Pop(); stack.Push(Unknown.Value); break;
                case Code.Ldsfld: stack.Push(Unknown.Value); break;
                case Code.Stfld: Pop(); Pop(); break;
                case Code.Stsfld: Pop(); break;
                case Code.Ldsflda: stack.Push(Unknown.Value); break;
                case Code.Ldflda: Pop(); stack.Push(Unknown.Value); break;

                // Delegates (lambdas in field initializers) and boxing: keep the stack balanced.
                case Code.Ldftn: stack.Push(Unknown.Value); break;
                case Code.Ldvirtftn: Pop(); stack.Push(Unknown.Value); break;
                case Code.Box: case Code.Unbox_Any: case Code.Castclass: case Code.Isinst:
                    break; // value stays the same as far as we are concerned

                // ---- arrays ---------------------------------------------------------------
                case Code.Newarr:
                {
                    object? count = Pop();
                    if (count is int n)
                    {
                        var arr = new CollectionValue();
                        for (int i = 0; i < n; i++) arr.Items.Add(null);
                        stack.Push(arr);
                    }
                    else stack.Push(Unknown.Value);
                    break;
                }
                case Code.Stelem_I: case Code.Stelem_I1: case Code.Stelem_I2: case Code.Stelem_I4:
                case Code.Stelem_I8: case Code.Stelem_Ref: case Code.Stelem_Any:
                {
                    object? value = Pop();
                    object? index = Pop();
                    object? array = Pop();
                    if (array is CollectionValue c && index is int i && i >= 0 && i < c.Items.Count)
                        c.Items[i] = value;
                    break;
                }

                // ---- calls ----------------------------------------------------------------
                case Code.Call:
                case Code.Callvirt:
                case Code.Newobj:
                {
                    var target = (MethodReference)ins.Operand;
                    bool isNewobj = op.Code == Code.Newobj;
                    int argCount = target.Parameters.Count + (target.HasThis && !isNewobj ? 1 : 0);
                    var callArgs = new object?[argCount];
                    for (int i = argCount - 1; i >= 0; i--) callArgs[i] = Pop(); // args are pushed left-to-right

                    if (!isNewobj && onCall != null && onCall(target, callArgs))
                        return new EvalResult(true, null, null); // caller got what it wanted

                    object? result = SimulateCall(target, callArgs, isNewobj, out bool pushesValue);
                    if (pushesValue) stack.Push(result);
                    break;
                }

                case Code.Ret:
                    return new EvalResult(true, stack.Count > 0 ? stack.Pop() : null, null);

                default:
                    // Branches, switch, throw, ... -> not straight-line code, give up.
                    return EvalResult.Fail($"unsupported opcode {op.Name} at IL_{ins.Offset:x4}");
            }
        }
        return EvalResult.Fail("fell off end of method");
    }

    /// <summary>Gives a few well-known BCL calls a meaning; everything else returns Unknown.</summary>
    private static object? SimulateCall(MethodReference target, object?[] args, bool isNewobj, out bool pushesValue)
    {
        string typeName = target.DeclaringType.Name; // e.g. "HashSet`1"
        pushesValue = isNewobj || target.ReturnType.MetadataType != MetadataType.Void;

        // RuntimeHelpers.InitializeArray(array, fieldHandle): the compiler stores big constant
        // arrays as a raw byte blob in a static field and copies it in with this call.
        if (target.Name == "InitializeArray" && typeName == "RuntimeHelpers")
        {
            if (args[0] is CollectionValue arr && args[1] is FieldReference fr && fr.Resolve() is { } fd)
            {
                byte[] blob = fd.InitialValue;
                int elemSize = arr.Items.Count > 0 ? blob.Length / arr.Items.Count : 0;
                for (int i = 0; i < arr.Items.Count && elemSize > 0; i++)
                {
                    arr.Items[i] = elemSize switch
                    {
                        1 => blob[i],
                        2 => (int)BitConverter.ToInt16(blob, i * 2),
                        4 => BitConverter.ToInt32(blob, i * 4),
                        _ => Unknown.Value,
                    };
                }
            }
            return null;
        }

        // Array.Empty<T>()
        if (target.Name == "Empty" && typeName == "Array")
            return new CollectionValue();

        // Constructors of collection types: new HashSet<T>(), new List<T>(items),
        // and the compiler-generated <>z__ReadOnlyArray<T>(array) / <>z__ReadOnlySingleElementList<T>(item).
        if (isNewobj && IsCollectionType(typeName))
        {
            var coll = new CollectionValue();
            if (args.Length == 1)
            {
                bool paramIsElement = target.Parameters[0].ParameterType.IsGenericParameter; // T item
                if (args[0] is CollectionValue src)
                    coll.Items.AddRange(src.Items);          // copy from array / IEnumerable<T>
                else if (paramIsElement)
                    coll.Items.Add(args[0]);                 // single element list
                else if (target.Parameters[0].ParameterType.MetadataType != MetadataType.Int32)
                    return Unknown.Value;                    // IEnumerable we don't know
                // else: List<T>(int capacity) -> empty
            }
            else if (args.Length > 1)
                return Unknown.Value;
            return coll;
        }

        // collection.Add(item) (collection initializer syntax `{ a, b }` compiles to Add calls)
        if (!isNewobj && target.Name == "Add" && args.Length == 2 && args[0] is CollectionValue addTo)
        {
            addTo.Items.Add(args[1]);
            return 1; // HashSet.Add returns true; for List.Add pushesValue is false anyway
        }

        return Unknown.Value;
    }

    private static bool IsCollectionType(string typeName) =>
        typeName.StartsWith("HashSet`") ||
        typeName.StartsWith("List`") ||
        typeName.Contains("ReadOnlyArray`") ||
        typeName.Contains("ReadOnlySingleElementList`") ||
        typeName.Contains("ReadOnlyList`");
}
