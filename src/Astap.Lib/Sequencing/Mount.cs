
using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Sequencing;

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

    public bool Tracking
    {
        get => Driver.Tracking;
        set => Driver.Tracking = value;
    }

    public bool IsSlewing => Driver.IsSlewing;

    public PierSide SideOfPier
    {
        get => Driver.SideOfPier;
        set => Driver.SideOfPier = value;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="ra">RA in degrees (0..360)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>True if slewing operation was accepted and mount is slewing</returns>
    public bool SlewAsync(double ra, double dec) => Driver.CanSlewAsync && Driver.SlewAsync(ra, dec);

    public DateTime UTCDate
    {
        get => Driver.UTCDate ?? throw new InvalidOperationException("Cannot determine UTC date from mount driver");
        set => Driver.UTCDate = value;
    }
}
