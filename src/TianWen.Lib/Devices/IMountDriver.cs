using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
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
    /// Determine the rates at which the telescope may be moved about the specified axis by the <see cref="MoveAxis(TelescopeAxis, double)"> method.
    /// </summary>
    /// <param name="axis"></param>
    /// <returns>axis rates in degrees per second</returns>
    IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis);

    /// <summary>
    /// Start or stop motion of the mount about the given mechanical axis at the given angular rate.
    /// </summary>
    /// <param name="axis">Which axis to move</param>
    /// <param name="rate">One of <see cref="AxisRates(TelescopeAxis)"/> or 0 to stop/></param>
    void MoveAxis(TelescopeAxis axis, double rate);

    TrackingSpeed TrackingSpeed { get; set; }

    IReadOnlyList<TrackingSpeed> TrackingSpeeds { get; }

    EquatorialCoordinateType EquatorialSystem { get; }

    AlignmentMode Alignment { get; }

    bool Tracking { get; set; }

    bool AtHome { get; }

    bool AtPark { get; }

    /// <summary>
    /// Async parking of the mount, will cause <see cref="IsSlewing"/> to be  <see langword="true"/>.
    /// </summary>
    void Park();

    /// <summary>
    /// Async unparking of the mount, will cause <see cref="IsSlewing"/> to be  <see langword="true"/>.
    /// Will throw an exception if <see cref="CanUnpark"/> is <see langword="false"/>.
    /// </summary>
    void Unpark();

    /// <summary>
    /// Moves the mount in the specified angular direction for the specified time (<paramref name="duration"/>).
    /// The directions are in the Equatorial coordinate system only, regardless of the mount’s <see cref="Alignment"/>. The distance moved depends on the <see cref="GuideRateDeclination"/> and <see cref="GuideRateRightAscension"/>,
    /// as well as <paramref name="duration"/>.
    /// </summary>
    /// <param name="direction">equatorial direction</param>
    /// <param name="duration">Duration (will be rounded to nearest millisecond internally)</param>
    void PulseGuide(GuideDirection direction, TimeSpan duration);

    /// <summary>
    /// True if slewing as a result of <see cref="BeginSlewRaDecAsync"/> or <see cref="BeginSlewHourAngleDecAsync"/>.
    /// </summary>
    bool IsSlewing { get; }

    /// <summary>
    /// True when a pulse guide command is still on-going (<see cref="PulseGuide(GuideDirection, TimeSpan)"/>
    /// </summary>
    bool IsPulseGuiding { get; }

    /// <summary>
    /// Read or set a secular rate of change to the mount's <see cref="RightAscension"/> (seconds of RA per sidereal second).
    /// https://ascom-standards.org/newdocs/trkoffset-faq.html#trkoffset-faq
    /// </summary>
    double RightAscensionRate { get; set; }

    /// <summary>
    /// Read or set a secular rate of change to the mount's <see cref="Declination"/> in arc seconds per UTC (SI) second.
    /// https://ascom-standards.org/newdocs/trkoffset-faq.html#trkoffset-faq
    /// </summary>
    double DeclinationRate { get; set; }

    /// <summary>
    /// The current rate of change of <see cref="RightAscension"/> (deg/sec) for guiding, typically via <see cref="PulseGuide(GuideDirection, TimeSpan)"/>.
    /// <list type="bullet">
    /// <item>This is the rate for both hardware/relay guiding and for <see cref="PulseGuide(GuideDirection, TimeSpan)"/></item>
    /// <item>The mount may not support separate right ascension and declination guide rates. If so, setting either rate must set the other to the same value.</item>
    /// <item>This value must be set to a default upon startup.</item>
    /// </list>
    /// </summary>
    double GuideRateRightAscension { get; set; }

    /// <summary>
    /// The current rate of change of <see cref="Declination"/> (deg/sec) for guiding, typically via <see cref="PulseGuide(GuideDirection, TimeSpan)"/>.
    /// <list type="bullet">
    /// <item>This is the rate for both hardware/relay guiding and for <see cref="PulseGuide(GuideDirection, TimeSpan)"/></item>
    /// <item>The mount may not support separate right ascension and declination guide rates. If so, setting either rate must set the other to the same value.</item>
    /// <item>This value must be set to a default upon startup.</item>
    /// </list>
    /// </summary>
    double GuideRateDeclination { get; set; }

    /// <summary>
    /// Stops all movement due to slew (might revert to previous tracking mode).
    /// </summary>
    void AbortSlew();

    /// <summary>
    /// Slews to given equatorial coordinates (RA, Dec) in the mounts native epoch, <see cref="EquatorialSystem"/>.
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    Task BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken = default);

    /// <summary>
    /// Slews to given equatorial coordinates (HA, Dec) in the mounts native epoch, <see cref="EquatorialSystem"/>.
    /// Uses current <see cref="SiderealTime"/> to convert to RA.
    /// Succeeds if <see cref="Connected"/> and <see cref="BeginSlewRaDecAsync(double, double)"/> succeeds.
    /// </summary>
    /// <param name="ha">HA in hours (-12..12), as returned by <see cref="HourAngle"/></param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    /// <returns>Completed task if slewing was started successfully</returns>
    Task BeginSlewHourAngleDecAsync(double ha, double dec, CancellationToken cancellationToken = default)
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

        return BeginSlewRaDecAsync(ConditionRA(SiderealTime - ha - 12), dec, cancellationToken);
    }

    /// <summary>
    /// Syncs to given equatorial coordinates (RA, Dec) in the mounts native epoch, <see cref="EquatorialSystem"/>.
    /// Can still throw exceptions when underlying implementation prohibits syncing.
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    void SyncRaDec(double ra, double dec);

    /// <summary>
    /// Calls <see cref="SyncRaDec(double, double)"/> by first transforming J2000 coordinates to native ones using <see cref="TryTransformJ2000ToMountNative"/>.
    /// </summary>
    /// <param name="ra">RA in hours (0..24)</param>
    /// <param name="dec">Declination in degrees (-90..90)</param>
    void SyncRaDecJ2000(double ra, double dec)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Device is not connected");
        }

        if (!CanSync)
        {
            throw new InvalidOperationException("Device does not support syncing");
        }

        if (!TryGetTransform(out var transform))
        {
            throw new InvalidOperationException("Failed intialize coordinate transform function");
        }

        if (TryTransformJ2000ToMountNative(transform, ra, dec, updateTime: false, out var raMount, out var decMount, out _, out _))
        {
            SyncRaDec(raMount, decMount);
        }
        else
        {
            throw new InvalidOperationException($"Failed to transform {HoursToHMS(ra)}, {DegreesToDMS(dec)} to device native coordinate system");
        }
    }

    /// <summary>
    /// The UTC date/time of the telescope's internal clock.
    /// Must be initalised from system time if no internal clock is supported.
    /// <see cref="IExternal.TimeProvider"/>.
    /// </summary>
    DateTime? UTCDate { get; set; }

    /// <summary>
    /// Returns true iff <see cref="UTCDate"/> was updated succcessfully when setting.
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

    /// <summary>
    /// Side of pier as an indicator of pointing state/meridian flip indicator.
    /// </summary>
    PointingState SideOfPier { get; set; }

    /// <summary>
    /// Predict side of pier for German equatorial mounts.
    /// </summary>
    /// <param name="ra">The destination right ascension(hours)</param>
    /// <param name="dec">The destination declination (degrees, positive North)</param>
    /// <returns></returns>
    PointingState DestinationSideOfPier(double ra, double dec);

    /// <summary>
    /// Uses <see cref="DestinationSideOfPier"/> and equatorial coordinates as of now (<see cref="RightAscension"/>, <see cref="Declination"/>)
    /// To calculate the <see cref="SideOfPier"/> that the telescope should be on if one where to slew there now.
    /// </summary>
    PointingState ExpectedSideOfPier => Connected ? DestinationSideOfPier(RightAscension, Declination) : PointingState.Unknown;

    /// <summary>
    /// The current hour angle, using <see cref="RightAscension"/> and <see cref="SiderealTime"/>, (-12,12).
    /// </summary>
    double HourAngle => Connected ? ConditionHA(SiderealTime - RightAscension) : double.NaN;

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
    bool TryGetTransform([NotNullWhen(true)] out Transform? transform)
    {
        if (Connected && TryGetUTCDate(out var utc))
        {
            transform = new Transform(External.TimeProvider)
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
        var pierSide = External.Catch(() => SideOfPier, PointingState.Unknown);
        var currentHourAngle = External.Catch(() => HourAngle, double.NaN);
        return pierSide == ExpectedSideOfPier
            && !double.IsNaN(currentHourAngle)
            && (pierSide != PointingState.Unknown || Math.Sign(hourAngleAtSlewTime) == Math.Sign(currentHourAngle));
    }

    public Task BeginSlewToZenithAsync(TimeSpan distMeridian, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Device is not connected");
        }

        if (!CanSlewAsync)
        {
            throw new InvalidOperationException("Device does not support slewing");
        }

        return BeginSlewHourAngleDecAsync((TimeSpan.FromHours(12) - distMeridian).TotalHours, SiteLatitude, cancellationToken);
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
        if (!TryGetTransform(out var transform)
            || !TryTransformJ2000ToMountNative(transform, target.RA, target.Dec, updateTime: false, out var raMount, out var decMount, out az, out alt)
            || double.IsNaN(alt)
            || alt < minAboveHorizonDegrees
            || (dsop = DestinationSideOfPier(raMount, decMount)) == PointingState.Unknown
        )
        {
            External.AppLogger.LogError("Failed to slew {MountName} to target {TargetName} az={AZ:0.00} alt={Alt:0.00} dsop={DestinationSoP}, skipping.",
                Name, target.Name, az, alt, dsop);
            return new SlewResult(SlewPostCondition.SkipToNext, double.NaN);
        }

        var hourAngle = HourAngle;
        await BeginSlewRaDecAsync(raMount, decMount, cancellationToken);

        return new SlewResult(SlewPostCondition.Slewing, hourAngle);
    }

    public async ValueTask<bool> WaitForSlewCompleteAsync(CancellationToken cancellationToken)
    {
        var period = TimeSpan.FromMilliseconds(250);
        var maxSlewTime = TimeSpan.FromSeconds(MAX_FAILSAFE);

        if (!TryGetUTCDate(out var slewStartTime))
        {
            return false;
        }

        while (!cancellationToken.IsCancellationRequested
            && IsSlewing
            && TryGetUTCDate(out var now)
            && now - slewStartTime < maxSlewTime
        )
        {
            await External.SleepAsync(period, cancellationToken);
        }

        var isStillSlewing = IsSlewing;
        if (isStillSlewing && cancellationToken.IsCancellationRequested)
        {
            AbortSlew();

            return false;
        }

        return !isStillSlewing;
    }

    public bool EnsureTracking(TrackingSpeed speed = TrackingSpeed.Sidereal)
    {
        if (!Connected)
        {
            return false;
        }

        if (CanSetTracking && (TrackingSpeed != speed || !Tracking))
        {
            TrackingSpeed = speed;
            Tracking = true;
        }

        return Tracking;
    }
}

public enum SlewPostCondition
{
    SkipToNext = 1,
    Slewing = 2
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