using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Meade;

/// <summary>
/// Materialises <see cref="MeadeDevice"/> instances from the serial matches published
/// by <see cref="MeadeSerialProbe"/> via <see cref="ISerialProbeService"/>. The probe
/// owns the actual wire I/O; this source just adapts the results to the
/// <see cref="IDeviceSource{TDevice}"/> contract.
/// </summary>
internal partial class MeadeDeviceSource(ISerialProbeService probeService) : IDeviceSource<MeadeDevice>
{
    private Dictionary<DeviceType, List<MeadeDevice>>? _cachedDevices;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    internal static readonly Regex SupportedProductsRegex = GenSupportedProductsRegex();
    internal static readonly ReadOnlyMemory<byte> HashTerminator = "#"u8.ToArray();

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var devices = new Dictionary<DeviceType, List<MeadeDevice>>();
        var matches = probeService.ResultsFor("Meade");
        if (matches.Count > 0)
        {
            var list = devices[DeviceType.Mount] = [];
            foreach (var match in matches)
            {
                list.Add(new MeadeDevice(match.DeviceUri));
            }
        }

        Interlocked.Exchange(ref _cachedDevices, devices);
        return ValueTask.CompletedTask;
    }

    internal static string SafeName(string name) => name.Replace('_', '-').Replace('/', '-').Replace(':', '-');

    /// <summary>
    /// LX200 mount-info handshake shared by <see cref="MeadeSerialProbe"/>. Issues
    /// <c>:GVP#</c>, <c>:GVN#</c>, and the four site-name queries <c>:GM..GP#</c>;
    /// if a site slot is unused and no UUID is already present, writes a fresh UUID
    /// into it so subsequent probes have a stable device id independent of the USB port.
    /// </summary>
    internal static async Task<(string? ProductName, string? ProductNumber, List<string> Sites, string? UUID)> TryGetMountInfo(ISerialConnection serialDevice, CancellationToken cancellationToken)
    {
        const string uuidPrefix = "TW@";
        const string unusedSiteName = "<AN UNUSED SITE>";
        const int maxSiteLength = 15;

        using var @lock = await serialDevice.WaitAsync(cancellationToken);

        var productName = await TryReadTerminatedAsync(":GVP#");

        if (productName?.TrimEnd() is not { Length: > 1 })
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
            if (trimmed is null || trimmed.Length is 0)
            {
                continue;
            }

            if (trimmed.Length is maxSiteLength && trimmed.StartsWith(uuidPrefix))
            {
                uuid ??= trimmed[uuidPrefix.Length..];
            }
            else if (trimmed is unusedSiteName && uuid is null)
            {
                using var random = ArrayPoolHelper.Rent<byte>((maxSiteLength - uuidPrefix.Length) * 3 / 4);
                Random.Shared.NextBytes(random);
                var newUuid = Base64UrlSafe.Base64UrlEncode(random);

                if (await serialDevice.TryWriteAsync($":S{siteChar}TW@{newUuid}#", cancellationToken) &&
                    await serialDevice.TryReadExactlyAsync(1, cancellationToken) is "1")
                {
                    uuid = newUuid;
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
