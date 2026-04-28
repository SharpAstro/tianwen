using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing.PolarAlignment;
using Xunit;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Functional tests for <see cref="PolarAlignmentSession"/>. Uses an
    /// NSubstitute mount + a synthetic <see cref="ICaptureSource"/> that
    /// fakes plate-solve outputs based on the mount's known polar offset.
    /// Validates: Phase A axis recovery wiring, frame-2 retry loop,
    /// reverse-axis restore on dispose, refinement loop settling.
    /// </summary>
    [Collection("Astrometry")]
    public class PolarAlignmentSessionTests
    {
        // Standard test site: lat=45deg, long=0, sea-level, std atm.
        private static readonly PolarAlignmentSite TestSite = new(
            LatitudeDeg: 45.0, LongitudeDeg: 0.0, ElevationM: 0.0,
            PressureHPa: 1010.0, TemperatureC: 10.0);

        // Ground-truth mount-axis offset: tilted 1deg from the J2000 north pole toward RA=6h.
        private static readonly Vec3 GroundTruthAxis = PolarAxisSolver.RaDecToUnitVec(6.0, 89.0);

        [Fact]
        public async Task SolveAsync_RecoversGroundTruthAxisFromSyntheticRotation()
        {
            var mount = BuildMockMount();
            var ext = Substitute.For<IExternal>();
            var solver = Substitute.For<IPlateSolver>();
            var time = new FakeTimeProviderWrapper();

            // Capture source synthesises v1 at "any RA position", then v2 = Rotate(v1, GroundTruthAxis, delta).
            // We don't simulate the mount's actual RA — it's enough that the camera reports a v2
            // consistent with a clean rotation around the ground-truth axis.
            var source = new SyntheticAxisCaptureSource(GroundTruthAxis, deltaRad: 60.0 * DEGREES2RADIANS);

            var config = PolarAlignmentConfiguration.Default with
            {
                RotationDeg = 60.0,
                SettleSeconds = 0, // skip settle in test
                MaxFrame2Retries = 0,
                ReferenceFrameAverages = 1,
                RotationMinStars = 25, // align with synthetic solver's matched-stars output // synthetic source returns v1/v2 on call-count basis; averaging confuses it
            };

            await using var session = new PolarAlignmentSession(
                ext, mount, source, solver, time, NullLogger.Instance, TestSite, config);

            var result = await session.SolveAsync(CancellationToken.None);

            result.Success.ShouldBeTrue(result.FailureReason);
            // Recovered axis matches ground truth within sub-arcsec (synthetic geometry, no noise).
            var dot = Math.Clamp(Vec3.Dot(result.AxisJ2000, GroundTruthAxis), -1.0, 1.0);
            var angularErrorArcsec = Math.Acos(dot) * RAD2SEC;
            angularErrorArcsec.ShouldBeLessThan(2.0, $"axis recovered to {angularErrorArcsec:F3} arcsec");

            // Total error magnitude should be ~60' (1 degree offset).
            result.TotalErrorArcmin.ShouldBeInRange(40.0, 90.0,
                $"reported {result.TotalErrorArcmin:F2} arcmin (cos(45) factor not applied here)");

            // Measured rotation matches commanded to sub-arcsec on synthetic
            // input (no mount slop, no sidereal contamination). Real hardware
            // will diverge — that's the diagnostic value of exposing both.
            var measuredDeg = result.MeasuredRotationRad * RADIANS2DEGREES;
            var commandedDeg = result.CommandedRotationRad * RADIANS2DEGREES;
            commandedDeg.ShouldBe(60.0, tolerance: 0.5,
                $"commanded rotation should reflect rate*elapsed near 60deg (got {commandedDeg:F3}deg)");
            (measuredDeg - commandedDeg).ShouldBe(0.0, tolerance: 0.001,
                $"measured ({measuredDeg:F4}deg) should equal commanded ({commandedDeg:F4}deg) on noise-free synthetic input");
        }

        [Fact]
        public async Task DisposeAsync_AfterSolve_IssuesReverseMoveAxisWithNegatedRate()
        {
            var mount = BuildMockMount();
            var ext = Substitute.For<IExternal>();
            var solver = Substitute.For<IPlateSolver>();
            var time = new FakeTimeProviderWrapper();
            var source = new SyntheticAxisCaptureSource(GroundTruthAxis, deltaRad: 60.0 * DEGREES2RADIANS);

            var config = PolarAlignmentConfiguration.Default with
            {
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 0,
                OnDone = PolarAlignmentOnDone.ReverseAxisBack,
                ReferenceFrameAverages = 1,
                RotationMinStars = 25, // align with synthetic solver's matched-stars output
            };

            var session = new PolarAlignmentSession(ext, mount, source, solver, time, NullLogger.Instance, TestSite, config);
            (await session.SolveAsync(CancellationToken.None)).Success.ShouldBeTrue();

            await session.DisposeAsync();

            // First MoveAxis: forward at +rate. Second: stop (rate=0). Third: reverse at -rate. Fourth: stop.
            var calls = mount.ReceivedCalls();
            var moveAxisRates = new List<double>();
            foreach (var call in calls)
            {
                if (call.GetMethodInfo().Name != "MoveAxisAsync") continue;
                var args = call.GetArguments();
                moveAxisRates.Add((double)args[1]!);
            }
            moveAxisRates.Count.ShouldBeGreaterThanOrEqualTo(4);
            moveAxisRates[0].ShouldBeGreaterThan(0); // forward
            moveAxisRates[1].ShouldBe(0);            // stop
            moveAxisRates[2].ShouldBeLessThan(0);    // reverse
            moveAxisRates[3].ShouldBe(0);            // stop
            // Forward and reverse magnitudes match (symmetric restore).
            Math.Abs(Math.Abs(moveAxisRates[0]) - Math.Abs(moveAxisRates[2])).ShouldBeLessThan(1e-9);
        }

        [Fact]
        public async Task DisposeAsync_LeaveInPlace_DoesNotIssueReverseMoveAxis()
        {
            var mount = BuildMockMount();
            var ext = Substitute.For<IExternal>();
            var solver = Substitute.For<IPlateSolver>();
            var time = new FakeTimeProviderWrapper();
            var source = new SyntheticAxisCaptureSource(GroundTruthAxis, deltaRad: 60.0 * DEGREES2RADIANS);

            var config = PolarAlignmentConfiguration.Default with
            {
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 0,
                OnDone = PolarAlignmentOnDone.LeaveInPlace,
                ReferenceFrameAverages = 1,
                RotationMinStars = 25, // align with synthetic solver's matched-stars output
            };

            var session = new PolarAlignmentSession(ext, mount, source, solver, time, NullLogger.Instance, TestSite, config);
            (await session.SolveAsync(CancellationToken.None)).Success.ShouldBeTrue();
            await session.DisposeAsync();

            // After Phase A there should be exactly two MoveAxis calls (forward + stop). No reverse.
            int moveCount = 0;
            foreach (var call in mount.ReceivedCalls())
                if (call.GetMethodInfo().Name == "MoveAxisAsync") moveCount++;
            moveCount.ShouldBe(2, "LeaveInPlace must not issue reverse MoveAxis");
        }

        [Fact]
        public async Task SolveAsync_Frame2Fails_RetriesUpToConfiguredLimit()
        {
            var mount = BuildMockMount();
            var ext = Substitute.For<IExternal>();
            var solver = Substitute.For<IPlateSolver>();
            var time = new FakeTimeProviderWrapper();

            // Source: v1 succeeds, frame-2 fails twice, then succeeds.
            var source = new SyntheticAxisCaptureSource(GroundTruthAxis, deltaRad: 60.0 * DEGREES2RADIANS)
            {
                Frame2InitialFailures = 2
            };

            var config = PolarAlignmentConfiguration.Default with
            {
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 3,
                ReferenceFrameAverages = 1,
                RotationMinStars = 25, // align with synthetic solver's matched-stars output
            };

            await using var session = new PolarAlignmentSession(
                ext, mount, source, solver, time, NullLogger.Instance, TestSite, config);

            var result = await session.SolveAsync(CancellationToken.None);

            result.Success.ShouldBeTrue(result.FailureReason);
            source.Frame2AttemptCount.ShouldBe(3); // 2 fails + 1 success
        }

        [Fact]
        public async Task SolveAsync_Frame2ExhaustsRetries_FailsButPreservesPhaseACompletedForReverseRestore()
        {
            var mount = BuildMockMount();
            var ext = Substitute.For<IExternal>();
            var solver = Substitute.For<IPlateSolver>();
            var time = new FakeTimeProviderWrapper();

            var source = new SyntheticAxisCaptureSource(GroundTruthAxis, deltaRad: 60.0 * DEGREES2RADIANS)
            {
                Frame2InitialFailures = 99 // never succeeds within retry budget
            };

            var config = PolarAlignmentConfiguration.Default with
            {
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 2,
                ReferenceFrameAverages = 1,
                RotationMinStars = 25, // align with synthetic solver's matched-stars output
            };

            var session = new PolarAlignmentSession(ext, mount, source, solver, time, NullLogger.Instance, TestSite, config);

            var result = await session.SolveAsync(CancellationToken.None);
            result.Success.ShouldBeFalse();
            result.FailureReason.ShouldNotBeNull().ShouldContain("Frame 2 plate solve failed");

            // Reverse-restore should still run because we already rotated the mount.
            await session.DisposeAsync();
            int reverseCount = 0;
            foreach (var call in mount.ReceivedCalls())
                if (call.GetMethodInfo().Name == "MoveAxisAsync" && (double)call.GetArguments()[1]! < 0) reverseCount++;
            reverseCount.ShouldBeGreaterThan(0, "reverse-restore must run even after frame-2 exhaustion");
        }

        [Fact]
        public async Task SolveAsync_WhenSourceSuppliesFailureReason_SurfacesItVerbatim()
        {
            var mount = BuildMockMount();
            var ext = Substitute.For<IExternal>();
            var solver = Substitute.For<IPlateSolver>();
            var time = new FakeTimeProviderWrapper();

            // Source returns Success=false on every rung with a structured reason —
            // exactly what GuiderCaptureSource does when PHD2 'Save Images' is disabled.
            const string SourceReason = "Guider produced no frame on disk \u2014 enable 'Save Images' in PHD2.";
            var source = new FailingCaptureSource(SourceReason);

            var config = PolarAlignmentConfiguration.Default with
            {
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 0,
                ReferenceFrameAverages = 1,
                RotationMinStars = 25, // align with synthetic solver's matched-stars output
            };

            await using var session = new PolarAlignmentSession(
                ext, mount, source, solver, time, NullLogger.Instance, TestSite, config);

            var result = await session.SolveAsync(CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.FailureReason.ShouldBe(SourceReason);
        }

        [Fact]
        public async Task RefineAsync_PopulatesOverlay_WithTruePoleAndAxisSkyPositions()
        {
            var mount = BuildMockMount();
            var ext = Substitute.For<IExternal>();
            var time = new FakeTimeProviderWrapper();
            var source = new SyntheticAxisCaptureSource(GroundTruthAxis, deltaRad: 60.0 * DEGREES2RADIANS);

            // Phase B uses CaptureAsync + IPlateSolver. The substitute solver
            // returns a WCS whose CenterRA / CenterDec map back to the
            // synthetic v2 unit vector that Phase A would have produced. No CD
            // matrix on the synthetic WCS so the IncrementalSolver stays
            // unseeded and the orchestrator runs the full-solve path each
            // tick -- behaviour matches the pre-incremental test contract.
            var v2 = PolarAxisSolver.Rotate(SyntheticAxisCaptureSource.SeedV1, GroundTruthAxis, 60.0 * DEGREES2RADIANS);
            var (v2Ra, v2Dec) = PolarAxisSolver.UnitVecToRaDec(v2);
            var solver = new SyntheticPlateSolver(new WCS(v2Ra, v2Dec), matchedStars: 25);

            var config = PolarAlignmentConfiguration.Default with
            {
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 0,
                SmoothingWindow = 1,
                ReferenceFrameAverages = 1,
                RotationMinStars = 25, // align with synthetic solver's matched-stars output
            };

            await using var session = new PolarAlignmentSession(
                ext, mount, source, solver, time, NullLogger.Instance, TestSite, config);
            (await session.SolveAsync(CancellationToken.None)).Success.ShouldBeTrue();

            using var ctsLoop = new CancellationTokenSource();
            LiveSolveResult? firstTick = null;
            await foreach (var tick in session.RefineAsync(ctsLoop.Token))
            {
                firstTick = tick;
                ctsLoop.Cancel();
                break;
            }

            firstTick.ShouldNotBeNull();
            var overlay = firstTick.Value.Overlay.ShouldNotBeNull();

            // True pole sits at J2000 (RA=0h, Dec=+90 for the northern test site).
            overlay.TruePoleRaHours.ShouldBe(0.0);
            overlay.TruePoleDecDeg.ShouldBe(90.0);
            overlay.Hemisphere.ShouldBe(Hemisphere.North);

            // Axis recovered close to ground truth (RA=6h, Dec=89), sub-arcsec on synthetic input.
            overlay.AxisRaHours.ShouldBeInRange(5.99, 6.01);
            overlay.AxisDecDeg.ShouldBeInRange(88.99, 89.01);

            overlay.RingRadiiArcmin.ShouldBe(ImmutableArray.Create(5f, 15f, 30f));
        }

        /// <summary>
        /// End-to-end correctness test for <see cref="PolarAlignmentSession.RefineAsync"/>:
        /// Phase A captures v1/v2 at sim Az=60', then the user "adjusts polar
        /// knobs" by moving the synthetic axis through a sweep
        /// [60', 45', 30', 15', 0']. Each refine tick must report an Az error
        /// matching the current sim within 2arcmin, validating that the
        /// Jacobian live tracker (LiveAxisRefiner) is wired correctly through
        /// the orchestrator -- not just unit-tested in isolation.
        ///
        /// Closes the gap between the Phase A round-trip test and the GUI: a
        /// regression that breaks the wiring (e.g. wrong v2 baseline, missing
        /// SiderealNormalise, swapped reference UTC) would surface here, not
        /// only in real hardware runs.
        /// </summary>
        [Fact]
        public async Task RefineAsync_TracksSimSweep_OverFiveTicks()
        {
            // South-hemisphere fixture matches the FakeSkywatcher unit tests so
            // we can reuse the same proven (axis -> v) synthesis path.
            const double siteLatDeg = -37.5;
            const double siteLonDeg = 145.9;
            const double siteElevM = 50.0;
            var testUtc = new DateTimeOffset(2026, 4, 27, 10, 0, 0, TimeSpan.Zero);
            var site = new PolarAlignmentSite(
                LatitudeDeg: siteLatDeg, LongitudeDeg: siteLonDeg, ElevationM: siteElevM,
                PressureHPa: 1010.0, TemperatureC: 10.0);
            const Hemisphere hemisphere = Hemisphere.South;

            var time = new FakeTimeProviderWrapper(testUtc);
            var mount = BuildMockMount();
            var ext = Substitute.For<IExternal>();

            // Phase A geometry: encoder1 at HA=3h, rotate by delta=60deg, sim Az=60'.
            const double encoder1Rad = Math.PI / 4;
            const double rotationDeg = 60.0;
            const double simInitialAz = 60.0;
            var deltaRad = rotationDeg * Math.PI / 180.0;

            var axisInitial = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                siteLatDeg, siteLonDeg, siteElevM, testUtc,
                azErrArcmin: simInitialAz, altErrArcmin: 0.0, hemisphere, time);
            var (ra1, dec1) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axisInitial, hemisphere, encoder1Rad);
            var v1 = PolarAxisSolver.RaDecToUnitVec(ra1, dec1);
            var (ra2, dec2) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axisInitial, hemisphere, encoder1Rad + deltaRad);
            var v2 = PolarAxisSolver.RaDecToUnitVec(ra2, dec2);

            // Refine sweep: encoder stays at encoder1+delta (user does not
            // turn RA during refining); only the axis moves as polar knobs
            // are adjusted, sweeping sim Az from initial down through 0.
            // Plain array (not ReadOnlySpan) because the async method body
            // crosses await/yield boundaries which spans cannot survive.
            double[] simSequence = [60.0, 45.0, 30.0, 15.0, 0.0];
            var refineWcsSequence = new List<WCS>(simSequence.Length);
            foreach (var simAz in simSequence)
            {
                var axisI = FakeSkywatcherMountDriver.TopocentricMisalignmentToJ2000Axis(
                    siteLatDeg, siteLonDeg, siteElevM, testUtc,
                    azErrArcmin: simAz, altErrArcmin: 0.0, hemisphere, time);
                var (raNow, decNow) = FakeSkywatcherMountDriver.ApplyPolarMisalignment(axisI, hemisphere, encoder1Rad + deltaRad);
                refineWcsSequence.Add(new WCS(raNow, decNow));
            }

            var source = new ScriptedTwoFrameSource(v1, v2, matchedStars: 25);
            var solver = new ScriptedPlateSolver(refineWcsSequence, matchedStars: 25);

            var config = PolarAlignmentConfiguration.Default with
            {
                RotationDeg = rotationDeg,
                SettleSeconds = 0,
                MaxFrame2Retries = 0,
                ReferenceFrameAverages = 1,
                RotationMinStars = 25,
                MinStarsForSolve = 25,
                SmoothingWindow = 1, // raw values per tick, no EWMA lag
                UseIncrementalSolver = false, // force the full-solve path each tick
                RefineFullSolveInterval = 0,
            };

            await using var session = new PolarAlignmentSession(
                ext, mount, source, solver, time, NullLogger.Instance, site, config);

            (await session.SolveAsync(CancellationToken.None)).Success.ShouldBeTrue();

            using var ctsLoop = new CancellationTokenSource();
            var observed = new List<(double azArcmin, double altArcmin)>();
            await foreach (var tick in session.RefineAsync(ctsLoop.Token))
            {
                var azArcmin = tick.AzErrorRad * RADIANS2DEGREES * 60.0;
                var altArcmin = tick.AltErrorRad * RADIANS2DEGREES * 60.0;
                observed.Add((azArcmin, altArcmin));
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"sim Az={simSequence[observed.Count - 1],5:F1}'  ->  reported Az={azArcmin,7:F2}'  Alt={altArcmin,7:F2}'");
                if (observed.Count >= simSequence.Length)
                {
                    ctsLoop.Cancel();
                    break;
                }
            }

            observed.Count.ShouldBe(simSequence.Length);
            for (int i = 0; i < simSequence.Length; i++)
            {
                observed[i].azArcmin.ShouldBe(simSequence[i], tolerance: 2.0,
                    $"tick {i}: sim Az={simSequence[i]:F1}' but reported {observed[i].azArcmin:F2}'");
                observed[i].altArcmin.ShouldBe(0.0, tolerance: 2.0,
                    $"tick {i}: pure-Az sim should give Alt~0 but reported {observed[i].altArcmin:F2}'");
            }
        }

        [Fact]
        public void TryBuildCorrectionArrow_AxisOnPole_ReturnsFalse()
        {
            var pole = PolarAxisSolver.RaDecToUnitVec(0.0, 90.0);
            var anchor = PolarAxisSolver.RaDecToUnitVec(2.5, 60.0); // arbitrary
            // Axis exactly on pole -> rotation angle 0 -> sub-pixel arrow.
            PolarAlignmentSession.TryBuildCorrectionArrow(pole, pole, anchor, out _).ShouldBeFalse();
        }

        [Fact]
        public void TryBuildCorrectionArrow_OffsetAxis_ProducesArrowConsistentWithRotation()
        {
            // Axis is the pole tilted by 1 degree toward RA=6h. The corrective
            // rotation axis r = axis x pole points along ~(1, 0, 0), i.e. RA=0h
            // on the equator. Pick an anchor *off* that rotation axis so the
            // rotation has a non-zero effect: RA=6h, Dec=0 sits at (0, 1, 0),
            // perpendicular to r.
            var pole = PolarAxisSolver.RaDecToUnitVec(0.0, 90.0);
            var axis = PolarAxisSolver.RaDecToUnitVec(6.0, 89.0);
            var anchor = PolarAxisSolver.RaDecToUnitVec(6.0, 0.0);

            PolarAlignmentSession.TryBuildCorrectionArrow(axis, pole, anchor, out var arrow).ShouldBeTrue();

            // Round-trip: convert arrow back to unit vectors and confirm both
            // sit on the unit sphere and end != start by a non-zero angle.
            var startVec = PolarAxisSolver.RaDecToUnitVec(arrow.StartRaHours, arrow.StartDecDeg);
            var endVec = PolarAxisSolver.RaDecToUnitVec(arrow.EndRaHours, arrow.EndDecDeg);
            var startToAnchorDot = Math.Clamp(Vec3.Dot(startVec, anchor), -1.0, 1.0);
            Math.Acos(startToAnchorDot).ShouldBe(0.0, tolerance: 1e-9, "start should match anchor");
            var moveAngleArcmin = Math.Acos(Math.Clamp(Vec3.Dot(startVec, endVec), -1.0, 1.0))
                * RADIANS2DEGREES * 60.0;
            moveAngleArcmin.ShouldBeGreaterThan(0.5,
                $"corrective rotation = 1deg should produce a non-zero arrow length on a non-degenerate anchor (got {moveAngleArcmin:F2}')");
        }

        // --- helpers ---

        private static IMountDriver BuildMockMount()
        {
            var mount = Substitute.For<IMountDriver>();
            mount.Connected.Returns(true);
            mount.CanMoveAxis(TelescopeAxis.Primary).Returns(true);
            mount.AxisRates(TelescopeAxis.Primary).Returns(new[] { new AxisRate(8.0) });
            mount.CanPark.Returns(false);
            mount.MoveAxisAsync(Arg.Any<TelescopeAxis>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
                 .Returns(ValueTask.CompletedTask);
            return mount;
        }

        /// <summary>
        /// Synthetic <see cref="ICaptureSource"/>: returns a fixed v1 and a v2
        /// that's a pure rotation of v1 by the configured delta around the
        /// ground-truth axis. Lets us drive the orchestrator end-to-end without
        /// needing real plate solving or pixel rendering.
        /// </summary>
        private sealed class SyntheticAxisCaptureSource(Vec3 groundTruthAxis, double deltaRad) : ICaptureSource
        {
            internal static readonly Vec3 SeedV1 = PolarAxisSolver.RaDecToUnitVec(0.0, 60.0); // arbitrary

            public string DisplayName => "Synthetic";
            public double FocalLengthMm => 200;
            public double ApertureMm => 50;
            public double PixelSizeMicrons => 3.0;

            public int CaptureCount { get; private set; }
            public int Frame2AttemptCount { get; private set; }
            /// <summary>Number of times frame-2 capture should fail before succeeding.</summary>
            public int Frame2InitialFailures { get; set; }

            public ValueTask<CaptureResult> CaptureAsync(TimeSpan exposure, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                // Refinement-loop only: returns a stub mono image. The paired
                // SyntheticPlateSolver supplies the actual WCS; the
                // IncrementalSolver stays unseeded because the synthetic WCS
                // has no CD matrix.
                var stub = Image.FromChannel(new float[2, 2], maxValue: 1f, minValue: 0f);
                return ValueTask.FromResult(new CaptureResult(
                    Success: true, Image: stub, OwnershipTransferredToUi: false,
                    SearchOrigin: null, ExposureUsed: exposure, FitsPath: null));
            }

            public ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(TimeSpan exposure, IPlateSolver solver, CancellationToken ct = default)
            {
                CaptureCount++;
                ct.ThrowIfCancellationRequested();

                if (CaptureCount == 1)
                {
                    // Frame 1 always solves at the first ramp rung.
                    return ValueTask.FromResult(new CaptureAndSolveResult(
                        Success: true, Wcs: null, WcsCenter: SeedV1,
                        StarsMatched: 25, ExposureUsed: exposure, FitsPath: null));
                }

                // Subsequent calls represent frame 2 retries or refinement ticks.
                Frame2AttemptCount++;
                if (Frame2AttemptCount <= Frame2InitialFailures)
                {
                    return ValueTask.FromResult(new CaptureAndSolveResult(
                        Success: false, Wcs: null, WcsCenter: default,
                        StarsMatched: 0, ExposureUsed: exposure, FitsPath: null));
                }

                var v2 = PolarAxisSolver.Rotate(SeedV1, groundTruthAxis, deltaRad);
                return ValueTask.FromResult(new CaptureAndSolveResult(
                    Success: true, Wcs: null, WcsCenter: v2,
                    StarsMatched: 25, ExposureUsed: exposure, FitsPath: null));
            }
        }

        /// <summary>
        /// Test plate solver that returns a fixed WCS regardless of input.
        /// Pairs with <see cref="SyntheticAxisCaptureSource"/>'s CaptureAsync
        /// to drive Phase B without a real solver.
        /// </summary>
        private sealed class SyntheticPlateSolver(WCS wcs, int matchedStars) : IPlateSolver
        {
            public string Name => "SyntheticPlateSolver";
            public float Priority => 1.0f;
            public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
            public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03f, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
                => Task.FromResult(new PlateSolveResult(wcs, TimeSpan.FromMilliseconds(1)) { MatchedStars = matchedStars });
            public Task<PlateSolveResult> SolveImageAsync(Image image, ImageDim? imageDim = null, float range = 0.03f, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
                => Task.FromResult(new PlateSolveResult(wcs, TimeSpan.FromMilliseconds(1)) { MatchedStars = matchedStars });
        }

        /// <summary>
        /// Capture source that scripts Phase A (returns fixed v1 on call 1, v2 on
        /// call 2+) and stub images for Phase B. Pairs with
        /// <see cref="ScriptedPlateSolver"/> to drive a full Phase A + sim-sweep
        /// refine through the orchestrator.
        /// </summary>
        private sealed class ScriptedTwoFrameSource(Vec3 v1, Vec3 v2, int matchedStars) : ICaptureSource
        {
            private int _captureAndSolveCount;

            public string DisplayName => "ScriptedTwoFrame";
            public double FocalLengthMm => 200;
            public double ApertureMm => 50;
            public double PixelSizeMicrons => 3.0;

            public ValueTask<CaptureResult> CaptureAsync(TimeSpan exposure, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                // Refine path consumes the WCS off the paired plate solver, not
                // off the image -- a 2x2 stub keeps allocations minimal.
                var stub = Image.FromChannel(new float[2, 2], maxValue: 1f, minValue: 0f);
                return ValueTask.FromResult(new CaptureResult(
                    Success: true, Image: stub, OwnershipTransferredToUi: false,
                    SearchOrigin: null, ExposureUsed: exposure, FitsPath: null));
            }

            public ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(TimeSpan exposure, IPlateSolver solver, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                _captureAndSolveCount++;
                var v = _captureAndSolveCount == 1 ? v1 : v2;
                return ValueTask.FromResult(new CaptureAndSolveResult(
                    Success: true, Wcs: null, WcsCenter: v,
                    StarsMatched: matchedStars, ExposureUsed: exposure, FitsPath: null));
            }
        }

        /// <summary>
        /// Plate solver that returns a scripted sequence of WCS solutions, one
        /// per <c>SolveImageAsync</c> call. Used by the sim-sweep integration
        /// test to feed each refine tick a different ground-truth axis.
        /// </summary>
        private sealed class ScriptedPlateSolver(IReadOnlyList<WCS> wcsSequence, int matchedStars) : IPlateSolver
        {
            private int _index;

            public string Name => "ScriptedPlateSolver";
            public float Priority => 1.0f;
            public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
            public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03f, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
                => Task.FromResult(NextResult());
            public Task<PlateSolveResult> SolveImageAsync(Image image, ImageDim? imageDim = null, float range = 0.03f, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
                => Task.FromResult(NextResult());

            private PlateSolveResult NextResult()
            {
                // Clamp at the last entry so an over-long refine doesn't throw;
                // the test asserts the exact tick count separately.
                var i = Math.Min(_index, wcsSequence.Count - 1);
                _index++;
                return new PlateSolveResult(wcsSequence[i], TimeSpan.FromMilliseconds(1)) { MatchedStars = matchedStars };
            }
        }

        /// <summary>
        /// Minimal source that fails every capture with a structured reason — used to
        /// pin the orchestrator's failure-reason propagation behaviour (Phase 5: PHD2
        /// 'Save Images' disabled message must reach the user).
        /// </summary>
        private sealed class FailingCaptureSource(string reason) : ICaptureSource
        {
            public string DisplayName => "FailingSource";
            public double FocalLengthMm => 200;
            public double ApertureMm => 50;
            public double PixelSizeMicrons => 3.0;

            public ValueTask<CaptureResult> CaptureAsync(TimeSpan exposure, CancellationToken ct = default) =>
                ValueTask.FromResult(new CaptureResult(
                    Success: false, Image: null, OwnershipTransferredToUi: false,
                    SearchOrigin: null, ExposureUsed: exposure, FitsPath: null,
                    FailureReason: reason));

            public ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(TimeSpan exposure, IPlateSolver solver, CancellationToken ct = default) =>
                ValueTask.FromResult(new CaptureAndSolveResult(
                    Success: false, Wcs: null, WcsCenter: default,
                    StarsMatched: 0, ExposureUsed: exposure, FitsPath: null,
                    FailureReason: reason));
        }
    }
}
