using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Meade;

internal class MeadeDeviceSource(IExternal external) : IDeviceSource<MeadeDevice>
{
    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    /// <summary>
    /// TODO: Move code from RegisteredDevices and await async on serial port.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount];

    public IEnumerable<MeadeDevice> RegisteredDevices(DeviceType deviceType)
    {
        foreach (var portName in external.EnumerateSerialPorts())
        {
            MeadeDevice? device;
            try
            {
                using var serialDevice = external.OpenSerialDevice(portName, 9600, Encoding.ASCII, TimeSpan.FromMilliseconds(100));

                if (serialDevice.TryWrite(":GVP#"u8) && serialDevice.TryReadTerminated(out var productName, "#"u8)
                    && productName.StartsWithAny("LX"u8, "Autostar"u8, "Audiostar"u8)
                    && serialDevice.TryWrite(":GVN#"u8) && serialDevice.TryReadTerminated(out var productNumber, "#"u8)
                )
                {
                    var productNameStr = serialDevice.Encoding.GetString(productName);
                    var productVersionStr = serialDevice.Encoding.GetString(productNumber);
                    var deviceId = $"{productNameStr}_{productVersionStr}";

                    device = new MeadeDevice(deviceType, deviceId, $"{productNameStr} ({productVersionStr})", portName);
                }
                else
                {
                    device = null;
                }
            }
            catch (Exception ex)
            {
                external.AppLogger.LogWarning(ex, "Failed to query device {PortName}", portName);
                device = null;
            }

            if (device is not null)
            {
                yield return device;
            }
        }
    }
}