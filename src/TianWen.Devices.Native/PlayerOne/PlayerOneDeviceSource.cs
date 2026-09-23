using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlayerOne.SDK;
using TianWen.DAL;
using static PlayerOne.SDK.PlayerOneCamera;

namespace TianWen.Lib.Devices.PlayerOne;

internal class PlayerOneDeviceSource : IDeviceSource<PlayerOneDevice>
{
    private static readonly bool _cameraSupported = CheckSupport();

    /// <summary>
    /// Is the native library present and answering?
    /// </summary>
    /// <remarks>
    /// Asking for the SDK version is the cheapest call that requires the library to have LOADED, so
    /// a machine without the Player One natives fails here, once, at type initialisation, rather
    /// than throwing a <see cref="DllNotFoundException"/> out of discovery. An empty answer counts
    /// as absent: the vendor returns a static string, so a blank one means nothing sensible is
    /// there.
    /// </remarks>
    private static bool CheckSupport()
    {
        try
        {
            return POAGetSDKVersion() is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_cameraSupported);

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = _cameraSupported ? [DeviceType.Camera] : [];

    public IEnumerable<PlayerOneDevice> RegisteredDevices(DeviceType deviceType)
        => _cameraSupported && deviceType is DeviceType.Camera ? ListCameras() : [];

    private static IEnumerable<PlayerOneDevice> ListCameras()
    {
        var ids = new HashSet<int>();

        foreach (var deviceInfo in new DeviceIterator<POACameraProperties>())
        {
            if (ids.Contains(deviceInfo.ID) || !deviceInfo.Open())
            {
                continue;
            }

            try
            {
                // Player One writes a printable serial straight into the properties struct, so
                // unlike ZWO this is usually present and is the stable identity. The name is the
                // last resort, and is NOT unique across two of the same model.
                var deviceId = deviceInfo.SerialNumber is { Length: > 0 } sn
                    ? sn
                    : deviceInfo.CustomId is { Length: > 0 } cid ? cid : deviceInfo.Name;

                // Escape both halves: a model name contains spaces, and a custom id is
                // user-supplied text that can contain anything. The fragment is the display name,
                // which is what INSTRUME records, so it takes SharpCap's spelling.
                var displayName = PlayerOneDevice.InstrumentName(deviceInfo.Name, deviceInfo.SensorModel);
                var uri = new Uri($"{DeviceType.Camera}://{typeof(PlayerOneDevice).Name}/{Uri.EscapeDataString(deviceId)}#{Uri.EscapeDataString(displayName)}");
                yield return new PlayerOneDevice(uri);

                ids.Add(deviceInfo.ID);
            }
            finally
            {
                _ = deviceInfo.Close();
            }
        }
    }
}
