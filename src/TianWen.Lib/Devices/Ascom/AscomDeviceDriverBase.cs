using Microsoft.Extensions.Logging;
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Ascom.ComInterop;

namespace TianWen.Lib.Devices.Ascom;

[SupportedOSPlatform("windows")]
internal abstract class AscomDeviceDriverBase(AscomDevice device, IServiceProvider serviceProvider)
    : DeviceDriverBase<AscomDevice, AscomDeviceInfo>(device, serviceProvider), IDeviceDriver
{
    protected readonly AscomDispatchDevice _dispatchDevice = new(device.DeviceId);

    public override string Name => _dispatchDevice.Name;

    public override string? Description => _dispatchDevice.Description;

    public override string? DriverInfo => _dispatchDevice.DriverInfo;

    public override string? DriverVersion => _dispatchDevice.DriverVersion;

    protected override async Task<(bool Success, int ConnectionId, AscomDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        bool success;
        try
        {
            _dispatchDevice.Connect();

            while (!cancellationToken.IsCancellationRequested && _dispatchDevice.Connecting)
            {
                await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
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
            _dispatchDevice.Disconnect();

            while (!cancellationToken.IsCancellationRequested && _dispatchDevice.Connecting)
            {
                await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            return !_dispatchDevice.Connected;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to disconnect from ASCOM device {DeviceId} ({DisplayName}): {ErrorMessage}", _device.DeviceId, _device.DisplayName, e.Message);
            return false;
        }
    }

    protected override void DisposeUnmanaged()
    {
        _dispatchDevice.Dispose();
        base.DisposeUnmanaged();
    }
}

internal record struct AscomDeviceInfo();
