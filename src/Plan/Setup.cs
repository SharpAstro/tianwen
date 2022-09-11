using Astap.Lib.Devices;
using System;
using System.Collections.Generic;

namespace Astap.Lib.Plan
{
    public class Setup<TDevice, TMountDriver, TCameraDriver, TCoverDriver, TFocuserDriver, TEFWDriver> : IDisposable
        where TDevice : DeviceBase
        where TMountDriver : IDeviceDriver
        where TCameraDriver : IDeviceDriver
        where TCoverDriver : IDeviceDriver
        where TFocuserDriver : IDeviceDriver
        where TEFWDriver : IDeviceDriver
    {
        private readonly List<Telescope<TDevice, TCameraDriver, TCoverDriver, TFocuserDriver, TEFWDriver>> _telescopes;
        private bool disposedValue;

        public Setup(
            MountBase<TDevice, TMountDriver> mount,
            Guider guider,
            Telescope<TDevice, TCameraDriver, TCoverDriver, TFocuserDriver, TEFWDriver> telescope,
            params Telescope<TDevice, TCameraDriver, TCoverDriver, TFocuserDriver, TEFWDriver>[] telescopes)
        {
            Mount = mount;
            Guider = guider;
            _telescopes = new(telescopes.Length + 1)
            {
                telescope
            };
            _telescopes.AddRange(telescopes);
        }

        public MountBase<TDevice, TMountDriver> Mount { get; }

        public Guider Guider { get; }

        public ICollection<Telescope<TDevice, TCameraDriver, TCoverDriver, TFocuserDriver, TEFWDriver>> Telescopes { get { return _telescopes; } }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Mount.Dispose();
                    Guider.Dispose();
                    foreach (var telescope in _telescopes)
                    {
                        telescope.Dispose();
                    }
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~Setup()
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
