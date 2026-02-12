using ASCOM.Common;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ASCOMDriverAccessDevice = ASCOM.Com.DriverAccess.ASCOMDevice;

namespace TianWen.Lib.Devices.Ascom;

[SupportedOSPlatform("windows")]
internal abstract class AscomDeviceDriverBase<TAscomDriverAccessDevice>(AscomDevice device, IExternal external, Func<string, ILogger, TAscomDriverAccessDevice> func)
    : DeviceDriverBase<AscomDevice,AscomDeviceInfo>(device, external), IDeviceDriver
    where TAscomDriverAccessDevice : ASCOMDriverAccessDevice
{
    protected readonly TAscomDriverAccessDevice _comObject = func(device.DeviceId, external.AppLogger);

    public override string Name => _comObject.Name;

    public override string? Description => _comObject.Description;

    public override string? DriverInfo => _comObject.DriverInfo;

    public override string? DriverVersion => _comObject.DriverVersion;

    protected override async Task<(bool Success, int ConnectionId, AscomDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        bool success;
        try
        {
            _comObject.Connect();

            while (!cancellationToken.IsCancellationRequested && _comObject.Connecting)
            {
                await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            success = _comObject.Connected;
        }
        catch (Exception e)
        {
            success = false;
            External.AppLogger.LogError(e, "Failed to connect to ASCOM device {DeviceId} ({DisplayName}): {ErrorMessage}", _device.DeviceId, _device.DisplayName, e.Message);
        }

        return (success, success ? CONNECTION_ID_EXCLUSIVE : CONNECTION_ID_UNKNOWN, new AscomDeviceInfo());
    }

    protected override async Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken)
    {
        try
        {
            _comObject.Disconnect();

            while (!cancellationToken.IsCancellationRequested && _comObject.Connecting)
            {
                await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            return !_comObject.Connected;
        }
        catch (Exception e)
        {
            External.AppLogger.LogError(e, "Failed to disconnect from ASCOM device {DeviceId} ({DisplayName}): {ErrorMessage}", _device.DeviceId, _device.DisplayName, e.Message);
            return false;
        }
    }
}

internal record struct AscomDeviceInfo();