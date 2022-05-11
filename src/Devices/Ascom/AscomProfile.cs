using System.Collections.Generic;

namespace Astap.Lib.Devices.Ascom;

public class AscomProfile : AscomBase, IDeviceSource<AscomDevice>
{
    public AscomProfile() : base("ASCOM.Utilities.Profile") { }

    public IEnumerable<string> RegisteredDeviceTypes => EnumerateProperty<string>(_comObject?.RegisteredDeviceTypes);

    public IEnumerable<AscomDevice> RegisteredDevices(string deviceType)
    {
        if (EnumerateKeyValueProperty(_comObject?.RegisteredDevices(deviceType)) is IEnumerable<(string key, string value)> devices)
        {
            foreach (var (deviceId, displayName) in devices)
            {
                yield return new AscomDevice(AscomDevice.CreateUri(deviceType, deviceId, displayName));
            }
        }
    }
}
