using System;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;

namespace TianWen.Lib.Astrometry;

/// <summary>
/// A planet as the Earth sees it at an instant (docs/plans/planetary-restoration.md, R1): which face is turned to us, how
/// its axis lies on the sky, and how it is lit. Angles in degrees; longitudes are WEST (the central meridian grows with
/// time, as observers count it); latitudes are planetographic unless named otherwise.
/// </summary>
/// <param name="CentralMeridianIII">The sub-observer longitude in the planet's System III (its magnetic field's rotation, the
/// IAU prime meridian): Jupiter's and Saturn's.</param>
/// <param name="CentralMeridianI">Jupiter's System I (its equatorial zone's rotation); NaN for another planet.</param>
/// <param name="CentralMeridianII">Jupiter's System II (the rest of its disk's rotation); NaN for another planet.</param>
/// <param name="SubObserverLatitude">Planetographic: the angle between the local vertical and the equator, which is what a
/// map of the planet is drawn in.</param>
/// <param name="SubObserverLatitudeCentric">Planetocentric: the angle from the planet's centre (Meeus's <c>DE</c>).</param>
/// <param name="PolePositionAngle">The north pole's position angle on the sky, east of celestial north (ICRF).</param>
/// <param name="PhaseAngle">The angle Sun-planet-Earth.</param>
/// <param name="AngularDiameterArcsec">The equatorial diameter the Earth sees.</param>
/// <param name="LightTimeSeconds">How long ago the light left the planet: every angle above is the planet as it was then.</param>
public readonly record struct PlanetAspect(
    CatalogIndex Planet,
    DateTimeOffset Utc,
    double CentralMeridianIII,
    double CentralMeridianI,
    double CentralMeridianII,
    double SubObserverLatitude,
    double SubObserverLatitudeCentric,
    double SubSolarLongitudeIII,
    double SubSolarLatitude,
    double PolePositionAngle,
    double PhaseAngle,
    double AngularDiameterArcsec,
    double DistanceAu,
    double LightTimeSeconds,
    double Flattening);

/// <summary>
/// The physical ephemeris of Jupiter and Saturn from first principles: the IAU WGCCRE 2015 rotation models (Archinal et al.
/// 2018: the pole and prime meridian in the ICRF, System III) and VSOP87 positions, light-time corrected. Jupiter's Systems
/// I and II are their defined rates, 877.900 and 870.270 degrees a day, from their IAU epochs.
/// </summary>
/// <remarks>
/// The orientation is never measured from an image, which supplies only the disk's centre and scale (#815, #1049). Checked
/// against JPL Horizons at the corpus's own session times and against Meeus's worked example (Astronomical Algorithms,
/// example 43.a) in <c>PhysicalEphemerisTests</c>.
/// </remarks>
public static class PhysicalEphemeris
{
    private const double DaysPerCentury = 36525.0;
    private const double SecondsPerDay = 86400.0;

    /// <summary>Whether <see cref="Compute"/> has a rotation model for <paramref name="planet"/>.</summary>
    public static bool Supports(CatalogIndex planet) => planet is CatalogIndex.Jupiter or CatalogIndex.Saturn;

    /// <summary>The planet as the geocentre sees it at <paramref name="utc"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A planet with no rotation model here (<see cref="Supports"/>).</exception>
    public static PlanetAspect Compute(CatalogIndex planet, DateTimeOffset utc)
    {
        if (!Supports(planet))
        {
            throw new ArgumentOutOfRangeException(nameof(planet), planet, "Only Jupiter and Saturn have a rotation model here");
        }
        var (equatorialKm, polarKm) = Radii(planet);

        utc.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        // TT for TDB: they differ by under two milliseconds, a hundred-thousandth of a degree of Jupiter's rotation.
        var jdTdb = tt1 + tt2;

        Span<double> earth = stackalloc double[3];
        Span<double> body = stackalloc double[3];
        Position(CatalogIndex.Earth, jdTdb, earth);

        // Light time, iterated: the planet where it was when the light we see left it, the Earth where it is now.
        var lightTimeDays = 0.0;
        Span<double> geocentric = stackalloc double[3];
        for (var i = 0; i < 3; i++)
        {
            Position(planet, jdTdb - lightTimeDays, body);
            for (var k = 0; k < 3; k++)
            {
                geocentric[k] = body[k] - earth[k];
            }
            lightTimeDays = Norm(geocentric) / Constants.C;
        }
        var distanceAu = Norm(geocentric);
        var emitted = jdTdb - lightTimeDays;

        // The body frame at the emission time: its pole, the ascending node of its equator on the ICRF equator, and its
        // prime meridian W degrees along the equator from that node.
        var (poleRa, poleDec, w) = Rotation(planet, emitted);
        var pole = Unit(poleRa, poleDec);
        Span<double> node = [-Math.Sin(poleRa), Math.Cos(poleRa), 0];
        Span<double> nodeNormal = stackalloc double[3];
        Cross(pole, node, nodeNormal);

        // Planet to observer, and planet to Sun (the Sun is the frame's origin).
        Span<double> toObserver = [-geocentric[0] / distanceAu, -geocentric[1] / distanceAu, -geocentric[2] / distanceAu];
        var heliocentric = Norm(body);
        Span<double> toSun = [-body[0] / heliocentric, -body[1] / heliocentric, -body[2] / heliocentric];

        var flattening = 1 - (polarKm / equatorialKm);
        var (observerLongitude, observerCentric) = SubPoint(toObserver, pole, node, nodeNormal, w);
        var (sunLongitude, sunCentric) = SubPoint(toSun, pole, node, nodeNormal, w);

        double centralI = double.NaN, centralII = double.NaN;
        if (planet == CatalogIndex.Jupiter)
        {
            var days = emitted - Constants.J2000BASE;
            centralI = Wrap(observerLongitude - w + (67.1 + (877.900 * days)));
            centralII = Wrap(observerLongitude - w + (43.3 + (870.270 * days)));
        }

        return new PlanetAspect(
            planet,
            utc,
            CentralMeridianIII: observerLongitude,
            CentralMeridianI: centralI,
            CentralMeridianII: centralII,
            SubObserverLatitude: Graphic(observerCentric, flattening),
            SubObserverLatitudeCentric: observerCentric,
            SubSolarLongitudeIII: sunLongitude,
            SubSolarLatitude: Graphic(sunCentric, flattening),
            PolePositionAngle: PoleOnTheSkyOfDate(geocentric, pole, tt1, tt2),
            PhaseAngle: Math.Acos(Math.Clamp(Dot(toObserver, toSun), -1, 1)) * Constants.RADIANS2DEGREES,
            AngularDiameterArcsec: 2 * Math.Atan(equatorialKm / (distanceAu * Constants.KMAU)) * Constants.RADIANS2DEGREES * 3600,
            DistanceAu: distanceAu,
            LightTimeSeconds: lightTimeDays * SecondsPerDay,
            Flattening: flattening);
    }

