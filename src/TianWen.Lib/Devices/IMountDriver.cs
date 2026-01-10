using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Devices;

public interface IMountDriver : IDeviceDriver
{
    bool CanSetTracking { get; }

    bool CanSetSideOfPier { get; }

    bool CanSetRightAscensionRate { get; }

    bool CanSetDeclinationRate { get; }

    bool CanSetGuideRates { get; }

    bool CanPulseGuide { get; }

    bool CanPark { get; }

    bool CanSetPark { get; }

    bool CanUnpark { get; }

    bool CanSlew { get; }

    bool CanSlewAsync { get; }

    bool CanSync { get; }

    bool CanMoveAxis(TelescopeAxis axis);

    /// <summary>
    /// Determine the rates at which the telescope may be moved about the specified axis by the <see cref="MoveAxisAsync(TelescopeAxis, double, CancellationToken)"> method.
    /// </summary>
    /// <param name="axis"></param>
    /// <returns>axis rates in degrees per second</returns>
    IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis);

    /// <summary>
    /// Start or stop motion of the mount about the given mechanical axis at the given angular rate.
    /// </summary>
    /// <param name="axis">Which axis to move</param>
    /// <param name="rate">One of <see cref="AxisRates(TelescopeAxis)"/> or 0 to stop/></param>
    ValueTask MoveAxisAsync(TelescopeAxis axis, double rate, CancellationToken cancellationToken);

    ValueTask<TrackingSpeed> GetTrackingSpeedAsync(CancellationToken cancellationToken);

    ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken);

    IReadOnlyList<TrackingSpeed> TrackingSpeeds { get; }

    EquatorialCoordinateType EquatorialSystem { get; }

    ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken);

    ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken);

    ValueTask SetTrackingAsync(bool tracking, CancellationToken cancellationToken);

    ValueTask<bool> AtHomeAsync(CancellationToken cancellationToken);

    ValueTask<bool> AtParkAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Async parking of the mount, will cause <see cref="IsSlewingAsync(CancellationToken)"/> to be  <see langword="true"/>.
    /// </summary>
    ValueTask ParkAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Async unparking of the mount, will cause <see cref="IsSlewingAsync(CancellationToken)"/> to be  <see langword="true"/>.
    /// Will throw an exception if <see cref="CanUnpark"/> is <see langword="false"/>.
    /// </summary>
    ValueTask UnparkAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Moves the mount in the specified angular direction for the specified time (<paramref name="duration"/>).
    /// The directions are in the Equatorial coordinate system only, regardless of the mount’s <see cref="GetAlignmentAsync(CancellationToken)"/>. The distance moved depends on the <see cref="GuideRateDeclination"/> and <see cref="GuideRateRightAscension"/>,
    /// as well as <paramref name="duration"/>.
    /// </summary>
    /// <param name="direction">equatorial direction</param>
    /// <param name="duration">Duration (will be rounded to nearest millisecond internally)</param>
    ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    /// True if slewing as a result of <see cref="BeginSlewRaDecAsync(double, double, CancellationToken)"/> or <see cref="BeginSlewHourAngleDecAsync(double, double, CancellationToken)"/>.
    /// </summary>
    ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// True when a pulse guide command is still on-going (<see cref="PulseGuideAsync(GuideDirection, TimeSpan, CancellationToken)"/>
    /// </summary>
    ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Read or set a secular rate of change to the mount's <see cref="GetRightAscensionAsync(CancellationToken)"/> (seconds of RA per sidereal second).
    /// https://ascom-standards.org/newdocs/trkoffset-faq.html#trkoffset-faq
    /// </summary>
    double RightAscensionRate { get; set; }

    /// <summary>
    /// Read or set a secular rate of change to the mount's <see cref="GetDeclinationAsync(CancellationToken)"/> in arc seconds per UTC (SI) second.
    /// https://ascom-standards.org/newdocs/trkoffset-faq.html#trkoffset-faq
    /// </summary>
    double DeclinationRate { get; set; }

    /// <summary>
    /// The current rate of change of <see cref="GetRightAscensionAsync(CancellationToken)"/> (deg/sec) for guiding, typically via <see cref="PulseGuideAsync(GuideDirection, TimeSpan, CancellationToken)"/>.
    /// <list type="bullet">
    /// <item>This is the rate for both hardware/relay guiding and for <see cref="PulseGuideAsync(GuideDirection, TimeSpan, CancellationToken)"/></item>
    /// <item>The mount may not support separate right ascension and declination guide rates. If so, setting either rate must set the other to the same value.</item>
    /// <item>This value must be set to a default upon startup.</item>
    /// </list>
    /// </summary>
    double GuideRateRightAscension { get; set; }

    /// <summary>
    /// The current rate of change of <see cref="GetDeclinationAsync(CancellationToken)"/> (deg/sec) for guiding, typically via <see cref="PulseGuideAsync(GuideDirection, TimeSpan, CancellationToken)"/>.
    /// <list type="bullet">
    /// <item>This is the rate for both hardware/relay guiding and for <see cref="PulseGuideAsync(GuideDirection, TimeSpan, CancellationToken)"/></item>
    /// <item>The mount may not support separate right ascension and declination guide rates. If so, setting either rate must set the other to the same value.</item>
    /// <item>This value must be set to a default upon startup.</item>
    /// </list>
    /// </summary>
    double GuideRateDeclination { get; set; }

    /// <summary>
    /// Stops all movement due to slew (might revert to previous tracking mode).
    /// </summary>
    ValueTask AbortSlewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Slews to given equatorial coordinates (RA, Dec) in the mounts native epoch, <see cref="EquatorialSystem"/>.
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken);

    /// <summary>
    /// Slews to given equatorial coordinates (HA, Dec) in the mounts native epoch, <see cref="EquatorialSystem"/>.
    /// Uses current <see cref="GetSiderealTimeAsync(CancellationToken)"/> to convert to RA.
    /// Succeeds if <see cref="Connected"/> and <see cref="BeginSlewRaDecAsync(double, double, CancellationToken)"/> succeeds.
    /// </summary>
    /// <param name="ha">HA in hours (-12..12), as returned by <see cref="GetHourAngleAsync(CancellationToken)"/></param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>Completed task if slewing was started successfully</returns>
    async ValueTask BeginSlewHourAngleDecAsync(double ha, double dec, CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Device is not connected");
        }

        if (double.IsNaN(ha) || ha is < -12 or > 12)
        {
            throw new ArgumentException("Hour angle must be in [-12..12]", nameof(ha));
        }

        if (double.IsNaN(dec) || dec is < -90 or > 90)
        {
            throw new ArgumentException("Declination must be in [-90..90]", nameof(dec));
        }

        await BeginSlewRaDecAsync(ConditionRA(await GetSiderealTimeAsync(cancellationToken) - ha - 12), dec, cancellationToken);
    }

    /// <summary>
    /// Syncs to given equatorial coordinates (RA, Dec) in the mounts native epoch, <see cref="EquatorialSystem"/>.
    /// Can still throw exceptions when underlying implementation prohibits syncing.
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken);

    /// <summary>
    /// Calls <see cref="SyncRaDecAsync(double, double, CancellationToken)"/> by first transforming J2000 coordinates to native ones using <see cref="TryTransformJ2000ToMountNativeAsync(Transform, double, double, bool, CancellationToken)"/>.
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    async ValueTask SyncRaDecJ2000Async(double ra, double dec, CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Device is not connected");
        }

        if (!CanSync)
        {
            throw new InvalidOperationException("Device does not support syncing");
        }

        if (await TryGetTransformAsync(cancellationToken) is not { } transform)
        {
            throw new InvalidOperationException("Failed intialize coordinate transform function");
        }

        if (await TryTransformJ2000ToMountNativeAsync(transform, ra, dec, updateTime: false, cancellationToken) is { } nativeCoords)
        {
            await SyncRaDecAsync(nativeCoords.RaMount, nativeCoords.DecMount, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException($"Failed to transform {HoursToHMS(ra)}, {DegreesToDMS(dec)} to device native coordinate system");
        }
    }

    /// <summary>
    /// The UTC date/time of the telescope's internal clock.
    /// Must be initalised via <see cref="SetUTCDateAsync(DateTime, CancellationToken)"/> from system time if no internal clock is supported.
    /// <see cref="IExternal.TimeProvider"/>.
    /// </summary>
    ValueTask<DateTime?> TryGetUTCDateFromMountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The UTC date/time of the telescope's internal clock.
    /// Must be initalised from system time if no internal clock is supported.
    /// <see cref="IExternal.TimeProvider"/>.
    /// </summary>
    ValueTask SetUTCDateAsync(DateTime dateTime, CancellationToken cancellationToken);

    /// <summary>
    /// Returns true iff time was updated via <see cref="SetUTCDateAsync(DateTime, CancellationToken)"/>.
    /// Will be equal to <see cref="IExternal.TimeProvider"/> if true.
    /// </summary>
    bool TimeIsSetByUs { get; }

    /// <summary>
    /// Get side of pier as an indicator of pointing state/meridian flip indicator.
    /// </summary>
    ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken);


    /// <summary>
    /// Force a flip of the mount, if <see cref="CanSetSideOfPier"></see> is supported.
    /// </summary>
    ValueTask SetSideOfPierAsync(PointingState pointingState, CancellationToken cancellationToken);

    /// <summary>
    /// Predict side of pier for German equatorial mounts.
    /// </summary>
    /// <param name="ra">The destination right ascension(hours)</param>
    /// <param name="dec">The destination declination (degrees, positive North)</param>
    /// <returns></returns>
    ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken);

    /// <summary>
    /// Uses <see cref="DestinationSideOfPierAsync(double, double, CancellationToken)"/> and equatorial coordinates as of now (<see cref="GetRightAscensionAsync(CancellationToken)"/>, <see cref="GetDeclinationAsync(CancellationToken)"/>)
    /// To calculate the <see cref="GetSideOfPierAsync(CancellationToken)"/> that the telescope should be on if one where to slew there now.
    /// </summary>
    async ValueTask<PointingState> GetExpectedSideOfPierAsync(CancellationToken cancellationToken)
    {
        if (Connected)
        {
            return await DestinationSideOfPierAsync(await GetRightAscensionAsync(cancellationToken), await GetDeclinationAsync(cancellationToken), cancellationToken);
        }
        else
        {
            return PointingState.Unknown;
        }
    }

    /// <summary>
    /// The current hour angle, using <see cref="GetRightAscensionAsync(CancellationToken)"/> and <see cref="GetSiderealTimeAsync(CancellationToken)"/>, (-12,12).
    /// </summary>
    ValueTask<double> GetHourAngleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The local apparent sidereal time from the telescope's internal clock (hours, sidereal).
    /// </summary>
    ValueTask<double> GetSiderealTimeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the right ascension (hours) of the telescope's current equatorial coordinates, in the coordinate system given by the <see cref="EquatorialSystem"/> property.
    /// </summary>
    ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The declination (degrees) of the telescope's current equatorial coordinates, in the coordinate system given by the <see cref="EquatorialSystem"/> property.
    /// </summary>
    ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the right ascension (hours) of the telescope's intended right ascension, in the coordinate system given by the <see cref="EquatorialSystem"/> property.
    /// </summary>
    ValueTask<double> GetTargetRightAscensionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets declination (degrees) of the telescope's intended declination, in the coordinate system given by the <see cref="EquatorialSystem"/> property.
    /// </summary>
    ValueTask<double> GetTargetDeclinationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The elevation above mean sea level (meters) of the site at which the telescope is located.
    /// </summary>
    ValueTask<double> GetSiteElevationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Set elevation 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask SetSiteElevationAsync(double elevation, CancellationToken cancellationToken);

    /// <summary>
    /// The geodetic(map) latitude (degrees, positive North, WGS84) of the site at which the telescope is located.
    /// </summary>
    ValueTask<double> GetSiteLatitudeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets the latitude for the site.
    /// </summary>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns></returns>
    ValueTask SetSiteLatitudeAsync(double latitude, CancellationToken cancellationToken);

    /// <summary>
    /// The longitude (degrees, positive East, WGS84) of the site at which the telescope is located.
    /// </summary>
    ValueTask<double> GetSiteLongitudeAsync(CancellationToken cancellationToken);
    
    ValueTask SetSiteLongitudeAsync(double longitude, CancellationToken cancellationToken);

    /// <summary>
    /// Initialises a <see cref="Transform"/> using standard pressure and atmosphere. Please adjust if available.
    /// </summary>
    /// <returns>Initialized transform or null if not connected/date time could not be established.</returns>
    async ValueTask<Transform?> TryGetTransformAsync(CancellationToken cancellationToken)
    {
        if (Connected && await TryGetUTCDateFromMountAsync(cancellationToken) is { } utc)
        {
            return new Transform(External.TimeProvider)
            {
                SiteElevation = await GetSiteElevationAsync(cancellationToken),
                SiteLatitude = await GetSiteLatitudeAsync(cancellationToken),
                SiteLongitude = await GetSiteLongitudeAsync(cancellationToken),
                SitePressure = 1010, // TODO standard atmosphere
                SiteTemperature = 10, // TODO check either online or if compatible devices connected
                DateTime = utc,
                Refraction = true // TODO assumes that driver does not support/do refraction
            };
        }

        return null;
    }

    /// <summary>
    /// Not reentrant if using a shared <paramref name="transform"/>.
    /// </summary>
    /// <param name="transform"></param>
    /// <param name="raMount"></param>
    /// <param name="decMount"></param>
    /// <returns>transformed coordinates on success</returns>
    async ValueTask<(double RaMount, double DecMount, double Az, double Alt)?> TryTransformJ2000ToMountNativeAsync(Transform transform, double raJ2000, double decJ2000, bool updateTime, CancellationToken cancellationToken)
    {
        if (Connected && updateTime && await TryGetUTCDateFromMountAsync(cancellationToken) is { } utc)
        {
            transform.DateTime = utc;
        }
        else if (updateTime || !Connected)
        {
            return null;
        }

        transform.SetJ2000(raJ2000, decJ2000);
        transform.Refresh();

        var (raMount, decMount) = EquatorialSystem switch
        {
            EquatorialCoordinateType.J2000 => (transform.RAJ2000, transform.DecJ2000),
            EquatorialCoordinateType.Topocentric => (transform.RAApparent, transform.DECApparent),
            _ => (double.NaN, double.NaN)
        };
        var az = transform.AzimuthTopocentric;
        var alt = transform.ElevationTopocentric;

        if (!double.IsNaN(raMount) && !double.IsNaN(decMount) && !double.IsNaN(az) && !double.IsNaN(alt))
        {
            return (raMount, decMount, az, alt);
        }
        else
        {
            return null;
        }
    }

    public async Task<bool> IsOnSamePierSideAsync(double hourAngleAtSlewTime, CancellationToken cancellationToken)
    {
        var pierSide = await External.CatchAsync(GetSideOfPierAsync, cancellationToken, PointingState.Unknown);
        var currentHourAngle = await External.CatchAsync(GetHourAngleAsync, cancellationToken, double.NaN);
        return pierSide == await External.CatchAsync(GetExpectedSideOfPierAsync, cancellationToken, PointingState.Unknown)
            && !double.IsNaN(currentHourAngle)
            && (pierSide != PointingState.Unknown || Math.Sign(hourAngleAtSlewTime) == Math.Sign(currentHourAngle));
    }

    public async ValueTask BeginSlewToZenithAsync(TimeSpan distMeridian, CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Device is not connected");
        }

        if (!CanSlewAsync)
        {
            throw new InvalidOperationException("Device does not support slewing");
        }

        await BeginSlewHourAngleDecAsync((TimeSpan.FromHours(12) - distMeridian).TotalHours, await GetSiteLatitudeAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Begins slewing to the specified target asynchronously.
    /// </summary>
    /// <param name="target">The target to slew to, containing RA and Dec coordinates.</param>
    /// <param name="minAboveHorizonDegrees">The minimum altitude above the horizon in degrees. Default is 10 degrees.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="SlewResult"/> indicating the post-condition and hour angle at slew time.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the device is not connected or if the target cannot be transformed to mount native coordinates.</exception>
    public async Task<SlewResult> BeginSlewToTargetAsync(Target target, int minAboveHorizonDegrees = 10, CancellationToken cancellationToken = default)
    {
        var az = double.NaN;
        var alt = double.NaN;
        var dsop = PointingState.Unknown;
        if (await TryGetTransformAsync(cancellationToken) is not { } transform
            || await TryTransformJ2000ToMountNativeAsync(transform, target.RA, target.Dec, updateTime: false, cancellationToken) is not { } nativeCoords
            || nativeCoords.Alt < minAboveHorizonDegrees
            || (dsop = await DestinationSideOfPierAsync(nativeCoords.RaMount, nativeCoords.DecMount, cancellationToken)) == PointingState.Unknown
        )
        {

            if (!double.IsNaN(alt) && alt < minAboveHorizonDegrees)
            {
                External.AppLogger.LogWarning("Target {TargetName} is below minimum altitude of {MinAlt} degrees.", target.Name, minAboveHorizonDegrees);
            
                return new SlewResult(SlewPostCondition.TargetBelowHorizonLimit, double.NaN);
            }
            else
            {
                External.AppLogger.LogError("Failed to slew {MountName} to target {TargetName} az={AZ:0.00} alt={Alt:0.00} dsop={DestinationSoP}, skipping.",
                    Name, target.Name, az, alt, dsop);

                return new SlewResult(SlewPostCondition.SlewNotPossible, double.NaN);
            }
        }

        var hourAngle = await GetHourAngleAsync(cancellationToken);
        await BeginSlewRaDecAsync(nativeCoords.RaMount, nativeCoords.DecMount, cancellationToken);

        return new SlewResult(SlewPostCondition.Slewing, hourAngle);
    }

    public async ValueTask<bool> WaitForSlewCompleteAsync(CancellationToken cancellationToken)
    {
        var period = TimeSpan.FromMilliseconds(251);
        var maxSlewTime = TimeSpan.FromSeconds(MAX_FAILSAFE);

        if (await TryGetUTCDateFromMountAsync(cancellationToken) is not { } slewStartTime)
        {
            return false;
        }

        while (!cancellationToken.IsCancellationRequested
            && await IsSlewingAsync(cancellationToken)
            && await TryGetUTCDateFromMountAsync(cancellationToken) is { } now
            && now - slewStartTime < maxSlewTime
        )
        {
            await External.SleepAsync(period, cancellationToken);
        }

        var isStillSlewing = await IsSlewingAsync(cancellationToken);
        if (isStillSlewing && cancellationToken.IsCancellationRequested)
        {
            await AbortSlewAsync(cancellationToken);

            return false;
        }

        return !isStillSlewing;
    }

    public async ValueTask<bool> EnsureTrackingAsync(TrackingSpeed speed = TrackingSpeed.Sidereal, CancellationToken cancellationToken = default)
    {
        if (speed is TrackingSpeed.None)
        {
            throw new ArgumentException("Tracking speed cannot be None", nameof(speed));
        }

        if (!Connected)
        {
            return false;
        }

        if (CanSetTracking && ((await GetTrackingSpeedAsync(cancellationToken)) != speed || !await IsTrackingAsync(cancellationToken)))
        {
            await SetTrackingSpeedAsync(speed, cancellationToken);
            await SetTrackingAsync(true, cancellationToken);
        }

        return await IsTrackingAsync(cancellationToken);
    }
}

public enum SlewPostCondition
{
    Slewing = 1,
    SlewNotPossible = 2,
    TargetBelowHorizonLimit = 3
}

public record struct SlewResult(SlewPostCondition PostCondition, double HourAngleAtSlewTime);

public record struct AxisRate(double Mininum, double Maximum)
{
    public AxisRate(double Rate) : this(Rate, Rate)
    {
        // empty
    }

    public static implicit operator AxisRate(double rate) => new AxisRate(rate);
}

public enum AlignmentMode
{
    /// <summary>
    /// Altitude-Azimuth alignment.
    /// </summary>
    AltAz = 0,

    /// <summary>
    /// Polar (equatorial) mount other than German equatorial.
    /// </summary>
    Polar = 1,

    /// <summary>
    /// German equatorial mount.
    /// </summary>
    GermanPolar = 2
}

public enum TrackingSpeed
{
    None = 0,
    Sidereal = 1,
    Lunar = 2,
    Solar = 3,
    King = 4,
}

public enum TelescopeAxis
{
    Primary = 0,
    Seconary = 1,
    Tertiary = 2
}