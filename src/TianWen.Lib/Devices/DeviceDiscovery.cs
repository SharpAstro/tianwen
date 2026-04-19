using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices;

internal class DeviceDiscovery(
    ILogger<DeviceDiscovery> logger,
    IEnumerable<IDeviceSource<DeviceBase>> deviceSources,
    ISerialProbeService serialProbeService) : IDeviceDiscovery
{
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initSem = new SemaphoreSlim(1, 1);
    private List<IDeviceSource<DeviceBase>> _supportedSources = [];

    public async ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            // will be true if we have at least one supported device source
            return _supportedSources.Count > 0;
        }

        var supportedSources = new ConcurrentBag<IDeviceSource<DeviceBase>>();
        using var @lock = await _initSem.AcquireLockAsync(cancellationToken);

        // double check after lock acquisition
        if (_initialized)
        {
            return _supportedSources.Count > 0;
        }

        await Parallel.ForEachAsync(
            deviceSources,
            cancellationToken,
            async (deviceSource, cancellationToken) =>
            {
                try
                {
                    if (await deviceSource.CheckSupportAsync(cancellationToken))
                    {
                        supportedSources.Add(deviceSource);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while checking support for {DeviceSource}", deviceSource.GetType().Name);
                }
            }
        );
        _ = Interlocked.Exchange(ref _supportedSources, [.. supportedSources]);

        _initialized = true;

        return _supportedSources.Count > 0;
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes => _supportedSources.SelectMany(s => s.RegisteredDeviceTypes).ToHashSet();

    public IEnumerable<DeviceBase> RegisteredDevices(DeviceType type) => _supportedSources.SelectMany(s => s.RegisteredDevices(type));

    public async ValueTask DiscoverOnlyDeviceType(DeviceType type, CancellationToken cancellationToken)
    {
        if (!await CheckSupportAsync(cancellationToken))
        {
            return;
        }

        // Centralised serial probing runs before per-source discovery so sources can
        // consume probe matches from ISerialProbeService instead of opening ports
        // themselves. Safe no-op when no ISerialProbe is registered (Phase 1 default).
        await RunSerialProbesAsync(cancellationToken);

        foreach (var source in _supportedSources)
        {
            if (source.RegisteredDeviceTypes.Contains(type))
            {
                try
                {
                    await source.DiscoverAsync(cancellationToken);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while discovering devices of type {DeviceType}", type);
                }
            }
        }
    }

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!await CheckSupportAsync(cancellationToken))
        {
            return;
        }

        await RunSerialProbesAsync(cancellationToken);

        foreach (var source in _supportedSources)
        {
            try
            {
                await source.DiscoverAsync(cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while discovering devices");
            }
        }
    }

    private async ValueTask RunSerialProbesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await serialProbeService.ProbeAllAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Serial probe pass failed — continuing with per-source discovery.");
        }
    }
}
