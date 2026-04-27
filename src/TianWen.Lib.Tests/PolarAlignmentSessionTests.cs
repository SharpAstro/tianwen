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
                MaxFrame2Retries = 0
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
                OnDone = PolarAlignmentOnDone.ReverseAxisBack
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
                OnDone = PolarAlignmentOnDone.LeaveInPlace
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
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 3
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
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 2
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
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 0
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
            var solver = Substitute.For<IPlateSolver>();
            var time = new FakeTimeProviderWrapper();
            var source = new SyntheticAxisCaptureSource(GroundTruthAxis, deltaRad: 60.0 * DEGREES2RADIANS);

            var config = PolarAlignmentConfiguration.Default with
            {
                RotationDeg = 60.0, SettleSeconds = 0, MaxFrame2Retries = 0,
                SmoothingWindow = 1
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
            private static readonly Vec3 InitialV1 = PolarAxisSolver.RaDecToUnitVec(0.0, 60.0); // arbitrary

            public string DisplayName => "Synthetic";
            public double FocalLengthMm => 200;
            public double ApertureMm => 50;
            public double PixelSizeMicrons => 3.0;

            public int CaptureCount { get; private set; }
            public int Frame2AttemptCount { get; private set; }
            /// <summary>Number of times frame-2 capture should fail before succeeding.</summary>
            public int Frame2InitialFailures { get; set; }

            public ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(TimeSpan exposure, IPlateSolver solver, CancellationToken ct = default)
            {
                CaptureCount++;
                ct.ThrowIfCancellationRequested();

                if (CaptureCount == 1)
                {
                    // Frame 1 always solves at the first ramp rung.
                    return ValueTask.FromResult(new CaptureAndSolveResult(
                        Success: true, Wcs: null, WcsCenter: InitialV1,
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

                var v2 = PolarAxisSolver.Rotate(InitialV1, groundTruthAxis, deltaRad);
                return ValueTask.FromResult(new CaptureAndSolveResult(
                    Success: true, Wcs: null, WcsCenter: v2,
                    StarsMatched: 25, ExposureUsed: exposure, FitsPath: null));
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

            public ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(TimeSpan exposure, IPlateSolver solver, CancellationToken ct = default) =>
                ValueTask.FromResult(new CaptureAndSolveResult(
                    Success: false, Wcs: null, WcsCenter: default,
                    StarsMatched: 0, ExposureUsed: exposure, FitsPath: null,
                    FailureReason: reason));
        }
    }
}
