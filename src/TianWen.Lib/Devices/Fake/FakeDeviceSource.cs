using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal class FakeDeviceSource : IDeviceSource<FakeDevice>
{
    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount, DeviceType.Camera, DeviceType.Focuser, DeviceType.FilterWheel, DeviceType.Guider];

    public IEnumerable<FakeDevice> RegisteredDevices(DeviceType deviceType)
    {
        var count = deviceType is DeviceType.Mount or DeviceType.Guider ? 1 : 2;
        for (var i = 1; i <= count; i++)
        {
            yield return new FakeDevice(deviceType, i);
        }
    }

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}
