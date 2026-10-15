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
/// <param name="HeliocentricDistanceAu">The body's distance from the Sun when the light left it; 0 for the Sun itself.</param>
/// <param name="BrightLimbPositionAngle">The position angle of the bright limb's midpoint, the direction toward the Sun on the
/// sky at the body, east of the true celestial north of date as <see cref="PolePositionAngle"/> is (Meeus's chi, ch. 48;
/// Horizons' PsAng plus 180). NaN for the Sun.</param>
/// <param name="SubSolarLatitudeCentric">The sub-solar latitude, planetocentric (Meeus's <c>DS</c>; Saturn's B'). NaN for the
/// Sun.</param>
/// <param name="HeliocentricEclipticLongitude">The body's heliocentric longitude on the ecliptic and equinox of J2000 when the
/// light left it, degrees: where it stands on its orbit (Mars's season, an orrery's placing). 0 for the Sun.</param>
/// <remarks>
/// <para><b>Longitudes are WEST for every body, retrograde rotators included.</b> The west longitude of a point is the prime
/// meridian's angle W (Archinal et al. 2018) less the point's angle east of the equator's node, whatever the sign of W's rate.
/// For a prograde rotator (Mercury, Mars, Jupiter, Saturn, Neptune, the Moon, the Sun) the central meridian GROWS with time;
/// for the two that turn retrograde about their IAU north pole, Venus and Uranus (<see cref="PhysicalEphemeris.RotatesRetrograde"/>),
/// it SHRINKS. The IAU's planetographic longitude of a retrograde rotator, and Horizons' for Venus, Uranus, the Moon (MOON_ME)
/// and the Sun, is EAST, 360 less the value here; one convention keeps <see cref="PhysicalEphemeris.SunOnTheDisk"/> and every
/// map lookup the same for all bodies.</para>
/// <para>The IAU north pole is the one on the north side of the invariable plane, which for Uranus is the pole its rotation
/// calls south.</para>
/// </remarks>
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
    double Flattening,
    double HeliocentricDistanceAu,
    double BrightLimbPositionAngle,
    double SubSolarLatitudeCentric,
    double HeliocentricEclipticLongitude)
{
    /// <summary>The fraction of the disk's area lit as the Earth sees it, (1 + cos i) / 2 for the phase angle i (Meeus 41.1).</summary>
    public double IlluminatedFraction => (1 + Math.Cos(PhaseAngle * Constants.DEGREES2RADIANS)) / 2;

    /// <summary>
    /// Saturn's ring opening B, the Earth's saturnicentric latitude above the ring plane (which is Saturn's equator): the ring
    /// is an ellipse of axis ratio sin |B|, and its south face is the one seen when B is negative (Meeus ch. 45). NaN for
    /// another body.
    /// </summary>
    public double RingOpening => Planet == CatalogIndex.Saturn ? SubObserverLatitudeCentric : double.NaN;

    /// <summary>Saturn's B', the Sun's saturnicentric latitude above the ring plane: the face the Sun lights. NaN for another body.</summary>
    public double RingOpeningFromSun => Planet == CatalogIndex.Saturn ? SubSolarLatitudeCentric : double.NaN;

    /// <summary>
    /// Saturn's P, the position angle of the ring ellipse's northern minor axis, east of north of date: the pole's
    /// (<see cref="PolePositionAngle"/>), since the ring lies in the equator. NaN for another body.
    /// </summary>
    public double RingPositionAngle => Planet == CatalogIndex.Saturn ? PolePositionAngle : double.NaN;

    /// <summary>
    /// The planet turned so <paramref name="centralMeridianIII"/> faces the observer, lit as before: the sub-solar longitude turns with
    /// it, since the Sun's place on the disk is their difference (<see cref="PhysicalEphemeris.SunOnTheDisk"/>), and a central meridian
    /// set alone swings the Sun round the planet (#1212: a rotation's disk means ranged 262 %). Systems I and II turn by the same angle.
    /// </summary>
    public PlanetAspect TurnedTo(double centralMeridianIII)
    {
        var turn = centralMeridianIII - CentralMeridianIII;
        return this with
        {
            CentralMeridianIII = centralMeridianIII,
            CentralMeridianI = CentralMeridianI + turn,
            CentralMeridianII = CentralMeridianII + turn,
            SubSolarLongitudeIII = SubSolarLongitudeIII + turn,
        };
    }
}

