using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

/// <summary>
/// The device plane's read side (P2 part 1 of docs/plans/hardware-in-the-server.md, #929): reads every connected device
/// at the cadence the GUI reads it, keeps the last reading of each, answers <c>GET /api/v1/devices/state</c> from them,
/// and pushes <c>DEVICE-STATE</c> when one changes.
/// </summary>
/// <remarks>
/// <para><b>It never reads a device a run holds.</b> The run reads it already (a session's <c>PollDeviceStatesAsync</c>),
/// and two readers on one serial port race; the GUI's own poll stands down while a session runs for the same reason. A
/// held device keeps its last reading, with its lease's owner beside it.</para>
/// <para><b>It reads at the GUI's cadences only while someone is watching</b>: a WebSocket client attached, or a snapshot
/// asked for within <see cref="WatchedFor"/>. With nobody watching, every device drops to <see cref="Unwatched"/>, since a
/// reading nobody sees is serial traffic for nothing.</para>
/// <para><b>A push is a change, never a read</b>: the reading time moves on every read, so it is left out of the
/// comparison, or every read would be a push.</para>
/// </remarks>
internal sealed class DeviceStatePoller(
    IDeviceHub hub,
    EventHub events,
    MountLimitWatcher limits,
    IHostedSession hosted,
    IExternal external,
    ITimeProvider timeProvider,
    ILogger<DeviceStatePoller> logger) : BackgroundService
{
    /// <summary>How often the poller looks at what is due.</summary>
    internal static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a snapshot request counts as someone watching.</summary>
    internal static readonly TimeSpan WatchedFor = TimeSpan.FromSeconds(10);

    /// <summary>The one cadence with nobody watching.</summary>
    internal static readonly TimeSpan Unwatched = TimeSpan.FromSeconds(10);

    /// <summary>How long the active profile's site is used before it is read again, for a topocentric mount's J2000.</summary>
    private static readonly TimeSpan SiteRefresh = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    private long _lastSnapshotRequest;
    private Profile? _siteProfile;
    private long _siteReadAt;

    /// <summary>What the node last read of a device, and when; <see cref="Compare"/> is that reading as a push compares it.</summary>
    private sealed record Entry(DeviceStateDto State, byte[] Compare, long ReadAt, long SteadySince);

    /// <summary>
    /// Every device connected or held, with its last reading, whether it is connected and who holds it as of now. The
    /// connection and the lease are read live, so a client never sees a device held that has just been let go.
    /// </summary>
    public DeviceStateDto[] Snapshot()
    {
        Volatile.Write(ref _lastSnapshotRequest, timeProvider.GetTimestamp());
        var devices = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uri, _) in hub.ConnectedDevices)
        {
            devices[uri.DeviceKey] = uri;
        }
        foreach (var lease in hub.Leases)
        {
            devices.TryAdd(lease.DeviceUri.DeviceKey, lease.DeviceUri);
        }

        return [.. devices.Select(d => Current(d.Value, _entries.TryGetValue(d.Key, out var entry) ? entry.State : null))
            .OrderBy(static s => s.DeviceUri, StringComparer.Ordinal)];
    }

    /// <summary>
    /// A device as it stands now: its last reading, every part of it, with its identity, connection and lease read live.
    /// </summary>
    private DeviceStateDto Current(Uri deviceUri, DeviceStateDto? last)
    {
        var type = hub.TryGetDeviceFromUri(deviceUri, out var device) ? device.DeviceType : last?.DeviceType ?? DeviceType.Unknown;
        var connected = hub.IsConnected(deviceUri);
        return (last ?? new DeviceStateDto { DeviceUri = deviceUri.ToString(), DeviceType = type, Connected = connected }) with
        {
            DeviceUri = deviceUri.ToString(),
            DeviceType = type,
            DisplayName = device?.DisplayName ?? last?.DisplayName,
            Connected = connected,
            LeaseOwner = hub.TryGetLease(deviceUri, out var lease) ? lease.OwnerLabel : null,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);
                await timeProvider.SleepAsync(Tick, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>One pass: reads every device that is due, pushes what changed, and says goodbye to what went away.</summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetTimestamp();
        var watched = events.ClientCount > 0
            || (Volatile.Read(ref _lastSnapshotRequest) is var asked && asked != 0 && timeProvider.GetElapsedTime(asked, now) < WatchedFor);

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uri, _) in hub.ConnectedDevices)
        {
            present.Add(uri.DeviceKey);
            _entries.TryGetValue(uri.DeviceKey, out var entry);

            // A device a run holds is the run's to read: its last reading stands, with the owner beside it.
            if (hub.TryGetLease(uri, out _))
            {
                Publish(uri, entry, Current(uri, entry?.State), entry?.ReadAt ?? 0, entry?.SteadySince ?? 0);
                continue;
            }

            if (entry is not null && timeProvider.GetElapsedTime(entry.ReadAt, now) < Interval(entry, watched, now))
            {
                continue;
            }

            try
            {
                var (state, steadySince) = await ReadAsync(uri, entry, now, cancellationToken);
                Publish(uri, entry, state, now, steadySince);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not read {Device}; trying again when it is next due", uri);
            }
        }

        // Held but not connected (a run that has claimed a device and not yet connected it) is still worth showing.
        foreach (var lease in hub.Leases)
        {
            if (present.Add(lease.DeviceUri.DeviceKey))
            {
                _entries.TryGetValue(lease.DeviceUri.DeviceKey, out var entry);
                Publish(lease.DeviceUri, entry, Current(lease.DeviceUri, entry?.State), entry?.ReadAt ?? 0, entry?.SteadySince ?? 0);
            }
        }

        // Gone: pushed once as disconnected, so a client does not go on showing a device the node let go.
        foreach (var (key, entry) in _entries)
        {
            if (!present.Contains(key) && _entries.TryRemove(key, out _))
            {
                var gone = Current(new Uri(entry.State.DeviceUri), entry.State);
                events.Broadcast(BroadcastEvents.DeviceState(gone));
            }
        }
    }

    /// <summary>How long a device may go unread: the GUI's cadences while someone watches, <see cref="Unwatched"/> otherwise.</summary>
    internal static TimeSpan Interval(DeviceType type, DeviceStateDto? last, long steadySince, bool watched, long now, ITimeProvider clock)
    {
        if (!watched)
        {
            return Unwatched;
        }

        return type switch
        {
            DeviceType.Focuser when last?.Focuser is { IsMoving: true } => TimeSpan.FromSeconds(1),
            DeviceType.CoverCalibrator when last?.Cover is { CoverState: CoverStatus.Moving } => TimeSpan.FromSeconds(1),
            DeviceType.Mount when last?.Mount is { IsSlewing: true } => TimeSpan.FromMilliseconds(500),
            // Fast for a while after the mount lands and starts tracking, so the reticle follows a finished goto, then
            // the steady sidereal rate, which is sub-pixel on any sky-map field and so only saves serial traffic.
            DeviceType.Mount when last?.Mount is { IsTracking: true } =>
                steadySince != 0 && clock.GetElapsedTime(steadySince, now) >= TimeSpan.FromSeconds(10)
                    ? TimeSpan.FromSeconds(10)
                    : TimeSpan.FromSeconds(1),
            _ => TimeSpan.FromSeconds(2),
        };
    }

    private TimeSpan Interval(Entry entry, bool watched, long now)
        => Interval(entry.State.DeviceType, entry.State, entry.SteadySince, watched, now, timeProvider);

    /// <summary>Reads the device by its type, and when its steady tracking began (0 when it is not steady).</summary>
    private async Task<(DeviceStateDto State, long SteadySince)> ReadAsync(Uri uri, Entry? entry, long now, CancellationToken cancellationToken)
    {
        var readUtc = timeProvider.GetUtcNow();
        var current = Current(uri, entry?.State);
        var steadySince = 0L;
        DeviceStateDto state;
        switch (current.DeviceType)
        {
            case DeviceType.Camera:
                var camera = await hub.ReadCameraAsync(uri, logger, cancellationToken);
                var intent = hub.TryGetCoolerIntent(uri, out var coolerIntent) ? coolerIntent : (CoolerIntent?)null;
                state = With(current, readUtc, camera: camera is { } c ? CameraDeviceStateDto.FromReading(c, intent) : null);
                break;

            case DeviceType.Focuser:
                var focuser = await hub.ReadFocuserAsync(uri, logger, cancellationToken);
                state = With(current, readUtc, focuser: focuser is { } f ? FocuserDeviceStateDto.FromReading(f) : null);
                break;

            case DeviceType.FilterWheel:
                var filterWheel = await hub.ReadFilterWheelAsync(uri, logger, cancellationToken);
                state = With(current, readUtc, filterWheel: filterWheel is { } w ? FilterWheelDeviceStateDto.FromReading(w) : null);
                break;

            case DeviceType.Mount:
                await RefreshSiteAsync(cancellationToken);
                var mount = await hub.ReadMountAsync(uri, SiteTransform, logger, cancellationToken);
                if (mount is { IsTracking: true, IsSlewing: false })
                {
                    steadySince = entry is { SteadySince: not 0 } ? entry.SteadySince : now;
                }
                state = With(current, readUtc, mount: mount is { } m ? MountDeviceStateDto.FromState(m, limits.VerdictFor(uri)) : null);
                break;

            case DeviceType.CoverCalibrator:
                var cover = await hub.ReadCoverAsync(uri, logger, cancellationToken);
                state = With(current, readUtc, cover: cover is { } k ? CoverDeviceStateDto.FromReading(k) : null);
                break;

            default:
                // No reading for this kind of device: its connection and lease are what there is to show.
                state = With(current, readUtc);
                break;
        }
        return (state, steadySince);
    }

    private static DeviceStateDto With(DeviceStateDto current, DateTimeOffset readUtc,
        CameraDeviceStateDto? camera = null, FocuserDeviceStateDto? focuser = null,
        FilterWheelDeviceStateDto? filterWheel = null, MountDeviceStateDto? mount = null, CoverDeviceStateDto? cover = null) => current with
    {
        ReadUtc = readUtc,
        Camera = camera,
        Focuser = focuser,
        FilterWheel = filterWheel,
        Mount = mount,
        Cover = cover,
    };

    /// <summary>Keeps <paramref name="state"/> as the device's last reading, and pushes it when it differs from the one before.</summary>
    private void Publish(Uri uri, Entry? entry, DeviceStateDto state, long readAt, long steadySince)
    {
        var compare = CompareBytes(state);
        _entries[uri.DeviceKey] = new Entry(state, compare, readAt, steadySince);
        if (entry is null || !entry.Compare.AsSpan().SequenceEqual(compare))
        {
            events.Broadcast(BroadcastEvents.DeviceState(state));
        }
    }

    /// <summary>
    /// The state as a push compares it: everything but when it was read, which moves on every read, with each sensor
    /// reading at the resolution a reader is shown it (a tenth of a degree, a whole percent of cooler power). A
    /// thermometer's last digits differ on every read, so compared in full, every device carrying one was pushed on every
    /// read. The served reading keeps its full precision; only the decision to push rounds.
    /// </summary>
    private static byte[] CompareBytes(DeviceStateDto state) => JsonSerializer.SerializeToUtf8Bytes(state with
    {
        ReadUtc = null,
        Camera = state.Camera is { } camera
            ? camera with
            {
                CcdTemperatureC = Shown(camera.CcdTemperatureC, 1),
                HeatsinkTemperatureC = Shown(camera.HeatsinkTemperatureC, 1),
                CoolerPowerPercent = Shown(camera.CoolerPowerPercent, 0),
            }
            : null,
        Focuser = state.Focuser is { } focuser ? focuser with { TemperatureC = Shown(focuser.TemperatureC, 1) } : null,
    }, HostingJsonContext.Default.DeviceStateDto);

    private static double? Shown(double? value, int digits) => value is { } v ? Math.Round(v, digits) : null;

    /// <summary>Reads the active profile's site again once <see cref="SiteRefresh"/> has passed: a small file, but a disk read.</summary>
    private async Task RefreshSiteAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetTimestamp();
        if (_siteReadAt != 0 && timeProvider.GetElapsedTime(_siteReadAt, now) < SiteRefresh)
        {
            return;
        }

        _siteReadAt = now;
        _siteProfile = hosted.ActiveProfileId is { } id && await Profile.TryReadDataAsync(external, id, cancellationToken) is { } data
            ? new Profile(id, "active", data)
            : null;
    }

    /// <summary>
    /// A transform at the active profile's site, for a topocentric mount's J2000; null when the node has no active
    /// profile or it names no site (<see cref="RefreshSiteAsync"/> keeps the profile).
    /// </summary>
    private Transform? SiteTransform() => _siteProfile is { } profile ? TransformFactory.FromProfile(profile, timeProvider, out _) : null;
}
