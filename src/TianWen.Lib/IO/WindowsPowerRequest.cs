using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.IO;

/// <summary>
/// <see cref="IPowerRequest"/> on Windows: one power request object per handle, set to <c>PowerRequestSystemRequired</c>
/// (the system stays awake, the display may sleep). Never <c>SetThreadExecutionState</c>, which belongs to a thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPowerRequest(ILogger logger) : IPowerRequest
{
    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;

    // REASON_CONTEXT: version, flags, then a union whose widest member (the detailed one) is three words and a pointer;
    // the simple form uses the first pointer-sized slot. Kept blittable so the source generator can pass it.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        public IntPtr Unused1;
        public IntPtr Unused2;
    }

    public IDisposable Acquire(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        var context = new ReasonContext { Version = 0, Flags = PowerRequestContextSimpleString, SimpleReasonString = text };
        var handle = PowerCreateRequest(in context);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastPInvokeError();
            Marshal.FreeHGlobal(text);
            logger.LogWarning("Could not create a power request ({Message}); the computer may sleep while the node holds hardware",
                new Win32Exception(error).Message);
            return NoPowerRequest.Handle;
        }
        if (!PowerSetRequest(handle, PowerRequestSystemRequired))
        {
            var error = Marshal.GetLastPInvokeError();
            CloseHandle(handle);
            Marshal.FreeHGlobal(text);
            logger.LogWarning("Could not set a power request ({Message}); the computer may sleep while the node holds hardware",
                new Win32Exception(error).Message);
            return NoPowerRequest.Handle;
        }
        return new Held(handle, text);
    }

    private sealed class Held(IntPtr handle, IntPtr text) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }
            PowerClearRequest(handle, PowerRequestSystemRequired);
            CloseHandle(handle);
            Marshal.FreeHGlobal(text);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr PowerCreateRequest(in ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(IntPtr request, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(IntPtr request, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