/// <summary>
/// The physical ephemeris of the Sun, the Moon and the planets from first principles: the IAU WGCCRE 2015 rotation models
/// (Archinal et al. 2018: the pole and prime meridian in the ICRF, System III for the giants, the IAU lunar orientation for
/// the Moon) and VSOP87 positions (Meeus's series for the Moon), light-time corrected. Jupiter's Systems I and II are their
/// defined rates, 877.900 and 870.270 degrees a day, from their IAU epochs.
/// </summary>
/// <remarks>
/// The orientation is never measured from an image, which supplies only the disk's centre and scale (#815, #1049). Checked
/// against JPL Horizons at the corpus's own session times and against Meeus's worked example (Astronomical Algorithms,
/// example 43.a) in <c>PhysicalEphemerisTests</c>; every other body, the bright limb and the lit fraction against Horizons,
/// and Venus's phase and Saturn's rings against Meeus's examples 41.a and 45.a, in <c>PhysicalEphemerisBodiesTests</c>. The
/// apparent magnitude of an aspect is <see cref="PlanetaryMagnitude.Apparent"/>.
/// </remarks>
public static class PhysicalEphemeris
{
    private const double DaysPerCentury = 36525.0;
    private const double SecondsPerDay = 86400.0;

    /// <summary>
    /// Whether <see cref="Compute"/> has a rotation model for <paramref name="planet"/>: every body the atlas draws, the Sun,
    /// the Moon and the planets but the Earth.
    /// </summary>
    public static bool Supports(CatalogIndex planet) => planet is CatalogIndex.Sol or CatalogIndex.Moon or CatalogIndex.Mercury
        or CatalogIndex.Venus or CatalogIndex.Mars or CatalogIndex.Jupiter or CatalogIndex.Saturn or CatalogIndex.Uranus or CatalogIndex.Neptune;

    /// <summary>
    /// Whether the planetary stacker's models of a turning, limb-darkened disk (de-rotation, channel alignment by limb fit,
    /// the derived sharpening, composition) are built and verified for <paramref name="planet"/>: Mars, Jupiter and Saturn.
    /// Every stacking gate asks this, never <see cref="Supports"/>, which widened to every body the atlas draws (#1037) and
    /// must not switch those models on for a Moon, Venus or Sun capture.
    /// </summary>
    public static bool SupportsPlanetaryStacking(CatalogIndex planet) => planet is CatalogIndex.Mars or CatalogIndex.Jupiter or CatalogIndex.Saturn;

    /// <summary>The planet as the geocentre sees it at <paramref name="utc"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A planet with no rotation model here (<see cref="Supports"/>).</exception>
    public static PlanetAspect Compute(CatalogIndex planet, DateTimeOffset utc)
    {
        if (!Supports(planet))
        {
            throw new ArgumentOutOfRangeException(nameof(planet), planet, "No rotation model here for this body");
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

        // Planet to observer, and planet to Sun (the Sun is the frame's origin, so the Sun itself has no direction to it).
        Span<double> toObserver = [-geocentric[0] / distanceAu, -geocentric[1] / distanceAu, -geocentric[2] / distanceAu];
        var heliocentric = Norm(body);
        var isSun = planet == CatalogIndex.Sol;
        Span<double> toSun = isSun
            ? [double.NaN, double.NaN, double.NaN]
            : [-body[0] / heliocentric, -body[1] / heliocentric, -body[2] / heliocentric];

        var flattening = 1 - (polarKm / equatorialKm);
        var (observerLongitude, observerCentric) = SubPoint(toObserver, pole, node, nodeNormal, w);
        var (sunLongitude, sunCentric) = isSun ? (double.NaN, double.NaN) : SubPoint(toSun, pole, node, nodeNormal, w);

        Span<double> ofDate = stackalloc double[9];
        SOFA.SofaFunctions.Pnm06a(tt1, tt2, ofDate);

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
            PolePositionAngle: PositionAngleOfDate(geocentric, pole, ofDate),
            // The Sun is seen from the Earth, which it lights: its phase is nought.
            PhaseAngle: isSun ? 0 : Math.Acos(Math.Clamp(Dot(toObserver, toSun), -1, 1)) * Constants.RADIANS2DEGREES,
            AngularDiameterArcsec: 2 * Math.Atan(equatorialKm / (distanceAu * Constants.KMAU)) * Constants.RADIANS2DEGREES * 3600,
            DistanceAu: distanceAu,
            LightTimeSeconds: lightTimeDays * SecondsPerDay,
            Flattening: flattening,
            HeliocentricDistanceAu: heliocentric,
            // The great circle from the body toward the Sun leaves it along the Sun's direction from the body, projected on
            // the sky: the same position angle as of a direction at infinity, which is how the pole's is read.
            BrightLimbPositionAngle: isSun ? double.NaN : PositionAngleOfDate(geocentric, toSun, ofDate),
            SubSolarLatitudeCentric: sunCentric,
            // Back from the J2000 equator to the J2000 ecliptic: VSOP87's own rotation (Rotvsop2J2000) undone, its frame-bias
            // terms of a few tenths of a milliarcsecond left out.
            HeliocentricEclipticLongitude: isSun ? 0 : Wrap(Math.Atan2((body[1] * 0.917482137087) + (body[2] * 0.397776982902), body[0]) * Constants.RADIANS2DEGREES));
    }

