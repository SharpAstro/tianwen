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

    /// <summary>
    /// One connect, adoption or disconnect of a device at a time, keyed like <see cref="_connected"/> (#806). Each reads the
    /// entry, awaits the device, then stores, so two at once (a session's initialisation and an Alpaca client's
    /// <c>Connected=true</c>) both found no connected entry, both connected a driver, and the last store won: the other
    /// driver stayed connected outside the hub, a second handle on a camera SDK and a refused open on a COM port. One gate
    /// per device ever connected, never removed or disposed (a SemaphoreSlim whose wait handle is never read holds
    /// nothing), so two devices never wait on each other.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each camera's cooler is being asked to do, keyed like <see cref="_connected"/>.</summary>
    private readonly ConcurrentDictionary<string, CoolerIntent> _coolerIntents = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<DeviceConnectedEventArgs>? DeviceStateChanged;

    public event EventHandler? CoolerIntentChanged;

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

    private SemaphoreSlim GateFor(string key) => _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    public async ValueTask<IDeviceDriver> ConnectAsync(DeviceBase device, CancellationToken cancellationToken = default)
    {
        var key = device.DeviceUri.DeviceKey;
        var gate = GateFor(key);
        IDeviceDriver driver;
        string verb;

        await gate.WaitAsync(cancellationToken);
        try
        {
            _connected.TryGetValue(key, out var existing);
            if (existing.Driver is { Connected: true } held)
            {
                return held;
            }

            if (existing.Driver is { } down && existing.Device.DeviceUri == device.DeviceUri)
            {
                await down.ConnectAsync(cancellationToken);
                driver = down;
                verb = "reconnected";
            }
            else
            {
                if (!device.TryInstantiateDriver<IDeviceDriver>(serviceProvider, out var created))
                {
                    throw new InvalidOperationException($"Could not instantiate driver for device {device.DisplayName} ({device.DeviceType})");
                }

                try
                {
                    await created.ConnectAsync(cancellationToken);
                }
                catch
                {
                    await created.DisposeAsync();
                    throw;
                }

                _connected[key] = (device, created);
                driver = created;
                verb = "connected";

                if (existing.Driver is { } replaced)
                {
                    await replaced.DisposeAsync();
                }
            }
        }
        finally
        {
            gate.Release();
        }

        logger.LogInformation("DeviceHub: {Verb} {DeviceType} {DisplayName}", verb, device.DeviceType, device.DisplayName);
        DeviceStateChanged?.Invoke(this, new DeviceConnectedEventArgs(connected: true));

        return driver;
    }

    public async ValueTask<IDeviceDriver> AdoptAsync(DeviceBase device, IDeviceDriver driver, CancellationToken cancellationToken = default)
    {
        var key = device.DeviceUri.DeviceKey;
        var gate = GateFor(key);

        await gate.WaitAsync(cancellationToken);
        try
        {
            _connected.TryGetValue(key, out var existing);
            if (existing.Driver is { Connected: true } held)
            {
                return held;
            }

            if (!driver.Connected)
            {
                await driver.ConnectAsync(cancellationToken);
            }

            _connected[key] = (device, driver);

            // A driver the entry held before went down on its own (a run's Finalise disconnects the mount it
            // drove). Nothing reaches it through the hub any more, so it is released here rather than leaked.
            if (existing.Driver is { } stale && !ReferenceEquals(stale, driver))
            {
                await stale.DisposeAsync();
            }
        }
        finally
        {
            gate.Release();
        }

        logger.LogInformation("DeviceHub: adopted {DeviceType} {DisplayName}", device.DeviceType, device.DisplayName);
        DeviceStateChanged?.Invoke(this, new DeviceConnectedEventArgs(connected: true));

        return driver;
    }

    public async ValueTask DisconnectAsync(Uri deviceUri, bool force = false, CancellationToken cancellationToken = default)
    {
        var key = deviceUri.DeviceKey;
        var gate = GateFor(key);
        (DeviceBase Device, IDeviceDriver Driver) entry;

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Ownership is checked BEFORE the TryRemove: refusing after the entry is gone would leave the hub
            // believing the device is disconnected while the run keeps driving it.
            if (!force && _leases.TryGetValue(key, out var lease))
            {
                throw new DeviceLeasedException(lease.Claim);
            }

            if (!_connected.TryRemove(key, out entry))
            {
                return;
            }

            // A camera that is no longer the hub's is no longer the node's to re-establish.
            _coolerIntents.TryRemove(key, out _);

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
        }
        finally
        {
            gate.Release();
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
        _connected.Values.Where(static e => e.Driver.Connected).Select(static e => (e.Device.DeviceUri, e.Driver)).ToList();

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

    // ── Cooler intent ──

    public void SetCoolerIntent(Uri cameraUri, CoolerIntent intent)
    {
        var key = cameraUri.DeviceKey;
        if (_coolerIntents.TryGetValue(key, out var previous) && previous == intent)
        {
            return;
        }

        _coolerIntents[key] = intent;
        logger.LogDebug("DeviceHub: the cooler of {DeviceUri} is asked to {Kind} {Setpoint}", cameraUri, intent.Kind, intent.SetpointC);
        CoolerIntentChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TryGetCoolerIntent(Uri cameraUri, out CoolerIntent intent) => _coolerIntents.TryGetValue(cameraUri.DeviceKey, out intent);

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