    /// <summary>
    /// The direction to the Sun in the disk frame of <paramref name="aspect"/>, a unit vector: its component toward the sky's
    /// west, toward the projected north pole, and toward the observer (whose angle from the last is the phase angle). The lit
    /// side lies along (West, North) on the disk, off the equator by <c>atan2(North, |West|)</c> whenever the Sun and the
    /// observer stand at different latitudes of the planet: 5.2 degrees on 2022-09-03, which a limb fit lighting the disk
    /// along its equator read as a centre 0.14 px off along the axis (#1050).
    /// </summary>
    public static (double West, double North, double Toward) SunOnTheDisk(in PlanetAspect aspect)
    {
        var q = 1 - aspect.Flattening;
        var (sinD, cosD) = Math.SinCos(aspect.SubObserverLatitudeCentric * Constants.DEGREES2RADIANS);
        // The sub-solar latitude is stated planetographic; the direction to the Sun has the planetocentric one.
        var sunCentric = Math.Atan(q * q * Math.Tan(aspect.SubSolarLatitude * Constants.DEGREES2RADIANS));
        var offset = (aspect.CentralMeridianIII - aspect.SubSolarLongitudeIII) * Constants.DEGREES2RADIANS;
        // In the body frame: e1 along the sky's west (it lies in the equator, the pole being in the north-toward plane), e2 the
        // equator's direction nearest the observer, and the pole; a point west of the central meridian has the smaller west
        // longitude. e2 = (0, -sin D, cos D) and the pole (0, cos D, sin D) in (west, north, toward).
        var (s1, s2, s3) = (Math.Cos(sunCentric) * Math.Sin(offset), Math.Cos(sunCentric) * Math.Cos(offset), Math.Sin(sunCentric));
        return (s1, (-sinD * s2) + (cosD * s3), (cosD * s2) + (sinD * s3));
    }

    // IAU equatorial and polar radii, km (Archinal et al. 2018, table 5).
    internal static (double Equatorial, double Polar) Radii(CatalogIndex planet) => planet switch
    {
        CatalogIndex.Jupiter => (71492, 66854),
        CatalogIndex.Saturn => (60268, 54364),
        _ => throw new ArgumentOutOfRangeException(nameof(planet), planet, null),
    };

