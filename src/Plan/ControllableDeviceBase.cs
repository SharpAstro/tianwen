using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Plan;

public abstract class ControllableDeviceBase<TDriver> : IDisposable
    where TDriver : IDeviceDriver
{
    private readonly TDriver _driver;
    private bool disposedValue;

    public ControllableDeviceBase(DeviceBase device)
    {
        Device = device;
        if (device.TryInstantiateDriver<TDriver>(out var driver))
        {
            _driver = driver;
            driver.DeviceConnectedEvent += Driver_DeviceConnectedEvent;
        }
        else
        {
            throw new ArgumentException($"Could not instantiate driver {typeof(TDriver)} for device {device} which is a {device.DeviceType}", nameof(device));
        }
    }

    protected abstract void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e);

    public DeviceBase Device { get; }

    public TDriver Driver => _driver;

    public bool Connected
    {
        get => _driver?.Connected == true;
        set
        {
            if (Driver is TDriver driver)
            {
                driver.Connected = value;
            }
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                if (_driver is IDeviceDriver driver)
                {
                    driver.Connected = false;
                    driver.DeviceConnectedEvent -= Driver_DeviceConnectedEvent;
                    driver.Dispose();
                }
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
