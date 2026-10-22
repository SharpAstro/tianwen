using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.DAL;

namespace TianWen.Lib.Devices.DAL;

internal abstract class DALDeviceDriverBase<TDevice, TDeviceInfo>(TDevice device, IServiceProvider serviceProvider) : DeviceDriverBase<TDevice, TDeviceInfo>(device, serviceProvider)
    where TDevice : DeviceBase
    where TDeviceInfo : struct, INativeDeviceInfo
{
    /// <summary>
    /// How long a close waits for the calls inside the vendor's SDK to come back before it gives up closing. A call that
    /// outlives it is stuck in the SDK, and closing under it would be the use-after-free this gate exists to prevent.
    /// </summary>
    internal static readonly TimeSpan SdkDrainTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SdkDrainPoll = TimeSpan.FromMilliseconds(20);

    private const int SdkClosed = 0;
    private const int SdkOpen = 1;

    /// <summary>Whether a call may enter the SDK: opened by a connect that found the camera, closed by every close and reset.</summary>
    private int _sdkGate = SdkClosed;

    /// <summary>The calls inside the SDK now, each between <see cref="EnterSdk"/> and its scope's end, and a reset's own work.</summary>
    private int _sdkInFlight;

    /// <summary>
    /// The closes and resets under way: the gate opens again only when the last of them ends, so a reset that finishes
    /// while a disconnect waits for it does not let calls in under the disconnect's close.
    /// </summary>
    private int _sdkClosers;

    /// <summary>The calls inside the SDK now: for a test that holds one in to race a close against it.</summary>
    internal int SdkCallsInFlight => Volatile.Read(ref _sdkInFlight);

    protected abstract INativeDeviceIterator<TDeviceInfo> NewIterator();

    /// <summary>
    /// Enters the vendor's SDK for one call, or is refused because the device is closed or closing.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a gate (#1758).</b> A native call made on a device the SDK has closed is a use-after-free inside the
    /// vendor's library: an access violation no .NET handler catches, which ends the whole process with any camera's
    /// cooler left as it was (a QHY178M faulted that way on 2026-10-22). Telemetry reads, exposure polls and downloads run
    /// outside the connect/disconnect transition, so without this a poll or a download could be inside the SDK when a
    /// disconnect, a forced disconnect at shutdown or the driver's own reset closed it.</para>
    /// <para><b>The order is what makes it safe, with no lock:</b> a caller counts itself in, THEN looks at the gate; a
    /// closer shuts the gate, THEN waits for the count to drain (<see cref="CloseSdkGateAsync"/>). Both are full fences
    /// (<see cref="Interlocked"/>), so either the caller sees the gate shut and backs out, or the closer sees the caller
    /// and waits for it.</para>
    /// <para>Every call into the SDK through <c>_deviceInfo</c> goes inside a scope: <c>using var sdk = EnterSdk();</c>,
    /// then <c>sdk.Entered</c> says whether it may. A refusal is answered as a closed device is. Values the binding
    /// caches at open (its size, Bayer pattern, name) need no scope.</para>
    /// </remarks>
    protected SdkScope EnterSdk()
    {
        Interlocked.Increment(ref _sdkInFlight);
        if (Volatile.Read(ref _sdkGate) == SdkOpen)
        {
            return new SdkScope(this, true);
        }

        Interlocked.Decrement(ref _sdkInFlight);
        return new SdkScope(this, false);
    }

    private void ExitSdk() => Interlocked.Decrement(ref _sdkInFlight);

    /// <summary>One call's stay inside the SDK, ended when the scope is disposed.</summary>
    protected readonly ref struct SdkScope(DALDeviceDriverBase<TDevice, TDeviceInfo> owner, bool entered)
    {
        /// <summary>False when the device was closed or closing: make no call into the SDK.</summary>
        public bool Entered => entered;

        public void Dispose()
        {
            if (entered)
            {
                owner.ExitSdk();
            }
        }
    }

    /// <summary>
    /// Lets calls into the SDK again once a connect has found the device, unless a close or a reset is under way: then
    /// the last of them opens it (<see cref="EndSdkClose"/>). A connect runs under the driver's transition, so no close
    /// of its own runs beside it, and a reset finding the device again is itself a closer.
    /// </summary>
    private void OpenSdkGateUnlessClosing()
    {
        if (Volatile.Read(ref _sdkClosers) == 0)
        {
            Interlocked.Exchange(ref _sdkGate, SdkOpen);
        }
    }

    /// <summary>
    /// Shuts the gate and waits, at most <see cref="SdkDrainTimeout"/>, for every call inside the SDK to come back, so the
    /// caller can close or reset the device under nothing. False when one did not: the caller must NOT close then. Every
    /// call is paired with <see cref="EndSdkClose"/>, whatever it returned.
    /// </summary>
    /// <remarks>
    /// <see cref="OnSdkClosing"/> runs between the two, while calls may still be inside: a camera aborts an exposure there,
    /// which is what an SDK's abort from another thread is for, so a download blocked on it comes back.
    /// </remarks>
    protected async ValueTask<bool> CloseSdkGateAsync(string why)
    {
        Interlocked.Increment(ref _sdkClosers);
        Interlocked.Exchange(ref _sdkGate, SdkClosed);
        OnSdkClosing();

        var since = TimeProvider.GetTimestamp();
        while (Volatile.Read(ref _sdkInFlight) > 0)
        {
            if (TimeProvider.GetElapsedTime(since) >= SdkDrainTimeout)
            {
                Logger.LogError("{DeviceId} ({DisplayName}): {Count} call(s) still inside the vendor's SDK {Timeout} after {Why} began; "
                    + "not closing under them, which would crash the process. The device stays open until a later disconnect finds them gone.",
                    _device.DeviceId, _device.DisplayName, Volatile.Read(ref _sdkInFlight), SdkDrainTimeout, why);
                return false;
            }

            // Never the caller's token: a close that was asked for must finish draining, or it leaves the device half shut.
            await TimeProvider.SleepAsync(SdkDrainPoll, CancellationToken.None);
        }

        return true;
    }

    /// <summary>
    /// Ends a close begun by <see cref="CloseSdkGateAsync"/>: the gate opens again when <paramref name="reopen"/> and no
    /// other close or reset is still under way (a disconnect that closed the device leaves it shut).
    /// </summary>
    protected void EndSdkClose(bool reopen)
    {
        if (Interlocked.Decrement(ref _sdkClosers) == 0 && reopen)
        {
            Interlocked.Exchange(ref _sdkGate, SdkOpen);
        }
    }

    /// <summary>
    /// Counts the caller's own work in, after <see cref="CloseSdkGateAsync"/> drained everyone else: a reset that talks to
    /// the device with the gate shut, so a disconnect arriving meanwhile waits for it rather than closing under it. Paired
    /// with <see cref="ReleaseSdkHold"/>.
    /// </summary>
    protected void HoldSdk() => Interlocked.Increment(ref _sdkInFlight);

    /// <summary>Ends <see cref="HoldSdk"/>.</summary>
    protected void ReleaseSdkHold() => Interlocked.Decrement(ref _sdkInFlight);

    /// <summary>
    /// Called by <see cref="CloseSdkGateAsync"/> once the gate is shut and before it waits: a device ends here what would
    /// keep a call inside the SDK (a camera aborts its exposure). The gate is shut, so this calls <c>_deviceInfo</c>
    /// directly, and must not throw.
    /// </summary>
    protected virtual void OnSdkClosing()
    {
    }

    protected override Task<(bool Success, int ConnectionId, TDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        var deviceIterator = NewIterator();
        var searchId = _device.DeviceId;

        foreach (var deviceInfo in deviceIterator)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            bool needsClosing = false;
            try
            {
                var isOpen = deviceInfo.Open();
                if (isOpen && (IsSameSerialNumber(deviceInfo) || IsSameCustomId(deviceInfo) || IsSameName(deviceInfo)))
                {
                    needsClosing = false;
                    OpenSdkGateUnlessClosing();
                    return Task.FromResult((true, deviceInfo.ID, deviceInfo));
                }
                else if (isOpen)
                {
                    needsClosing = true;
                }
            }
            finally
            {
                if (needsClosing)
                {
                    deviceInfo.Close();
                }
            }
        }

        return Task.FromResult((false, CONNECTION_ID_UNKNOWN, default(TDeviceInfo)));

        bool IsSameSerialNumber(in TDeviceInfo deviceInfo) => deviceInfo.SerialNumber is { Length: > 0 } serialNumber && serialNumber == searchId;

        bool IsSameCustomId(in TDeviceInfo deviceInfo) => deviceInfo.IsUSB3Device && deviceInfo.CustomId is { Length: > 0 } customId && customId == searchId;

        bool IsSameName(in TDeviceInfo deviceInfo) => deviceInfo.Name is { Length: > 0 } name && name == searchId;
    }

    /// <summary>
    /// Closes the device once nothing is inside its SDK. False, leaving it open, when a call stayed inside past
    /// <see cref="SdkDrainTimeout"/>: the disconnect then fails and is tried again, where closing would have crashed.
    /// </summary>
    protected override async Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken)
    {
        var drained = await CloseSdkGateAsync("a disconnect");
        try
        {
            return drained && _deviceInfo.Close();
        }
        finally
        {
            // Shut either way: closed, nothing may call in; not drained, the disconnect failed and is tried again.
            EndSdkClose(reopen: false);
        }
    }
}
