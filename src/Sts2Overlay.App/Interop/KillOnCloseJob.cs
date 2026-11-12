using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sts2Overlay.App.Interop;

/// <summary>
/// A Windows job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every process put in it is killed when the last
/// handle to the job closes. The overlay holds that handle for its whole life (<see cref="Shared"/>, never closed), so when
/// the overlay exits in any way, including a crash or "End task", Windows closes the handle and kills the child helper
/// processes with it. Children the helper starts itself land in the same job.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private static readonly Lazy<KillOnCloseJob?> s_shared = new(TryCreate);

    private readonly SafeFileHandle _handle;

    private KillOnCloseJob(SafeFileHandle handle) => _handle = handle;

    /// <summary>The process-wide job, created on first use; null when Windows refused (the caller then only kills on exit).</summary>
    public static KillOnCloseJob? Shared => s_shared.Value;

    /// <summary>The last Win32 error of a failed create (for the log), or 0.</summary>
    public static int CreateError { get; private set; }

    private static KillOnCloseJob? TryCreate()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            CreateError = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CreateError = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }
        return new KillOnCloseJob(handle);
    }

    /// <summary>Puts <paramref name="process"/> in the job. False (with the Win32 error) when Windows refused.</summary>
    public bool TryAdd(Process process, out int error)
    {
        error = 0;
        try
        {
            if (AssignProcessToJobObject(_handle, process.Handle)) return true;
            error = Marshal.GetLastPInvokeError();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            error = -1; // the process is already gone
        }
        return false;
    }

    /// <summary>Closing the handle kills every process in the job. <see cref="Shared"/> is never disposed.</summary>
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
