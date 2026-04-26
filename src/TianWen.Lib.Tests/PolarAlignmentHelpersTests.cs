using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing.PolarAlignment;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Tests for the pure helpers used by the polar-alignment routine:
    /// <see cref="AdaptiveExposureRamp"/> and <see cref="CaptureSourceRanker"/>.
    /// The orchestrator and concrete capture sources have their own functional tests.
    /// </summary>
    public class PolarAlignmentHelpersTests
    {
        // --- AdaptiveExposureRamp ---

        [Fact]
        public async Task GivenFirstRungSolvesWhenProbeThenStopsAtFirstRung()
        {
            var ramp = ImmutableArray.Create(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));
            var source = new ScriptedCaptureSource([(true, 30), (true, 30), (true, 30)]);

            var result = await AdaptiveExposureRamp.ProbeAsync(source, NullPlateSolver.Instance, ramp, minStarsMatched: 15, CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.ExposureUsed.ShouldBe(TimeSpan.FromMilliseconds(100));
            source.AttemptCount.ShouldBe(1);
        }

        [Fact]
        public async Task GivenThirdRungSolvesWhenProbeThenStopsAtThirdRung()
        {
            var ramp = ImmutableArray.Create(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
            var source = new ScriptedCaptureSource([(false, 0), (false, 5), (true, 20)]);

            var result = await AdaptiveExposureRamp.ProbeAsync(source, NullPlateSolver.Instance, ramp, minStarsMatched: 15, CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.ExposureUsed.ShouldBe(TimeSpan.FromMilliseconds(500));
            source.AttemptCount.ShouldBe(3);
        }

        [Fact]
        public async Task GivenSolvesButTooFewStarsWhenProbeThenAdvancesToNextRung()
        {
            var ramp = ImmutableArray.Create(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            // First rung "solves" but only 10 stars (< minStarsMatched=15) -> advance.
            var source = new ScriptedCaptureSource([(true, 10), (true, 25)]);

            var result = await AdaptiveExposureRamp.ProbeAsync(source, NullPlateSolver.Instance, ramp, minStarsMatched: 15, CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.StarsMatched.ShouldBe(25);
            result.ExposureUsed.ShouldBe(TimeSpan.FromMilliseconds(500));
            source.AttemptCount.ShouldBe(2);
        }

        [Fact]
        public async Task GivenAllRungsFailWhenProbeThenReturnsLastFailedAttempt()
        {
            var ramp = ImmutableArray.Create(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
            var source = new ScriptedCaptureSource([(false, 0), (false, 3), (false, 7)]);

            var result = await AdaptiveExposureRamp.ProbeAsync(source, NullPlateSolver.Instance, ramp, minStarsMatched: 15, CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.ExposureUsed.ShouldBe(TimeSpan.FromSeconds(5)); // last attempted rung
            source.AttemptCount.ShouldBe(3);
        }

        [Fact]
        public async Task GivenCancellationWhenProbeThenThrows()
        {
            var ramp = ImmutableArray.Create(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
            var source = new ScriptedCaptureSource([(false, 0), (true, 30)]);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await AdaptiveExposureRamp.ProbeAsync(source, NullPlateSolver.Instance, ramp, 15, cts.Token));
        }

        // --- CaptureSourceRanker ---

        [Fact]
        public void GivenFastGuideScopeAndSlowMainCamWhenRankThenGuideScopeWins()
        {
            // 50mm f/4 mini-guider with small pixel cam (3"/px) vs 200mm f/10 SCT (2"/px main cam).
            var guider = new FixedSpecsCaptureSource("Mini-guider", focalLengthMm: 200, apertureMm: 50, pixelSizeMicrons: 3.0); // f/4, ~3.1"/px
            var mainCam = new FixedSpecsCaptureSource("SCT main", focalLengthMm: 2000, apertureMm: 200, pixelSizeMicrons: 4.0); // f/10, 0.41"/px (clamped to 1)

            var ranking = CaptureSourceRanker.Rank([mainCam, guider], isMainCamera: src => src == mainCam);

            ranking.Length.ShouldBe(2);
            ranking[0].Source.DisplayName.ShouldBe("Mini-guider");
            ranking[0].IsMainCamera.ShouldBeFalse();
            ranking[1].Source.DisplayName.ShouldBe("SCT main");
        }

        [Fact]
        public void GivenEqualFRatioWhenRankThenMainCameraTieBreakWins()
        {
            // Two f/5 sources with identical pixel scale -> identical numeric score.
            var guideCam = new FixedSpecsCaptureSource("Guider", focalLengthMm: 250, apertureMm: 50, pixelSizeMicrons: 3.0);
            var mainCam = new FixedSpecsCaptureSource("Main",   focalLengthMm: 250, apertureMm: 50, pixelSizeMicrons: 3.0);

            var ranking = CaptureSourceRanker.Rank([guideCam, mainCam], isMainCamera: src => src == mainCam);

            ranking[0].Source.DisplayName.ShouldBe("Main");
            ranking[0].IsMainCamera.ShouldBeTrue();
        }

        [Fact]
        public void GivenSourceWithBadOpticsWhenScoreThenZero()
        {
            var bad = new FixedSpecsCaptureSource("Broken", focalLengthMm: 0, apertureMm: 0, pixelSizeMicrons: 3.0);
            CaptureSourceRanker.Score(bad).ShouldBe(0);
        }

        [Theory]
        [InlineData(0.5, 1.0)] // below band -> clamped up
        [InlineData(3.0, 3.0)] // in band
        [InlineData(8.0, 5.0)] // above band -> clamped down
        public void GivenPixelScaleOutOfBandWhenScoreThenClampedIntoBand(double pxScaleArcsec, double expectedClamp)
        {
            // Build a source with a known f-ratio and the requested pixel scale.
            // pixelScale = 206.265 * pixelSizeMicrons / focalLengthMm  =>  pixelSizeMicrons = pxScaleArcsec * focalLengthMm / 206.265
            const double focalLengthMm = 200;
            const double apertureMm = 50; // f/4
            double pixelSize = pxScaleArcsec * focalLengthMm / 206.265;
            var src = new FixedSpecsCaptureSource("Test", focalLengthMm, apertureMm, pixelSize);

            // Expected score = (1 / fRatio) * clamp = 0.25 * expectedClamp.
            double expectedScore = 0.25 * expectedClamp;
            CaptureSourceRanker.Score(src).ShouldBe(expectedScore, 1e-6);
        }

        // --- RefinementSmoother ---

        [Fact]
        public void GivenStableInputWhenSmoothThenSettledAfterWindowFills()
        {
            var sm = new RefinementSmoother(window: 5, settleSigmaArcmin: 0.5);
            // Feed identical samples (zero variance).
            var arcmin = 2.0 * Math.PI / (180.0 * 60.0); // 2 arcmin in radians
            (double az, double alt, bool settled) last = default;
            for (int i = 0; i < 5; i++)
            {
                last = sm.Update(arcmin, arcmin);
            }
            last.settled.ShouldBeTrue();
            // EWMA should converge toward the steady value (won't be exact since alpha != 1).
            last.az.ShouldBe(arcmin, arcmin * 0.3);
        }

        [Fact]
        public void GivenNoisyInputWhenSmoothThenNotSettled()
        {
            var sm = new RefinementSmoother(window: 5, settleSigmaArcmin: 0.5);
            // Alternate between 0 and 5 arcmin -> sigma >> 0.5 arcmin.
            var bigArcmin = 5.0 * Math.PI / (180.0 * 60.0);
            (double az, double alt, bool settled) last = default;
            for (int i = 0; i < 6; i++)
            {
                var v = (i % 2 == 0) ? 0 : bigArcmin;
                last = sm.Update(v, v);
            }
            last.settled.ShouldBeFalse();
        }

        [Fact]
        public void GivenStableThenJitterWhenSmoothThenSettledFlagFlips()
        {
            var sm = new RefinementSmoother(window: 4, settleSigmaArcmin: 0.5);
            var v = 1.0 * Math.PI / (180.0 * 60.0); // 1 arcmin
            // Stable region.
            for (int i = 0; i < 4; i++) sm.Update(v, v);
            var settledStable = sm.Update(v, v).IsSettled;
            settledStable.ShouldBeTrue();

            // User starts moving knobs -> wide swings.
            var jitter = 5.0 * Math.PI / (180.0 * 60.0);
            for (int i = 0; i < 4; i++) sm.Update(jitter * (i % 2 == 0 ? 1 : -1), v);
            var settledNow = sm.Update(jitter, v).IsSettled;
            settledNow.ShouldBeFalse();
        }

        [Fact]
        public void GivenWindowSizeOneWhenSmoothThenAlwaysSettled()
        {
            var sm = new RefinementSmoother(window: 1, settleSigmaArcmin: 0.5);
            var v = 10.0 * Math.PI / (180.0 * 60.0);
            // With window=1 the variance is zero by construction -> always settled.
            sm.Update(v, v).IsSettled.ShouldBeTrue();
            sm.Update(0, 0).IsSettled.ShouldBeTrue();
        }

        // --- SelectRotationRate ---

        [Fact]
        public void GivenDiscreteRateListWhenSelectThenSecondHighest()
        {
            var rates = new[]
            {
                new TianWen.Lib.Devices.AxisRate(0.5),
                new TianWen.Lib.Devices.AxisRate(2.0),
                new TianWen.Lib.Devices.AxisRate(8.0),
                new TianWen.Lib.Devices.AxisRate(20.0)
            };
            var picked = PolarAlignmentSession.SelectRotationRate(rates);
            picked.ShouldBe(8.0); // one below max
        }

        [Fact]
        public void GivenContinuousRangeWhenSelectThenSeventyPercentOfMax()
        {
            var rates = new[] { new TianWen.Lib.Devices.AxisRate(0.001, 30.0) };
            var picked = PolarAlignmentSession.SelectRotationRate(rates);
            picked.ShouldBe(21.0, 0.001); // 0.7 * 30
        }

        [Fact]
        public void GivenSingleDiscreteRateWhenSelectThenSeventyPercentOfMax()
        {
            var rates = new[] { new TianWen.Lib.Devices.AxisRate(15.0) };
            var picked = PolarAlignmentSession.SelectRotationRate(rates);
            picked.ShouldBe(15.0 * 0.7, 0.001);
        }

        [Fact]
        public void GivenEmptyRatesWhenSelectThenFallbackEightDegreesPerSec()
        {
            var rates = System.Array.Empty<TianWen.Lib.Devices.AxisRate>();
            PolarAlignmentSession.SelectRotationRate(rates).ShouldBe(8.0);
        }

        // --- Helper fakes ---

        /// <summary>Capture source that returns scripted (success, starsMatched) per attempt.</summary>
        private sealed class ScriptedCaptureSource : ICaptureSource
        {
            private readonly IReadOnlyList<(bool Success, int Stars)> _script;
            public int AttemptCount { get; private set; }

            public ScriptedCaptureSource(IReadOnlyList<(bool, int)> script) => _script = script;

            public string DisplayName => "Scripted";
            public double FocalLengthMm => 200;
            public double ApertureMm => 50;
            public double PixelSizeMicrons => 3.0;

            public ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(TimeSpan exposure, IPlateSolver solver, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (AttemptCount >= _script.Count)
                {
                    throw new InvalidOperationException("Script exhausted");
                }
                var (success, stars) = _script[AttemptCount++];
                return ValueTask.FromResult(new CaptureAndSolveResult(
                    Success: success, Wcs: null, WcsCenter: default, StarsMatched: stars,
                    ExposureUsed: exposure, FitsPath: null));
            }
        }

        /// <summary>Capture source with fixed optical specs; never invokes the solver.</summary>
        private sealed class FixedSpecsCaptureSource(string name, double focalLengthMm, double apertureMm, double pixelSizeMicrons) : ICaptureSource
        {
            public string DisplayName { get; } = name;
            public double FocalLengthMm { get; } = focalLengthMm;
            public double ApertureMm { get; } = apertureMm;
            public double PixelSizeMicrons { get; } = pixelSizeMicrons;

            public ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(TimeSpan exposure, IPlateSolver solver, CancellationToken ct)
                => throw new NotSupportedException();
        }

        /// <summary>Plate solver stub: never called by these tests.</summary>
        private sealed class NullPlateSolver : IPlateSolver
        {
            public static readonly NullPlateSolver Instance = new();
            public string Name => "Null";
            public float Priority => 0;
            public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
            public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03f, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
        }
    }
}
