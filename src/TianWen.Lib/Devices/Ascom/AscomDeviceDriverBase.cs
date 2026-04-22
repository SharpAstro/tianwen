using Microsoft.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Ascom.ComInterop;

namespace TianWen.Lib.Devices.Ascom;

[SupportedOSPlatform("windows")]
internal abstract class AscomDeviceDriverBase(AscomDevice device, IServiceProvider serviceProvider)
    : DeviceDriverBase<AscomDevice, AscomDeviceInfo>(device, serviceProvider), IDeviceDriver
{
    // ASCOM Platform 7 introduced the Connect() / Disconnect() methods and Connecting
    // property; Platform 6 drivers (GS Server, many OnStep ASCOM forks) only expose
    // the legacy `Connected = bool` property setter, which blocks until the handshake
    // completes. COMException(DISP_E_UNKNOWNNAME, 0x80020006) from GetDispId signals
    // "this driver doesn't expose that name" — we treat that as "Platform 6 driver"
    // and fall back.
    private const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);

    protected readonly AscomDispatchDevice _dispatchDevice = new(device.DeviceId);

    public override string Name => SafeGet(() => _dispatchDevice.Name, _device.DisplayName);

    public override string? Description => SafeGet(() => _dispatchDevice.Description, null);

    public override string? DriverInfo => SafeGet(() => _dispatchDevice.DriverInfo, null);

    public override string? DriverVersion => SafeGet(() => _dispatchDevice.DriverVersion, null);

    // ASCOM drivers routinely throw COMException for unimplemented members, partially-connected
    // hubs, or transient hardware faults. Any such exception escaping to AOT's unhandled-exception
    // handler fail-fasts the process (STATUS_STACK_BUFFER_OVERRUN 0xc0000409). Every COM property/
    // method call in a driver subclass goes through one of these three helpers.
    protected T SafeGet<T>(Func<T> read, T fallback, [CallerMemberName] string? member = null)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ASCOM {DeviceId} {Member} threw {Type}: {Msg}",
                _device.DeviceId, member, ex.GetType().Name, ex.Message);
            return fallback;
        }
    }

    protected void SafeDo(Action op, [CallerMemberName] string? member = null)
    {
        try
        {
            op();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ASCOM {DeviceId} {Member} threw {Type}: {Msg}",
                _device.DeviceId, member, ex.GetType().Name, ex.Message);
        }
    }

    // Async-facing COM invoke: never throws sync; faults the returned task on COM failure so
    // `await` sees the error (but the call-site expression — e.g. `return SafeTask(...)` —
    // can't escape with an unhandled sync throw out of a Task-returning method).
    protected Task SafeTask(Action op, [CallerMemberName] string? member = null)
    {
        try
        {
            op();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ASCOM {DeviceId} {Member} threw {Type}: {Msg}",
                _device.DeviceId, member, ex.GetType().Name, ex.Message);
            return Task.FromException(ex);
        }
    }

    protected ValueTask SafeValueTask(Action op, [CallerMemberName] string? member = null)
    {
        try
        {
            op();
            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ASCOM {DeviceId} {Member} threw {Type}: {Msg}",
                _device.DeviceId, member, ex.GetType().Name, ex.Message);
            return ValueTask.FromException(ex);
        }
    }

    protected override async Task<(bool Success, int ConnectionId, AscomDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        bool success;
        try
        {
            var usedLegacyConnect = TryPlatform7Connect() is false;
            if (usedLegacyConnect)
            {
                _dispatchDevice.Connected = true;
            }
            else
            {
                await PollWhileConnectingAsync(cancellationToken);
            }

            success = _dispatchDevice.Connected;
        }
        catch (Exception e)
        {
            success = false;
            Logger.LogError(e, "Failed to connect to ASCOM device {DeviceId} ({DisplayName}): {ErrorMessage}", _device.DeviceId, _device.DisplayName, e.Message);
        }

        return (success, success ? CONNECTION_ID_EXCLUSIVE : CONNECTION_ID_UNKNOWN, new AscomDeviceInfo());
    }

    protected override async Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken)
    {
        try
        {
            var usedLegacyDisconnect = TryPlatform7Disconnect() is false;
            if (usedLegacyDisconnect)
            {
                _dispatchDevice.Connected = false;
            }
            else
            {
                await PollWhileConnectingAsync(cancellationToken);
            }

            return !_dispatchDevice.Connected;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to disconnect from ASCOM device {DeviceId} ({DisplayName}): {ErrorMessage}", _device.DeviceId, _device.DisplayName, e.Message);
            return false;
        }
    }

    /// <summary>
    /// Returns true if the driver's <c>Connect()</c> method was invoked successfully,
    /// false if the method is missing (Platform 6 driver). Any other error propagates.
    /// </summary>
    private bool TryPlatform7Connect()
    {
        try
        {
            _dispatchDevice.Connect();
            return true;
        }
        catch (COMException ex) when (ex.HResult == DISP_E_UNKNOWNNAME)
        {
            Logger.LogDebug("ASCOM driver {DeviceId} has no Connect() method — falling back to Platform 6 `Connected = true`.", _device.DeviceId);
            return false;
        }
    }

    private bool TryPlatform7Disconnect()
    {
        try
        {
            _dispatchDevice.Disconnect();
            return true;
        }
        catch (COMException ex) when (ex.HResult == DISP_E_UNKNOWNNAME)
        {
            Logger.LogDebug("ASCOM driver {DeviceId} has no Disconnect() method — falling back to Platform 6 `Connected = false`.", _device.DeviceId);
            return false;
        }
    }

    /// <summary>
    /// Platform 7 <c>Connecting</c> polling. Platform 6 drivers don't expose
    /// <c>Connecting</c>; treat DISP_E_UNKNOWNNAME as "handshake was synchronous, done."
    /// </summary>
    private async Task PollWhileConnectingAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool stillConnecting;
            try
            {
                stillConnecting = _dispatchDevice.Connecting;
            }
            catch (COMException ex) when (ex.HResult == DISP_E_UNKNOWNNAME)
            {
                return;
            }

            if (!stillConnecting) return;
            await TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    protected override void DisposeUnmanaged()
    {
        // Null-safe: the field initializer `= new(device.DeviceId)` can throw (e.g.
        // CoCreateInstance fails with CLASS_NOT_REGISTERED for a 32-bit-only ProgID
        // in a 64-bit process). The object is then partially-constructed, becomes
        // eligible for finalization, and the GC calls DisposeUnmanaged on a still-
        // null `_dispatchDevice`. Guarding here keeps the finalizer safe so the
        // original construction exception (already logged upstream) is the only
        // thing that surfaces to the user.
        _dispatchDevice?.Dispose();
        base.DisposeUnmanaged();
    }
}

internal record struct AscomDeviceInfo();
