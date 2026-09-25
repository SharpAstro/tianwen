using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public abstract record ControllableDeviceBase<TDriver> : IAsyncDisposable
    where TDriver : class, IDeviceDriver
{
    private bool _borrowed;

    public ControllableDeviceBase(DeviceBase device, IServiceProvider sp)
    {
        Device = device;

        // Try to borrow a connected driver from the hub first
        var hub = sp.GetService<IDeviceHub>();
        if (hub is not null && hub.TryGetConnectedDriver<TDriver>(device.DeviceUri, out var hubDriver))
        {
            Driver = hubDriver;
            _borrowed = true;
        }
        else if (device.TryInstantiateDriver<TDriver>(sp, out var driver))
        {
            Driver = driver;
            _borrowed = false;
        }
        else
        {
            throw new ArgumentException($"Could not instantiate driver {typeof(TDriver)} for device {device.DisplayName} which is a {device.DeviceType}", nameof(device));
        }
    }

    public DeviceBase Device { get; }

    /// <summary>
    /// The driver. Replaced only by <see cref="ConnectAsync"/>, and only when the hub turns out to hold a
    /// connected driver for this device already, so a caller reads it afresh rather than keeping a copy from
    /// before the connect.
    /// </summary>
    public TDriver Driver { get; private set; }

    /// <summary>
    /// Whether this device's driver is the hub's: borrowed when the wrapper was built, or handed to the hub by
    /// <see cref="ConnectAsync"/>. The hub's drivers are not disconnected on dispose; they stay in the hub.
    /// </summary>
    public bool Borrowed => _borrowed;

    /// <summary>
    /// Connects this device THROUGH <paramref name="hub"/>, so the hub holds its driver and every surface sees
    /// the one instance. The driver this wrapper built is handed to the hub (<see cref="IDeviceHub.AdoptAsync"/>)
    /// with whatever its caller configured on it, or, when the hub already holds a connected one, this wrapper
    /// switches to that. Without a hub it connects its own driver, as before.
    /// <para>
    /// A run used to connect drivers of its own unless the hub held them connected already, so on a server,
    /// where nothing pre-connects, the run and the hub were two driver worlds: <c>/devices</c> said the run's
    /// devices were disconnected, the Alpaca plane could not see them, and its <c>Connected=true</c> opened a
    /// second driver on a device the run was driving (P0b item 11 of docs/plans/hardware-in-the-server.md,
    /// #752).
    /// </para>
    /// </summary>
    internal async ValueTask ConnectAsync(IDeviceHub? hub, CancellationToken cancellationToken)
    {
        if (hub is null)
        {
            await Driver.ConnectAsync(cancellationToken);
            return;
        }

        var held = await hub.AdoptAsync(Device, Driver, cancellationToken);
        if (!ReferenceEquals(held, Driver))
        {
            if (held is not TDriver hubs)
            {
                throw new InvalidOperationException(
                    $"The hub holds a {held.GetType().Name} for {Device.DisplayName}, not a {typeof(TDriver).Name}");
            }

            // Something connected this device through the hub after this wrapper was built. Its own driver
            // never connected, so nothing else holds it and it is released here.
            var own = Driver;
            Driver = hubs;
            if (!_borrowed)
            {
                await own.DisposeAsync();
            }
        }

        _borrowed = true;
    }

    public override string ToString() => Device.DisplayName;

    /// <summary>
    /// Disconnects a driver this wrapper created itself; a borrowed driver stays the hub's. This is
    /// the wrapper's ONLY resource: it deliberately subscribes to nothing on the driver (an earlier
    /// <c>DeviceConnectedEvent</c> subscription fed an abstract handler that every subclass
    /// implemented empty, and its only observable effect was that a wrapper orphaned by a
    /// mid-assembly throw in <c>SessionFactory</c> stayed subscribed to a hub-lived driver forever).
    /// A wrapper whose driver never connected therefore holds nothing, and disposing it is a true
    /// no-op -- the fact SessionFactory's CA2000 suppression relies on.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_borrowed && Driver.Connected)
        {
            await Driver.DisconnectAsync();
        }

        GC.SuppressFinalize(this);
    }
}
