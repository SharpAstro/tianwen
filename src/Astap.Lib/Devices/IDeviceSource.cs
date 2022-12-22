using System.Collections.Generic;

namespace Astap.Lib.Devices;

public interface IDeviceSource<TDevice>
    where TDevice : DeviceBase
{
    bool IsSupported { get; }

    IEnumerable<string> RegisteredDeviceTypes { get; }

    IEnumerable<TDevice> RegisteredDevices(string deviceType);
}
