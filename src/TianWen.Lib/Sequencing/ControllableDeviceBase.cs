using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public abstract record ControllableDeviceBase<TDriver> : IAsyncDisposable
    where TDriver : IDeviceDriver
{
    public ControllableDeviceBase(DeviceBase device, IExternal external)
    {
        Device = device;
        if (device.TryInstantiateDriver<TDriver>(external, out var driver))
        {
            (Driver = driver).DeviceConnectedEvent += Driver_DeviceConnectedEvent;
        }
        else
        {
            throw new ArgumentException($"Could not instantiate driver {typeof(TDriver)} for device {device.DisplayName} which is a {device.DeviceType}", nameof(device));
        }
    }

    protected abstract void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e);

    public DeviceBase Device { get; }

    public TDriver Driver { get; }

    public override string ToString() => Device.DisplayName;

    public async ValueTask DisposeAsync()
    {
        if (Driver.Connected)
        {
            await Driver.DisconnectAsync();
        }

        GC.SuppressFinalize(this);
    }
}