    /// <summary>
    /// The body at its heliocentric place at <paramref name="utc"/>, seen from far away along <paramref name="toObserver"/> (a unit
    /// vector from the body, J2000 equatorial): what the orrery's camera sees of it (docs/plans/atlas-planet-detail.md, A5), so a body
    /// between the camera and the Sun shows its night side. The Earth is accepted beside every body <see cref="Supports"/> names.
    /// </summary>
    /// <remarks>
    /// The same frame and sub-point arithmetic as <see cref="Compute"/>, with the observer a DIRECTION rather than the geocentre: the
    /// orrery may draw its distances compressed, which keeps every direction from the Sun and changes every other, so only the caller
    /// knows where its camera stands relative to the drawn body. No light time (the scene is one instant), and nothing that needs a
    /// sky: <see cref="PlanetAspect.PolePositionAngle"/>, <see cref="PlanetAspect.BrightLimbPositionAngle"/>, the distance and the
    /// angular diameter are NaN; the pole's screen direction is the caller's, from <see cref="PoleDirection"/>.
    /// </remarks>
    public static PlanetAspect ComputeSeenAlong(CatalogIndex planet, DateTimeOffset utc, ReadOnlySpan<double> toObserver)
    {
        utc.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        Span<double> body = stackalloc double[3];
        Position(planet, tt1 + tt2, body);
        return ComputeSeenAlong(planet, utc, body, toObserver);
    }

    /// <summary>
    /// <see cref="ComputeSeenAlong(CatalogIndex, DateTimeOffset, ReadOnlySpan{double})"/> with the body's heliocentric place (J2000
    /// equatorial, AU) already in hand, as the orrery has it each frame: no ephemeris, only the rotation model.
    /// </summary>
    public static PlanetAspect ComputeSeenAlong(CatalogIndex planet, DateTimeOffset utc, ReadOnlySpan<double> body, ReadOnlySpan<double> toObserver)
    {
        if (!Supports(planet) && planet != CatalogIndex.Earth)
        {
            throw new ArgumentOutOfRangeException(nameof(planet), planet, "No rotation model here for this body");
        }
        var (equatorialKm, polarKm) = Radii(planet);
        utc.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        var jdTdb = tt1 + tt2;

        var (poleRa, poleDec, w) = Rotation(planet, jdTdb);
        var pole = Unit(poleRa, poleDec);
        Span<double> node = [-Math.Sin(poleRa), Math.Cos(poleRa), 0];
        Span<double> nodeNormal = stackalloc double[3];
        Cross(pole, node, nodeNormal);

        var heliocentric = Norm(body);
        var isSun = planet == CatalogIndex.Sol;
        Span<double> toSun = isSun
            ? [double.NaN, double.NaN, double.NaN]
            : [-body[0] / heliocentric, -body[1] / heliocentric, -body[2] / heliocentric];
        var flattening = 1 - (polarKm / equatorialKm);
        var (observerLongitude, observerCentric) = SubPoint(toObserver, pole, node, nodeNormal, w);
        var (sunLongitude, sunCentric) = isSun ? (double.NaN, double.NaN) : SubPoint(toSun, pole, node, nodeNormal, w);

        return new PlanetAspect(
            planet,
            utc,
            CentralMeridianIII: observerLongitude,
            CentralMeridianI: double.NaN,
            CentralMeridianII: double.NaN,
            SubObserverLatitude: Graphic(observerCentric, flattening),
            SubObserverLatitudeCentric: observerCentric,
            SubSolarLongitudeIII: sunLongitude,
            SubSolarLatitude: Graphic(sunCentric, flattening),
            PolePositionAngle: double.NaN,
            PhaseAngle: isSun ? 0 : Math.Acos(Math.Clamp(Dot(toObserver, toSun), -1, 1)) * Constants.RADIANS2DEGREES,
            AngularDiameterArcsec: double.NaN,
            DistanceAu: double.NaN,
            LightTimeSeconds: 0,
            Flattening: flattening,
            HeliocentricDistanceAu: heliocentric,
            BrightLimbPositionAngle: double.NaN,
            SubSolarLatitudeCentric: sunCentric,
            HeliocentricEclipticLongitude: isSun ? 0 : Wrap(Math.Atan2((body[1] * 0.917482137087) + (body[2] * 0.397776982902), body[0]) * Constants.RADIANS2DEGREES));
    }

