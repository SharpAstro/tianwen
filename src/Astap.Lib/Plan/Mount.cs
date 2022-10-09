using Astap.Lib.Devices;

namespace Astap.Lib.Plan;

public class Mount : ControllableDeviceBase<IMountDriver>
{
    public Mount(DeviceBase device) : base(device) { }

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }

    public TrackingSpeed TrackingSpeed
    {
        get => Driver.TrackingSpeed;
        set => Driver.TrackingSpeed = value;
    }

    public bool IsSlewing => Driver.IsSlewing;

    /// <summary>
    ///
    /// </summary>
    /// <param name="ra">RA in degrees (0..360)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>True if slewing operation was accepted and mount is slewing</returns>
    public bool SlewAsync(double ra, double dec) => Driver.SlewAsync(ra, dec);
}
