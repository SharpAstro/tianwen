using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Meade;

internal partial class MeadeDeviceSource(IExternal external) : IDeviceSource<MeadeDevice>
{
    private Dictionary<DeviceType, List<MeadeDevice>>? _cachedDevices;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    private static readonly Regex SupportedProductsRegex = GenSupportedProductsRegex();
    private static readonly ReadOnlyMemory<byte> HashTerminator = "#"u8.ToArray();
    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var devices = new Dictionary<DeviceType, List<MeadeDevice>>();

        using var resourceLock = await external.WaitForSerialPortEnumerationAsync(cancellationToken);

        foreach (var portName in external.EnumerateAvailableSerialPorts(resourceLock))
        {
            try
            {
                await QueryPortAsync(devices, portName, cancellationToken);
            }
            catch (Exception ex)
            {
                external.AppLogger.LogWarning(ex, "Failed to query device {PortName}", portName);
            }
        }

        Interlocked.Exchange(ref _cachedDevices, devices);
    }

    private async Task QueryPortAsync(Dictionary<DeviceType, List<MeadeDevice>> devices, string portName, CancellationToken cancellationToken)
    {
        using var serialDevice = external.OpenSerialDevice(portName, 9600, Encoding.ASCII, TimeSpan.FromMilliseconds(100));

        var (productName, productNumber, siteNames) = await TryGetMountInfo(serialDevice, cancellationToken);
        if (productName is { } && productNumber is { } && SupportedProductsRegex.IsMatch(productName) && siteNames is { })
        {
            List<MeadeDevice> deviceList;
            if (devices.TryGetValue(DeviceType.Mount, out var existingMounts))
            {
                deviceList = existingMounts;
            }
            else
            {
                devices[DeviceType.Mount] = deviceList = [];
            }

            var portNameWithoutProtoPrefix = ISerialConnection.RemoveProtoPrrefix(portName);
            var deviceId = string.Join('_',
                SafeName(productName),
                SafeName(productNumber),
                SafeName(siteNames),
                deviceList.Count + 1,
                SafeName(portNameWithoutProtoPrefix)
            );

            var device = new MeadeDevice(DeviceType.Mount, deviceId, $"{productName} ({productNumber}) on {portNameWithoutProtoPrefix}", portName);
            deviceList.Add(device);
        }

        static string SafeName(string name) => name.Replace('_', '-').Replace('/', '-').Replace(':', '-');
    }

    private static async Task<(string? ProductName, string? ProductNumber, string? SiteString)> TryGetMountInfo(Connections.ISerialConnection serialDevice, CancellationToken cancellationToken)
    {
        const string UnusedSiteName = "<AN UNUSED SITE>";

        using var @lock = await serialDevice.WaitAsync(cancellationToken);

        var productName = await TryReadTerminatedAsync(":GVP#");
        var productNumber = await TryReadTerminatedAsync(":GVN#");
        var site1Name = await TryReadTerminatedAsync(":GM#");
        var site2Name = await TryReadTerminatedAsync(":GN#");
        var site3Name = await TryReadTerminatedAsync(":GO#");
        var site4Name = await TryReadTerminatedAsync(":GP#");

        var sites = new StringBuilder(site1Name?.Length ?? 0 + site2Name?.Length ?? 0 + site3Name?.Length ?? 0 + site4Name?.Length ?? 0);

        foreach (var site in new string?[] { site1Name, site2Name, site3Name, site4Name })
        {
            var trimmed = site?.TrimEnd();
            if (trimmed is not { } || trimmed.Length is 0 || trimmed is UnusedSiteName)
            {
                continue;
            }

            if (sites.Length > 0)
            {
                sites.Append(',');
            }
            sites.Append(trimmed);
        }

        return (productName, productNumber, sites.ToString());

        async Task<string?> TryReadTerminatedAsync(string command)
        {
            if (await serialDevice.TryWriteAsync(command, cancellationToken))
            {
                return await serialDevice.TryReadTerminatedAsync(HashTerminator, cancellationToken);
            }

            return null;
        }
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount];

    public IEnumerable<MeadeDevice> RegisteredDevices(DeviceType deviceType)
    {
        if (_cachedDevices != null && _cachedDevices.TryGetValue(deviceType, out var devices))
        {
            return devices;
        }
        return [];
    }

    [GeneratedRegex("^(?:LX|Autostar|Audiostar)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex GenSupportedProductsRegex();
}