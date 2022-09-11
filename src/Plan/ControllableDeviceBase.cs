using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Plan
{
    public abstract class ControllableDeviceBase<TDevice, TDriver> : IDisposable
        where TDevice : DeviceBase
        where TDriver : IDeviceDriver
    {
        private readonly TDriver _driver;
        private bool disposedValue;

        public ControllableDeviceBase(TDevice device)
        {
            Device = device;
            if (device.TryInstantiateDriver<TDriver>(out var driver))
            {
                _driver = driver;
            }
            else
            {
                throw new ArgumentException($"Could not instantiate driver {typeof(TDriver)} for device {device}", nameof(device));
            }
        }

        public TDevice Device { get; }

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
                    _driver?.Dispose();
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
}
