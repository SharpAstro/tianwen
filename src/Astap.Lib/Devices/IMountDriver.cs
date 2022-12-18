using System;

namespace Astap.Lib.Devices;

public interface IMountDriver : IDeviceDriver
{
    bool CanSetTracking { get; }

    bool CanSetSideOfPier { get; }

    bool CanPark { get; }

    bool CanSetPark { get; }

    bool CanSlew { get; }

    bool CanSlewAsync { get; }

    TrackingSpeed TrackingSpeed { get; set; }

    bool Tracking { get; set; }

    bool AtHome { get; }

    bool IsSlewing { get; }

    /// <summary>
    /// Slews to given coordinates (in the mounts native epoch).
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>True if slewing operation was accepted and mount is slewing</returns>
    bool SlewAsync(double ra, double dec);

    DateTime? UTCDate { get; set; }

    PierSide SideOfPier { get; set; }
}