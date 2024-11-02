using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace TianWen.Lib.Devices;

internal class DeviceMap<TDevice> : IDeviceManager<TDevice>
    where TDevice : DeviceBase
{
    private readonly IDeviceSource<TDevice> _source;
    private Dictionary<string, TDevice> _deviceMap = [];

    public DeviceMap(IDeviceSource<TDevice> source) => _source = source;

    public void Refresh()
    {
        var types = _source.RegisteredDeviceTypes;

        var deviceMap = new Dictionary<string, TDevice>();

        foreach (var type in types)
        {
            foreach (var device in _source.RegisteredDevices(type))
            {
                deviceMap[device.DeviceId] = device;
            }
        }

        Interlocked.Exchange(ref _deviceMap, deviceMap);
    }

    public bool TryFindByDeviceId(string deviceId, [NotNullWhen(true)] out TDevice? device) => _deviceMap.TryGetValue(deviceId, out device);

    public IReadOnlyList<TDevice> FindAllByType(DeviceType type) => _deviceMap.Values.Where(device => device.DeviceType == type).ToList();

    public IEnumerator<TDevice> GetEnumerator() => _deviceMap.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
