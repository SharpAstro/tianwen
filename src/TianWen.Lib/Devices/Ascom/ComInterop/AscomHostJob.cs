using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// A process-wide Win32 Job Object with <c>KILL_ON_JOB_CLOSE</c> that every spawned
/// <c>tianwen-ascomhost</c> helper is assigned to. When our process exits (even a hard kill), the OS
/// closes the job handle and terminates every assigned helper — no orphaned CET-off hosts.
/// <para>
/// This is a backstop. The primary lifetime tie is the loopback socket: when the parent dies its TCP
/// connection closes, the helper's server loop sees EOF and exits on its own. The Job Object covers the
/// edge case where a helper is blocked in a driver COM call and hasn't noticed the EOF yet.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class AscomHostJob
{
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    private static readonly Lock _gate = new();
    private static nint _job; // 0 until created; intentionally never closed (closing it would kill helpers)

    /// <summary>Assigns <paramref name="processHandle"/> to the shared kill-on-close job. Best-effort:
    /// on any failure the caller still has the socket-EOF lifetime tie, so we swallow and move on.</summary>
    public static void TryAssign(nint processHandle)
    {
        try
        {
            var job = EnsureJob();
            if (job != 0)
            {
                _ = AssignProcessToJobObject(job, processHandle);
            }
        }
        catch
        {
            // best-effort backstop only
        }
    }

    private static nint EnsureJob()
    {
        if (_job != 0)
        {
            return _job;
        }

        lock (_gate) // one-time job creation; not on any hot/render path
        {
            if (_job != 0)
            {
                return _job;
            }

            var job = CreateJobObjectW(0, null);
            if (job == 0)
            {
                return 0;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                _ = CloseHandle(job);
                return 0;
            }

            _job = job;
            return _job;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint hJob, int jobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
