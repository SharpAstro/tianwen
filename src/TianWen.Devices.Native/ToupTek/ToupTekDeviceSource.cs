using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using ToupTek.SDK;

namespace TianWen.Lib.Devices.ToupTek;

internal class ToupTekDeviceSource : IDeviceSource<ToupTekDevice>
{
    private static readonly bool _cameraSupported = CheckSupport();

    /// <summary>
    /// Is at least one ToupTek-family library installed and complete?
    /// </summary>
    /// <remarks>
    /// Loading a brand's library resolves every entry point the binding uses, so a machine without any
    /// of the eleven libraries (or with an old build missing a function) answers false here, once, at
    /// type initialisation, rather than throwing out of discovery.
    /// </remarks>
    private static bool CheckSupport()
    {
        try
        {
            return ToupcamBrand.All.Any(brand => brand.Api is not null);
        }
        catch
        {
            return false;
        }
    }

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_cameraSupported);

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = _cameraSupported ? [DeviceType.Camera] : [];

    public IEnumerable<ToupTekDevice> RegisteredDevices(DeviceType deviceType)
        => _cameraSupported && deviceType is DeviceType.Camera ? ListCameras() : [];

    private static IEnumerable<ToupTekDevice> ListCameras()
    {
        // Keyed on the IDENTITY each camera is addressed by, not the SDK's id: that id is a USB device
        // path (\\?\usb#vid_0547&pid_14bc#...), so it names a PORT, and a body enumerated by two brand
        // libraries would otherwise be listed twice.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var deviceInfo in new DeviceIterator())
        {
            if (!deviceInfo.Open())
            {
                continue;
            }

            try
            {
                // The serial needs an open handle on this SDK, which is why it is read here and not
                // from the enumeration. The name is the last resort and is NOT unique across two of
                // one model.
                var deviceId = deviceInfo.SerialNumber is { Length: > 0 } sn
                    ? sn
                    : deviceInfo.CustomId is { Length: > 0 } cid ? cid : deviceInfo.Name;

                if (!seen.Add(deviceId))
                {
                    continue;
                }

                // A rebadged body shows its brand, so an Altair camera does not read as a ToupTek one.
                var displayName = deviceInfo.Brand is { } brand && brand != ToupcamBrand.ToupTek
                    ? $"{brand.Name} {deviceInfo.Name}"
                    : deviceInfo.Name;

                yield return new ToupTekDevice(new Uri($"{DeviceType.Camera}://{typeof(ToupTekDevice).Name}/{Uri.EscapeDataString(deviceId)}#{Uri.EscapeDataString(displayName)}"));
            }
            finally
            {
                _ = deviceInfo.Close();
            }
        }
    }
}
