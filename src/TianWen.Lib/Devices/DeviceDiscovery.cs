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
    private readonly IReadOnlyList<IDeviceSource<DeviceBase>> _allSources = [.. deviceSources];
    private readonly SemaphoreSlim _initSem = new SemaphoreSlim(1, 1);
    private volatile bool _initialized;
    private List<IDeviceSource<DeviceBase>> _supportedSources = [];

    public async ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return _supportedSources.Count > 0;
        }

        using var @lock = await _initSem.AcquireLockAsync(cancellationToken);
        if (_initialized)
        {
            return _supportedSources.Count > 0;
        }

        _supportedSources = await ProbeSupportAsync(_allSources, cancellationToken);
        _initialized = true;
        return _supportedSources.Count > 0;
    }

    /// <summary>
    /// Re-probes the sources that previously reported "unsupported" so a transient
    /// startup failure (e.g. network briefly offline when the HTTP-based Open-Meteo
    /// source first checked reachability) doesn't lock a device family out of the
    /// picker for the rest of the session. Sources that passed the last check are
    /// kept as-is. Called from <see cref="DiscoverAsync"/> and
    /// <see cref="DiscoverOnlyDeviceType"/> so a user-initiated discovery always
    /// sees the freshest view of what's available.
    /// </summary>
    private async ValueTask RefreshUnsupportedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized) return;

        var supportedSet = new HashSet<IDeviceSource<DeviceBase>>(_supportedSources);
        var unsupported = _allSources.Where(s => !supportedSet.Contains(s)).ToList();
        if (unsupported.Count == 0) return;

        using var @lock = await _initSem.AcquireLockAsync(cancellationToken);
        var newlySupported = await ProbeSupportAsync(unsupported, cancellationToken);
        if (newlySupported.Count == 0) return;

        foreach (var s in newlySupported)
        {
            supportedSet.Add(s);
        }
        _supportedSources = [.. supportedSet];
        foreach (var s in newlySupported)
        {
            logger.LogInformation("Device source {Source} now supports this platform (previously unsupported).", s.GetType().Name);
        }
    }

    private async Task<List<IDeviceSource<DeviceBase>>> ProbeSupportAsync(
        IReadOnlyList<IDeviceSource<DeviceBase>> candidates, CancellationToken cancellationToken)
    {
        var supported = new ConcurrentBag<IDeviceSource<DeviceBase>>();
        await Parallel.ForEachAsync(
            candidates,
            cancellationToken,
            async (deviceSource, ct) =>
            {
                try
                {
                    if (await deviceSource.CheckSupportAsync(ct))
                    {
                        supported.Add(deviceSource);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while checking support for {DeviceSource}", deviceSource.GetType().Name);
                }
            });
        return [.. supported];
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes => _supportedSources.SelectMany(s => s.RegisteredDeviceTypes).ToHashSet();

    public IEnumerable<DeviceBase> RegisteredDevices(DeviceType type)
        => NativeDriverBlacklist.FilterSuperseded(_supportedSources.SelectMany(s => s.RegisteredDevices(type)), logger);

    public async ValueTask DiscoverOnlyDeviceType(DeviceType type, CancellationToken cancellationToken)
    {
        if (!await CheckSupportAsync(cancellationToken))
        {
            return;
        }

        await RefreshUnsupportedAsync(cancellationToken);

        // Centralised serial probing runs before per-source discovery so sources can
        // consume probe matches from ISerialProbeService instead of opening ports
        // themselves. Safe no-op when no ISerialProbe is registered (Phase 1 default).
        await RunSerialProbesAsync(cancellationToken);

        await Parallel.ForEachAsync(
            _supportedSources.Where(s => s.RegisteredDeviceTypes.Contains(type)),
            cancellationToken,
            async (source, ct) =>
            {
                try
                {
                    await source.DiscoverAsync(ct);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while discovering devices of type {DeviceType} from {DeviceSource}", type, source.GetType().Name);
                }
            });
    }

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!await CheckSupportAsync(cancellationToken))
        {
            // Even if the initial support check found nothing, allow a refresh — a
            // host that started offline will often be back online by the time the
            // user clicks Discover.
            await RefreshUnsupportedAsync(cancellationToken);
            if (_supportedSources.Count == 0) return;
        }
        else
        {
            await RefreshUnsupportedAsync(cancellationToken);
        }

        // Start serial probing CONCURRENTLY with the per-source discovery rather than strictly before it.
        // Sources that consume the probe results (ConsumesSerialProbe: OnStep/Meade/iOptron/Skywatcher/Gemini/
        // QHYCCD) await it before their DiscoverAsync; the independent network/USB sources (Alpaca/Canon/PHD2/
        // weather/ZWO) run alongside the multi-second serial pass instead of serialising after it. Per-source
        // discovery is independent (each populates its own list) and mostly I/O-bound, so run them in parallel
        // with a per-source try/catch so one failure never sinks the rest (mirrors ProbeSupportAsync).
        // RunSerialProbesAsync swallows its own non-cancellation exceptions, and AsTask() lets the consuming
        // sources all await the one probe pass.
        var serialProbeTask = RunSerialProbesAsync(cancellationToken).AsTask();

        await Parallel.ForEachAsync(
            _supportedSources,
            cancellationToken,
            async (source, ct) =>
            {
                try
                {
                    if (source.ConsumesSerialProbe)
                    {
                        await serialProbeTask;
                    }
                    await source.DiscoverAsync(ct);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while discovering devices from {DeviceSource}", source.GetType().Name);
                }
            });

        // Observe the probe pass even if no consuming source awaited it (e.g. none supported) — surfaces a
        // cancellation and ensures it has completed before DiscoverAsync returns.
        await serialProbeTask;
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
