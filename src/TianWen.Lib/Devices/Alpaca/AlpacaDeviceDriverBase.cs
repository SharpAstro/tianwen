using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Alpaca;


internal record struct AlpacaDeviceInfo();

/// <summary>
/// Base driver class for ASCOM Alpaca devices. Connects/disconnects via the Alpaca REST API
/// and provides convenience methods for property access and method invocation.
/// </summary>
internal abstract class AlpacaDeviceDriverBase(AlpacaDevice device, IServiceProvider serviceProvider)
    : DeviceDriverBase<AlpacaDevice, AlpacaDeviceInfo>(device, serviceProvider), IDeviceDriver
{
    protected AlpacaClient Client => _device.Client ?? throw new InvalidOperationException("AlpacaClient is not set on the device");

    protected string BaseUrl => _device.BaseUrl;
    protected string AlpacaDeviceType => _device.AlpacaDeviceType;
    protected int AlpacaDeviceNumber => _device.DeviceNumber;

    public override string? Description => null;

    public override string? DriverInfo => $"Alpaca {_device.AlpacaDeviceType} at {_device.Host}:{_device.Port}";

    /// <summary>
    /// PUT (set a property or invoke a method) on the Alpaca device.
    /// </summary>
    protected Task PutMethodAsync(string method, IEnumerable<KeyValuePair<string, string>>? parameters = null, CancellationToken cancellationToken = default)
    {
        return Client.PutAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, method, parameters, cancellationToken);
    }

    protected override async Task<(bool Success, int ConnectionId, AlpacaDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PutMethodAsync("connected", [new("Connected", "true")], cancellationToken);

            // Poll until connected or cancelled
            for (int i = 0; i < IDeviceDriver.MAX_FAILSAFE && !cancellationToken.IsCancellationRequested; i++)
            {
                if (await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "connected", cancellationToken))
                {
                    return (true, CONNECTION_ID_EXCLUSIVE, new AlpacaDeviceInfo());
                }

                await TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            return (false, CONNECTION_ID_UNKNOWN, new AlpacaDeviceInfo());
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to connect to Alpaca device {DeviceId} ({DisplayName}): {ErrorMessage}", _device.DeviceId, _device.DisplayName, e.Message);
            return (false, CONNECTION_ID_UNKNOWN, new AlpacaDeviceInfo());
        }
    }

    protected override async Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken)
    {
        try
        {
            await PutMethodAsync("connected", [new("Connected", "false")], cancellationToken);

            for (int i = 0; i < IDeviceDriver.MAX_FAILSAFE && !cancellationToken.IsCancellationRequested; i++)
            {
                if (!await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "connected", cancellationToken))
                {
                    return true;
                }

                await TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            return false;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to disconnect from Alpaca device {DeviceId} ({DisplayName}): {ErrorMessage}", _device.DeviceId, _device.DisplayName, e.Message);
            return false;
        }
    }
}
