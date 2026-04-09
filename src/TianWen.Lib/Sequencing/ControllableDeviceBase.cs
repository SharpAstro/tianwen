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
            (Driver = hubDriver).DeviceConnectedEvent += Driver_DeviceConnectedEvent;
            _borrowed = true;
        }
        else if (device.TryInstantiateDriver<TDriver>(sp, out var driver))
        {
            (Driver = driver).DeviceConnectedEvent += Driver_DeviceConnectedEvent;
            _borrowed = false;
        }
        else
        {
            throw new ArgumentException($"Could not instantiate driver {typeof(TDriver)} for device {device.DisplayName} which is a {device.DeviceType}", nameof(device));
        }
    }

    protected abstract void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e);

    public DeviceBase Device { get; }

    public TDriver Driver { get; }

    /// <summary>
    /// Whether this device's driver was borrowed from <see cref="IDeviceHub"/> rather than
    /// created fresh. Borrowed drivers are not disconnected on dispose — they stay in the hub.
    /// </summary>
    public bool Borrowed => _borrowed;

    public override string ToString() => Device.DisplayName;

    public async ValueTask DisposeAsync()
    {
        Driver.DeviceConnectedEvent -= Driver_DeviceConnectedEvent;

        if (!_borrowed && Driver.Connected)
        {
            await Driver.DisconnectAsync();
        }

        GC.SuppressFinalize(this);
    }
}
