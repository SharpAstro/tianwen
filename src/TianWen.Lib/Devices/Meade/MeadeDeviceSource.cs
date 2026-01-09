using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Meade;

internal class MeadeDeviceSource(IExternal external) : IDeviceSource<MeadeDevice>
{
    private Dictionary<DeviceType, List<MeadeDevice>>? _cachedDevices;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    private static readonly ReadOnlyMemory<byte> HashTerminator = "#"u8.ToArray();
    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var devices = new Dictionary<DeviceType, List<MeadeDevice>>();

        foreach (var portName in external.EnumerateSerialPorts())
        {
            try
            {
                using var serialDevice = external.OpenSerialDevice(portName, 9600, Encoding.ASCII, TimeSpan.FromMilliseconds(100));

                string? productName;
                string? productNumber;
                if (await serialDevice.TryWriteAsync(":GVP#", cancellationToken)
                    && (productName = await serialDevice.TryReadTerminatedAsync(HashTerminator, cancellationToken)) is { }
                    && (productName.StartsWith("LX") || productName.StartsWith("Autostar") || productName.StartsWith("Audiostar"))
                    && await serialDevice.TryWriteAsync(":GVN#", cancellationToken)
                    && (productNumber = await serialDevice.TryReadTerminatedAsync(HashTerminator, cancellationToken)) is { }
                )
                {
                    var deviceId = $"{productName}_{productNumber}";

                    var device = new MeadeDevice(DeviceType.Mount, deviceId, $"{productName} ({productNumber})", portName);

                    if (devices.TryGetValue(DeviceType.Mount, out var deviceList))
                    {
                        deviceList.Add(device);
                    }
                    else
                    {
                        devices[DeviceType.Mount] = new List<MeadeDevice> { device };
                    }
                }
            }
            catch (Exception ex)
            {
                external.AppLogger.LogWarning(ex, "Failed to query device {PortName}", portName);
            }
        }

        Interlocked.Exchange(ref _cachedDevices, devices);
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
}