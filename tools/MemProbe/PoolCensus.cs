using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MemProbe;

/// <summary>
/// What <c>ArrayPool&lt;T&gt;.Shared</c> holds right now, read by reflection from the runtime's SharedArrayPool: every
/// thread's thread-local slots (the pool keeps them in <c>_allTlsBuckets</c>, a ConditionalWeakTable, for trimming) and
/// the per-core partitions (<c>_buckets</c>). ClrMD does not report .NET 10 thread statics as roots, so a heap walk
/// alone would call the thread-local arrays garbage. Runtime internals: names checked against .NET 10.
/// </summary>
internal static class PoolCensus
{
    public sealed record Pooled(string Element, int Length, long Bytes, string Where, object Array);

    public static List<Pooled> Collect()
    {
        var list = new List<Pooled>();
        Collect<byte>(list);
        Collect<int>(list);
        Collect<float>(list);
        Collect<double>(list);
        Collect<ushort>(list);
        Collect<short>(list);
        Collect<uint>(list);
        Collect<bool>(list);
        return list;
    }

    private static void Collect<T>(List<Pooled> list)
    {
        object pool = System.Buffers.ArrayPool<T>.Shared;
        Type type = pool.GetType();
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        string elem = typeof(T).Name;
        long size = Unsafe.SizeOf<T>();

        // Thread-local slots of every thread that ever used this pool.
        if (type.GetField("_allTlsBuckets", F)?.GetValue(pool) is IEnumerable tls)
        {
            foreach (object kv in tls)
            {
                if (kv.GetType().GetProperty("Key")?.GetValue(kv) is not Array slots) continue;
                foreach (object? slot in slots)
                {
                    if (slot is null) continue;
                    if (slot.GetType().GetField("Array", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(slot) is T[] a)
                        list.Add(new Pooled(elem, a.Length, a.Length * size, "thread-local", a));
                }
            }
        }
        // Per-core partitions: _buckets[bucket]._partitions[core]._arrays[0.._count).
        if (type.GetField("_buckets", F)?.GetValue(pool) is Array buckets)
        {
            foreach (object? bucket in buckets)
            {
                if (bucket?.GetType().GetField("_partitions", F)?.GetValue(bucket) is not Array partitions) continue;
                foreach (object? part in partitions)
                {
                    if (part?.GetType().GetField("_arrays", F)?.GetValue(part) is not Array arrays) continue;
                    foreach (object? a in arrays)
                        if (a is T[] arr) list.Add(new Pooled(elem, arr.Length, arr.Length * size, "per-core", arr));
                }
            }
        }
    }
}