    /// <summary>
    /// The body's IAU north pole at <paramref name="utc"/> as a unit vector, J2000 equatorial: the direction a view that is not the sky
    /// (the orrery) projects to turn the body's sprite. The Earth is accepted beside every body <see cref="Supports"/> names.
    /// </summary>
    public static void PoleDirection(CatalogIndex planet, DateTimeOffset utc, Span<double> pole)
    {
        utc.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        var (poleRa, poleDec, _) = Rotation(planet, tt1 + tt2);
        Unit(poleRa, poleDec).CopyTo(pole);
    }

    /// <summary>
    /// The body's IAU north pole and the direction of its prime meridian in its equator at <paramref name="utc"/>, unit vectors, J2000
    /// equatorial: the frame a renderer in some other view (the orrery's GPU discs) reads a point's west longitude in, as
    /// <c>-atan2(point . (pole x primeMeridian), point . primeMeridian)</c>, the very longitude <see cref="ComputeSeenAlong(CatalogIndex, DateTimeOffset, ReadOnlySpan{double}, ReadOnlySpan{double})"/>
    /// states for a sub-point. The Earth is accepted beside every body <see cref="Supports"/> names.
    /// </summary>
    public static void BodyAxes(CatalogIndex planet, DateTimeOffset utc, Span<double> pole, Span<double> primeMeridian)
    {
        utc.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        var (poleRa, poleDec, w) = Rotation(planet, tt1 + tt2);
        Unit(poleRa, poleDec).CopyTo(pole);
        Span<double> node = [-Math.Sin(poleRa), Math.Cos(poleRa), 0];
        Span<double> nodeNormal = stackalloc double[3];
        Cross(pole, node, nodeNormal);
        // The prime meridian lies W east of the node, in the equator (SubPoint's own reading).
        var (sinW, cosW) = Math.SinCos(w * Constants.DEGREES2RADIANS);
        for (var i = 0; i < 3; i++)
        {
            primeMeridian[i] = (cosW * node[i]) + (sinW * nodeNormal[i]);
        }
    }

    /// <summary>Whether the body turns retrograde about its IAU north pole (W decreasing): Venus and Uranus.</summary>
    public static bool RotatesRetrograde(CatalogIndex body) => body is CatalogIndex.Venus or CatalogIndex.Uranus;

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

    // IAU equatorial and polar radii, km (Archinal et al. 2018, table 5; the Sun's the IAU 2015 nominal radius, Resolution B3).
    // Mercury's two equatorial axes differ by nothing at this precision, so its mean equatorial radius stands for both.
    internal static (double Equatorial, double Polar) Radii(CatalogIndex planet) => planet switch
    {
        CatalogIndex.Sol => (695700, 695700),
        CatalogIndex.Moon => (1737.4, 1737.4),
        CatalogIndex.Earth => (6378.1366, 6356.7519),
        CatalogIndex.Mercury => (2440.53, 2438.26),
        CatalogIndex.Venus => (6051.8, 6051.8),
        CatalogIndex.Mars => (3396.19, 3376.20),
        CatalogIndex.Jupiter => (71492, 66854),
        CatalogIndex.Saturn => (60268, 54364),
        CatalogIndex.Uranus => (25559, 24973),
        CatalogIndex.Neptune => (24764, 24341),
        _ => throw new ArgumentOutOfRangeException(nameof(planet), planet, null),
    };

