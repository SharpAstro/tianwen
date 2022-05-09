using System.Collections.Generic;

namespace Astap.Lib;

public class AscomProfile : AscomBase
{
    public AscomProfile() : base("ASCOM.Utilities.Profile") { }

    public IEnumerable<string> RegisteredDeviceTypes => EnumerateProperty<string>(_comObject?.RegisteredDeviceTypes);

    public IEnumerable<(string progId, string displayName)> RegisteredDevices(string deviceType) => EnumerateKeyValueProperty(_comObject?.RegisteredDevices(deviceType));
}
