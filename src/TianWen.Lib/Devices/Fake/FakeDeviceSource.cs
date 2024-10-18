using System.Collections.Generic;

namespace TianWen.Lib.Devices.Fake;

internal class FakeDeviceSource : IDeviceSource<FakeDevice>
{
    public bool IsSupported => true;

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Camera, DeviceType.Focuser, DeviceType.FilterWheel];

    const int FakeDeviceCount = 9;

    public IEnumerable<FakeDevice> RegisteredDevices(DeviceType deviceType)
    {
        for (var i = 1; i <= FakeDeviceCount; i++)
        {
            yield return new FakeDevice(deviceType, i);
        }
    }
}
