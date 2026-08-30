using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

internal class DeviceHub(IServiceProvider serviceProvider, ILogger<DeviceHub> logger) : IDeviceHub
{
    private readonly ConcurrentDictionary<string, (DeviceBase Device, IDeviceDriver Driver)> _connected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Live ownership claims, keyed the same way as <see cref="_connected"/> so a lease survives the
    /// query part of a URI changing (a re-plugged mount moving COM5 -> COM6 is the same device).
    /// A lease is deliberately independent of connection state: a run owns its devices across a driver
    /// reconnect, which is precisely when a stray disconnect would do the most damage.
    /// </summary>
    private readonly ConcurrentDictionary<string, LeaseHandle> _leases = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<DeviceConnectedEventArgs>? DeviceStateChanged;

    // ── URI → DeviceBase factory (absorbed from DeviceUriRegistry) ──

    public bool TryGetDeviceFromUri(Uri uri, [NotNullWhen(true)] out DeviceBase? device)
    {
        var func = serviceProvider.GetKeyedService<Func<Uri, DeviceBase>>(uri.Host.ToLowerInvariant());

        if (func is not null)
        {
            device = func(uri);
            return true;
        }

        device = default;
        return false;
    }

    // ── Driver lifecycle ──

    public async ValueTask<IDeviceDriver> ConnectAsync(DeviceBase device, CancellationToken cancellationToken = default)
    {
        var key = device.DeviceUri.DeviceKey;

        if (_connected.TryGetValue(key, out var existing) && existing.Driver.Connected)
        {
            return existing.Driver;
        }

        if (!device.TryInstantiateDriver<IDeviceDriver>(serviceProvider, out var driver))
        {
            throw new InvalidOperationException($"Could not instantiate driver for device {device.DisplayName} ({device.DeviceType})");
        }

        try
        {
            await driver.ConnectAsync(cancellationToken);
        }
        catch
        {
            await driver.DisposeAsync();
            throw;
        }

        _connected[key] = (device, driver);

        logger.LogInformation("DeviceHub: connected {DeviceType} {DisplayName}", device.DeviceType, device.DisplayName);
        DeviceStateChanged?.Invoke(this, new DeviceConnectedEventArgs(connected: true));

        return driver;
    }

    public async ValueTask DisconnectAsync(Uri deviceUri, bool force = false, CancellationToken cancellationToken = default)
    {
        var key = deviceUri.DeviceKey;

        // Ownership is checked BEFORE the TryRemove: refusing after the entry is gone would leave the hub
        // believing the device is disconnected while the run keeps driving it.
        if (!force && _leases.TryGetValue(key, out var lease))
        {
            throw new DeviceLeasedException(lease.Claim);
        }

        if (!_connected.TryRemove(key, out var entry))
        {
            return;
        }

        try
        {
            if (entry.Driver.Connected)
            {
                await entry.Driver.DisconnectAsync(cancellationToken);
            }
        }
        finally
        {
            await entry.Driver.DisposeAsync();
        }

        logger.LogInformation("DeviceHub: disconnected {DeviceType} {DisplayName}", entry.Device.DeviceType, entry.Device.DisplayName);
        DeviceStateChanged?.Invoke(this, new DeviceConnectedEventArgs(connected: false));
    }

    public bool TryGetConnectedDriver<T>(Uri deviceUri, [NotNullWhen(true)] out T? driver) where T : class, IDeviceDriver
    {
        var key = deviceUri.DeviceKey;

        if (_connected.TryGetValue(key, out var entry) && entry.Driver is T typed && entry.Driver.Connected)
        {
            driver = typed;
            return true;
        }

        driver = default;
        return false;
    }

    public IReadOnlyList<(Uri DeviceUri, IDeviceDriver Driver)> ConnectedDevices =>
        _connected.Values.Select(e => (e.Device.DeviceUri, e.Driver)).ToList();

    public bool IsConnected(Uri deviceUri) =>
        _connected.TryGetValue(deviceUri.DeviceKey, out var entry) && entry.Driver.Connected;

    // ── Ownership ──

    public bool TryAcquireLease(Uri deviceUri, string ownerLabel, [NotNullWhen(true)] out IDisposable? lease)
    {
        var key = deviceUri.DeviceKey;
        var handle = new LeaseHandle(this, key, new DeviceLease(deviceUri, ownerLabel));

        if (!_leases.TryAdd(key, handle))
        {
            lease = null;
            return false;
        }

        logger.LogDebug("DeviceHub: {Owner} took ownership of {DeviceUri}", ownerLabel, deviceUri);
        lease = handle;
        return true;
    }

    public bool TryGetLease(Uri deviceUri, out DeviceLease lease)
    {
        if (_leases.TryGetValue(deviceUri.DeviceKey, out var handle))
        {
            lease = handle.Claim;
            return true;
        }

        lease = default;
        return false;
    }

    public IReadOnlyList<DeviceLease> Leases => _leases.Values.Select(static h => h.Claim).ToList();

    /// <summary>
    /// Releases a claim, but only if the slot still holds THIS handle.
    /// <para>
    /// Keyed on the handle's reference identity rather than the <see cref="DeviceLease"/> value, because
    /// two successive claims on the same device by the same owner ("the imaging session", twice in one
    /// evening) are equal <i>by value</i> -- so a stale handle disposed late would silently unlock the
    /// device the CURRENT run is driving. Reference identity makes that impossible.
    /// </para>
    /// </summary>
    private void ReleaseLease(string key, LeaseHandle handle)
    {
        if (_leases.TryRemove(new KeyValuePair<string, LeaseHandle>(key, handle)))
        {
            logger.LogDebug("DeviceHub: {Owner} released {DeviceUri}", handle.Claim.OwnerLabel, handle.Claim.DeviceUri);
        }
    }

    private sealed class LeaseHandle(DeviceHub hub, string key, DeviceLease claim) : IDisposable
    {
        private int _released;

        internal DeviceLease Claim { get; } = claim;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                hub.ReleaseLease(key, this);
            }
        }
    }

    public async ValueTask<bool> IsCoolingAsync(Uri deviceUri, CancellationToken cancellationToken = default)
    {
        if (!_connected.TryGetValue(deviceUri.DeviceKey, out var entry))
        {
            return false;
        }

        if (entry.Driver is not ICameraDriver camera || !camera.CanGetCoolerOn)
        {
            return false;
        }

        return await camera.GetCoolerOnAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, (device, _)) in _connected.ToArray())
        {
            try
            {
                // force: the process is going down, so the hardware must come down with it regardless of
                // which run still believes it owns a driver. A quit path is supposed to abort the local
                // session first (which drops its leases); this is the backstop for when it did not.
                await DisconnectAsync(device.DeviceUri, force: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DeviceHub: error during shutdown disconnect for {Device}", device.DisplayName);
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Device identity key: scheme + authority + path, ignoring query/fragment.
    /// Matches <see cref="DeviceBase.SameDevice"/>.
    /// </summary>
}
