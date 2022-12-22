using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Astap.Lib.Astrometry;

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

    IReadOnlyCollection<TrackingSpeed> TrackingSpeeds { get; }

    EquatorialCoordinateType EquatorialSystem { get; }

    bool Tracking { get; set; }

    bool AtHome { get; }

    bool IsSlewing { get; }

    /// <summary>
    /// Slews to given coordinates (in the mounts native epoch, <see cref="EquatorialSystem"/>).
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>True if slewing operation was accepted and mount is slewing</returns>
    bool SlewAsync(double ra, double dec);

    DateTime? UTCDate { get; set; }

    bool TryGetUTCDate(out DateTime dateTime)
    {
        if (Connected && UTCDate is DateTime utc)
        {
            dateTime = utc;
            return true;
        }

        dateTime = DateTime.MinValue;
        return false;
    }

    PierSide SideOfPier { get; set; }

    /// <summary>
    /// The elevation above mean sea level (meters) of the site at which the telescope is located.
    /// </summary>
    double SiteElevation { get; set; }

    /// <summary>
    /// The geodetic(map) latitude (degrees, positive North, WGS84) of the site at which the telescope is located.
    /// </summary>
    double SiteLatitude { get; set; }

    /// <summary>
    /// The longitude (degrees, positive East, WGS84) of the site at which the telescope is located.
    /// </summary>
    double SiteLongitude { get; set; }
}