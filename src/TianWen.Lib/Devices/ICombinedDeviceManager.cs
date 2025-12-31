using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

public interface ICombinedDeviceManager : IDeviceManager<DeviceBase>
{

    /// <summary>
    /// Discovers only devices of a specified type <paramref name="type"/> asynchronously.
    /// </summary>
    /// <param name="type"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask DiscoverOnlyDeviceType(DeviceType type, CancellationToken cancellationToken);
}