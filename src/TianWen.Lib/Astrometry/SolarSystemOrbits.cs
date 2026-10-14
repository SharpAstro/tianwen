using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;

namespace TianWen.Lib.Astrometry;

/// <summary>
/// Where the Sun's planets are and the paths they take, heliocentric and in VSOP87's ecliptic of J2000 (AU): what the atlas's orrery
/// draws (docs/plans/atlas-planet-detail.md, A5). Positions are <see cref="VSOP87a.GetBody"/>'s, with no light time, since the
/// orrery shows one instant from outside; an orbit is one sidereal period of the same series, so a planet always sits on its own.
/// </summary>
public static class SolarSystemOrbits
{
    /// <summary>The planets, the Earth among them, from the Sun outward.</summary>
    public static readonly ImmutableArray<CatalogIndex> Planets =
    [
        CatalogIndex.Mercury, CatalogIndex.Venus, CatalogIndex.Earth, CatalogIndex.Mars,
        CatalogIndex.Jupiter, CatalogIndex.Saturn, CatalogIndex.Uranus, CatalogIndex.Neptune,
    ];

    /// <summary>The sidereal period in days (the IAU/NASA fact sheet's): the span one sampled orbit covers.</summary>
    public static double SiderealPeriodDays(CatalogIndex planet) => planet switch
    {
        CatalogIndex.Mercury => 87.969,
        CatalogIndex.Venus => 224.701,
        CatalogIndex.Earth => 365.256,
        CatalogIndex.Mars => 686.980,
        CatalogIndex.Jupiter => 4332.589,
        CatalogIndex.Saturn => 10759.22,
        CatalogIndex.Uranus => 30685.4,
        CatalogIndex.Neptune => 60189.0,
        _ => throw new ArgumentOutOfRangeException(nameof(planet), planet, "Not a planet"),
    };

    /// <summary>Julian millennia of TT from J2000, VSOP87's time argument, for <paramref name="utc"/>.</summary>
    public static double JulianMillennia(DateTimeOffset utc)
    {
        utc.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        return (tt1 - Constants.J2000BASE + tt2) / 365250.0;
    }

    /// <summary>
    /// The body's heliocentric ecliptic position at <paramref name="utc"/> into <paramref name="xyz"/>: the Sun (the origin), a planet,
    /// the Earth or the Moon (Meeus's series about VSOP87's Earth). False for any other body.
    /// </summary>
    public static bool TryGetPosition(CatalogIndex body, DateTimeOffset utc, Span<double> xyz)
        => VSOP87a.GetBody(body, JulianMillennia(utc), xyz);

    /// <summary>
    /// One sidereal period of <paramref name="planet"/>'s path from <paramref name="start"/>, as <paramref name="points"/>.Length / 3
    /// points (x, y, z in turn), the last a copy of the first so the polyline closes (VSOP87's own orbit does not quite, by the
    /// perihelion's drift over one period).
    /// </summary>
    public static void SampleOrbit(CatalogIndex planet, DateTimeOffset start, Span<double> points)
    {
        var count = points.Length / 3;
        if (count < 2)
        {
            return;
        }
        var et0 = JulianMillennia(start);
        var span = SiderealPeriodDays(planet) / 365250.0;
        for (var i = 0; i < count - 1; i++)
        {
            VSOP87a.GetBody(planet, et0 + (span * i / (count - 1)), points.Slice(3 * i, 3));
        }
        points[..3].CopyTo(points.Slice(3 * (count - 1), 3));
    }

    /// <summary>
    /// A direction (or position) in VSOP87's ecliptic of J2000 turned into the J2000 equator, the frame of
    /// <see cref="PhysicalEphemeris"/>: <see cref="VSOP87a.Rotvsop2J2000"/>.
    /// </summary>
    public static void EclipticToEquatorial(Span<double> xyz) => VSOP87a.Rotvsop2J2000(xyz);

    /// <summary>The inverse of <see cref="EclipticToEquatorial"/> (its transpose, the rotation being orthonormal).</summary>
    public static void EquatorialToEcliptic(Span<double> xyz)
    {
        var a = xyz[0] + (xyz[1] * -0.000000479966);
        var b = (xyz[0] * 0.000000440360) + (xyz[1] * 0.917482137087) + (xyz[2] * 0.397776982902);
        var c = (xyz[0] * -0.000000190919) + (xyz[1] * -0.397776982902) + (xyz[2] * 0.917482137087);
        xyz[0] = a;
        xyz[1] = b;
        xyz[2] = c;
    }
}
