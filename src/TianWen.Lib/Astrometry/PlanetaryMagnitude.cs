using System;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Astrometry;

/// <summary>
/// The apparent V magnitude of a solar-system body from its aspect (docs/plans/atlas-planet-detail.md, A1, finding 2): the
/// planets by Mallama and Hilton (2018, "Computing apparent planetary magnitudes for The Astronomical Almanac", Astronomy and
/// Computing 25, 10), which JPL Horizons' APmag also evaluates; Saturn's rings by their term in the geometric mean of the
/// Earth's and the Sun's ring openings; the Moon by Allen's phase law (Astrophysical Quantities, 1976); the Sun as its
/// absolute magnitude at the Earth's distance. Pure: everything comes from the <see cref="PlanetAspect"/>, so a magnitude
/// and the geometry drawn beside it are one computation.
/// </summary>
/// <remarks>
/// Checked against Horizons' APmag at four instants for every body in <c>PlanetaryMagnitudeTests</c>. Every branch follows
/// the authors' own implementation, Ap_Mag V3 (sourceforge.net/projects/planetary-magnitudes), where the paper leaves a choice
/// open: Mars's rotation and season corrections, and Uranus's and Neptune's phase curves only past what the Earth sees.
/// </remarks>
public static class PlanetaryMagnitude
{
    /// <summary>
    /// The apparent V magnitude of <paramref name="aspect"/>'s body, or NaN for a body with no model here (or a phase angle past
    /// the range its formula was fitted over, where it would extrapolate).
    /// </summary>
    public static double Apparent(in PlanetAspect aspect)
    {
        var r = aspect.HeliocentricDistanceAu;
        var delta = aspect.DistanceAu;
        var a = aspect.PhaseAngle;
        var distance = 5 * Math.Log10(r * delta);
        return aspect.Planet switch
        {
            CatalogIndex.Sol => -26.74 + (5 * Math.Log10(delta)),
            CatalogIndex.Moon => 0.23 + distance + (0.026 * a) + (4.0e-9 * Math.Pow(a, 4)),
            CatalogIndex.Mercury => distance + Mercury(a),
            CatalogIndex.Venus => distance + Venus(a),
            CatalogIndex.Mars => distance + Mars(a) + MarsRotation(aspect) + MarsSeason(aspect.HeliocentricEclipticLongitude),
            CatalogIndex.Jupiter => distance + Jupiter(a),
            CatalogIndex.Saturn => distance + Saturn(a, aspect.SubObserverLatitudeCentric, aspect.SubSolarLatitudeCentric),
            // Uranus's term is in the mean of the two sub-latitudes' magnitudes, planetographic, as the paper states it. Its
            // phase curve (eq. 15, Voyager's) is used only past the 3.1 degrees the Earth can see, where eq. 14 has none, as
            // Ap_Mag does; Neptune's (eq. 17) likewise past 1.9 degrees.
            CatalogIndex.Uranus => distance - 7.110 - (8.4e-04 * ((Math.Abs(aspect.SubObserverLatitude) + Math.Abs(aspect.SubSolarLatitude)) / 2))
                + (a > 3.1 ? (6.587e-3 * a) + (1.045e-4 * a * a) : 0),
            CatalogIndex.Neptune => distance + NeptuneAtUnitDistance(aspect.Utc) + (a > 1.9 ? (7.944e-3 * a) + (9.617e-5 * a * a) : 0),
            _ => double.NaN,
        };
    }

    private static double Mercury(double a) => -0.613 + (6.3280e-02 * a) - (1.6336e-03 * a * a) + (3.3644e-05 * Math.Pow(a, 3))
        - (3.4265e-07 * Math.Pow(a, 4)) + (1.6893e-09 * Math.Pow(a, 5)) - (3.0334e-12 * Math.Pow(a, 6));

    // Two branches: past 163.7 degrees, near inferior conjunction, forward scattering in the atmosphere brightens it again.
    private static double Venus(double a) => a <= 163.7
        ? -4.384 - (1.044e-03 * a) + (3.687e-04 * a * a) - (2.814e-06 * Math.Pow(a, 3)) + (8.938e-09 * Math.Pow(a, 4))
        : 236.05828 - (2.81914 * a) + (8.39034e-03 * a * a);

    private static double Mars(double a) => a <= 50
        ? -1.601 + (0.02267 * a) - (0.0001302 * a * a)
        : -0.367 - (0.02573 * a) + (0.0003445 * a * a);

    // Mallama (2007, Icarus 192, 404), tables 6 and 8 as Mallama and Hilton's Ap_Mag V3 carries them: the magnitude
    // correction every 10 degrees from -20 to 370, so that a five-point stencil about any whole ten degrees stays inside.
    // Rotation: Mars is brightest with Amazonis-Tharsis (about 115) facing us, the table's most negative entries, and faintest
    // with Syrtis Major (about 290).
    private static readonly double[] MarsRotationTable =
    [
        0.024, 0.034, 0.036, 0.045, 0.038, 0.023, 0.015, 0.011, 0.000, -0.012, -0.018, -0.036, -0.044, -0.059, -0.060, -0.055,
        -0.043, -0.041, -0.041, -0.036, -0.036, -0.018, -0.038, -0.011, 0.002, 0.004, 0.018, 0.019, 0.035, 0.050, 0.035, 0.027,
        0.037, 0.048, 0.025, 0.022, 0.024, 0.034, 0.036, 0.045,
    ];

