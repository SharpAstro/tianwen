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

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken) => await Task.Run(Discover, cancellationToken);

    private void Discover()
    {
        var types = source.RegisteredDeviceTypes;

        var deviceMap = new Dictionary<string, TDevice>();

        foreach (var type in types)
        {
            foreach (var device in source.RegisteredDevices(type))
            {
                deviceMap[device.DeviceId] = device;
            }
        }

        Interlocked.Exchange(ref _deviceMap, deviceMap);
    }

    public bool TryFindByDeviceId(string deviceId, [NotNullWhen(true)] out TDevice? device) => _deviceMap.TryGetValue(deviceId, out device);

    public IEnumerable<TDevice> RegisteredDevices(DeviceType deviceType) => _deviceMap.Values.Where(d => d.DeviceType == deviceType);

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => source.CheckSupportAsync(cancellationToken);
}
