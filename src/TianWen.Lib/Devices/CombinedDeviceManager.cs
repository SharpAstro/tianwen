using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace TianWen.Lib.Devices;

internal class CombinedDeviceManager(IEnumerable<IDeviceSource<DeviceBase>> deviceSources) : IDeviceManager<DeviceBase>
{
    private bool _refreshedOnce;

    private readonly List<DeviceMap<DeviceBase>> _deviceMaps = deviceSources
        .Where(source => source.IsSupported)
        .Select(source => new DeviceMap<DeviceBase>(source))
        .ToList();
    private readonly ConcurrentDictionary<string, DeviceMap<DeviceBase>> deviceIdCache = [];

    public IReadOnlyList<DeviceBase> FindAllByType(DeviceType type) => _deviceMaps.SelectMany(map => FindAllByType(type)).ToList();

    public IEnumerator<DeviceBase> GetEnumerator()
    {
        if (!_refreshedOnce)
        {
            _refreshedOnce = true;
            Refresh();
        }

        foreach (var map in _deviceMaps)
        {
            foreach (var device in map)
            {
                yield return device;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Refresh()
    {
        foreach (var deviceMap in _deviceMaps)
        {
            deviceMap.Refresh();
        }
    }

    public bool TryFindByDeviceId(string deviceId, [NotNullWhen(true)] out DeviceBase? device)
    {
        if (deviceIdCache.TryGetValue(deviceId, out var map))
        {
            return map.TryFindByDeviceId(deviceId, out device);
        }

        foreach (var deviceMap in _deviceMaps)
        {
            if (deviceMap.TryFindByDeviceId(deviceId, out device))
            {
                deviceIdCache[deviceId] = deviceMap;
                return true;
            }
        }

        device = null;
        return false;
    }
}