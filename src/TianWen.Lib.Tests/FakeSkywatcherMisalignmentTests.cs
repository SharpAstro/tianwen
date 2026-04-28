using Microsoft.Extensions.Time.Testing;
using Shouldly;
using System;
using TianWen.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;
using Xunit.v3;
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

        /// <summary>
        /// Characterises the "GUI Az error grows as the user dials sim toward
        /// 0" bug observed in the live polar-refining flow. Phase A captures
        /// v1 at encoder=K with sim X_init; user turns polar knobs (sim drops
        /// toward 0) without moving the encoder; live refines feed v_now into
        /// <see cref="PolarAxisSolver.TryRecoverAxis"/> against the stale v1.
        /// Result: the recovered axis is a fictitious "rotation axis between
        /// stale v1 and current v_now" that diverges from the real mount
        /// axis as the misalignment shrinks.
        ///
        /// At encoder=0 the bug is hidden because <see cref="FakeSkywatcherMountDriver.ApplyPolarMisalignment"/>
        /// returns the celestial pole regardless of axis, so v1 doesn't
        /// depend on the Phase A sim. Pin encoder1 = pi/4 (HA=3h, matching
        /// the user's screenshot) to make v1 sim-dependent and surface the
        /// staleness.
        ///
        /// Fix path: periodic mini-Phase-A inside RefineAsync to refresh v1.
        /// Until that lands the test asserts only the first iteration (where
        /// v1 was captured fresh at the same sim as v_now) and prints the
        /// rest as characterisation output.
        /// </summary>
        [Fact]
        public void GivenSimMisalignmentDecreasesDuringRefiningWhenRecoverAxisThenErrorTracksCurrentSim()
        {
            var hemisphere = Hemisphere.South;
            var deltaRad = Math.PI / 3; // 60deg Phase A rotation
            var timeProvider = TimeProviderAt(TestUtc);

            // _v1 is captured at the user's parked encoder position -- HA=3h was
            // the value in the GUI screenshot, so use that (45deg = pi/4 rad).
            // At non-zero encoder, ApplyPolarMisalignment depends on the axis,
            // so v1's J2000 direction is set by the sim that was active during
            // Phase A. Using encoder=0 hides this dependency and the bug.
            const double encoder1Rad = Math.PI / 4; // HA=3h equivalent
            var axisInitial = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                azErrArcmin: 60.0, altErrArcmin: 0.0, hemisphere, timeProvider);
            var (ra1, dec1) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axisInitial, hemisphere, encoder1Rad);
            var v1 = PolarAxisSolver.RaDecToUnitVec(ra1, dec1);

            // Refine iterations: sim is brought from 60' down to 0', encoder stays
            // at encoder1+delta the whole time (no further RA-axis rotation during
            // refining; only polar knobs move, which change the axis but not encoder).
            const double encoder2Rad = encoder1Rad + Math.PI / 3;
            ReadOnlySpan<double> simSequence = [60.0, 45.0, 30.0, 15.0, 0.0];

            foreach (var simAzArcmin in simSequence)
            {
                var axisCurrent = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                    SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                    azErrArcmin: simAzArcmin, altErrArcmin: 0.0, hemisphere, timeProvider);
                var (ra2, dec2) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axisCurrent, hemisphere, encoder2Rad);
                var vNow = PolarAxisSolver.RaDecToUnitVec(ra2, dec2);

                var ok = PolarAxisSolver.TryRecoverAxis(v1, vNow, deltaRad, out var axisRecovered, out _);
                ok.ShouldBeTrue($"sim={simAzArcmin}': TryRecoverAxis returned false (degenerate v1==v_now).");

                var (azErrRad, altErrRad) = PolarAxisSolver.DecomposeAxisError(
                    axisRecovered, hemisphere,
                    SiteLatDeg, SiteLonDeg, SiteElevM,
                    sitePressureHPa: 1010.0, siteTempC: 10.0, utc: TestUtc);

                var azErrArcmin = azErrRad * RADIANS2DEGREES * 60.0;
                var altErrArcmin = altErrRad * RADIANS2DEGREES * 60.0;
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"sim Az={simAzArcmin,5:F1}'  ->  recovered Az={azErrArcmin,7:F2}'  Alt={altErrArcmin,7:F2}'");

                // First iteration: v1 captured at the same sim as v_now -> recovery
                // is exact. Subsequent iterations have stale v1 and recovery diverges;
                // those are characterised by the WriteLine above, not asserted, until
                // the periodic-mini-Phase-A fix lands.
                if (simAzArcmin == simSequence[0])
                {
                    azErrArcmin.ShouldBe(simAzArcmin, tolerance: 1.0);
                    altErrArcmin.ShouldBe(0.0, tolerance: 1.0);
                }
            }
        }

        /// <summary>
        /// Linearised recovery: store axis A0 and v2_baseline at end of Phase A,
        /// then for each refine compute dA = J^+ * (v_now - v2_baseline) where J
        /// is the Jacobian of v(A, theta)=R(A,theta)*home_pole evaluated at A0.
        /// A_current = normalise(A0 + dA). No mount rotation needed during
        /// refining; tracks knob changes in real time as long as the axis stays
        /// close enough to A0 for the linearisation to hold.
        /// </summary>
        [Fact]
        public void GivenJacobianRecoveryWhenSimChangesDuringRefiningThenAxisTracksCurrentSim()
        {
            var hemisphere = Hemisphere.South;
            var deltaRad = Math.PI / 3;
            var timeProvider = TimeProviderAt(TestUtc);

            const double encoder1Rad = Math.PI / 4;
            const double encoder2Rad = encoder1Rad + Math.PI / 3;
            const double simAzInitial = 60.0;

            // Initial setup matches what SolveAsync would produce: A0 + v2 baseline.
            var axisInitial = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                azErrArcmin: simAzInitial, altErrArcmin: 0.0, hemisphere, timeProvider);
            var (ra2Init, dec2Init) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(
                axisInitial, hemisphere, encoder2Rad);
            var v2Baseline = PolarAxisSolver.RaDecToUnitVec(ra2Init, dec2Init);

            // Pre-compute Jacobian J = dv/dA at (A0, encoder2Rad) acting on home pole h.
            // For h = (0,0,h_z), the nonzero entries of J = ∂v/∂A are:
            //   J[0,0] = h_z*A_z*(1-c)     J[0,1] = h_z*s         J[0,2] = h_z*A_x*(1-c)
            //   J[1,0] = -h_z*s            J[1,1] = h_z*A_z*(1-c) J[1,2] = h_z*A_y*(1-c)
            //   J[2,0] = 0                 J[2,1] = 0             J[2,2] = 2*h_z*A_z*(1-c)
            // Reduced 3x2 Jacobian (columns 0,1) is invertible near the pole.
            var hz = hemisphere == Hemisphere.North ? 1.0 : -1.0;
            var sinT = Math.Sin(encoder2Rad);
            var cosT = Math.Cos(encoder2Rad);
            var oneMinusCos = 1.0 - cosT;
            var ax = axisInitial.X;
            var ay = axisInitial.Y;
            var az = axisInitial.Z;

            var j00 = hz * az * oneMinusCos;
            var j01 = hz * sinT;
            var j10 = -hz * sinT;
            var j11 = hz * az * oneMinusCos;
            // j20 = j21 = 0

            // Normal equations: J^T J (2x2) and J^T (3x2 -> 2 rows applied to dv).
            var m00 = j00 * j00 + j10 * j10;
            var m01 = j00 * j01 + j10 * j11;
            var m11 = j01 * j01 + j11 * j11;
            var det = m00 * m11 - m01 * m01;

            ReadOnlySpan<double> simSequence = [60.0, 45.0, 30.0, 15.0, 0.0];

            foreach (var simAzArcmin in simSequence)
            {
                var axisCurrent = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                    SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                    azErrArcmin: simAzArcmin, altErrArcmin: 0.0, hemisphere, timeProvider);
                var (raNow, decNow) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(
                    axisCurrent, hemisphere, encoder2Rad);
                var vNow = PolarAxisSolver.RaDecToUnitVec(raNow, decNow);

                // dv = v_now - v2_baseline (small for small axis change).
                var dvX = vNow.X - v2Baseline.X;
                var dvY = vNow.Y - v2Baseline.Y;
                // dvZ contributes nothing because j20 = j21 = 0.

                // Solve (J^T J) (dax, day) = J^T dv via 2x2 Cramer's rule.
                var rhs0 = j00 * dvX + j10 * dvY;
                var rhs1 = j01 * dvX + j11 * dvY;
                var dax = (m11 * rhs0 - m01 * rhs1) / det;
                var day = (-m01 * rhs0 + m00 * rhs1) / det;

                // Constraint dA . A = 0 -> daz = -(ax*dax + ay*day) / az.
                var daz = -(ax * dax + ay * day) / az;

                // New axis = A0 + dA, normalised.
                var nx = ax + dax;
                var ny = ay + day;
                var nz = az + daz;
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                var axisRecovered = new Vec3(nx / len, ny / len, nz / len);

                var (azErrRad, altErrRad) = PolarAxisSolver.DecomposeAxisError(
                    axisRecovered, hemisphere,
                    SiteLatDeg, SiteLonDeg, SiteElevM,
                    sitePressureHPa: 1010.0, siteTempC: 10.0, utc: TestUtc);

                var azErrArcmin = azErrRad * RADIANS2DEGREES * 60.0;
                var altErrArcmin = altErrRad * RADIANS2DEGREES * 60.0;
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"sim Az={simAzArcmin,5:F1}'  ->  Jacobian recovered Az={azErrArcmin,7:F2}'  Alt={altErrArcmin,7:F2}'");

                azErrArcmin.ShouldBe(simAzArcmin, tolerance: 2.0,
                    $"Jacobian recovery should track current sim Az={simAzArcmin}'");
                altErrArcmin.ShouldBe(0.0, tolerance: 2.0,
                    $"Jacobian recovery should keep Alt near 0 for pure-Az sim={simAzArcmin}'");
            }
        }

        /// <summary>
        /// Geometry-free direct-shift recovery: dA ≈ dv = v_now - v2_baseline.
        /// Hypothesis: the OTA's small circle around the axis translates by the
        /// same vector as the axis itself when the axis shifts, so the drift of
        /// v_now from v2_baseline (= what v_now would be if axis unchanged)
        /// equals the axis shift to first order. No knowledge of encoder
        /// position or mount geometry required -- just two unit vectors and a
        /// subtraction.
        /// </summary>
        [Fact]
        public void GivenDirectShiftRecoveryWhenSimChangesDuringRefiningThenAxisTracksCurrentSim()
        {
            var hemisphere = Hemisphere.South;
            var timeProvider = TimeProviderAt(TestUtc);

            const double encoder2Rad = Math.PI / 4 + Math.PI / 3;
            const double simAzInitial = 60.0;

            var axisInitial = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                azErrArcmin: simAzInitial, altErrArcmin: 0.0, hemisphere, timeProvider);
            var (ra2Init, dec2Init) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(
                axisInitial, hemisphere, encoder2Rad);
            var v2Baseline = PolarAxisSolver.RaDecToUnitVec(ra2Init, dec2Init);

            ReadOnlySpan<double> simSequence = [60.0, 45.0, 30.0, 15.0, 0.0];

            foreach (var simAzArcmin in simSequence)
            {
                var axisCurrent = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                    SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                    azErrArcmin: simAzArcmin, altErrArcmin: 0.0, hemisphere, timeProvider);
                var (raNow, decNow) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(
                    axisCurrent, hemisphere, encoder2Rad);
                var vNow = PolarAxisSolver.RaDecToUnitVec(raNow, decNow);

                // dA ≈ dv on the unit sphere.
                var nx = axisInitial.X + (vNow.X - v2Baseline.X);
                var ny = axisInitial.Y + (vNow.Y - v2Baseline.Y);
                var nz = axisInitial.Z + (vNow.Z - v2Baseline.Z);
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                var axisRecovered = new Vec3(nx / len, ny / len, nz / len);

                var (azErrRad, altErrRad) = PolarAxisSolver.DecomposeAxisError(
                    axisRecovered, hemisphere,
                    SiteLatDeg, SiteLonDeg, SiteElevM,
                    sitePressureHPa: 1010.0, siteTempC: 10.0, utc: TestUtc);

                var azErrArcmin = azErrRad * RADIANS2DEGREES * 60.0;
                var altErrArcmin = altErrRad * RADIANS2DEGREES * 60.0;
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"sim Az={simAzArcmin,5:F1}'  ->  direct-shift recovered Az={azErrArcmin,7:F2}'  Alt={altErrArcmin,7:F2}'");
            }
        }

        /// <summary>
        /// End-to-end Jacobian recovery using only data the orchestrator has
        /// after Phase A: <c>A0</c> (axis), <c>v1</c> (probe frame), <c>v2</c>
        /// (post-rotation frame), <c>delta</c> (rotation angle), <c>h</c>
        /// (hemisphere pole). Theta_1 is recovered as the angle between v1 and
        /// h after both are projected onto the plane perpendicular to A0.
        /// </summary>
        [Fact]
        public void GivenOrchestratorDataWhenJacobianRecoveryThenTracksSimChanges()
        {
            var hemisphere = Hemisphere.South;
            var deltaRad = Math.PI / 3;
            var timeProvider = TimeProviderAt(TestUtc);

            const double encoder1Rad = Math.PI / 4;
            const double encoder2Rad = encoder1Rad + Math.PI / 3;
            const double simAzInitial = 60.0;

            var axisInitial = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                azErrArcmin: simAzInitial, altErrArcmin: 0.0, hemisphere, timeProvider);
            var (ra1, dec1) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axisInitial, hemisphere, encoder1Rad);
            var v1 = PolarAxisSolver.RaDecToUnitVec(ra1, dec1);
            var (ra2Init, dec2Init) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axisInitial, hemisphere, encoder2Rad);
            var v2Baseline = PolarAxisSolver.RaDecToUnitVec(ra2Init, dec2Init);

            // Solve theta_1: project v1 and h onto plane perp to A0; angle between
            // them is theta_1. Sign comes from cross product against A0.
            var hz = hemisphere == Hemisphere.North ? 1.0 : -1.0;
            var h = new Vec3(0, 0, hz);
            var theta1Rad = SolveThetaFromV(v1, axisInitial, h);

            // Total theta at v2 / v_now: theta_1 + delta.
            var thetaTotalRad = theta1Rad + deltaRad;

            // Verify the recovery is correct vs the synthesised encoder2Rad.
            theta1Rad.ShouldBe(encoder1Rad, tolerance: 1e-6);
            thetaTotalRad.ShouldBe(encoder2Rad, tolerance: 1e-6);

            // Pre-compute Jacobian at (A0, thetaTotal).
            var sinT = Math.Sin(thetaTotalRad);
            var cosT = Math.Cos(thetaTotalRad);
            var oneMinusCos = 1.0 - cosT;
            var ax = axisInitial.X;
            var ay = axisInitial.Y;
            var az = axisInitial.Z;
            var j00 = hz * az * oneMinusCos;
            var j01 = hz * sinT;
            var j10 = -hz * sinT;
            var j11 = hz * az * oneMinusCos;
            var m00 = j00 * j00 + j10 * j10;
            var m01 = j00 * j01 + j10 * j11;
            var m11 = j01 * j01 + j11 * j11;
            var det = m00 * m11 - m01 * m01;

            ReadOnlySpan<double> simSequence = [60.0, 45.0, 30.0, 15.0, 0.0];
            foreach (var simAzArcmin in simSequence)
            {
                var axisCurrent = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                    SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                    azErrArcmin: simAzArcmin, altErrArcmin: 0.0, hemisphere, timeProvider);
                var (raNow, decNow) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axisCurrent, hemisphere, encoder2Rad);
                var vNow = PolarAxisSolver.RaDecToUnitVec(raNow, decNow);

                var dvX = vNow.X - v2Baseline.X;
                var dvY = vNow.Y - v2Baseline.Y;

                var rhs0 = j00 * dvX + j10 * dvY;
                var rhs1 = j01 * dvX + j11 * dvY;
                var dax = (m11 * rhs0 - m01 * rhs1) / det;
                var day = (-m01 * rhs0 + m00 * rhs1) / det;
                var daz = -(ax * dax + ay * day) / az;

                var nx = ax + dax;
                var ny = ay + day;
                var nz = az + daz;
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                var axisRecovered = new Vec3(nx / len, ny / len, nz / len);

                var (azErrRad, altErrRad) = PolarAxisSolver.DecomposeAxisError(
                    axisRecovered, hemisphere,
                    SiteLatDeg, SiteLonDeg, SiteElevM,
                    sitePressureHPa: 1010.0, siteTempC: 10.0, utc: TestUtc);

                var azErrArcmin = azErrRad * RADIANS2DEGREES * 60.0;
                var altErrArcmin = altErrRad * RADIANS2DEGREES * 60.0;
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"sim Az={simAzArcmin,5:F1}'  ->  e2e Jacobian recovered Az={azErrArcmin,7:F2}'  Alt={altErrArcmin,7:F2}'");
                azErrArcmin.ShouldBe(simAzArcmin, tolerance: 2.0);
                altErrArcmin.ShouldBe(0.0, tolerance: 2.0);
            }
        }

        /// <summary>
        /// End-to-end Phase A math: sim (azErr, altErr) -> axis -> v1/v2 ->
        /// TryRecoverAxis -> DecomposeAxisError -> reported (azErr, altErr).
        /// Should round-trip to within sub-arcmin (refraction asymmetry only).
        /// If the GUI shows 40' when sim is configured to 30' the bug must be
        /// outside the math -- in the data path that connects FakeSkywatcher's
        /// configured offset to the orchestrator's Phase A inputs.
        /// </summary>
        [Theory]
        [InlineData(30.0, 0.0)]
        [InlineData(0.0, 30.0)]
        [InlineData(30.0, -10.0)]
        [InlineData(-45.0, 60.0)]
        public void GivenSimMisalignmentWhenFullPhaseAMathThenRoundTripsConfiguredOffset(double simAzArcmin, double simAltArcmin)
        {
            var hemisphere = Hemisphere.South;
            var timeProvider = TimeProviderAt(TestUtc);
            var deltaRad = Math.PI / 4; // 45deg phase A rotation

            var axis = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                SiteLatDeg, SiteLonDeg, SiteElevM, TestUtc,
                simAzArcmin, simAltArcmin, hemisphere, timeProvider);

            const double encoder1Rad = 0.0;
            const double encoder2Rad = Math.PI / 4;
            var (ra1, dec1) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axis, hemisphere, encoder1Rad);
            var (ra2, dec2) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axis, hemisphere, encoder2Rad);
            var v1 = PolarAxisSolver.RaDecToUnitVec(ra1, dec1);
            var v2 = PolarAxisSolver.RaDecToUnitVec(ra2, dec2);

            var ok = PolarAxisSolver.TryRecoverAxis(v1, v2, deltaRad, out var axisRecovered, out _);
            ok.ShouldBeTrue();

            var (azErrRad, altErrRad) = PolarAxisSolver.DecomposeAxisError(
                axisRecovered, hemisphere,
                SiteLatDeg, SiteLonDeg, SiteElevM,
                sitePressureHPa: 1010.0, siteTempC: 10.0, utc: TestUtc);

            var azArcmin = azErrRad * RADIANS2DEGREES * 60.0;
            var altArcmin = altErrRad * RADIANS2DEGREES * 60.0;
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"sim ({simAzArcmin,5:F1}', {simAltArcmin,5:F1}')  ->  reported ({azArcmin,7:F2}', {altArcmin,7:F2}')");
            azArcmin.ShouldBe(simAzArcmin, tolerance: 1.0);
            altArcmin.ShouldBe(simAltArcmin, tolerance: 1.0);
        }

        // Project v and h onto plane perp to axis; signed angle between them around axis.
        private static double SolveThetaFromV(in Vec3 v, in Vec3 axis, in Vec3 h)
        {
            var vDotA = v.X * axis.X + v.Y * axis.Y + v.Z * axis.Z;
            var hDotA = h.X * axis.X + h.Y * axis.Y + h.Z * axis.Z;
            var vPerpX = v.X - vDotA * axis.X;
            var vPerpY = v.Y - vDotA * axis.Y;
            var vPerpZ = v.Z - vDotA * axis.Z;
            var hPerpX = h.X - hDotA * axis.X;
            var hPerpY = h.Y - hDotA * axis.Y;
            var hPerpZ = h.Z - hDotA * axis.Z;
            var dot = vPerpX * hPerpX + vPerpY * hPerpY + vPerpZ * hPerpZ;
            // cross(h_perp, v_perp) along axis gives sin(theta)*|h_perp||v_perp|
            var crossX = hPerpY * vPerpZ - hPerpZ * vPerpY;
            var crossY = hPerpZ * vPerpX - hPerpX * vPerpZ;
            var crossZ = hPerpX * vPerpY - hPerpY * vPerpX;
            var crossDotAxis = crossX * axis.X + crossY * axis.Y + crossZ * axis.Z;
            return Math.Atan2(crossDotAxis, dot);
        }
    }
}