    // The IAU WGCCRE 2015 rotation model at TDB Julian date `jd`: the pole (radians) and the prime meridian W (degrees).
    private static (double PoleRa, double PoleDec, double W) Rotation(CatalogIndex planet, double jd)
    {
        var d = jd - Constants.J2000BASE;
        var t = d / DaysPerCentury;
        switch (planet)
        {
            case CatalogIndex.Jupiter:
                // The Galilean satellites' nutation terms, Ja to Je, a few thousandths of a degree.
                var satA = SatelliteArgument(99.360714, 4850.4046, t);
                var satB = SatelliteArgument(175.895369, 1191.9605, t);
                var satC = SatelliteArgument(300.323162, 262.5475, t);
                var satD = SatelliteArgument(114.012305, 6070.2476, t);
                var satE = SatelliteArgument(49.511251, 64.3000, t);
                var ra = 268.056595 - (0.006499 * t) + (0.000117 * Math.Sin(satA)) + (0.000938 * Math.Sin(satB)) + (0.001432 * Math.Sin(satC))
                    + (0.000030 * Math.Sin(satD)) + (0.002150 * Math.Sin(satE));
                var dec = 64.495303 + (0.002413 * t) + (0.000050 * Math.Cos(satA)) + (0.000404 * Math.Cos(satB)) + (0.000617 * Math.Cos(satC))
                    - (0.000013 * Math.Cos(satD)) + (0.000926 * Math.Cos(satE));
                return (ra * Constants.DEGREES2RADIANS, dec * Constants.DEGREES2RADIANS, Wrap(284.95 + (870.5360000 * d)));
            case CatalogIndex.Saturn:
                return ((40.589 - (0.036 * t)) * Constants.DEGREES2RADIANS, (83.537 - (0.004 * t)) * Constants.DEGREES2RADIANS,
                    Wrap(38.90 + (810.7939024 * d)));
            default:
                throw new ArgumentOutOfRangeException(nameof(planet), planet, null);
        }
    }

    private static double SatelliteArgument(double atEpoch, double perCentury, double centuries)
        => (atEpoch + (perCentury * centuries)) * Constants.DEGREES2RADIANS;

    // Heliocentric, J2000 equatorial, AU, at TDB Julian date `jd`.
    private static void Position(CatalogIndex body, double jd, Span<double> position)
    {
        if (!VSOP87a.GetBody(body, (jd - Constants.J2000BASE) / 365250.0, position))
        {
            throw new InvalidOperationException($"VSOP87 has no position for {body}");
        }
        VSOP87a.Rotvsop2J2000(position);
    }

    // The longitude (west, degrees, in the frame whose prime meridian is W) and planetocentric latitude of the point on the
    // planet under `direction`, a unit vector from the planet's centre.
    private static (double WestLongitude, double CentricLatitude) SubPoint(ReadOnlySpan<double> direction, ReadOnlySpan<double> pole,
        ReadOnlySpan<double> node, ReadOnlySpan<double> nodeNormal, double w)
    {
        var alongNode = Dot(direction, node);
        var alongNormal = Dot(direction, nodeNormal);
        // The angle east of the node, in the equator; the prime meridian lies W east of the node, and west longitude is the
        // angle from the prime meridian westward.
        var east = Math.Atan2(alongNormal, alongNode) * Constants.RADIANS2DEGREES;
        var latitude = Math.Asin(Math.Clamp(Dot(direction, pole), -1, 1)) * Constants.RADIANS2DEGREES;
        return (Wrap(w - east), latitude);
    }

    // Planetocentric to planetographic latitude on an oblate spheroid of flattening f.
    private static double Graphic(double centricDeg, double flattening)
    {
        var axisRatio = 1 - flattening;
        return Math.Atan(Math.Tan(centricDeg * Constants.DEGREES2RADIANS) / (axisRatio * axisRatio)) * Constants.RADIANS2DEGREES;
    }

    // The pole's position angle, east of north, seen at the disk's centre (`toPlanet`), with north the true celestial pole OF
    // DATE, as a physical ephemeris gives it (Horizons' NP.ang, Meeus's P) and as the sky over a telescope lies: referred to
    // the ICRF's pole instead it drifts by precession, 0.14 degree for Jupiter in 2024, whose direction it depends on.
    private static double PoleOnTheSkyOfDate(ReadOnlySpan<double> toPlanet, ReadOnlySpan<double> pole, double tt1, double tt2)
    {
        Span<double> ofDate = stackalloc double[9];
        SOFA.SofaFunctions.Pnm06a(tt1, tt2, ofDate);
        Span<double> planet = stackalloc double[3];
        Span<double> axis = stackalloc double[3];
        SOFA.SofaFunctions.Rxp(ofDate, toPlanet, planet);
        SOFA.SofaFunctions.Rxp(ofDate, pole, axis);
        var dec = Math.Asin(planet[2] / Norm(planet));
        var ra = Math.Atan2(planet[1], planet[0]);
        var poleDec = Math.Asin(axis[2] / Norm(axis));
        var poleRa = Math.Atan2(axis[1], axis[0]);
        var y = Math.Cos(poleDec) * Math.Sin(poleRa - ra);
        var x = (Math.Sin(poleDec) * Math.Cos(dec)) - (Math.Cos(poleDec) * Math.Sin(dec) * Math.Cos(poleRa - ra));
        return Wrap(Math.Atan2(y, x) * Constants.RADIANS2DEGREES);
    }

    private static double[] Unit(double ra, double dec) => [Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec)];

    private static void Cross(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> result)
    {
        result[0] = (a[1] * b[2]) - (a[2] * b[1]);
        result[1] = (a[2] * b[0]) - (a[0] * b[2]);
        result[2] = (a[0] * b[1]) - (a[1] * b[0]);
    }

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double Norm(ReadOnlySpan<double> a) => Math.Sqrt(Dot(a, a));

    private static double Wrap(double degrees)
    {
        var wrapped = degrees % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }
}