    // The IAU WGCCRE 2015 rotation model at TDB Julian date `jd`: the pole (radians) and the prime meridian W (degrees).
    private static (double PoleRa, double PoleDec, double W) Rotation(CatalogIndex planet, double jd)
    {
        var d = jd - Constants.J2000BASE;
        var t = d / DaysPerCentury;
        switch (planet)
        {
            case CatalogIndex.Mars:
            {
                // Four short-period terms each, and one long-period term (0.5042615 degrees a century) that moves the pole by up to
                // 1.6 degrees and the prime meridian by 0.6: Archinal et al. 2018, table 2.
                var marsRa = 317.269202 - (0.10927547 * t)
                    + (0.000068 * Math.Sin(SatelliteArgument(198.991226, 19139.4819985, t)))
                    + (0.000238 * Math.Sin(SatelliteArgument(226.292679, 38280.8511281, t)))
                    + (0.000052 * Math.Sin(SatelliteArgument(249.663391, 57420.7251593, t)))
                    + (0.000009 * Math.Sin(SatelliteArgument(266.183510, 76560.6367950, t)))
                    + (0.419057 * Math.Sin(SatelliteArgument(79.398797, 0.5042615, t)));
                var marsDec = 54.432516 - (0.05827105 * t)
                    + (0.000051 * Math.Cos(SatelliteArgument(122.433576, 19139.9407476, t)))
                    + (0.000141 * Math.Cos(SatelliteArgument(43.058401, 38280.8753272, t)))
                    + (0.000031 * Math.Cos(SatelliteArgument(57.663379, 57420.7517205, t)))
                    + (0.000005 * Math.Cos(SatelliteArgument(79.476401, 76560.6495004, t)))
                    + (1.591274 * Math.Cos(SatelliteArgument(166.325722, 0.5042615, t)));
                var marsW = 176.049863 + (350.891982443297 * d)
                    + (0.000145 * Math.Sin(SatelliteArgument(129.071773, 19140.0328244, t)))
                    + (0.000157 * Math.Sin(SatelliteArgument(36.352167, 38281.0473591, t)))
                    + (0.000040 * Math.Sin(SatelliteArgument(56.668646, 57420.9295360, t)))
                    + (0.000001 * Math.Sin(SatelliteArgument(67.364003, 76560.2552215, t)))
                    + (0.000001 * Math.Sin(SatelliteArgument(104.792680, 95700.4387578, t)))
                    + (0.584542 * Math.Sin(SatelliteArgument(95.391654, 0.5042615, t)));
                return (marsRa * Constants.DEGREES2RADIANS, marsDec * Constants.DEGREES2RADIANS, Wrap(marsW));
            }
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
            case CatalogIndex.Sol:
                // Carrington's rotation (Archinal et al. 2018, table 1).
                return (286.13 * Constants.DEGREES2RADIANS, 63.87 * Constants.DEGREES2RADIANS, Wrap(84.176 + (14.1844000 * d)));
            case CatalogIndex.Mercury:
            {
                // The 3:2 spin-orbit resonance's libration, five harmonics of the mean anomaly (Margot 2009, adopted 2015).
                var m1 = (174.7910857 + (4.092335 * d)) * Constants.DEGREES2RADIANS;
                var mercuryW = 329.5988 + (6.1385108 * d)
                    + (0.01067257 * Math.Sin(m1)) - (0.00112309 * Math.Sin(2 * m1)) - (0.00011040 * Math.Sin(3 * m1))
                    - (0.00002539 * Math.Sin(4 * m1)) - (0.00000571 * Math.Sin(5 * m1));
                return ((281.0103 - (0.0328 * t)) * Constants.DEGREES2RADIANS, (61.4155 - (0.0049 * t)) * Constants.DEGREES2RADIANS, Wrap(mercuryW));
            }
            case CatalogIndex.Venus:
                // Retrograde: W falls (RotatesRetrograde).
                return (272.76 * Constants.DEGREES2RADIANS, 67.16 * Constants.DEGREES2RADIANS, Wrap(160.20 - (1.4813688 * d)));
            case CatalogIndex.Uranus:
                // Retrograde about the IAU north pole, the one north of the invariable plane.
                return (257.311 * Constants.DEGREES2RADIANS, -15.175 * Constants.DEGREES2RADIANS, Wrap(203.81 - (501.1600928 * d)));
            case CatalogIndex.Neptune:
            {
                var n = SatelliteArgument(357.85, 52.316, t);
                return ((299.36 + (0.70 * Math.Sin(n))) * Constants.DEGREES2RADIANS, (43.46 - (0.51 * Math.Cos(n))) * Constants.DEGREES2RADIANS,
                    Wrap(249.978 + (541.1397757 * d) - (0.48 * Math.Sin(n))));
            }
            case CatalogIndex.Moon:
                return MoonRotation(d, t);
            case CatalogIndex.Earth:
                // Only the orrery draws the Earth (ComputeSeenAlong): the IAU's own model, precession as a linear pole.
                return ((0.00 - (0.641 * t)) * Constants.DEGREES2RADIANS, (90.00 - (0.557 * t)) * Constants.DEGREES2RADIANS,
                    Wrap(190.147 + (360.9856235 * d)));
            default:
                throw new ArgumentOutOfRangeException(nameof(planet), planet, null);
        }
    }

