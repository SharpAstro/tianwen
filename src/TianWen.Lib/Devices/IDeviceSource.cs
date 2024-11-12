using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace TianWen.Lib.Devices;

public interface IDeviceSource<out TDevice> : IAsyncSupportedCheck where TDevice : DeviceBase
{
    ValueTask DiscoverAsync(CancellationToken cancellationToken = default);

    IEnumerable<DeviceType> RegisteredDeviceTypes { get; }

    IEnumerable<TDevice> RegisteredDevices(DeviceType deviceType);
}