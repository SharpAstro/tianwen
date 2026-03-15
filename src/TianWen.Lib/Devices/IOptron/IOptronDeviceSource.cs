using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.IOptron;

internal partial class IOptronDeviceSource(IExternal external) : IDeviceSource<IOptronDevice>
{
    private Dictionary<DeviceType, List<IOptronDevice>>? _cachedDevices;

    private static readonly ReadOnlyMemory<byte> HashTerminator = "#"u8.ToArray();

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var devices = new Dictionary<DeviceType, List<IOptronDevice>>();

        using var resourceLock = await external.WaitForSerialPortEnumerationAsync(cancellationToken);

        foreach (var portName in external.EnumerateAvailableSerialPorts(resourceLock))
        {
            try
            {
                await QueryPortAsync(devices, portName, cancellationToken);
            }
            catch (Exception ex)
            {
                external.AppLogger.LogWarning(ex, "Failed to query iOptron device on {PortName}", portName);
            }
        }

        Interlocked.Exchange(ref _cachedDevices, devices);
    }

    private async Task QueryPortAsync(Dictionary<DeviceType, List<IOptronDevice>> devices, string portName, CancellationToken cancellationToken)
    {
        var ioTimeout = TimeSpan.FromMilliseconds(500);
        using var cts = new CancellationTokenSource(ioTimeout, external.TimeProvider);
        var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken).Token;

        using var serialDevice = external.OpenSerialDevice(portName, IOptronDevice.SGP_BAUD_RATE, Encoding.ASCII);

        using var @lock = await serialDevice.WaitAsync(linkedToken);

        // Stage 1: Send firmware version query
        if (!await serialDevice.TryWriteAsync(":MRSVE#", linkedToken))
        {
            return;
        }

        var response = await serialDevice.TryReadTerminatedAsync(HashTerminator, linkedToken);

        // Stage 2: Check for SGP identifier (prefix "12" in :RMRVE12xxxxxx#)
        if (response is null || !SgpFirmwareRegex().IsMatch(response))
        {
            return;
        }

        var fwMatch = SgpFirmwareRegex().Match(response);
        var firmwareVersion = fwMatch.Groups[1].Value;

        var portNameWithoutProtoPrefix = ISerialConnection.RemoveProtoPrrefix(portName);

        if (!devices.TryGetValue(DeviceType.Mount, out var deviceList))
        {
            devices[DeviceType.Mount] = deviceList = [];
        }

        // Fallback device ID: product_firmware_count_port (no UUID mechanism on SGP)
        var deviceId = string.Join('_',
            "SkyGuider-Pro",
            SafeName(firmwareVersion),
            deviceList.Count + 1,
            SafeName(portNameWithoutProtoPrefix)
        );
        var displayName = $"iOptron SkyGuider Pro ({firmwareVersion}) on {portNameWithoutProtoPrefix}";

        var device = new IOptronDevice(DeviceType.Mount, deviceId, displayName, portName);
        deviceList.Add(device);
    }

    private static string SafeName(string name) => name.Replace('_', '-').Replace('/', '-').Replace(':', '-');

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount];

    public IEnumerable<IOptronDevice> RegisteredDevices(DeviceType deviceType)
    {
        if (_cachedDevices is not null && _cachedDevices.TryGetValue(deviceType, out var devices))
        {
            return devices;
        }
        return [];
    }

    /// <summary>
    /// Matches SGP firmware response: :RMRVE12xxxxxx where xxxxxx is the firmware version date.
    /// </summary>
    [GeneratedRegex(@"^:RMRVE12(\d{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex SgpFirmwareRegex();
}
