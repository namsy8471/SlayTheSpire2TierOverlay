using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sts2Overlay.App.Interop;

namespace Sts2Overlay.App.Tests;

/// <summary>Tests touching <see cref="ComEventHandler.LiveObjects"/> run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ComObjectsCollection
{
    public const string Name = "COM objects";
}

/// <summary>The hand-built WinRT delegate object: what native callers see through its vtable.</summary>
[Collection(ComObjectsCollection.Name)]
public unsafe class ComEventHandlerTests
{
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private static readonly Guid HandlerIid = new("51a947f7-79cf-5a3e-a3a5-1289cfa6dfe8");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid IID_IInspectable = new("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90");

    [Fact]
    public void QueryInterface_answers_IUnknown_the_handler_iid_and_IAgileObject_with_itself()
    {
        var h = ComEventHandler.Create(HandlerIid, _ => { });
        try
        {
            foreach (var iid in new[] { IID_IUnknown, HandlerIid, ComEventHandler.IID_IAgileObject })
            {
                Assert.Equal(0, Marshal.QueryInterface(h, in iid, out var p));
                Assert.Equal(h, p);
                Marshal.Release(p);
            }
            // A delegate is not IInspectable.
            Assert.Equal(E_NOINTERFACE, Marshal.QueryInterface(h, in IID_IInspectable, out var none));
            Assert.Equal(IntPtr.Zero, none);
        }
        finally
        {
            Assert.Equal(0, Marshal.Release(h));
        }
    }

    [Fact]
    public void IMarshal_comes_from_the_aggregated_free_threaded_marshaler_and_counts_on_the_handler()
    {
        var h = ComEventHandler.Create(HandlerIid, _ => { });
        Assert.Equal(0, Marshal.QueryInterface(h, in ComEventHandler.IID_IMarshal, out var marshal));
        Assert.NotEqual(IntPtr.Zero, marshal);
        Assert.NotEqual(h, marshal); // the marshaler's interface, not ours...
        Assert.Equal(3, Marshal.AddRef(h)); // ...but its reference is on the handler: 1 (ours) + 1 (IMarshal) + 1
        Assert.Equal(2, Marshal.Release(h));
        Assert.Equal(1, Marshal.Release(marshal));
        Assert.Equal(0, Marshal.Release(h));
    }

    [Fact]
    public void Another_apartment_gets_the_same_pointer_not_a_proxy()
    {
        // Standard marshaling has no proxy for this IID, so this only works because the object is free-threaded.
        var h = ComEventHandler.Create(HandlerIid, _ => { });
        try
        {
            Assert.Equal(0, CoMarshalInterThreadInterfaceInStream(in HandlerIid, h, out var stream));
            IntPtr other = IntPtr.Zero;
            var hr = -1;
            var sta = new Thread(() => hr = CoGetInterfaceAndReleaseStream(stream, in HandlerIid, out other));
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join();
            Assert.Equal(0, hr);
            Assert.Equal(h, other);
            Marshal.Release(other);
        }
        finally
        {
            Marshal.Release(h);
        }
    }

    [Fact]
    public void Invoke_passes_the_sender_and_turns_exceptions_into_hresults()
    {
        IntPtr seen = IntPtr.Zero;
        var ok = ComEventHandler.Create(HandlerIid, s => seen = s);
        var failing = ComEventHandler.Create(HandlerIid, _ => throw new InvalidOperationException("boom"));
        try
        {
            Assert.Equal(0, Invoke(ok, 0x1234, IntPtr.Zero));
            Assert.Equal((IntPtr)0x1234, seen);
            Assert.Equal(new InvalidOperationException().HResult, Invoke(failing, 1, IntPtr.Zero));
        }
        finally
        {
            Marshal.Release(ok);
            Marshal.Release(failing);
        }
    }

    [Fact]
    public void The_callback_lives_exactly_as_long_as_native_references()
    {
        var before = ComEventHandler.LiveObjects;
        var (h, callback) = CreateWithWeakCallback();
        Assert.Equal(before + 1, ComEventHandler.LiveObjects);
        Assert.Equal(2, Marshal.AddRef(h)); // e.g. an event source registering it
        Collect();
        Assert.True(callback.IsAlive); // only native code references it: the GCHandle keeps it
        Assert.Equal(1, Marshal.Release(h));
        Collect();
        Assert.True(callback.IsAlive);
        Assert.Equal(0, Marshal.Release(h));
        Assert.Equal(before, ComEventHandler.LiveObjects);
        Collect();
        Assert.False(callback.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IntPtr Handler, WeakReference Callback) CreateWithWeakCallback()
    {
        var captured = new object();
        Action<IntPtr> callback = _ => GC.KeepAlive(captured);
        return (ComEventHandler.Create(HandlerIid, callback), new WeakReference(callback));
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Invoke is slot 3 of every WinRT delegate (after IUnknown).</summary>
    private static int Invoke(IntPtr handler, IntPtr sender, IntPtr args)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)(*(void***)handler)[3])(handler, sender, args);

    [DllImport("ole32.dll")]
    private static extern int CoMarshalInterThreadInterfaceInStream(in Guid iid, IntPtr unknown, out IntPtr stream);

    [DllImport("ole32.dll")]
    private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, in Guid iid, out IntPtr result);
}
