using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.SOFA;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using static Astap.Lib.Astrometry.CoordinateUtils;

namespace Astap.Lib.Devices;

public interface IMountDriver : IDeviceDriver
{
    bool CanSetTracking { get; }

    bool CanSetSideOfPier { get; }

    bool CanPark { get; }

    bool CanSetPark { get; }

    bool CanUnpark { get; }

    bool CanSlew { get; }

    bool CanSlewAsync { get; }

    bool CanSync { get; }

    TrackingSpeed TrackingSpeed { get; set; }

    IReadOnlyCollection<TrackingSpeed> TrackingSpeeds { get; }

    EquatorialCoordinateType EquatorialSystem { get; }

    bool Tracking { get; set; }

    bool AtHome { get; }

    bool AtPark { get; }

    // returns true if park command was accepted
    bool Park();

    bool Unpark();

    bool PulseGuide(GuideDirection direction, TimeSpan duration);

    bool IsSlewing { get; }

    /// <summary>
    /// Slews to given equatorial coordinates (RA, Dec) in the mounts native epoch, <see cref="EquatorialSystem"/>.
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>True if slewing operation was accepted and mount is slewing</returns>
    bool SlewRaDecAsync(double ra, double dec);

    /// <summary>
    /// Slews to given equatorial coordinates (HA, Dec) in the mounts native epoch, <see cref="EquatorialSystem"/>.
    /// Uses current <see cref="SiderealTime"/> to convert to RA.
    /// Succeeds if <see cref="Connected"/> and <see cref="SlewRaDecAsync(double, double)"/> succeeds.
    /// </summary>
    /// <param name="ha">HA in hours (-12..12), as returned by <see cref="HourAngle"/></param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>True if slewing operation was accepted and mount is slewing</returns>
    bool SlewHourAngleDecAsync(double ha, double dec)
        => Connected
        && !double.IsNaN(SiderealTime)
        && ha is >= -12 and <= 12
        && SlewRaDecAsync(ConditionRA(SiderealTime - ha - 12), dec);

    /// <summary>
    /// Syncs to given equatorial coordinates (RA, Dec) in the mounts native epoch, <see cref="EquatorialSystem"/>.
    /// Can still throw exceptions when underlying implementation prohibits syncing.
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>true if mount is synced to the given coordinates.</returns>
    bool SyncRaDec(double ra, double dec);

    /// <summary>
    /// Calls <see cref="SyncRaDec(double, double)"/> by first transforming J2000 coordinates to native ones using <see cref="TryTransformJ2000ToMountNative"/>.
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>true if mount is synced to the given coordinates.</returns>
    bool SyncRaDecJ2000(double ra, double dec, TimeProvider? timeProvider = null)
        => Connected && CanSync
        && TryGetTransform(timeProvider ?? TimeProvider.System, out var transform)
        && TryTransformJ2000ToMountNative(transform, ra, dec, updateTime: false, out var raMount, out var decMount, out _, out _)
        && SyncRaDec(raMount, decMount);

    /// <summary>
    /// The UTC date/time of the telescope's internal clock.
    /// Must be initalised from system time if no internal clock is supported.
    /// </summary>
    DateTime? UTCDate { get; set; }

    /// <summary>
    /// Returns true iff <see cref="UTCDate"/> was updated succcessfully when setting,
    /// typically via <code>UTCDate = DateTime.UTCNow</code>.
    /// </summary>
    bool TimeIsSetByUs { get; }