    // The IAU's lunar orientation (Archinal et al. 2018, table 2): an approximation of the mean-Earth/polar-axis frame, the
    // one selenographic maps are drawn in, good to about 150 m on the surface; the E terms are the physical librations.
    private static (double PoleRa, double PoleDec, double W) MoonRotation(double d, double t)
    {
        static double E(double atEpoch, double perDay, double days) => (atEpoch + (perDay * days)) * Constants.DEGREES2RADIANS;
        var e1 = E(125.045, -0.0529921, d);
        var e2 = E(250.089, -0.1059842, d);
        var e3 = E(260.008, 13.0120009, d);
        var e4 = E(176.625, 13.3407154, d);
        var e5 = E(357.529, 0.9856003, d);
        var e6 = E(311.589, 26.4057084, d);
        var e7 = E(134.963, 13.0649930, d);
        var e8 = E(276.617, 0.3287146, d);
        var e9 = E(34.226, 1.7484877, d);
        var e10 = E(15.134, -0.1589763, d);
        var e11 = E(119.743, 0.0036096, d);
        var e12 = E(239.961, 0.1643573, d);
        var e13 = E(25.053, 12.9590088, d);
        var ra = 269.9949 + (0.0031 * t) - (3.8787 * Math.Sin(e1)) - (0.1204 * Math.Sin(e2)) + (0.0700 * Math.Sin(e3))
            - (0.0172 * Math.Sin(e4)) + (0.0072 * Math.Sin(e6)) - (0.0052 * Math.Sin(e10)) + (0.0043 * Math.Sin(e13));
        var dec = 66.5392 + (0.0130 * t) + (1.5419 * Math.Cos(e1)) + (0.0239 * Math.Cos(e2)) - (0.0278 * Math.Cos(e3))
            + (0.0068 * Math.Cos(e4)) - (0.0029 * Math.Cos(e6)) + (0.0009 * Math.Cos(e7)) + (0.0008 * Math.Cos(e10)) - (0.0009 * Math.Cos(e13));
        var w = 38.3213 + (13.17635815 * d) - (1.4e-12 * d * d) + (3.5610 * Math.Sin(e1)) + (0.1208 * Math.Sin(e2))
            - (0.0642 * Math.Sin(e3)) + (0.0158 * Math.Sin(e4)) + (0.0252 * Math.Sin(e5)) - (0.0066 * Math.Sin(e6))
            - (0.0047 * Math.Sin(e7)) - (0.0046 * Math.Sin(e8)) + (0.0028 * Math.Sin(e9)) + (0.0052 * Math.Sin(e10))
            + (0.0040 * Math.Sin(e11)) + (0.0019 * Math.Sin(e12)) - (0.0044 * Math.Sin(e13));
        return (ra * Constants.DEGREES2RADIANS, dec * Constants.DEGREES2RADIANS, Wrap(w));
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

    // The position angle of a direction (the pole, or the Sun from the body), east of north, seen at the disk's centre
    // (`toPlanet`), with north the true celestial pole OF
    // DATE, as a physical ephemeris gives it (Horizons' NP.ang, Meeus's P) and as the sky over a telescope lies: referred to
    // the ICRF's pole instead it drifts by precession, 0.14 degree for Jupiter in 2024, whose direction it depends on.
    // `ofDate` is Pnm06a's bias-precession-nutation matrix at the instant, formed ONCE by the caller for both angles: its
    // nutation series is the costliest step of Compute, which the planetary stacker calls per frame.
    private static double PositionAngleOfDate(ReadOnlySpan<double> toPlanet, ReadOnlySpan<double> pole, ReadOnlySpan<double> ofDate)
    {
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
