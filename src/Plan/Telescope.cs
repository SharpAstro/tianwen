using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using System;

namespace Astap.Lib.Plan
{
    public class Telescope<TDevice, TCameraDriver, TCoverDriver, TFocuserDriver, TEFWDriver> : IDisposable
        where TDevice : DeviceBase
        where TCameraDriver : IDeviceDriver
        where TCoverDriver : IDeviceDriver
        where TFocuserDriver : IDeviceDriver
        where TEFWDriver : IDeviceDriver
    {
        private bool disposedValue;

        public Telescope(
            string name,
            int focalLength,
            CameraBase<TDevice, TCameraDriver> camera,
            CoverBase<TDevice, TCoverDriver>? cover,
            FocuserBase<TDevice, TFocuserDriver>? focuser,
            FilterWheelBase<TDevice, TEFWDriver> filterWheel
        )
        {
            Name = name;
            FocalLength = focalLength;
            Camera = camera;
            Cover = cover;
            Focuser = focuser;
            FilterWheel = filterWheel;
        }

        public string Name { get; }

        public int FocalLength { get; }

        public CameraBase<TDevice, TCameraDriver> Camera { get; }

        public CoverBase<TDevice, TCoverDriver>? Cover { get; }

        public FocuserBase<TDevice, TFocuserDriver>? Focuser { get; }

        public FilterWheelBase<TDevice, TEFWDriver>? FilterWheel { get; }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Camera.Dispose();
                    Cover?.Dispose();
                    Focuser?.Dispose();
                    FilterWheel?.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~Telescope()
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
