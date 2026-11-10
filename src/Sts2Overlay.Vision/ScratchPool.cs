using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sts2Overlay.Vision;

/// <summary>
/// A small pool of scratch arrays for one role of the per-frame recognisers (integral tables, price label grids), used
/// instead of <see cref="System.Buffers.ArrayPool{T}.Shared"/>.
///
/// <para>Why not the shared pool: it rounds every request up to a power of two (an 11.8 MB card-grid table takes a
/// 16 MB array), keeps one array per size class in EVERY thread that returned one (the capture thread and each
/// Parallel.For worker) and trims only on a gen2 GC. Measured at 4K (tools/MemProbe heap): 43 MB held between frames,
/// 31 MB of it the integral tables of four bands that are never needed at the same time. On a PC under memory load the
/// shared pool instead drops everything at each gen2 GC, and the next frame allocates the large arrays again.</para>
///
/// <para>Here a role keeps at most <see cref="Keep"/> arrays of exactly the size asked for (see <see cref="Rent"/> for
/// which one a request gets), so the integral tables settle at one grid-band table plus one shop-bottom-row table.
/// The arrays are not cleared: callers write before they read. All pools let go of their arrays once recognition has
/// been idle for <see cref="VisionScratch.IdleRelease"/>.</para>
/// </summary>
internal sealed class ScratchPool<T>
{
    private readonly Lock _lock = new();
    private readonly T[]?[] _free;
    private int _rented; // arrays handed out and not yet returned

    /// <param name="role">What the arrays are for (diagnostics).</param>
    /// <param name="keep">Free arrays kept at most.</param>
    /// <param name="register">Part of <see cref="VisionScratch"/> (its held total and release); false for a test's own pool.</param>
    public ScratchPool(string role, int keep, bool register = true)
    {
        Role = role;
        _free = new T[]?[Math.Max(1, keep)];
        if (register) VisionScratch.Register(Release, HeldBytes);
    }

    public string Role { get; }
    public int Keep => _free.Length;

    /// <summary>An array of at least <paramref name="length"/> elements; its contents are left over from earlier use.</summary>
    public T[] Rent(int length)
    {
        length = Math.Max(1, length);
        VisionScratch.Touch();
        lock (_lock)
        {
            // The first array of a burst (none rented out) is the one that must be large: the card-grid band follows the
            // reward band on the same frame, the shop's top row is three times its bottom row. A request made while
            // others are out takes the smallest that fits, so the second slot stays the size of the second need.
            bool first = _rented == 0;
            _rented++;
            int smallestFit = -1, largestFit = -1, largestShort = -1;
            for (int i = 0; i < _free.Length; i++)
            {
                if (_free[i] is not { } a) continue;
                if (a.Length >= length)
                {
                    if (smallestFit < 0 || a.Length < _free[smallestFit]!.Length) smallestFit = i;
                    if (largestFit < 0 || a.Length > _free[largestFit]!.Length) largestFit = i;
                }
                else if (largestShort < 0 || a.Length > _free[largestShort]!.Length) largestShort = i;
            }
            int pick = first ? largestFit : smallestFit;
            if (pick >= 0)
            {
                T[] hit = _free[pick]!;
                _free[pick] = null;
                return hit;
            }
            // Nothing fits the first (largest) need: the role is growing (a bigger band, a larger frame). The new array
            // replaces the largest free one, so the pool ends up holding the sizes in use, not every size it ever saw.
            if (first && largestShort >= 0) _free[largestShort] = null;
        }
        return GC.AllocateUninitializedArray<T>(length);
    }

    public void Return(T[] array)
    {
        lock (_lock)
        {
            _rented = Math.Max(0, _rented - 1);
            int empty = -1, smallest = -1;
            for (int i = 0; i < _free.Length; i++)
            {
                if (_free[i] is not { } a)
                {
                    if (empty < 0) empty = i;
                }
                else if (smallest < 0 || a.Length < _free[smallest]!.Length) smallest = i;
            }
            if (empty >= 0) _free[empty] = array;
            else if (_free[smallest]!.Length < array.Length) _free[smallest] = array; // keep the larger one
        }
        VisionScratch.Touch();
    }

    /// <summary>Drops every free array; returns their size in bytes.</summary>
    public long Release()
    {
        long bytes = 0;
        lock (_lock)
        {
            for (int i = 0; i < _free.Length; i++)
            {
                if (_free[i] is { } a) bytes += (long)a.Length * Unsafe.SizeOf<T>();
                _free[i] = null;
            }
        }
        return bytes;
    }

