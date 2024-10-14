using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Sequencing;

public abstract record ControllableDeviceBase<TDriver> : IDisposable
    where TDriver : IDeviceDriver
{
    private bool disposedValue;

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

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                Driver.Connected = false;
                Driver.DeviceConnectedEvent -= Driver_DeviceConnectedEvent;
                Driver.Dispose();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~ControllableDeviceBase()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
