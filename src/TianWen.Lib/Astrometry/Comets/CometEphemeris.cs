using System;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// Geocentric J2000 position of a comet from its osculating two-body elements, plus the IAU
/// total-magnitude prediction. This is the comet analogue of <see cref="VSOP87a"/> for the planets:
/// a pure, allocation-free function of (<see cref="CometElements"/>, time) with no I/O -- the data
/// source (SBDB) supplies the elements, this turns them into an apparent RA/Dec and a magnitude.
///
/// <para>Method: a universal-variable Kepler propagation from the perihelion state
/// (r0 = q, purely transverse velocity), so one code path covers elliptic, parabolic and hyperbolic
/// orbits without the near-parabolic singularity that plagues the classic per-conic solvers -- most
/// observable comets sit at e ~ 1. The resulting heliocentric ecliptic-J2000 vector has Earth's
/// heliocentric position (from <see cref="VSOP87a.GetBody"/>) subtracted, is light-time corrected, and
/// is rotated to the J2000 equatorial frame with the same <see cref="VSOP87a.Rotvsop2J2000"/> matrix the
/// planet path uses. Accuracy is arcminute-class near the element epoch -- sufficient for GoTo + a
/// plate-solve centering pass, planner altitude curves, and magnitude curves; it deliberately does NOT
/// model non-gravitational (outgassing) forces or planetary perturbations, which a per-object Horizons
/// ephemeris fetch would add later for sub-arcsecond work.</para>
/// </summary>
public static class CometEphemeris
{
    // Gaussian gravitational constant (AU^1.5 / day); GM_sun = k^2 in these units for a massless comet.
    private const double GaussK = 0.01720209895;
    private const double Mu = GaussK * GaussK;

    /// <summary>
    /// Computes the comet's geocentric J2000 equatorial position and the helio-/geocentric distances
    /// at <paramref name="time"/>. Returns false only if the underlying Earth ephemeris is unavailable
    /// or the Kepler solve fails to converge (returned outputs are then NaN).
    /// </summary>
    /// <param name="raJ2000Hours">Geocentric astrometric RA (J2000), hours.</param>
    /// <param name="decJ2000Deg">Geocentric astrometric Dec (J2000), degrees.</param>
    /// <param name="heliocentricDistanceAu">Sun-comet distance r, AU.</param>
    /// <param name="geocentricDistanceAu">Earth-comet distance delta, AU.</param>
    public static bool TryGetEquatorialJ2000(
        in CometElements elements,
        DateTimeOffset time,
        out double raJ2000Hours,
        out double decJ2000Deg,
        out double heliocentricDistanceAu,
        out double geocentricDistanceAu)
    {
        time.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        var jdTt = tt1 + tt2;
        var et = (tt1 - Constants.J2000BASE + tt2) / 365250.0;

        Span<double> earth = stackalloc double[3];
        if (!VSOP87a.GetBody(CatalogIndex.Earth, et, earth))
        {
            raJ2000Hours = decJ2000Deg = heliocentricDistanceAu = geocentricDistanceAu = double.NaN;
            return false;
        }

        Span<double> helio = stackalloc double[3];
        if (!TryHeliocentricEcliptic(elements, jdTt, helio, out heliocentricDistanceAu))
        {
            raJ2000Hours = decJ2000Deg = geocentricDistanceAu = double.NaN;
            return false;
        }

        // Geocentric ecliptic vector. VSOP87's dynamical ecliptic differs from the ecliptic-J2000 the
        // elements use by a fixed-frame bias of order 0.05" -- negligible against our arcminute target.
        var gx = helio[0] - earth[0];
        var gy = helio[1] - earth[1];
        var gz = helio[2] - earth[2];
        geocentricDistanceAu = Math.Sqrt(gx * gx + gy * gy + gz * gz);

        // Light-time: light seen now left the comet delta/c days ago. Earth stays at time t.
        var lightTimeDays = geocentricDistanceAu / Constants.C;
        if (!TryHeliocentricEcliptic(elements, jdTt - lightTimeDays, helio, out heliocentricDistanceAu))
        {
            raJ2000Hours = decJ2000Deg = double.NaN;
            return false;
        }

        Span<double> geo = stackalloc double[3];
        geo[0] = helio[0] - earth[0];
        geo[1] = helio[1] - earth[1];
        geo[2] = helio[2] - earth[2];
        geocentricDistanceAu = Math.Sqrt(geo[0] * geo[0] + geo[1] * geo[1] + geo[2] * geo[2]);

        // Ecliptic-J2000 -> equatorial-J2000 (same rotation the planet path applies).
        VSOP87a.Rotvsop2J2000(geo);

        var r = Math.Sqrt(geo[0] * geo[0] + geo[1] * geo[1] + geo[2] * geo[2]);
        decJ2000Deg = Math.Asin(geo[2] / r) * Constants.RADIANS2DEGREES;
        raJ2000Hours = CoordinateUtils.ConditionRA(Math.Atan2(geo[1], geo[0]) * Constants.RADIANS2HOURS);
        return true;
    }

