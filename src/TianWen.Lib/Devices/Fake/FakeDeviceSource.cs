using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal class FakeDeviceSource : IDeviceSource<FakeDevice>
{
    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount, DeviceType.Camera, DeviceType.Focuser, DeviceType.FilterWheel];

    const int FakeDeviceCount = 9;

    public IEnumerable<FakeDevice> RegisteredDevices(DeviceType deviceType)
    {
        for (var i = 1; i <= FakeDeviceCount; i++)
        {
            yield return new FakeDevice(deviceType, i);
        }
    }

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}
