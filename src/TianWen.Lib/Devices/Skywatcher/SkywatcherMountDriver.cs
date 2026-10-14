using Microsoft.Extensions.DependencyInjection;
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
    /// process (<see cref="LocalOnlyMode"/>) does not open it, and the connect fails saying why (a job's error and the GUI
    /// show the message). A serial mount connects as ever.
    /// </summary>
    protected override Task<(bool Success, int ConnectionId, SkywatcherDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        if (_device.IsWifi && !_localOnly.MayBindTheNetwork)
        {
            throw new InvalidOperationException(
                $"{_device.DisplayName} is a WiFi Skywatcher mount, reached over UDP, which a local-only process ({LocalOnlyMode.EnvironmentVariable}) does not open");
        }

        return base.DoConnectDeviceAsync(cancellationToken);
    }
}
