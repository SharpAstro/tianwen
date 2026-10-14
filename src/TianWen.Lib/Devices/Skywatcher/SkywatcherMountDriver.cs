using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Skywatcher;

internal class SkywatcherMountDriver(SkywatcherDevice device, IServiceProvider serviceProvider) : SkywatcherMountDriverBase<SkywatcherDevice>(device, serviceProvider)
{
    // A host that registers no mode (AddExternal registers one) is not local-only.
    private readonly LocalOnlyMode _localOnly = serviceProvider.GetService<LocalOnlyMode>() ?? LocalOnlyMode.Off;

    /// <summary>
    /// A WiFi mount is the one device reached over UDP, from a socket the OS binds on every interface, so a local-only
    /// process (<see cref="LocalOnlyMode"/>) does not open it and says why. A serial mount connects as ever.
    /// </summary>
    protected override Task<(bool Success, int ConnectionId, SkywatcherDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        if (_device.IsWifi && !_localOnly.MayBindTheNetwork)
        {
            Logger.LogError("Not connecting {DeviceUri}: a WiFi Skywatcher mount is reached over UDP, which this local-only process ({EnvVar}) does not open",
                _device.DeviceUri, LocalOnlyMode.EnvironmentVariable);
            return Task.FromResult((false, CONNECTION_ID_UNKNOWN, default(SkywatcherDeviceInfo)));
        }

        return base.DoConnectDeviceAsync(cancellationToken);
    }
}
