using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public abstract record ControllableDeviceBase<TDriver> : IAsyncDisposable
    where TDriver : class, IDeviceDriver
{
    private readonly bool _borrowed;

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

    public TDriver Driver { get; }

    /// <summary>
    /// Whether this device's driver was borrowed from <see cref="IDeviceHub"/> rather than
    /// created fresh. Borrowed drivers are not disconnected on dispose; they stay in the hub.
    /// </summary>
    public bool Borrowed => _borrowed;

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