    private static readonly double[] MarsSeasonTable =
    [
        -0.030, -0.017, -0.029, -0.017, -0.014, -0.006, -0.018, -0.020, -0.014, -0.030, -0.008, -0.040, -0.024, -0.037, -0.036, -0.032,
        0.010, 0.010, -0.001, 0.044, 0.025, -0.004, -0.016, -0.008, 0.029, -0.054, -0.033, 0.055, 0.017, 0.052, 0.006, 0.087,
        0.006, 0.064, 0.030, 0.019, -0.030, -0.017, -0.029, -0.017,
    ];

    // The rotation term is read at the lit hemisphere's middle, the mean of the sub-Earth and sub-solar (west) longitudes.
    private static double MarsRotation(in PlanetAspect aspect)
    {
        var observer = aspect.CentralMeridianIII;
        var sun = aspect.SubSolarLongitudeIII;
        var effective = (observer + sun) / 2;
        if (Math.Abs(observer - sun) > 180)
        {
            effective += 180;
        }
        return Stirling(MarsRotationTable, effective % 360);
    }

    // The season term in Mars's Ls, its heliocentric longitude less 85 degrees (Ap_Mag's offset to the Martian equinox).
    private static double MarsSeason(double heliocentricLongitude)
    {
        var ls = (heliocentricLongitude - 85) % 360;
        return Stirling(MarsSeasonTable, ls < 0 ? ls + 360 : ls);
    }

    /// <summary>
    /// Stirling's central-difference interpolation to fourth differences (the Explanatory Supplement, 14.2.4) in a table every 10
    /// degrees whose entry k is at (k - 2) * 10 degrees, as Ap_Mag V3 does it, with one difference: its Fortran forms three
    /// first differences where the formula's fourth-order term needs four (its own comment reads "for i in range(4)", Hilton's
    /// Python), leaving the fourth unset.
    /// </summary>
    private static double Stirling(double[] table, double angle)
    {
        var zero = (int)(angle / 10);
        var p = (angle / 10) - zero;
        Span<double> first = stackalloc double[4];
        for (var i = 0; i < 4; i++)
        {
            first[i] = table[zero + i + 1] - table[zero + i];
        }
        Span<double> second = [first[1] - first[0], first[2] - first[1], first[3] - first[2]];
        Span<double> third = [second[1] - second[0], second[2] - second[1]];
        var fourth = third[1] - third[0];
        var a4 = fourth / 24;
        var a3 = (third[0] + third[1]) / 12;
        var a2 = (second[1] / 2) - a4;
        var a1 = ((first[1] + first[2]) / 2) - a3;
        return table[zero + 2] + (a1 * p) + (a2 * p * p) + (a3 * p * p * p) + (a4 * p * p * p * p);
    }

    private static double Jupiter(double a)
    {
        if (a <= 12)
        {
            return -9.395 - (3.7e-04 * a) + (6.16e-04 * a * a);
        }
        var x = a / 180;
        return -9.428 - (2.5 * Math.Log10(1 - (1.507 * x) - (0.363 * x * x) - (0.062 * Math.Pow(x, 3)) + (2.809 * Math.Pow(x, 4)) - (1.876 * Math.Pow(x, 5))));
    }

    /// <summary>
    /// Saturn with its rings: the ring term is in the geometric mean of the Earth's and the Sun's saturnicentric latitudes above
    /// the ring plane (B and B'), and nothing when the two stand on opposite faces, since the face the Earth sees is then unlit.
    /// Past 6.5 degrees of phase, which the Earth never sees, or a 27 degree opening, past Saturn's largest, the globe alone.
    /// </summary>
    private static double Saturn(double a, double earthLatitude, double sunLatitude)
    {
        var b = earthLatitude * sunLatitude > 0 ? Math.Sqrt(earthLatitude * sunLatitude) : 0;
        var sinB = Math.Sin(b * Constants.DEGREES2RADIANS);
        if (a <= 6.5 && b <= 27)
        {
            return -8.914 - (1.825 * sinB) + (0.026 * a) - (0.378 * sinB * Math.Exp(-2.25 * a));
        }
        return -8.94 + (2.446e-4 * a) + (2.672e-4 * a * a) - (1.505e-6 * Math.Pow(a, 3)) + (4.767e-9 * Math.Pow(a, 4));
    }

    // Neptune brightened from 1980 to 2000 (Mallama and Hilton's secular term) and has held since.
    private static double NeptuneAtUnitDistance(DateTimeOffset utc)
    {
        var year = utc.Year + (utc.DayOfYear / 365.25);
        return year >= 2000 ? -7.00 : year <= 1980 ? -6.89 : -6.89 - (0.0054 * (year - 1980));
    }
}
