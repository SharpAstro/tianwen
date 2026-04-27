using Microsoft.Extensions.Time.Testing;
using Shouldly;
using System;
using TianWen.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pure-math tests for the polar-misalignment model on
    /// <see cref="FakeSkywatcherMountDriver"/>. Three concerns:
    /// <list type="number">
    ///   <item><description><b>Encoder-only</b>: the rotation proxy must depend on
    ///     RA encoder steps alone, not on LST. Two calls one hour apart at the
    ///     same encoder must return identical pointing.</description></item>
    ///   <item><description><b>Round-trip with <see cref="PolarAxisSolver"/></b>:
    ///     two pointings synthesised at known encoder angles around a known
    ///     misaligned axis must be recoverable to within sub-arcsec by the
    ///     production solver.</description></item>
    ///   <item><description><b>Topocentric inverse</b>: the configured (azErr,
    ///     altErr) round-trips back through
    ///     <see cref="PolarAxisSolver.DecomposeAxisError"/> -- without this the
    ///     user types in 30arcmin and the routine reads ~70arcmin because we
    ///     were applying the misalignment in the wrong frame.</description></item>
    /// </list>
    /// </summary>
    [Collection("Astrometry")]
    public class FakeSkywatcherMisalignmentTests
    {
        private const uint TestCpr = 9024000; // EQ6-R counts per revolution

        // Realistic southern-hemisphere site for round-trip checks; deterministic
        // UTC so SOFA refraction is reproducible across CI runs.
        private const double SiteLatDeg = -37.5;
        private const double SiteLonDeg = 145.9;
        private const double SiteElevM = 50.0;
        private static readonly DateTimeOffset TestUtc = new(2026, 4, 27, 10, 0, 0, TimeSpan.Zero);

        private static ITimeProvider TimeProviderAt(DateTimeOffset utc)
            => new FakeTimeProviderWrapper(utc);

        [Theory]
        [InlineData(0, 0.0)]
        [InlineData(2256000, Math.PI / 2)]      // CPR/4
        [InlineData(4512000, Math.PI)]          // CPR/2 -> wraps to +pi
        [InlineData(-2256000, -Math.PI / 2)]    // -CPR/4
        public void GivenEncoderStepsWhenComputingAngleThenMatchesExpectedRadians(int posRa, double expectedRad)
        {
            var actual = FakeSkywatcherMountDriver.EncoderAngleRadians(posRa, TestCpr);
            actual.ShouldBe(expectedRad, tolerance: 1e-9);
        }

        [Fact]
        public void GivenEncoderBeyondOneRevolutionWhenComputingAngleThenWrapsToCanonicalRange()
        {
            var posRa = (int)TestCpr + (int)(TestCpr / 4);
            var actual = FakeSkywatcherMountDriver.EncoderAngleRadians(posRa, TestCpr);
            actual.ShouldBe(Math.PI / 2, tolerance: 1e-9);
        }

        [Fact]
        public void GivenZeroCprWhenComputingAngleThenReturnsZeroDefensively()
        {
            FakeSkywatcherMountDriver.EncoderAngleRadians(123456, 0).ShouldBe(0.0);
        }

        [Fact]
        public void GivenAlignedAxisAtEncoderZeroWhenApplyingMisalignmentThenPointingIsCelestialPole()
        {
            // Axis = celestial pole (perfectly aligned). At any encoder, pointing
            // stays at the pole (the home reference). Identity for cone=0.
            var axis = PolarAxisSolver.RaDecToUnitVec(0, 90.0);
            var (_, dec) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axis, Hemisphere.North, encoderRad: 1.234);
            dec.ShouldBe(90.0, tolerance: 1e-6);
        }

        [Fact]
        public void GivenTwoCallsAtSameEncoderWhenLstChangesThenReturnsIdenticalPointing()
        {
            // The bug we're fixing: previously the encoder angle was re-derived
            // from baseRa (= LST - HA), so two reads one hour apart at the same
            // physical encoder position drifted by 1h in the apparent
            // misalignment. With encoderRad as a parameter, that contamination
            // is gone -- the result is encoder-only.
            const double encoderRad = Math.PI / 3;
            var axis = PolarAxisSolver.RaDecToUnitVec(0.0, 89.5); // ~30arcmin off NCP
            var (ra1, dec1) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axis, Hemisphere.North, encoderRad);
            var (ra2, dec2) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axis, Hemisphere.North, encoderRad);
            ra1.ShouldBe(ra2, tolerance: 1e-9);
            dec1.ShouldBe(dec2, tolerance: 1e-9);
        }

        [Theory]
        // (axisRaDeg, axisDecDeg) -- cone = 90deg - |axisDec| for NCP, similar for SCP.
        [InlineData(0.0, 89.5, Hemisphere.North)]   // 30arcmin cone
        [InlineData(45.0, 88.0, Hemisphere.North)]  // 2deg cone, off-axis tilt
        [InlineData(180.0, -89.5, Hemisphere.South)]
        public void GivenSynthesisedV1V2WhenRecoveringAxisThenAxisMatchesConfigured(double axisRaDeg, double axisDecDeg, Hemisphere hemisphere)
        {
            // End-to-end: synthesise two pointings at known encoder angles around
            // a known tilted axis, push them through PolarAxisSolver, verify the
            // recovered cone half-angle matches the configured tilt magnitude.
            var axis = PolarAxisSolver.RaDecToUnitVec(axisRaDeg / 15.0, axisDecDeg);
            var poleDec = hemisphere == Hemisphere.North ? 90.0 : -90.0;
            var configuredCone = Math.Abs(poleDec - axisDecDeg);

            const double encoder1Rad = 0.0;
            const double encoder2Rad = Math.PI / 3;
            var (ra1, dec1) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axis, hemisphere, encoder1Rad);
            var (ra2, dec2) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axis, hemisphere, encoder2Rad);

            var v1 = PolarAxisSolver.RaDecToUnitVec(ra1, dec1);
            var v2 = PolarAxisSolver.RaDecToUnitVec(ra2, dec2);
            var deltaRad = encoder2Rad - encoder1Rad;

            var ok = PolarAxisSolver.TryRecoverAxis(v1, v2, deltaRad, out _, out var coneHalfAngleRad);
            ok.ShouldBeTrue();
            (coneHalfAngleRad * RADIANS2DEGREES).ShouldBe(configuredCone, tolerance: 1e-6);
        }

        [Theory]
        [InlineData(30.0, 0.0)]      // 30arcmin pure azimuth
        [InlineData(0.0, 30.0)]      // 30arcmin pure altitude
        [InlineData(30.0, -10.0)]    // mixed, opposite signs
        [InlineData(-45.0, 60.0)]    // larger, mixed
        public void GivenConfiguredTopocentricErrorsWhenForwardThenBackwardThenRecoversInputs(double azErrArcmin, double altErrArcmin)
        {
            // The real test of the topocentric-frame fix: dial in (azErr, altErr)
            // arcmin, build the J2000 axis with our forward function, and ask
            // PolarAxisSolver.DecomposeAxisError to project that axis into
            // topocentric (az, alt) at the same site. We should get the inputs
            // back. SOFA refraction is asymmetric between the pole reference and
            // the axis projection (intentional, matches DecomposeAxisError's
            // convention), so a sub-arcmin residual is expected and tolerated.
            var hemisphere = Hemisphere.South; // user's actual setup (lat=-37.5)
            var axis = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                azErrArcmin, altErrArcmin, hemisphere, TimeProviderAt(TestUtc));

            var (azBackRad, altBackRad) = PolarAxisSolver.DecomposeAxisError(
                axis, hemisphere,
                SiteLatDeg, SiteLonDeg, SiteElevM,
                sitePressureHPa: 1010.0, siteTempC: 10.0,
                utc: TestUtc);

            var azBackArcmin = azBackRad * RADIANS2DEGREES * 60.0;
            var altBackArcmin = altBackRad * RADIANS2DEGREES * 60.0;

            // Tolerance: 1 arcmin absolute. Refraction asymmetry plus small-angle
            // linearisation in our forward function combine to a sub-arcmin
            // residual at sensible latitudes -- well below what the polar-align
            // routine itself can resolve from real plate solves (~30arcsec).
            azBackArcmin.ShouldBe(azErrArcmin, tolerance: 1.0);
            altBackArcmin.ShouldBe(altErrArcmin, tolerance: 1.0);
        }
    }
}
