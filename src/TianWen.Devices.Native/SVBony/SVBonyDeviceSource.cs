using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SVBony.SDK;

namespace TianWen.Lib.Devices.SVBony;

internal class SVBonyDeviceSource : IDeviceSource<SVBonyDevice>
{
    private static readonly bool _cameraSupported = CheckSupport();

    /// <summary>
    /// Is the native library present and answering? Asking for the SDK version is the cheapest call that needs it
    /// LOADED, so a machine without it (macOS, which the vendor drop has no build for) answers false here, once,
    /// rather than throwing out of discovery.
    /// </summary>
    private static bool CheckSupport()
    {
        try
        {
            return SVBCamera.SVBGetSDKVersion() is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_cameraSupported);

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = _cameraSupported ? [DeviceType.Camera] : [];

    public IEnumerable<SVBonyDevice> RegisteredDevices(DeviceType deviceType)
        => _cameraSupported && deviceType is DeviceType.Camera ? ListCameras() : [];

    /// <summary>
    /// Lists with NO open: the SDK's enumeration carries the friendly name and the serial, so a listing never
    /// touches a camera a driver holds (and never pays the seconds an open of this SDK can take).
    /// </summary>
    private static IEnumerable<SVBonyDevice> ListCameras()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deviceInfo in new DeviceIterator())
        {
            // The serial is the identity; the name is the last resort and is NOT unique across two of one model.
            var deviceId = deviceInfo.SerialNumber is { Length: > 0 } sn ? sn : deviceInfo.Name;
            if (!seen.Add(deviceId))
            {
                continue;
            }

            // The fragment is the display name, which INSTRUME records: the SDK's friendly name, SVBONY SV605CC,
            // which is N.I.N.A.'s spelling through the same SDK, so calibration taken here pairs with its lights.
            yield return new SVBonyDevice(new Uri($"{DeviceType.Camera}://{typeof(SVBonyDevice).Name}/{Uri.EscapeDataString(deviceId)}#{Uri.EscapeDataString(deviceInfo.Name)}"));
        }
    }
}
