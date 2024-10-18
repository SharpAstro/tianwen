using Astap.Lib.Devices;

namespace Astap.Lib.Sequencing;

public record Mount(DeviceBase Device, IExternal External) : ControllableDeviceBase<IMountDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }

    public bool EnsureTracking(TrackingSpeed speed = TrackingSpeed.Sidereal)
    {
        if (!Driver.Connected)
        {
            return false;
        }

        if (Driver.CanSetTracking && (Driver.TrackingSpeed != speed || !Driver.Tracking))
        {
            Driver.TrackingSpeed = speed;
            Driver.Tracking = true;
        }

        return Driver.Tracking;
    }
}