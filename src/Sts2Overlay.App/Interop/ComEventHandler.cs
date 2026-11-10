using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sts2Overlay.App.Interop;

/// <summary>
/// A COM object that WinRT can call back: one instantiation of <c>TypedEventHandler&lt;TSender, IInspectable&gt;</c>, built
/// by hand so we need no WinRT projection.
///
/// A WinRT delegate is a plain COM interface: IUnknown (slots 0-2) plus <c>Invoke(sender, args)</c> (slot 3), see the
/// C vtables in windows.graphics.capture.h (10.0.22621.0) lines 1721 and 1770. Its IID depends on the type arguments
/// (a "parameterized interface" IID), so one vtable serves every instantiation and the IID lives in each object.
///
/// The object is a small native block: [vtable pointer | ref count | IID | GCHandle of the managed callback | free-threaded
/// marshaler]. While native code holds a reference the GCHandle keeps the callback (and what it captures) alive; the
/// last Release frees the handle and the block. Event sources call Invoke on their own threads (the capture frame pool
/// is free-threaded), so the object is agile: it answers IAgileObject and hands out the free-threaded marshaler for
/// IMarshal, which tells COM to pass the raw pointer to other apartments instead of building a proxy.
///
/// Why not ComWrappers / [GeneratedComClass]: their QueryInterface is not ours to control, and the delegate IIDs are
/// computed values that a [Guid] attribute on a generic interface cannot express. Hand-built, every slot and every
/// answered IID is visible here, in the same style as <see cref="D3D11Interop"/>.
/// </summary>
internal static unsafe class ComEventHandler
{
    private const int S_OK = 0;
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_POINTER = unchecked((int)0x80004003);

    /// <summary>IUnknown, Unknwnbase.h line 116.</summary>
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");

    /// <summary>IAgileObject (marker: "call me from any thread"), objidlbase.h line 847.</summary>
    internal static readonly Guid IID_IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

    /// <summary>IMarshal, objidlbase.h line 555.</summary>
    internal static readonly Guid IID_IMarshal = new("00000003-0000-0000-c000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    private struct Handler
    {
        public void** Vtbl;
        public int RefCount;
        public Guid Iid;
        public IntPtr Callback; // GCHandle (strong) of the Action<IntPtr>
        public IntPtr Ftm;      // the aggregated free-threaded marshaler's own (non-delegating) IUnknown
    }

    private static readonly void** Vtbl = CreateVtbl();

    private static int _live;

    /// <summary>Handler objects not yet freed by their last Release (for leak checks).</summary>
    public static int LiveObjects => Volatile.Read(ref _live);

    private static void** CreateVtbl()
    {
        // Type-associated memory lives as long as the type, i.e. the process: the vtable is never freed.
        var v = (void**)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ComEventHandler), 4 * sizeof(void*));
        v[0] = (delegate* unmanaged[Stdcall]<Handler*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&Release;
        v[3] = (delegate* unmanaged[Stdcall]<Handler*, IntPtr, IntPtr, int>)&Invoke;
        return v;
    }

    /// <summary>
    /// A new handler object answering <paramref name="iid"/>, with one reference owned by the caller (release it once it
    /// is registered: the event source keeps its own). <paramref name="callback"/> gets the sender pointer, which is only
    /// valid during the call (no reference is added for it).
    /// </summary>
    public static IntPtr Create(in Guid iid, Action<IntPtr> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var self = (Handler*)NativeMemory.AllocZeroed((nuint)sizeof(Handler));
        self->Vtbl = Vtbl;
        self->RefCount = 1;
        self->Iid = iid;
        self->Callback = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
        // Aggregated: the marshaler's IMarshal forwards AddRef/Release to us, so it shares our lifetime.
        var hr = CoCreateFreeThreadedMarshaler((IntPtr)self, out var ftm);
        if (hr < 0)
        {
            GCHandle.FromIntPtr(self->Callback).Free();
            NativeMemory.Free(self);
            Marshal.ThrowExceptionForHR(hr);
        }
        self->Ftm = ftm;
        Interlocked.Increment(ref _live);
        return (IntPtr)self;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface(Handler* self, Guid* iid, void** result)
    {
        if (result is null) return E_POINTER;
        if (iid is null)
        {
            *result = null;
            return E_POINTER;
        }
        var id = *iid;
        if (id == IID_IUnknown || id == self->Iid || id == IID_IAgileObject)
        {
            Interlocked.Increment(ref self->RefCount);
            *result = self;
            return S_OK;
        }
        if (id == IID_IMarshal)
        {
            // IUnknown::QueryInterface of the marshaler's inner object (slot 0); it AddRefs us through the aggregation.
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, void**, int>)(*(void***)self->Ftm)[0])(self->Ftm, iid, result);
        }
        *result = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRef(Handler* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Release(Handler* self)
    {
        var count = Interlocked.Decrement(ref self->RefCount);
        if (count == 0)
        {
            // The marshaler's inner IUnknown is non-delegating: releasing it never calls back into us.
            if (self->Ftm != IntPtr.Zero) Marshal.Release(self->Ftm);
            GCHandle.FromIntPtr(self->Callback).Free();
            NativeMemory.Free(self);
            Interlocked.Decrement(ref _live);
        }
        return (uint)count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Invoke(Handler* self, IntPtr sender, IntPtr args)
    {
        // A managed exception must never unwind into the caller's native frames: report it as an HRESULT instead.
        try
        {
            ((Action<IntPtr>)GCHandle.FromIntPtr(self->Callback).Target!)(sender);
            return S_OK;
        }
        catch (Exception ex)
        {
            return ex.HResult < 0 ? ex.HResult : E_FAIL;
        }
    }

    /// <summary>combaseapi.h line 862.</summary>
    [DllImport("ole32.dll")]
    private static extern int CoCreateFreeThreadedMarshaler(IntPtr outer, out IntPtr marshaler);
}
