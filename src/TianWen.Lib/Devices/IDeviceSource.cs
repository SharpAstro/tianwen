using System.Collections.Generic;
namespace TianWen.Lib.Devices;

public interface IDeviceSource<out TDevice>
    where TDevice : DeviceBase
{
    bool IsSupported { get; }

    IEnumerable<DeviceType> RegisteredDeviceTypes { get; }

    IEnumerable<TDevice> RegisteredDevices(DeviceType deviceType);
}