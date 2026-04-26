using Shouldly;
using System;
using TianWen.Lib.Astrometry;
using Xunit;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Tests for <see cref="PolarAxisSolver"/>: synthetic axis recovery,
    /// chord-angle sanity, RA/Dec ↔ unit-vector roundtrip, and refraction-
    /// aware pole-error decomposition.
    /// </summary>
    [Collection("Astrometry")]
    public class PolarAxisSolverTests
    {
        // 1 arcsecond in radians, used as the baseline tolerance for axis recovery
        // from clean synthetic inputs (no plate-solve noise modeled here).
        private const double OneArcsec = (1.0 / 3600.0) * DEGREES2RADIANS;

        [Theory]
        // Axis = north pole; v1 on equator at RA=0; rotation = 60deg.
        [InlineData(0.0, 90.0, 0.0, 0.0, 60.0)]
        // Axis tilted 5deg from north pole (still near-pole); v1 near zenith.
        [InlineData(0.0, 85.0, 6.0, 70.0, 60.0)]
        // Axis 30deg from pole (gross misalignment); larger Delta to keep geometry well-conditioned.
        [InlineData(45.0, 60.0, 18.0, 45.0, 90.0)]
        // South-hemisphere axis; rotation 45deg.
        [InlineData(180.0, -88.0, 12.0, -75.0, 45.0)]
        public void GivenSyntheticRotationWhenRecoverAxisThenAxisMatchesWithinArcsec(
            double axisRaDeg, double axisDecDeg,
            double v1RaHours, double v1DecDeg,
            double deltaDeg)
        {
            // Build axis and v1 from spherical inputs.
            var axisTrue = PolarAxisSolver.RaDecToUnitVec(axisRaDeg / 15.0, axisDecDeg);
            var v1 = PolarAxisSolver.RaDecToUnitVec(v1RaHours, v1DecDeg);
            var deltaRad = deltaDeg * DEGREES2RADIANS;
            var v2 = PolarAxisSolver.Rotate(v1, axisTrue, deltaRad);

            var ok = PolarAxisSolver.TryRecoverAxis(v1, v2, deltaRad, out var axisRecovered, out var coneHalfAngleRad);

            ok.ShouldBeTrue();
            // Angular distance between recovered axis and ground truth.
            var dot = Math.Clamp(Vec3.Dot(axisRecovered, axisTrue), -1.0, 1.0);
            var angularError = Math.Acos(dot);
            angularError.ShouldBeLessThan(OneArcsec, $"axis recovered to {angularError * RAD2SEC:F3} arcsec");

            // Cone half-angle should equal the input-vector-to-axis distance.
            var expectedCone = Math.Acos(Math.Clamp(Vec3.Dot(v1, axisTrue), -1.0, 1.0));
            coneHalfAngleRad.ShouldBe(expectedCone, OneArcsec);
        }

        [Fact]
        public void GivenAntipodalVectorsWhenRecoverAxisThenReturnsFalse()
        {
            var v1 = PolarAxisSolver.RaDecToUnitVec(0, 0);
            var v2 = PolarAxisSolver.RaDecToUnitVec(12, 0); // RA=12h, Dec=0 -> antipodal

            var ok = PolarAxisSolver.TryRecoverAxis(v1, v2, Math.PI, out _, out _);
            ok.ShouldBeFalse();
        }

        [Fact]
        public void GivenIdenticalVectorsWhenRecoverAxisThenReturnsFalse()
        {
            var v1 = PolarAxisSolver.RaDecToUnitVec(3.0, 45.0);
            var ok = PolarAxisSolver.TryRecoverAxis(v1, v1, 60.0 * DEGREES2RADIANS, out _, out _);
            ok.ShouldBeFalse();
        }

        [Fact]
        public void GivenChordExceedsRotationWhenRecoverAxisThenReturnsFalse()
        {
            // v1 and v2 separated by 90 degrees, but commanded delta only 30 degrees:
            // chord arc = 2 * sin(theta) * sin(delta/2) <= 2 sin(15deg) < 90deg, impossible.
            var v1 = PolarAxisSolver.RaDecToUnitVec(0, 0);
            var v2 = PolarAxisSolver.RaDecToUnitVec(6, 0); // 90 deg from v1
            var ok = PolarAxisSolver.TryRecoverAxis(v1, v2, 30.0 * DEGREES2RADIANS, out _, out _);
            ok.ShouldBeFalse();
        }

        [Theory]
        [InlineData(60.0, 30.0)]
        [InlineData(45.0, 5.0)]
        [InlineData(90.0, 75.0)]
        public void GivenSyntheticPairWhenChordAngleAndPredictedAgreeWithinArcsec(double deltaDeg, double coneDeg)
        {
            // Build v1, v2 separated by the chord arc that delta + cone produce.
            var axis = PolarAxisSolver.RaDecToUnitVec(0, 90); // north pole
            var v1 = PolarAxisSolver.RaDecToUnitVec(0, 90.0 - coneDeg);
            var v2 = PolarAxisSolver.Rotate(v1, axis, deltaDeg * DEGREES2RADIANS);

            var observed = PolarAxisSolver.ChordAngle(v1, v2);
            var predicted = PolarAxisSolver.PredictedChordAngle(deltaDeg * DEGREES2RADIANS, coneDeg * DEGREES2RADIANS);

            Math.Abs(observed - predicted).ShouldBeLessThan(OneArcsec);
        }

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(6.0, 45.0)]
        [InlineData(23.5, -89.9)]
        [InlineData(12.0, 0.0)]
        public void GivenRaDecWhenRoundTripThroughUnitVecThenValuesMatch(double raHours, double decDeg)
        {
            var v = PolarAxisSolver.RaDecToUnitVec(raHours, decDeg);
            var (raOut, decOut) = PolarAxisSolver.UnitVecToRaDec(v);

            decOut.ShouldBe(decDeg, 1e-9);
            // RA wraps; ensure both are in [0, 24).
            var raExpected = (raHours % 24 + 24) % 24;
            raOut.ShouldBe(raExpected, 1e-9);
        }

        // Fixed epoch used by all decomposition tests so they don't drift over time.
        private static readonly DateTimeOffset FixedUtc = new(2023, 2, 25, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void GivenAxisAtTruePoleWhenDecomposeThenAltErrorMatchesRefractionAtSiteLat()
        {
            // Site at lat=45deg, standard atmosphere. The true celestial pole sits at
            // geometric altitude=45deg; the apparent pole is lifted ~56 arcsec by refraction.
            // If the mount's RA axis points exactly at the J2000 north pole (Z=1), then
            // axisAlt = 45deg geometric, poleAlt_apparent ~= 45deg + refraction.
            // Therefore altError = axisAlt - poleAlt = -refraction (negative -> axis sits
            // below the apparent pole).
            const double lat = 45.0;
            var axisJ2000 = new Vec3(0, 0, 1); // J2000 north pole

            var (azErrRad, altErrRad) = PolarAxisSolver.DecomposeAxisError(
                axisJ2000, Hemisphere.North,
                siteLatDeg: lat, siteLonDeg: 0.0, siteElevM: 0.0,
                sitePressureHPa: 1010.0, siteTempC: 10.0, utc: FixedUtc);

            // Bennett's formula at h=45: R ~ 56-60 arcsec. AltError should be near that, negative.
            var altErrArcsec = altErrRad * RAD2SEC;
            altErrArcsec.ShouldBeInRange(-90.0, -30.0, $"altErr was {altErrArcsec:F1} arcsec");

            // Az error should be tiny (true pole and J2000 pole coincide modulo precession).
            var azErrArcmin = Math.Abs(azErrRad * RAD2SEC / 60.0);
            azErrArcmin.ShouldBeLessThan(5.0, $"azErr was {azErrArcmin:F2} arcmin (precession only)");
        }

        [Fact]
        public void GivenAxisOffsetByOneDegreeWhenDecomposeThenSkyDistanceApprox60Arcmin()
        {
            // Axis tilted 1 degree from the pole -> sky-projected angular error ~60 arcmin.
            // Note: AzError is reported as a longitude angle, not a sky arc, so converting
            // to actual sky separation requires multiplying by cos(altitude). At lat=45deg,
            // axisAlt ~= 45deg, so cos(45deg) ~= 0.707 reduces the az contribution.
            const double lat = 45.0;
            var axisJ2000 = PolarAxisSolver.RaDecToUnitVec(6.0, 89.0); // 1 deg from pole

            var (azErrRad, altErrRad) = PolarAxisSolver.DecomposeAxisError(
                axisJ2000, Hemisphere.North,
                siteLatDeg: lat, siteLonDeg: 0.0, siteElevM: 0.0,
                sitePressureHPa: 1010.0, siteTempC: 10.0, utc: FixedUtc);

            // Sky-distance approximation: dAlt direct, dAz scaled by cos(altitude near pole = lat).
            var effectiveAzRad = azErrRad * Math.Cos(lat * DEGREES2RADIANS);
            var skyDistArcmin = Math.Sqrt(effectiveAzRad * effectiveAzRad + altErrRad * altErrRad) * RAD2DEG * 60.0;
            // 60' nominal +/- a few ' for refraction & precession.
            skyDistArcmin.ShouldBeInRange(57.0, 63.0, $"sky distance was {skyDistArcmin:F2} arcmin");
        }

        [Fact]
        public void GivenSouthHemisphereWhenDecomposeAxisAtSouthPoleThenAltErrorMatchesRefraction()
        {
            const double lat = -35.0; // Australian observatory
            var axisJ2000 = new Vec3(0, 0, -1); // J2000 south pole

            var (_, altErrRad) = PolarAxisSolver.DecomposeAxisError(
                axisJ2000, Hemisphere.South,
                siteLatDeg: lat, siteLonDeg: 145.0, siteElevM: 100.0,
                sitePressureHPa: 1010.0, siteTempC: 10.0, utc: FixedUtc);

            var altErrArcsec = altErrRad * RAD2SEC;
            // Refraction at altitude=35deg is ~80 arcsec; altError should be negative.
            altErrArcsec.ShouldBeInRange(-130.0, -40.0, $"altErr was {altErrArcsec:F1} arcsec");
        }
    }
}
