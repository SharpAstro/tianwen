using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices.Skywatcher;

namespace TianWen.Lib.Devices.Fake;

internal class FakeSkywatcherMountDriver(FakeDevice device, IServiceProvider serviceProvider) : SkywatcherMountDriverBase<FakeDevice>(device, serviceProvider)
{
    /// <summary>
    /// Current azimuth misalignment in arcminutes, in the topocentric (horizon)
    /// frame at the site's latitude / longitude. Initialised from the
    /// <c>polarMisalignmentAzArcmin</c> URI query key, then mutable via
    /// <see cref="NudgeMisalignment"/> so the live polar-align panel can
    /// simulate twisting az/alt knobs during refining. The polar-alignment
    /// routine's <see cref="PolarAxisSolver.DecomposeAxisError"/> reports
    /// az/alt errors in this same frame, so the configured value round-trips
    /// back through the solver -- "I dialled in 30', the routine reads 30'".
    /// </summary>
    private double _azErrArcmin = ParseDoubleQuery(device, DeviceQueryKey.PolarMisalignmentAzArcmin.Key, defaultValue: 30.0);

    /// <summary>
    /// Current altitude misalignment in arcminutes, topocentric. Sign convention
    /// matches <see cref="PolarAxisSolver.DecomposeAxisError"/>: positive = axis
    /// above the apparent pole. See <see cref="_azErrArcmin"/> for mutability
    /// rationale.
    /// </summary>
    private double _altErrArcmin = ParseDoubleQuery(device, DeviceQueryKey.PolarMisalignmentAltArcmin.Key, defaultValue: -10.0);

    /// <summary>
    /// Apply a delta to the configured topocentric (az, alt) misalignment in
    /// arcminutes. Used by the polar-align refining UI to simulate the user
    /// twisting alt/az knobs while the routine watches the field drift across
    /// the pole. The next <see cref="GetRightAscensionAsync"/> /
    /// <see cref="GetDeclinationAsync"/> call picks up the new values via the
    /// existing per-call SOFA recompute -- no driver reconnect needed.
    /// </summary>
    internal void NudgeMisalignment(double dAzArcmin, double dAltArcmin)
    {
        _azErrArcmin += dAzArcmin;
        _altErrArcmin += dAltArcmin;
    }

    /// <summary>Current (az, alt) misalignment in arcminutes -- read-only snapshot.</summary>
    internal (double AzArcmin, double AltArcmin) CurrentMisalignment => (_azErrArcmin, _altErrArcmin);

    /// <summary>
    /// True when the configured (az, alt) misalignment is large enough to model;
    /// otherwise the override no-ops and the driver behaves as a perfectly
    /// polar-aligned mount. Threshold is the smallest value the polar-alignment
    /// routine could meaningfully resolve.
    /// </summary>
    private bool MisalignmentEnabled => Math.Abs(_azErrArcmin) > 1e-3 || Math.Abs(_altErrArcmin) > 1e-3;