    /// <summary>
    /// The IAU total (nuclear + coma) apparent magnitude law <c>m = M1 + 5*log10(delta) + K1*log10(r)</c>.
    /// Returns NaN when the comet has no SBDB photometric model.
    /// </summary>
    public static double PredictTotalMagnitude(in CometElements elements, double heliocentricDistanceAu, double geocentricDistanceAu)
        => elements.HasMagnitudeModel
            ? elements.AbsoluteMagnitudeM1
                + 5.0 * Math.Log10(geocentricDistanceAu)
                + elements.SlopeK1 * Math.Log10(heliocentricDistanceAu)
            : double.NaN;

    /// <summary>
    /// Convenience: position + predicted magnitude in one call. Magnitude is NaN if the position solve
    /// fails or the comet has no photometric model.
    /// </summary>
    public static bool TryGetEquatorialJ2000WithMagnitude(
        in CometElements elements,
        DateTimeOffset time,
        out double raJ2000Hours,
        out double decJ2000Deg,
        out double magnitude)
    {
        if (TryGetEquatorialJ2000(elements, time, out raJ2000Hours, out decJ2000Deg, out var r, out var delta))
        {
            magnitude = PredictTotalMagnitude(elements, r, delta);
            return true;
        }

        magnitude = double.NaN;
        return false;
    }

    /// <summary>
    /// Heliocentric ecliptic-J2000 position (AU) at <paramref name="jdTt"/> by universal-variable
    /// propagation from the perihelion state. <paramref name="heliocentricDistanceAu"/> is |r|.
    /// </summary>
    private static bool TryHeliocentricEcliptic(in CometElements el, double jdTt, Span<double> helio, out double heliocentricDistanceAu)
    {
        var q = el.PerihelionDistanceAu;
        var e = el.Eccentricity;
        var dt = jdTt - el.PerihelionJdTt;

        // Perihelion state in the perifocal frame: r0 = (q, 0, 0), v0 = (0, vp, 0) with zero radial
        // velocity, so the universal Kepler equation reduces to  q*chi + e*chi^3*S(z) = k*dt  with
        // z = alpha*chi^2, alpha = 1/a = (1 - e)/q. F(chi) is strictly increasing (F' = q + e*chi^2*C(z)
        // >= q > 0 for all z), so Newton converges globally from the straight-line guess.
        var alpha = (1.0 - e) / q;
        var vp = Math.Sqrt(Mu * (1.0 + e) / q);
        var target = GaussK * dt;

        var chi = target / q;
        var converged = false;
        for (var it = 0; it < 64; it++)
        {
            var z = alpha * chi * chi;
            Stumpff(z, out var c, out var s);
            var f = q * chi + e * chi * chi * chi * s - target;
            var df = q + e * chi * chi * c;
            var dchi = f / df;
            chi -= dchi;
            if (Math.Abs(dchi) <= 1e-10 * (1.0 + Math.Abs(chi)))
            {
                converged = true;
                break;
            }
        }

        if (!converged || double.IsNaN(chi))
        {
            helio.Clear();
            heliocentricDistanceAu = double.NaN;
            return false;
        }

        var zf = alpha * chi * chi;
        Stumpff(zf, out var cf, out var sf);

        // f and g relative to the perihelion state give the perifocal position (z_pf = 0).
        var fLagrange = 1.0 - chi * chi / q * cf;
        var gLagrange = dt - chi * chi * chi / GaussK * sf;
        var xPf = fLagrange * q;
        var yPf = gLagrange * vp;
        heliocentricDistanceAu = Math.Sqrt(xPf * xPf + yPf * yPf);

        // Perifocal -> heliocentric ecliptic-J2000 via (Omega, i, omega).
        var node = el.AscendingNodeDeg * Constants.DEGREES2RADIANS;
        var inc = el.InclinationDeg * Constants.DEGREES2RADIANS;
        var argP = el.ArgumentOfPerihelionDeg * Constants.DEGREES2RADIANS;
        var cosNode = Math.Cos(node);
        var sinNode = Math.Sin(node);
        var cosInc = Math.Cos(inc);
        var sinInc = Math.Sin(inc);
        var cosArg = Math.Cos(argP);
        var sinArg = Math.Sin(argP);

        var px = cosNode * cosArg - sinNode * sinArg * cosInc;
        var py = sinNode * cosArg + cosNode * sinArg * cosInc;
        var pz = sinArg * sinInc;
        var qx = -cosNode * sinArg - sinNode * cosArg * cosInc;
        var qy = -sinNode * sinArg + cosNode * cosArg * cosInc;
        var qz = cosArg * sinInc;

        helio[0] = px * xPf + qx * yPf;
        helio[1] = py * xPf + qy * yPf;
        helio[2] = pz * xPf + qz * yPf;
        return true;
    }

    /// <summary>
    /// Stumpff functions C(z) and S(z), the conic-agnostic series that let one universal-variable
    /// formulation cover elliptic (z &gt; 0), parabolic (z = 0) and hyperbolic (z &lt; 0) orbits.
    /// </summary>
    private static void Stumpff(double z, out double c, out double s)
    {
        if (z > 1e-6)
        {
            var sz = Math.Sqrt(z);
            c = (1.0 - Math.Cos(sz)) / z;
            s = (sz - Math.Sin(sz)) / (sz * sz * sz);
        }
        else if (z < -1e-6)
        {
            var sz = Math.Sqrt(-z);
            c = (Math.Cosh(sz) - 1.0) / -z;
            s = (Math.Sinh(sz) - sz) / (sz * sz * sz);
        }
        else
        {
            c = 0.5;
            s = 1.0 / 6.0;
        }
    }
}
