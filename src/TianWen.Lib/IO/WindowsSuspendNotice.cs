using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.IO;

/// <summary>
/// <see cref="ISuspendNotice"/> on Windows: <c>PowerRegisterSuspendResumeNotification</c> with <c>DEVICE_NOTIFY_CALLBACK</c>,
/// so the notice arrives on a system thread with no window and no message pump (AOT-clean, unlike <c>SystemEvents</c>).
/// Unregistered when disposed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsSuspendNotice : ISuspendNotice, IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmSuspend = 0x4;
    private const uint PbtApmResumeAutomatic = 0x12;

    /// <summary>How long the callback waits for the handlers: Windows allows about two seconds before it suspends.</summary>
    internal static readonly TimeSpan CallbackWait = TimeSpan.FromSeconds(1.8);

    // DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS: the callback routine and its context.
    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint> Callback;
        public IntPtr Context;
    }

    private readonly ILogger _logger;
    private GCHandle _self;
    private IntPtr _registration;

    public event Func<Task>? Suspending;

    public event Action? Resumed;

    public WindowsSuspendNotice(ILogger logger)
    {
        _logger = logger;
        _self = GCHandle.Alloc(this);
        var parameters = new SubscribeParameters { Callback = &OnPowerEvent, Context = GCHandle.ToIntPtr(_self) };
        var result = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, in parameters, out _registration);
        if (result != 0)
        {
            _registration = IntPtr.Zero;
            _self.Free();
            _logger.LogWarning("Could not subscribe to suspend notices ({Message}); the mounts are not stopped before a suspend",
                new Win32Exception((int)result).Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnPowerEvent(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is WindowsSuspendNotice notice)
            {
                if (type == PbtApmSuspend)
                {
                    notice.RaiseSuspending();
                }
                else if (type == PbtApmResumeAutomatic)
                {
                    notice.Resumed?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            // Nothing may escape an unmanaged callback.
            try
            {
                if (GCHandle.FromIntPtr(context).Target is WindowsSuspendNotice notice)
                {
                    notice._logger.LogError(ex, "A suspend notice handler failed");
                }
            }
            catch
            {
                // Logging is best effort here.
            }
        }
        return 0;
    }

    private void RaiseSuspending()
    {
        var handlers = Suspending?.GetInvocationList().Cast<Func<Task>>().ToArray() ?? [];
        if (handlers.Length == 0)
        {
            return;
        }
        var tasks = handlers.Select(static handler => handler()).ToArray();
        // The one place that blocks, and it must: the OS suspends the machine when this callback returns, so the work has to be
        // done by then. It is the OS's own thread, never a pool or render thread, and the wait is bounded.
        if (!Task.WaitAll(tasks, CallbackWait))
        {
            _logger.LogWarning("A suspend handler was still working after {Wait}; the suspend goes ahead", CallbackWait);
        }
    }

    public void Dispose()
    {
        var registration = Interlocked.Exchange(ref _registration, IntPtr.Zero);
        if (registration != IntPtr.Zero)
        {
            PowerUnregisterSuspendResumeNotification(registration);
            _self.Free();
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, in SubscribeParameters recipient, out IntPtr registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