    /// <summary>Bytes of the free arrays held right now.</summary>
    public long HeldBytes()
    {
        long bytes = 0;
        lock (_lock)
        {
            foreach (T[]? a in _free)
                if (a is not null) bytes += (long)a.Length * Unsafe.SizeOf<T>();
        }
        return bytes;
    }
}

/// <summary>
/// The scratch memory of the per-frame recognisers (<see cref="CardRecognizer"/>, the merchant relic reader, the shop
/// price reader) and its idle release. While frames come in (1.5-20 Hz) the arrays are reused; once no recogniser has
/// touched them for <see cref="IdleRelease"/> (capture paused: game not in front or overlay hidden; or the screen did not
/// change) they are dropped, and one background GC hands them back to the system: the app hardly allocates while it
/// idles, so the dropped large arrays could otherwise stay committed until some much later gen2 collection.
/// </summary>
public static class VisionScratch
{
    private static readonly Lock Gate = new();
    private static readonly List<(Func<long> Release, Func<long> Held)> Pools = [];
    private static long _lastUse = Stopwatch.GetTimestamp();
    private static Timer? _timer;
    private static TimeSpan _idleRelease = TimeSpan.FromSeconds(30);

    /// <summary>Released at least this much: start the background GC (smaller amounts wait for the next natural one).</summary>
    private const long CollectAfterRelease = 4 << 20;

    /// <summary>Card-grid, reward, shop and relic band integral tables: at most two are in use at once (the shop's two rows).</summary>
    internal static readonly ScratchPool<int> Tables = new("integral tables", keep: 2);

    /// <summary>Shop price label grids, read from up to seven slot workers at once.</summary>
    internal static readonly ScratchPool<float> PriceCover = new("price cover", keep: 4);
    internal static readonly ScratchPool<float> PriceText = new("price text", keep: 4);
    internal static readonly ScratchPool<int> PriceLabels = new("price labels", keep: 4);

    /// <summary>Bytes held by the free scratch arrays right now (tools/MemProbe).</summary>
    public static long HeldBytes
    {
        get
        {
            lock (Gate) return Pools.Sum(p => p.Held());
        }
    }

    /// <summary>Drops all free scratch arrays now; returns their size in bytes (no GC is started).</summary>
    public static long Release()
    {
        lock (Gate) return Pools.Sum(p => p.Release());
    }

    /// <summary>Idle time after which the scratch arrays are dropped; <see cref="Timeout.InfiniteTimeSpan"/> keeps them.</summary>
    public static TimeSpan IdleRelease
    {
        get => _idleRelease;
        set
        {
            lock (Gate) _idleRelease = value;
        }
    }

    internal static void Register(Func<long> release, Func<long> held)
    {
        lock (Gate) Pools.Add((release, held));
    }

    /// <summary>A recogniser used its scratch: restart the idle clock, and the idle check if it had stopped.</summary>
    internal static void Touch()
    {
        Volatile.Write(ref _lastUse, Stopwatch.GetTimestamp());
        if (Volatile.Read(ref _timer) is not null) return;
        lock (Gate)
        {
            if (_timer is not null || _idleRelease == Timeout.InfiniteTimeSpan) return;
            // A third of the idle time: arrays go at most a third later than asked; the check itself is a clock read.
            TimeSpan period = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, _idleRelease.Ticks / 3));
            _timer = new Timer(static _ => OnTimer(), null, period, period);
        }
    }

    private static void OnTimer()
    {
        if (ReleaseIfIdle(Stopwatch.GetTimestamp()) >= CollectAfterRelease)
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
    }

    /// <summary>
    /// At <paramref name="now"/> (a <see cref="Stopwatch"/> timestamp): when nothing used the scratch for
    /// <see cref="IdleRelease"/>, stops the idle check (the next use starts it again) and drops the free arrays.
    /// Returns the bytes dropped (0 when not idle).
    /// </summary>
    internal static long ReleaseIfIdle(long now)
    {
        lock (Gate)
        {
            if (_idleRelease == Timeout.InfiniteTimeSpan || Stopwatch.GetElapsedTime(Volatile.Read(ref _lastUse), now) < _idleRelease) return 0;
            _timer?.Dispose();
            _timer = null;
            return Pools.Sum(p => p.Release());
        }
    }
}
