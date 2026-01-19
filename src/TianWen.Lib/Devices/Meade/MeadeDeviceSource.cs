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
        TimeSpan ioTimeout;
#if DEBUG
        ioTimeout = TimeSpan.FromMinutes(1);
#else
        ioTimeout = TimeSpan.FromMilliseconds(250);
#endif
        using var cts = new CancellationTokenSource(ioTimeout, external.TimeProvider);
        var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken).Token;

        using var serialDevice = external.OpenSerialDevice(portName, 9600, Encoding.ASCII);

        var (productName, productNumber, siteNames, uuid) = await TryGetMountInfo(serialDevice, linkedToken)
            .WaitAsync(ioTimeout, external.TimeProvider, cancellationToken);
        if (productName is { } && productNumber is { } && SupportedProductsRegex.IsMatch(productName))
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
            string deviceId;
            if (uuid is { })
            {
                deviceId = string.Join('_', SafeName(productName), SafeName(productNumber), uuid);
            }
            else
            {
                deviceId = string.Join('_',
                    SafeName(productName),
                    SafeName(productNumber),
                    SafeName(string.Join(',', siteNames)),
                    deviceList.Count + 1,
                    SafeName(portNameWithoutProtoPrefix)
                );
            }

            var device = new MeadeDevice(DeviceType.Mount, deviceId, $"{productName} ({productNumber}) on {portNameWithoutProtoPrefix}", portName);
            deviceList.Add(device);
        }

        static string SafeName(string name) => name.Replace('_', '-').Replace('/', '-').Replace(':', '-');
    }

    private static async Task<(string? ProductName, string? ProductNumber, List<string> Sites, string? UUID)> TryGetMountInfo(ISerialConnection serialDevice, CancellationToken cancellationToken)
    {
        const string UUIDPrefix = "TW@";
        const string UnusedSiteName = "<AN UNUSED SITE>";
        const int MaxSiteLength = 15;

        using var @lock = await serialDevice.WaitAsync(cancellationToken);

        var productName = await TryReadTerminatedAsync(":GVP#");

        if (productName?.TrimEnd() is not { Length: > 1})
        {
            return (null, null, [], null);
        }

        var productNumber = await TryReadTerminatedAsync(":GVN#");

        string? uuid = null;
        var sites = new List<string>();
        for (var siteNo = 4; siteNo >= 1; siteNo--)
        {
            var siteChar = (char)('M' + siteNo - 1);
            var siteName = await TryReadTerminatedAsync($":G{siteChar}#");

            var trimmed = siteName?.TrimEnd();
            if (trimmed is not { } || trimmed.Length is 0)
            {
                continue;
            }
            else if (trimmed.Length is MaxSiteLength && trimmed.StartsWith(UUIDPrefix))
            {
                uuid = trimmed[UUIDPrefix.Length..];
            }
            else if (trimmed is UnusedSiteName)
            {
                if (uuid is null)
                {
                    using var random = ArrayPoolHelper.Rent<byte>((MaxSiteLength - UUIDPrefix.Length) * 3 / 4);
                    Random.Shared.NextBytes(random);
                    var newUUID = Base64UrlSafe.Base64UrlEncode(random);

                    if (await serialDevice.TryWriteAsync($":S{siteChar}TW@{newUUID}#", cancellationToken) &&
                        await serialDevice.TryReadExactlyAsync(1, cancellationToken) is "1")
                    {
                        uuid = newUUID;
                    }
                }
                else
                {
                    continue;
                }
            }
            else
            {
                sites.Add(trimmed);
            }
        }

        sites.Reverse();

        return (productName, productNumber, sites, uuid);

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