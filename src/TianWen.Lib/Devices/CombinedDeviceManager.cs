using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

internal class CombinedDeviceManager(IExternal external, IEnumerable<IDeviceSource<DeviceBase>> deviceSources) : ICombinedDeviceManager
{
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initSem = new SemaphoreSlim(1, 1);
    private List<DeviceMap<DeviceBase>> _deviceMaps = [];
    private readonly ConcurrentDictionary<string, DeviceMap<DeviceBase>> deviceIdCache = [];

    public async ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return _deviceMaps.Count > 0;
        }

        var deviceMaps = new ConcurrentBag<DeviceMap<DeviceBase>>();
        await _initSem.WaitAsync(cancellationToken);
        _initialized = true;
        try
        {
            await Parallel.ForEachAsync(
                deviceSources,
                cancellationToken,
                async (deviceSource, cancellationToken) =>
                {
                    var map = new DeviceMap<DeviceBase>(deviceSource);
                    if (await map.CheckSupportAsync(cancellationToken))
                    {
                        deviceMaps.Add(map);
                    }
                }
            );
        }
        finally
        {
            _initSem.Release();
        }

        _ = Interlocked.Exchange(ref _deviceMaps, [.. deviceMaps]);

        return _deviceMaps.Count > 0;
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes => _deviceMaps.SelectMany(map => map.RegisteredDeviceTypes).ToHashSet();

    public IEnumerable<DeviceBase> RegisteredDevices(DeviceType type) => _deviceMaps.SelectMany(map => map.RegisteredDevices(type));

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!await CheckSupportAsync(cancellationToken))
        {
            return;
        }

        foreach (var deviceMap in _deviceMaps)
        {
            try
            {
                await deviceMap.DiscoverAsync(cancellationToken);
            }
            catch (Exception e)
            {
                external.AppLogger.LogError(e, "Error while discovering devices");
            }
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