    bool TryGetUTCDate(out DateTime dateTime)
    {
        try
        {
            if (Connected && UTCDate is DateTime utc)
            {
                dateTime = utc;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        dateTime = DateTime.MinValue;
        return false;
    }

    PierSide SideOfPier { get; set; }

    /// <summary>
    /// Predict side of pier for German equatorial mounts.
    /// </summary>
    /// <param name="ra">The destination right ascension(hours)</param>
    /// <param name="dec">The destination declination (degrees, positive North)</param>
    /// <returns></returns>
    PierSide DestinationSideOfPier(double ra, double dec);

    /// <summary>
    /// Uses <see cref="DestinationSideOfPier"/> and equatorial coordinates as of now (<see cref="RightAscension"/>, <see cref="Declination"/>)
    /// To calculate the <see cref="SideOfPier"/> that the telescope should be on if one where to slew there now.
    /// </summary>
    PierSide ExpectedSideOfPier => Connected ? DestinationSideOfPier(RightAscension, Declination) : PierSide.Unknown;

    /// <summary>
    /// The current hour angle, using <see cref="RightAscension"/> and <see cref="SiderealTime"/>, (-12,12).
    /// </summary>
    double HourAngle => Connected ? ConditionHA(SiderealTime - RightAscension + 12) : double.NaN;

    /// <summary>
    /// The local apparent sidereal time from the telescope's internal clock (hours, sidereal).
    /// </summary>
    double SiderealTime { get; }

    /// <summary>
    /// The right ascension (hours) of the telescope's current equatorial coordinates, in the coordinate system given by the <see cref="EquatorialSystem"/> property.
    /// </summary>
    double RightAscension { get; }

    /// <summary>
    /// The declination (degrees) of the telescope's current equatorial coordinates, in the coordinate system given by the <see cref="EquatorialSystem"/> property.
    /// </summary>
    double Declination { get; }

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

    /// <summary>
    /// Initialises using standard pressure and atmosphere. Please adjust if available.
    /// </summary>
    /// <param name="transform"></param>
    /// <returns></returns>
    bool TryGetTransform(TimeProvider timeProvider, [NotNullWhen(true)] out Transform? transform)
    {
        if (Connected && TryGetUTCDate(out var utc))
        {
            transform = new Transform(timeProvider)
            {
                SiteElevation = SiteElevation,
                SiteLatitude = SiteLatitude,
                SiteLongitude = SiteLongitude,
                SitePressure = 1010, // TODO standard atmosphere
                SiteTemperature = 10, // TODO check either online or if compatible devices connected
                DateTime = utc,
                Refraction = true // TODO assumes that driver does not support/do refraction
            };

            return true;
        }

        transform = null;
        return false;
    }

    /// <summary>
    /// Not reentrant if using a shared <paramref name="transform"/>.
    /// </summary>
    /// <param name="mount"></param>
    /// <param name="transform"></param>
    /// <param name="observation"></param>
    /// <param name="raMount"></param>
    /// <param name="decMount"></param>
    /// <returns>true if transform was successful.</returns>
    bool TryTransformJ2000ToMountNative(Transform transform, double raJ2000, double decJ2000, bool updateTime, out double raMount, out double decMount, out double az, out double alt)
    {
        if (Connected && updateTime && TryGetUTCDate(out var utc))
        {
            transform.DateTime = utc;
        }
        else if (updateTime || !Connected)
        {
            raMount = double.NaN;
            decMount = double.NaN;
            az = double.NaN;
            alt = double.NaN;
            return false;
        }

        transform.SetJ2000(raJ2000, decJ2000);
        transform.Refresh();

        (raMount, decMount) = EquatorialSystem switch
        {
            EquatorialCoordinateType.J2000 => (transform.RAJ2000, transform.DecJ2000),
            EquatorialCoordinateType.Topocentric => (transform.RAApparent, transform.DECApparent),
            _ => (double.NaN, double.NaN)
        };
        az = transform.AzimuthTopocentric;
        alt = transform.ElevationTopocentric;

        return !double.IsNaN(raMount) && !double.IsNaN(decMount) && !double.IsNaN(az) && !double.IsNaN(alt);
    }

    public bool IsOnSamePierSide(double hourAngleAtSlewTime)
    {
        var pierSide = External.Catch(() => SideOfPier, PierSide.Unknown);
        var currentHourAngle = External.Catch(() => HourAngle, double.NaN);
        return pierSide == ExpectedSideOfPier
            && !double.IsNaN(currentHourAngle)
            && (pierSide != PierSide.Unknown || Math.Sign(hourAngleAtSlewTime) == Math.Sign(currentHourAngle));
    }

    public bool SlewToZenith(TimeSpan distMeridian, CancellationToken cancellationToken)
    {
        if (CanSlew && SlewHourAngleDecAsync((TimeSpan.FromHours(12) - distMeridian).TotalHours, SiteLatitude))
        {
            while (IsSlewing && !cancellationToken.IsCancellationRequested)
            {
                External.Sleep(TimeSpan.FromSeconds(1));
            }

            return !cancellationToken.IsCancellationRequested;
        }

        return false;
    }

    public SlewResult SlewToTarget(int minAboveHorizon, Target target, CancellationToken cancellationToken)
    {
        var az = double.NaN;
        var alt = double.NaN;
        var dsop = PierSide.Unknown;
        if (!TryGetTransform(External.TimeProvider, out var transform)
            || !TryTransformJ2000ToMountNative(transform, target.RA, target.Dec, updateTime: false, out var raMount, out var decMount, out az, out alt)
            || double.IsNaN(alt)
            || alt < minAboveHorizon
            || (dsop = DestinationSideOfPier(raMount, decMount)) == PierSide.Unknown
            || !SlewRaDecAsync(raMount, decMount)
        )
        {
            External.LogError($"Failed to slew {Name} to target {target.Name} az={az:0.00} alt={alt:0.00} dsop={dsop}, skipping.");
            return new SlewResult(SlewPostCondition.SkipToNext, double.NaN);
        }

        int failsafeCounter = 0;

        while (IsSlewing && failsafeCounter++ < MAX_FAILSAFE && !cancellationToken.IsCancellationRequested)
        {
            External.Sleep(TimeSpan.FromSeconds(1));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            External.LogWarning($"Cancellation requested, abort slewing to target {target.Name} and quit imaging loop.");
            return new SlewResult(SlewPostCondition.Cancelled, double.NaN);
        }

        if (IsSlewing || failsafeCounter >= MAX_FAILSAFE)
        {
            throw new InvalidOperationException($"Failsafe activated when slewing {Name} to {target.Name}.");
        }

        var actualSop = SideOfPier;
        if (actualSop != dsop)
        {
            External.LogError($"Slewing {Name} to {target.Name} completed but actual side of pier {actualSop} is different from the expected one {dsop}, skipping.");
            return new SlewResult(SlewPostCondition.SkipToNext, double.NaN);
        }

        double hourAngleAtSlewTime;
        if (double.IsNaN(hourAngleAtSlewTime = HourAngle))
        {
            External.LogError($"Could not obtain hour angle after slewing {Name} to {target.Name}, skipping.");
            return new SlewResult(SlewPostCondition.SkipToNext, double.NaN);
        }

        External.LogInfo($"Finished slewing mount {Name} to target {target.Name}.");

        return new SlewResult(SlewPostCondition.Success, hourAngleAtSlewTime);
    }
}
public enum SlewPostCondition
{
    Success = 0,
    SkipToNext = 1,
    Abort = 2,
    Cancelled = 3
}

public record struct SlewResult(SlewPostCondition PostCondition, double HourAngleAtSlewTime);