    /// <inheritdoc/>
    /// <remarks>
    /// Applies a topocentric polar-misalignment transform on top of the base
    /// driver's perfect-alignment RA so the synthesised fake-camera frames
    /// trace a small circle around an offset axis as the RA encoder rotates.
    /// The misaligned axis is constructed in the same topocentric frame
    /// <see cref="PolarAxisSolver.DecomposeAxisError"/> projects into, so the
    /// configured arcmin values round-trip cleanly through the polar-align
    /// routine -- "I dialled in 30', the routine reads 30'".
    /// </remarks>
    public override async ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken)
    {
        var baseRa = await base.GetRightAscensionAsync(cancellationToken);
        if (!MisalignmentEnabled || CprRa == 0)
        {
            return baseRa;
        }
        var baseDec = await base.GetDeclinationAsync(cancellationToken);
        var (raMis, _) = await ComputeMisalignedPointingAsync(baseRa, baseDec, cancellationToken);
        return raMis;
    }

    /// <inheritdoc/>
    public override async ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken)
    {
        var baseDec = await base.GetDeclinationAsync(cancellationToken);
        if (!MisalignmentEnabled || CprRa == 0)
        {
            return baseDec;
        }
        var baseRa = await base.GetRightAscensionAsync(cancellationToken);
        var (_, decMis) = await ComputeMisalignedPointingAsync(baseRa, baseDec, cancellationToken);
        return decMis;
    }

    /// <summary>
    /// Read site parameters, build the misaligned axis in J2000, and project
    /// the perfectly-aligned home pointing through the encoder angle around
    /// that axis. Pulled out of both overrides so the two-call sequence (RA
    /// then Dec) doesn't duplicate the per-call SOFA work twice.
    /// </summary>
    private async ValueTask<(double Ra, double Dec)> ComputeMisalignedPointingAsync(
        double baseRa, double baseDec, CancellationToken ct)
    {
        var siteLatDeg = await GetSiteLatitudeAsync(ct);
        var siteLonDeg = await GetSiteLongitudeAsync(ct);
        var siteElevM = await GetSiteElevationAsync(ct);
        var hemisphere = baseDec >= 0 ? Hemisphere.North : Hemisphere.South;
        var encoderRad = EncoderAngleRadians(PosRa, CprRa);
        var utc = TimeProvider.GetUtcNow();
        var axis = TopocentricMisalignmentToJ2000Axis(
            siteLatDeg, siteLonDeg, siteElevM, utc,
            _azErrArcmin, _altErrArcmin, hemisphere, TimeProvider);
        return ApplyPolarMisalignment(axis, hemisphere, encoderRad);
    }

    /// <summary>
    /// Convert RA encoder steps to the equivalent rotation angle about the
    /// polar axis, in radians. Fully encoder-derived -- no LST term -- so a
    /// stationary axis reports the same angle from one second to the next
    /// even as sidereal time advances. This is the key correctness pivot
    /// over the previous <c>baseRa</c>-based proxy: <c>StepsToRa</c>
    /// computes <c>RA = LST - HA(steps)</c>, which drifted the angle even
    /// when the encoder didn't move and inflated the observed misalignment.
    /// </summary>
    /// <param name="posRa">Encoder steps from home. May be negative.</param>
    /// <param name="cprRa">Steps per full revolution. Must be &gt; 0.</param>
    /// <returns>Rotation angle in radians, wrapped to [-pi, pi).</returns>
    internal static double EncoderAngleRadians(int posRa, uint cprRa)
    {
        if (cprRa == 0) return 0.0;
        var rev = (double)posRa / cprRa;
        var rad = rev * 2.0 * Math.PI;
        // Wrap to (-pi, pi] so the Rodrigues rotation behaves identically at
        // wrap-around boundaries -- otherwise a 359deg vs -1deg encoder gives
        // numerically different small-circle positions over many revolutions.
        rad %= 2.0 * Math.PI;
        if (rad > Math.PI) rad -= 2.0 * Math.PI;
        else if (rad <= -Math.PI) rad += 2.0 * Math.PI;
        return rad;
    }

    /// <summary>
    /// Convert a topocentric (azErr, altErr) misalignment offset into the
    /// J2000 unit vector of the actual mount RA-axis. Inverts the geometry
    /// of <see cref="PolarAxisSolver.DecomposeAxisError"/>: that function
    /// asks "given a J2000 axis, where is it relative to the apparent pole
    /// in topocentric az/alt?" -- here we ask the reverse, "given a desired
    /// (azErr, altErr) offset from the apparent pole, what J2000 axis vector
    /// produces that?". Round-tripping a value through this function and
    /// then through <c>DecomposeAxisError</c> recovers the input within the
    /// small-angle linearisation noise of SOFA's refraction model.
    /// </summary>
    /// <remarks>
    /// We hold pressure / temperature constant (standard atmosphere) for the
    /// pole-position lookup so the test is deterministic across machines.
    /// The axis itself bypasses refraction (mechanical orientation), which
    /// matches <c>DecomposeAxisError</c>'s convention; this means the round
    /// trip has a small residual (sub-arcsec at sensible site latitudes)
    /// from the asymmetric refraction handling -- which is intentional and
    /// not a bug.
    /// </remarks>
    internal static Vec3 TopocentricMisalignmentToJ2000Axis(
        double siteLatDeg, double siteLonDeg, double siteElevM,
        DateTimeOffset utc,
        double azErrArcmin, double altErrArcmin,
        Hemisphere hemisphere,
        ITimeProvider timeProvider)
    {
        // Apparent pole in topocentric (refracted), matching the pole branch
        // of DecomposeAxisError. Use a standard atmosphere so the test is
        // reproducible -- real users aren't running this during tests.
        const double sitePressureHPa = 1010.0;
        const double siteTempC = 10.0;
        utc.ToSOFAUtcJd(out var utc1, out var utc2);
        var poleDecDeg = hemisphere == Hemisphere.North ? 90.0 : -90.0;
        var (_, _, poleAzDeg, poleAltDeg) = SOFAHelpers.J2000ToTopo(
            ra: 0.0, dec: poleDecDeg,
            utc1: utc1, utc2: utc2,
            siteLat: siteLatDeg, siteLong: siteLonDeg, siteElevation: siteElevM,
            sitePressure: sitePressureHPa, siteTemp: siteTempC);

        // Misaligned axis topocentric = pole + offset. arcmin -> degrees.
        var axisAzDeg = poleAzDeg + azErrArcmin / 60.0;
        var axisAltDeg = poleAltDeg + altErrArcmin / 60.0;

        // Convert axis topocentric -> J2000 with refraction OFF (axis is a
        // mechanical orientation, not a sky observation). Transform's
        // refraction is gated on pressure/temp being non-NaN, so we leave
        // them at their default NaN to skip refraction on this path.
        var transform = new Transform(timeProvider);
        transform.JulianDateUTC = utc1 + utc2;
        transform.SiteLatitude = siteLatDeg;
        transform.SiteLongitude = siteLonDeg;
        transform.SiteElevation = siteElevM;
        transform.SetAzimuthElevation(axisAzDeg, axisAltDeg);
        return PolarAxisSolver.RaDecToUnitVec(transform.RAJ2000, transform.DecJ2000);
    }

    /// <summary>
    /// Project the home (celestial-pole) pointing through an encoder rotation
    /// of <paramref name="encoderRad"/> about the misaligned axis vector.
    /// Pure rotation math (Rodrigues' formula) -- the topocentric-to-J2000
    /// conversion happens in <see cref="TopocentricMisalignmentToJ2000Axis"/>.
    /// Decoupling these two steps lets the unit tests exercise each on its
    /// own.
    /// </summary>
    /// <returns>Misaligned (RA, Dec). RA in [0, 24) hours; Dec in [-90, 90] degrees.</returns>
    internal static (double Ra, double Dec) ApplyPolarMisalignment(
        Vec3 misalignedAxisJ2000,
        Hemisphere hemisphere,
        double encoderRad)
    {
        var poleZ = hemisphere == Hemisphere.North ? 1.0 : -1.0;

        // Tilted axis components.
        var kx = misalignedAxisJ2000.X;
        var ky = misalignedAxisJ2000.Y;
        var kz = misalignedAxisJ2000.Z;

        // Home pointing = celestial pole of the user's hemisphere. At home
        // the scope physically lies along its OWN polar axis, but its OTA
        // points wherever the user parked it; for the polar-align routine to
        // work the user is expected to start near the pole, so this is the
        // right home reference for the synthesiser.
        var vx = 0.0;
        var vy = 0.0;
        var vz = poleZ;

        var cosT = Math.Cos(encoderRad);
        var sinT = Math.Sin(encoderRad);

        // Rodrigues: R(k, theta) . v = v.cos(theta) + (k cross v).sin(theta) + k.(k . v).(1 - cos(theta)).
        var kDotV = kx * vx + ky * vy + kz * vz;
        var crossX = ky * vz - kz * vy;
        var crossY = kz * vx - kx * vz;
        var crossZ = kx * vy - ky * vx;
        var oneMinusCos = 1.0 - cosT;
        var rx = vx * cosT + crossX * sinT + kx * kDotV * oneMinusCos;
        var ry = vy * cosT + crossY * sinT + ky * kDotV * oneMinusCos;
        var rz = vz * cosT + crossZ * sinT + kz * kDotV * oneMinusCos;

        // Decompose back to (RA, Dec). Clamp z to avoid Asin domain errors
        // from accumulated float noise at the pole.
        var dec = Math.Asin(Math.Clamp(rz, -1.0, 1.0)) * 180.0 / Math.PI;
        var raDeg = Math.Atan2(ry, rx) * 180.0 / Math.PI;
        var ra = raDeg / 15.0;
        if (ra < 0.0) ra += 24.0;
        return (ra, dec);
    }

    /// <summary>
    /// Pull a double-valued URI query key off the device, falling back to
    /// <paramref name="defaultValue"/> when absent or unparseable. Uses
    /// invariant culture so test-author URIs stay portable across machines.
    /// </summary>
    private static double ParseDoubleQuery(FakeDevice device, string key, double defaultValue)
    {
        var raw = device.Query[key];
        if (string.IsNullOrEmpty(raw))
        {
            return defaultValue;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : defaultValue;
    }
}
