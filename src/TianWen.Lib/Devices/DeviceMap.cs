using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

internal class DeviceMap<TDevice>(IDeviceSource<TDevice> source) : IDeviceManager<TDevice>
    where TDevice : DeviceBase
{
    private Dictionary<string, TDevice> _deviceMap = [];

    public IEnumerable<DeviceType> RegisteredDeviceTypes => source.RegisteredDeviceTypes;

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await source.DiscoverAsync(cancellationToken);

        var deviceMap = RegisteredDeviceTypes
            .SelectMany(source.RegisteredDevices)
            .ToDictionary(device => device.DeviceId, device => device);

        Interlocked.Exchange(ref _deviceMap, deviceMap);
    }

    public bool TryFindByDeviceId(string deviceId, [NotNullWhen(true)] out TDevice? device) => _deviceMap.TryGetValue(deviceId, out device);

    public IEnumerable<TDevice> RegisteredDevices(DeviceType deviceType) => _deviceMap.Values.Where(d => d.DeviceType == deviceType);

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => source.CheckSupportAsync(cancellationToken);
}
