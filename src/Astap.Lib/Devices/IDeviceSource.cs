using System.Collections.Generic;

namespace Astap.Lib.Devices;

public interface IDeviceSource<TDevice>
    where TDevice : DeviceBase
{
    bool IsSupported { get; }

    IEnumerable<DeviceType> RegisteredDeviceTypes { get; }

    IEnumerable<TDevice> RegisteredDevices(DeviceType deviceType);
}
