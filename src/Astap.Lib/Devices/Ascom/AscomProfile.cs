using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Devices.Ascom;

public class AscomProfile : DynamicComObject, IDeviceSource<AscomDevice>
{
    public AscomProfile() : base("ASCOM.Utilities.Profile") { }

    /// <summary>
    /// Returns true if COM object was initalised successfully.
    /// </summary>
    public bool IsSupported => _comObject is not null;

    public IEnumerable<string> RegisteredDeviceTypes => EnumerateProperty<string>(_comObject?.RegisteredDeviceTypes);

    IEnumerable<KeyValuePair<string, string>> RegisteredDevicesKV(string deviceType) => EnumerateKeyValueProperty(_comObject?.RegisteredDevices(deviceType));

    public IEnumerable<AscomDevice> RegisteredDevices(string deviceType)
        => RegisteredDevicesKV(deviceType).Select(p => new AscomDevice(deviceType, p.Key, p.Value));
}
