using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Devices.Ascom;

public class AscomProfile : DynamicComObject, IDeviceSource<AscomDevice>
{
    public AscomProfile() : base("ASCOM.Utilities.Profile") { }

    public IEnumerable<string> RegisteredDeviceTypes => EnumerateProperty<string>(_comObject?.RegisteredDeviceTypes);

    IEnumerable<(string key, string value)> RegisteredDevicesKV(string deviceType) => EnumerateKeyValueProperty(_comObject?.RegisteredDevices(deviceType));

    public IEnumerable<AscomDevice> RegisteredDevices(string deviceType)
        => RegisteredDevicesKV(deviceType).Select(p => new AscomDevice(deviceType, p.key, p.value));
}
