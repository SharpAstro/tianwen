using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Astrometry;

/// <summary>
/// A Galilean moon's place beside Jupiter as seen from the Earth, in Jupiter's equatorial radii: <paramref name="X"/> along the planet's
/// equator, positive toward the west, and <paramref name="Y"/> along its axis, positive toward the north; with its diameter.
/// </summary>
/// <param name="Name">Io, Europa, Ganymede or Callisto.</param>
/// <param name="X">The offset along Jupiter's equator, equatorial radii, positive toward the west.</param>
/// <param name="Y">The offset along Jupiter's axis, equatorial radii, positive toward the north.</param>
/// <param name="DiameterKm">The moon's mean diameter, km.</param>
public readonly record struct GalileanMoon(string Name, double X, double Y, double DiameterKm)
{
    /// <summary>The moon's angular diameter, arcseconds, from Jupiter's distance <paramref name="distanceAu"/>.</summary>
    public double DiameterArcsec(double distanceAu) => DiameterKm / (distanceAu * 149_597_870.7) * 206_264.806;

    /// <summary>The moon's radius in Jupiter's equatorial radii, the unit of <see cref="X"/> and <see cref="Y"/>.</summary>
    public double Radius => DiameterKm / 2 / PhysicalEphemeris.Radii(Catalogs.CatalogIndex.Jupiter).Equatorial;
}

/// <summary>
/// The four Galilean moons' places beside Jupiter by Meeus' low-accuracy theory (Astronomical Algorithms, 2nd ed., chapter 44): a tenth of
/// a radius or so, enough to name the moons in a planetary capture's field and so know their diameters (R8 follow-up 4, a moon as a
/// near-point probe of the kernel's finest band, #1120).
/// </summary>
public static class GalileanMoons
{
    /// <summary>The moons' mean diameters, km (Io, Europa, Ganymede, Callisto).</summary>
    public static readonly ImmutableArray<(string Name, double DiameterKm)> Moons = [("Io", 3643.2), ("Europa", 3121.6), ("Ganymede", 5268.2), ("Callisto", 4820.6)];

    /// <summary>
    /// The four moons at <paramref name="utc"/>, and Jupiter's distance from the Earth in AU, by Meeus' chapter 44 (low accuracy; terrestrial
    /// time taken as UTC plus 69 s, which at this accuracy is the same for decades either side of 2022).
    /// </summary>
    public static (ImmutableArray<GalileanMoon> Moons, double DistanceAu) At(DateTimeOffset utc)
    {
        var jde = (utc.ToUnixTimeMilliseconds() / 86_400_000.0) + 2_440_587.5 + (69.0 / 86_400);
        return At(jde);
    }

    /// <summary>The four moons at Julian ephemeris day <paramref name="jde"/>, and Jupiter's distance from the Earth in AU.</summary>
    public static (ImmutableArray<GalileanMoon> Moons, double DistanceAu) At(double jde)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;
        static double Sin(double degrees) => Math.Sin(Rad(degrees));
        static double Cos(double degrees) => Math.Cos(Rad(degrees));

        var d = jde - 2_451_545.0;
        var v = 172.74 + (0.00111588 * d);
        var m = 357.529 + (0.9856003 * d);
        var n = 20.020 + (0.0830853 * d) + (0.329 * Sin(v));
        var j = 66.115 + (0.9025179 * d) - (0.329 * Sin(v));
        var a = (1.915 * Sin(m)) + (0.020 * Sin(2 * m));
        var b = (5.555 * Sin(n)) + (0.168 * Sin(2 * n));
        var k = j + a - b;
        var sunDistance = 1.00014 - (0.01671 * Cos(m)) - (0.00014 * Cos(2 * m));
        var jupiterDistance = 5.20872 - (0.25208 * Cos(n)) - (0.00611 * Cos(2 * n));
        var delta = Math.Sqrt((jupiterDistance * jupiterDistance) + (sunDistance * sunDistance) - (2 * jupiterDistance * sunDistance * Cos(k)));
        var psi = Math.Asin(sunDistance / delta * Sin(k)) * 180 / Math.PI;

        var t = d - (delta / 173);
        var u1 = 163.8069 + (203.4058646 * t) + psi - b;
        var u2 = 358.4140 + (101.2916335 * t) + psi - b;
        var u3 = 5.7176 + (50.2345180 * t) + psi - b;
        var u4 = 224.8092 + (21.4879800 * t) + psi - b;
        var g = 331.18 + (50.310482 * t);
        var h = 87.45 + (21.569231 * t);
        var (c1, c2, c3, c4) = (0.473 * Sin(2 * (u1 - u2)), 1.065 * Sin(2 * (u2 - u3)), 0.165 * Sin(g), 0.843 * Sin(h));
        var r1 = 5.9057 - (0.0244 * Cos(2 * (u1 - u2)));
        var r2 = 9.3966 - (0.0882 * Cos(2 * (u2 - u3)));
        var r3 = 14.9883 - (0.0216 * Cos(g));
        var r4 = 26.3627 - (0.1939 * Cos(h));
        (u1, u2, u3, u4) = (u1 + c1, u2 + c2, u3 + c3, u4 + c4);

        var lambda = 34.35 + (0.083091 * d) + (0.329 * Sin(v)) + b;
        var ds = 3.12 * Sin(lambda + 42.8);
        var de = ds - (2.22 * Sin(psi) * Cos(lambda + 22)) - (1.30 * (jupiterDistance - delta) / delta * Sin(lambda - 100.5));

        GalileanMoon Moon(int index, double u, double r) =>
            new GalileanMoon(Moons[index].Name, r * Sin(u), -r * Cos(u) * Sin(de), Moons[index].DiameterKm);
        return ([Moon(0, u1, r1), Moon(1, u2, r2), Moon(2, u3, r3), Moon(3, u4, r4)], delta);
    }
}
