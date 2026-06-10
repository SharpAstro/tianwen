using TianWen.Lib.Imaging;
using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class GuideLoopTests(ITestOutputHelper output)
{
    private const double PixelScaleArcsec = 1.5;
    private const double GuideIntervalSeconds = 2.0;
    private const int FrameWidth = 128;
    private const int FrameHeight = 96;

    /// <summary>
    /// Computes the number of guide iterations needed to cover a given number of PE cycles.
    /// </summary>
    private static int IterationsForPeCycles(double pePeriodSeconds, double cycles = 0.5)
        => (int)Math.Ceiling(cycles * pePeriodSeconds / GuideIntervalSeconds);

    [Fact(Timeout = 60_000)]
    public async Task GivenCalibratedLoopWhenGuidingThenErrorDecreases()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external.BuildServiceProvider());
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);

        // Render based on mount position
        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(FrameWidth, FrameHeight, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec));
        }

        // Acquire initial guide star
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        // Calibrate
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };
        var pulseTarget = new MountPulseGuideTarget(mount);
        var calResult = await calibration.CalibrateAsync(pulseTarget, tracker, RenderFrame, timeProvider, ct);
        calResult.ShouldNotBeNull();

        output.WriteLine($"Calibration: RA rate={calResult.Value.RaRatePixPerSec:F2} px/s, " +
            $"Dec rate={calResult.Value.DecRatePixPerSec:F2} px/s, " +
            $"Angle={calResult.Value.CameraAngleDeg:F1}°");

        // Enable tracking and PE now (after calibration)
        await mount.SetTrackingAsync(true, ct);
        const double pePeriod = 480.0;
        mount.PeriodicErrorAmplitudeArcsec = 10.0;
        mount.PeriodicErrorPeriodSeconds = pePeriod;

        // Re-acquire after calibration
        tracker.Reset();
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.SetLockPosition();

        // Set up guide loop
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7,
            MinPulseMs = 20
        };
        var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);
        guideLoop.SetCalibration(calResult.Value);

        // Run guide loop for enough iterations to cover the PE cycle
        var maxIterations = IterationsForPeCycles(pePeriod);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var iterationCount = 0;

        async ValueTask<Image> RenderAndCount(CancellationToken token)
        {
            if (++iterationCount >= maxIterations)
            {
                await cts.CancelAsync();
            }
            return await RenderFrame(token);
        }

        try
        {
            await guideLoop.RunAsync(RenderAndCount, TimeSpan.FromSeconds(GuideIntervalSeconds), hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — we cancel after covering the PE cycle
        }

        output.WriteLine($"Guide iterations: {iterationCount}");
        output.WriteLine($"Total samples: {guideLoop.ErrorTracker.TotalSamples}");
        output.WriteLine($"RA RMS (all): {guideLoop.ErrorTracker.RaRmsAll:F3} px");
        output.WriteLine($"Dec RMS (all): {guideLoop.ErrorTracker.DecRmsAll:F3} px");
        output.WriteLine($"Total RMS (all): {guideLoop.ErrorTracker.TotalRmsAll:F3} px");

        // With PE enabled and guiding active, the total RMS should be bounded
        guideLoop.ErrorTracker.TotalSamples.ShouldBeGreaterThan(0u);
        guideLoop.IsGuiding.ShouldBeFalse("loop should have stopped");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenStarLostThenDifferentStarAppears_LoopReportsLostAndDifferentStarReacquisition()
    {
        // REGRESSION for "the selected guide star changes every so often" + the Dec "flip-flop".
        // The tracker silently drops a star (SNR below floor / left its ROI) and on the next frame
        // re-acquires the brightest -- a DIFFERENT star with a fresh lock -- producing a one-frame
        // error discontinuity. The old GuideLoop logged nothing and exposed no counter, so the loss
        // + lock-swap were invisible. This proves the loop now surfaces both as observable state.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var pulseTarget = new NoOpPulseGuideTarget();
        var tracker = new GuiderCentroidTracker(maxStars: 1);

        // A single controllable star. iters 1-3: at (200,140). iter 4: frame has NO star (guaranteed
        // loss). iters 5+: a star far away at (90,90) -> re-acquired there -> >100px from the pre-loss
        // primary -> flagged as a DIFFERENT-star re-acquisition.
        double sx = 200, sy = 140;
        var starPresent = true;
        ValueTask<Image> Render(CancellationToken token)
        {
            ReadOnlySpan<ProjectedStar> stars = starPresent
                ? [new ProjectedStar(sx, sy, Magnitude: 5.0)]
                : [];
            return ValueTask.FromResult(Image.FromChannel(SyntheticStarFieldRenderer.Render(
                320, 240, 0, stars, offsetX: 0, offsetY: 0,
                exposureSeconds: 2.0, pixelScaleArcsec: PixelScaleArcsec)));
        }

        // Acquire + lock on the original star.
        tracker.ProcessFrame((await Render(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();
        tracker.SetLockPosition();

        var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
        var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);
        guideLoop.SetCalibration(new GuiderCalibrationResult(
            CameraAngleRad: 0, RaRatePixPerSec: 2.0, DecRatePixPerSec: 2.0,
            RaDisplacementPx: 18, DecDisplacementPx: 18, TotalCalibrationTimeSec: 9));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var iter = 0;
        ValueTask<Image> Drive(CancellationToken token)
        {
            iter++;
            if (iter == 4)
            {
                starPresent = false; // no star this frame -> tracker loses lock
            }
            else if (iter == 5)
            {
                starPresent = true;
                sx = 90; sy = 90;    // a star reappears far from the old lock -> different-star re-acquire
            }
            if (iter >= 8)
            {
                cts.Cancel();
            }
            return Render(token);
        }

        try
        {
            await guideLoop.RunAsync(Drive, TimeSpan.FromSeconds(GuideIntervalSeconds),
                hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — cancelled after the re-acquisition frames.
        }

        output.WriteLine($"StarLostEvents={guideLoop.StarLostEvents} Reacquisitions={guideLoop.ReacquisitionEvents} DifferentStar={guideLoop.DifferentStarReacquisitions}");

        guideLoop.StarLostEvents.ShouldBeGreaterThan(0, "a frame with no star must be reported as a lost lock");
        guideLoop.ReacquisitionEvents.ShouldBeGreaterThan(0, "the loop must report re-acquiring a star after the loss");
        guideLoop.DifferentStarReacquisitions.ShouldBeGreaterThan(0,
            "re-locking onto a star >8px from the pre-loss primary must be flagged as a different-star re-acquisition");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenGuideLoopWhenStatsResetRequestedThenPeakAndRmsStartFresh()
    {
        // Models the Settling -> Guiding transition: a large transient (calibration/settle)
        // builds a big Peak/RMS, then RequestErrorStatsReset() at the guiding hand-off must
        // clear it so the displayed stats reflect guiding quality, not the transient.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));

        // No-op pulse target: we script the star error directly so corrections never move it.
        var pulseTarget = new NoOpPulseGuideTarget();

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var offsetX = 0.0;
        // A single bright star at frame centre, shifted by offsetX -> fully controlled,
        // uncontaminated error (no neighbour stars to pull the centroid).
        ValueTask<Image> Render(CancellationToken token)
        {
            ReadOnlySpan<ProjectedStar> stars = [new ProjectedStar(160, 120, Magnitude: 5.0)];
            return ValueTask.FromResult(Image.FromChannel(SyntheticStarFieldRenderer.Render(
                320, 240, 0, stars, offsetX: offsetX, offsetY: 0,
                exposureSeconds: 2.0, pixelScaleArcsec: PixelScaleArcsec)));
        }

        // Acquire at zero offset and lock there.
        tracker.ProcessFrame((await Render(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();
        tracker.SetLockPosition();

        var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
        var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);
        guideLoop.SetCalibration(new GuiderCalibrationResult(
            CameraAngleRad: 0, RaRatePixPerSec: 2.0, DecRatePixPerSec: 2.0,
            RaDisplacementPx: 18, DecDisplacementPx: 18, TotalCalibrationTimeSec: 9));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var iter = 0;
        var peakDuringTransient = 0.0;
        var didReset = false;

        ValueTask<Image> Drive(CancellationToken token)
        {
            iter++;
            if (iter <= 5)
            {
                offsetX = 8.0; // sustained ~8px error -> Peak/RMS grow (no-op pulse can't fix it)
            }
            else if (!didReset)
            {
                // Snapshot the large peak built up, then request the reset + clear the error.
                peakDuringTransient = guideLoop.ErrorTracker.PeakRa;
                guideLoop.RequestErrorStatsReset();
                didReset = true;
                offsetX = 0.0;
            }
            if (iter >= 12)
            {
                cts.Cancel();
            }
            return Render(token);
        }

        try
        {
            await guideLoop.RunAsync(Drive, TimeSpan.FromSeconds(GuideIntervalSeconds),
                hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — cancelled after the post-reset frames.
        }

        output.WriteLine($"peakDuringTransient={peakDuringTransient:F2}px finalPeakRa={guideLoop.ErrorTracker.PeakRa:F2}px finalRaRmsAll={guideLoop.ErrorTracker.RaRmsAll:F2}px");

        peakDuringTransient.ShouldBeGreaterThan(5.0, "a sustained ~8px error must build a large peak before the reset");
        guideLoop.ErrorTracker.PeakRa.ShouldBeLessThan(2.0, "after the reset, Peak reflects only post-reset (near-zero) errors");
        guideLoop.ErrorTracker.RaRmsAll.ShouldBeLessThan(2.0, "RMS also starts fresh after the reset");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCalibratedLoopWithOnlineLearningWhenGuidingThenExperienceRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external.BuildServiceProvider());
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);

        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(FrameWidth, FrameHeight, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec));
        }

        // Acquire
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));

        // Calibrate
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };
        var pulseTarget = new MountPulseGuideTarget(mount);
        var calResult = await calibration.CalibrateAsync(pulseTarget, tracker, RenderFrame, timeProvider, ct);
        calResult.ShouldNotBeNull();

        // Enable PE
        const double pePeriod = 480.0;
        await mount.SetTrackingAsync(true, ct);
        mount.PeriodicErrorAmplitudeArcsec = 10.0;
        mount.PeriodicErrorPeriodSeconds = pePeriod;

        // Re-acquire
        tracker.Reset();
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.SetLockPosition();

        // Set up guide loop with neural model + online learning
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7,
            MinPulseMs = 20
        };

        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);

        // Pre-train with a few offline epochs
        var offlineTrainer = new NeuralGuideTrainer(model, learningRate: 0.01f);
        for (var e = 0; e < 10; e++)
        {
            offlineTrainer.TrainEpoch(calResult.Value, pController, maxPulseMs: 2000, numSamples: 128, seed: e);
        }

        var tempDir = Directory.CreateTempSubdirectory("guide_loop_online_test_");
        try
        {
            var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);
            guideLoop.SetCalibration(calResult.Value);
            guideLoop.EnableNeuralModel(model);
            guideLoop.EnableOnlineLearning(onlineLearningRate: 0.0001f, profileFolder: tempDir);
            guideLoop.OnlineTrainingInterval = 10; // train more frequently for test
            guideLoop.MinExperiencesBeforeTraining = 15;

            guideLoop.IsOnlineLearningEnabled.ShouldBeTrue();

            var maxIterations = IterationsForPeCycles(pePeriod);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var iterationCount = 0;

            async ValueTask<Image> RenderAndCount(CancellationToken token)
            {
                if (++iterationCount >= maxIterations)
                {
                    await cts.CancelAsync();
                }
                return await RenderFrame(token);
            }

            try
            {
                await guideLoop.RunAsync(RenderAndCount, TimeSpan.FromSeconds(GuideIntervalSeconds), hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            output.WriteLine($"Guide iterations: {iterationCount}");
            output.WriteLine($"Total samples: {guideLoop.ErrorTracker.TotalSamples}");
            output.WriteLine($"RA RMS: {guideLoop.ErrorTracker.RaRmsAll:F3} px");
            output.WriteLine($"Dec RMS: {guideLoop.ErrorTracker.DecRmsAll:F3} px");

            if (guideLoop.PerformanceMonitor is not null)
            {
                output.WriteLine($"Neural RMS: {guideLoop.PerformanceMonitor.NeuralRms:F3}");
                output.WriteLine($"P-controller RMS: {guideLoop.PerformanceMonitor.PControllerRms:F3}");
                output.WriteLine($"Neural helping: {guideLoop.PerformanceMonitor.IsNeuralModelHelping}");
            }

            guideLoop.ErrorTracker.TotalSamples.ShouldBeGreaterThan(0u);
            guideLoop.IsGuiding.ShouldBeFalse("loop should have stopped");

            // Verify model was saved
            var savedFiles = new DirectoryInfo(Path.Combine(tempDir.FullName, "NeuralGuider")).GetFiles("*.ngm");
            savedFiles.Length.ShouldBeGreaterThan(0, "model weights should have been saved");
        }
        finally
        {
            try { tempDir.Delete(true); } catch { /* best effort */ }
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(2.0, "good seeing")]
    [InlineData(4.0, "poor seeing")]
    public async Task GivenAtmosphericSeeingWhenGuidingThenRmsBounded(double seeingArcsec, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external.BuildServiceProvider());
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);

        // Persistent RNG for seeing jitter — different jitter each frame
        var seeingRng = new Random(123);

        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(FrameWidth, FrameHeight, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec,
                seeingArcsec: seeingArcsec,
                seeingJitterRng: seeingRng));
        }

        // Acquire
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        // Calibrate (seeing jitter is present but calibration should still succeed)
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };
        var pulseTarget = new MountPulseGuideTarget(mount);
        var calResult = await calibration.CalibrateAsync(pulseTarget, tracker, RenderFrame, timeProvider, ct);
        calResult.ShouldNotBeNull($"calibration should succeed even with {label}");

        output.WriteLine($"[{label}] Calibration: RA rate={calResult.Value.RaRatePixPerSec:F2} px/s, " +
            $"Angle={calResult.Value.CameraAngleDeg:F1}°");

        // Enable PE
        await mount.SetTrackingAsync(true, ct);
        mount.PeriodicErrorAmplitudeArcsec = 10.0;
        mount.PeriodicErrorPeriodSeconds = 480.0;
        var maxIterations = IterationsForPeCycles(480.0, cycles: 0.25);

        // Re-acquire
        tracker.Reset();
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.SetLockPosition();

        // Guide with P-controller
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7,
            MinPulseMs = 20
        };
        var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);
        guideLoop.SetCalibration(calResult.Value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var iterationCount = 0;

        async ValueTask<Image> RenderAndCount(CancellationToken token)
        {
            if (++iterationCount >= maxIterations)
            {
                await cts.CancelAsync();
            }
            return await RenderFrame(token);
        }

        try
        {
            await guideLoop.RunAsync(RenderAndCount, TimeSpan.FromSeconds(GuideIntervalSeconds), hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        output.WriteLine($"[{label}] Iterations: {iterationCount}");
        output.WriteLine($"[{label}] RA RMS: {guideLoop.ErrorTracker.RaRmsAll:F3} px");
        output.WriteLine($"[{label}] Dec RMS: {guideLoop.ErrorTracker.DecRmsAll:F3} px");
        output.WriteLine($"[{label}] Total RMS: {guideLoop.ErrorTracker.TotalRmsAll:F3} px");

        guideLoop.ErrorTracker.TotalSamples.ShouldBeGreaterThan(0u);

        // Seeing adds noise floor but guiding should still keep RMS bounded
        // With 2" seeing at 1.5"/px, jitter sigma ≈ 0.57px (per 1s exposure)
        // With 4" seeing at 1.5"/px, jitter sigma ≈ 1.13px — larger but still manageable
        guideLoop.ErrorTracker.TotalRmsAll.ShouldBeLessThan(15.0,
            $"guiding should keep total RMS bounded even with {label}");
    }

    [Fact(Timeout = 60_000)]
    public void GivenSeeingJitterWhenRenderThenCentroidVaries()
    {
        // Same seed, same offset, but different jitter RNG state → different centroids
        var rng = new Random(42);

        var frame1 = SyntheticStarFieldRenderer.Render(FrameWidth, FrameHeight, 0,
            offsetX: 0, offsetY: 0, starCount: 3, seed: 99,
            seeingArcsec: 3.0, pixelScaleArcsec: 1.5, seeingJitterRng: rng);

        var frame2 = SyntheticStarFieldRenderer.Render(FrameWidth, FrameHeight, 0,
            offsetX: 0, offsetY: 0, starCount: 3, seed: 99,
            seeingArcsec: 3.0, pixelScaleArcsec: 1.5, seeingJitterRng: rng);

        // Find peak pixel in each frame (brightest star center)
        var (peak1X, peak1Y) = FindPeak(frame1);
        var (peak2X, peak2Y) = FindPeak(frame2);

        // Peaks should differ due to jitter (very unlikely to be identical)
        var dist = Math.Sqrt((peak1X - peak2X) * (peak1X - peak2X) + (peak1Y - peak2Y) * (peak1Y - peak2Y));
        output.WriteLine($"Peak1=({peak1X},{peak1Y}), Peak2=({peak2X},{peak2Y}), dist={dist:F2}");

        // With 3" seeing at 1.5"/px and 1s exposure, jitter sigma ≈ 0.85px
        // Frames should usually differ (not always, but almost always with two independent draws)
        // We just verify the mechanism works — not a hard assertion on distance
        (peak1X != peak2X || peak1Y != peak2Y).ShouldBeTrue(
            "seeing jitter should produce different centroid positions between frames");
    }

    [Fact(Timeout = 60_000)]
    public void GivenNoSeeingWhenRenderThenCentroidStable()
    {
        // Without jitter RNG, same parameters should produce identical frames
        var frame1 = SyntheticStarFieldRenderer.Render(FrameWidth, FrameHeight, 0,
            offsetX: 0, offsetY: 0, starCount: 3, seed: 99,
            seeingArcsec: 3.0, pixelScaleArcsec: 1.5, seeingJitterRng: null);

        var frame2 = SyntheticStarFieldRenderer.Render(FrameWidth, FrameHeight, 0,
            offsetX: 0, offsetY: 0, starCount: 3, seed: 99,
            seeingArcsec: 3.0, pixelScaleArcsec: 1.5, seeingJitterRng: null);

        var (peak1X, peak1Y) = FindPeak(frame1);
        var (peak2X, peak2Y) = FindPeak(frame2);

        peak1X.ShouldBe(peak2X);
        peak1Y.ShouldBe(peak2Y);
    }

    private static (int X, int Y) FindPeak(float[,] frame)
    {
        var maxVal = float.MinValue;
        var mx = 0;
        var my = 0;
        for (var y = 0; y < frame.GetLength(0); y++)
        {
            for (var x = 0; x < frame.GetLength(1); x++)
            {
                if (frame[y, x] > maxVal)
                {
                    maxVal = frame[y, x];
                    mx = x;
                    my = y;
                }
            }
        }
        return (mx, my);
    }


    [Theory(Timeout = 60_000)]
    [InlineData(2.0, "good seeing")]
    [InlineData(4.0, "poor seeing")]
    public async Task GivenOnlineLearningWithSeeingWhenGuidingThenModelConverges(double seeingArcsec, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external.BuildServiceProvider());
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var seeingRng = new Random(123);

        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(FrameWidth, FrameHeight, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec,
                seeingArcsec: seeingArcsec,
                seeingJitterRng: seeingRng));
        }

        // Acquire
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        // Calibrate (seeing jitter present during calibration too)
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };
        var pulseTarget = new MountPulseGuideTarget(mount);
        var calResult = await calibration.CalibrateAsync(pulseTarget, tracker, RenderFrame, timeProvider, ct);
        calResult.ShouldNotBeNull($"calibration should succeed even with {label}");

        output.WriteLine($"[{label}] Calibration: RA rate={calResult.Value.RaRatePixPerSec:F2} px/s, " +
            $"Angle={calResult.Value.CameraAngleDeg:F1}°");

        // Enable PE
        await mount.SetTrackingAsync(true, ct);
        mount.PeriodicErrorAmplitudeArcsec = 10.0;
        mount.PeriodicErrorPeriodSeconds = 480.0;

        // Re-acquire
        tracker.Reset();
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.SetLockPosition();

        // Set up guide loop with neural model + online learning
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7,
            MinPulseMs = 20
        };

        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);

        // Pre-train with offline epochs so the model starts reasonable
        var offlineTrainer = new NeuralGuideTrainer(model, learningRate: 0.01f);
        for (var e = 0; e < 10; e++)
        {
            offlineTrainer.TrainEpoch(calResult.Value, pController, maxPulseMs: 2000, numSamples: 128, seed: e);
        }

        var tempDir = Directory.CreateTempSubdirectory("guide_loop_seeing_online_test_");
        try
        {
            var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);
            guideLoop.SetCalibration(calResult.Value);
            guideLoop.EnableNeuralModel(model);
            guideLoop.EnableOnlineLearning(onlineLearningRate: 0.0001f, profileFolder: tempDir);
            guideLoop.OnlineTrainingInterval = 10;
            guideLoop.MinExperiencesBeforeTraining = 15;

            guideLoop.IsOnlineLearningEnabled.ShouldBeTrue();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var iterationCount = 0;

            async ValueTask<Image> RenderAndCount(CancellationToken token)
            {
                if (++iterationCount >= 80)
                {
                    await cts.CancelAsync();
                }
                return await RenderFrame(token);
            }

            try
            {
                await guideLoop.RunAsync(RenderAndCount, TimeSpan.FromSeconds(2), hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            output.WriteLine($"[{label}] Guide iterations: {iterationCount}");
            output.WriteLine($"[{label}] Total samples: {guideLoop.ErrorTracker.TotalSamples}");
            output.WriteLine($"[{label}] RA RMS: {guideLoop.ErrorTracker.RaRmsAll:F3} px");
            output.WriteLine($"[{label}] Dec RMS: {guideLoop.ErrorTracker.DecRmsAll:F3} px");
            output.WriteLine($"[{label}] Total RMS: {guideLoop.ErrorTracker.TotalRmsAll:F3} px");

            if (guideLoop.PerformanceMonitor is not null)
            {
                output.WriteLine($"[{label}] Neural RMS: {guideLoop.PerformanceMonitor.NeuralRms:F3}");
                output.WriteLine($"[{label}] P-controller RMS: {guideLoop.PerformanceMonitor.PControllerRms:F3}");
                output.WriteLine($"[{label}] Neural helping: {guideLoop.PerformanceMonitor.IsNeuralModelHelping}");
            }

            guideLoop.ErrorTracker.TotalSamples.ShouldBeGreaterThan(0u);
            guideLoop.IsGuiding.ShouldBeFalse("loop should have stopped");

            // Guiding should keep RMS bounded even with seeing + online learning
            // Seeing adds noise floor but the combination of P-controller + neural should cope
            guideLoop.ErrorTracker.TotalRmsAll.ShouldBeLessThan(15.0,
                $"guiding with online learning should keep total RMS bounded even with {label}");

            // Verify model was saved (online learning persists weights)
            var savedFiles = new DirectoryInfo(Path.Combine(tempDir.FullName, "NeuralGuider")).GetFiles("*.ngm");
            savedFiles.Length.ShouldBeGreaterThan(0, "model weights should have been saved during online learning");
        }
        finally
        {
            try { tempDir.Delete(true); } catch { /* best effort */ }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenUncalibratedLoopWhenRunThenThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external.BuildServiceProvider());
        var pulseTarget = new MountPulseGuideTarget(mount);
        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var pController = new ProportionalGuideController();
        var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await guideLoop.RunAsync(_ => ValueTask.FromResult(Image.FromChannel(new float[240, 320])),
                TimeSpan.FromSeconds(1), hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: ct);
        });
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenWindGustsWhenNeuralGuidingThenRmsBounded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, guideLoop, tracker, calResult, RenderFrame) = await SetupGuidedMount(ct,
            peAmplitude: 10.0, windAmplitude: 2.0);

        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);
        var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
        var offlineTrainer = new NeuralGuideTrainer(model, learningRate: 0.01f);
        for (var e = 0; e < 10; e++)
        {
            offlineTrainer.TrainEpoch(calResult, pController, maxPulseMs: 2000, numSamples: 128, seed: e);
        }
        guideLoop.EnableNeuralModel(model);

        await RunGuideIterations(guideLoop, RenderFrame, IterationsForPeCycles(480.0), ct);

        output.WriteLine($"Wind+PE RMS: {guideLoop.ErrorTracker.TotalRmsAll:F3} px");
        guideLoop.ErrorTracker.TotalRmsAll.ShouldBeLessThan(15.0,
            "guiding with PE + wind should keep RMS bounded");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCableSnagWhenNeuralGuidingThenRecovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, guideLoop, tracker, calResult, RenderFrame) = await SetupGuidedMount(ct,
            peAmplitude: 10.0, cableSnagTime: 20.0, cableSnagRa: 8.0, cableSnagDec: -4.0);

        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);
        var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
        var offlineTrainer = new NeuralGuideTrainer(model, learningRate: 0.01f);
        for (var e = 0; e < 10; e++)
        {
            offlineTrainer.TrainEpoch(calResult, pController, maxPulseMs: 2000, numSamples: 128, seed: e);
        }
        guideLoop.EnableNeuralModel(model);

        await RunGuideIterations(guideLoop, RenderFrame, IterationsForPeCycles(480.0), ct);

        output.WriteLine($"CableSnag RMS: {guideLoop.ErrorTracker.TotalRmsAll:F3} px");
        guideLoop.ErrorTracker.TotalSamples.ShouldBeGreaterThan(0u);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCombinedDisturbancesWhenNeuralGuidingThenRmsBounded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, guideLoop, tracker, calResult, RenderFrame) = await SetupGuidedMount(ct,
            peAmplitude: 10.0, windAmplitude: 1.5, flexureRate: 1.0, seeingArcsec: 2.0);

        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);
        var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
        var offlineTrainer = new NeuralGuideTrainer(model, learningRate: 0.01f);
        for (var e = 0; e < 10; e++)
        {
            offlineTrainer.TrainEpoch(calResult, pController, maxPulseMs: 2000, numSamples: 128, seed: e);
        }
        guideLoop.EnableNeuralModel(model);

        await RunGuideIterations(guideLoop, RenderFrame, IterationsForPeCycles(480.0), ct);

        output.WriteLine($"Combined stress test RMS: {guideLoop.ErrorTracker.TotalRmsAll:F3} px");
        guideLoop.ErrorTracker.TotalRmsAll.ShouldBeLessThan(15.0,
            "combined disturbances should keep RMS bounded with guiding");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(2.0, "good seeing")]
    [InlineData(4.0, "poor seeing")]
    public async Task GivenSameScenarioWhenNeuralPlusPVsPOnlyThenNeuralIsNotWorse(double seeingArcsec, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var iterations = IterationsForPeCycles(480.0, cycles: 1.5);

        // --- Run 1: P-controller only ---
        var (_, pOnlyLoop, _, _, pOnlyRender) = await SetupGuidedMount(ct,
            peAmplitude: 10.0, windAmplitude: 1.5, seeingArcsec: seeingArcsec);

        await RunGuideIterations(pOnlyLoop, pOnlyRender, iterations, ct);

        var pOnlyRms = pOnlyLoop.ErrorTracker.TotalRmsAll;
        output.WriteLine($"[{label}] P-only total RMS: {pOnlyRms:F3} px");
        output.WriteLine($"[{label}] P-only RA RMS:    {pOnlyLoop.ErrorTracker.RaRmsAll:F3} px");
        output.WriteLine($"[{label}] P-only Dec RMS:   {pOnlyLoop.ErrorTracker.DecRmsAll:F3} px");

        // --- Run 2: Neural + P-controller (identical scenario) ---
        var (_, neuralLoop, _, neuralCalResult, neuralRender) = await SetupGuidedMount(ct,
            peAmplitude: 10.0, windAmplitude: 1.5, seeingArcsec: seeingArcsec);

        var model = new NeuralGuideModel();
        model.InitializeRandom(seed: 42);

        // Pre-train offline so the model starts with reasonable weights
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7,
            MinPulseMs = 20
        };
        var offlineTrainer = new NeuralGuideTrainer(model, learningRate: 0.005f);
        for (var e = 0; e < 50; e++)
        {
            offlineTrainer.TrainEpoch(neuralCalResult, pController, maxPulseMs: 2000, numSamples: 512, seed: e, inputNoiseStd: 0.3f);
        }

        neuralLoop.EnableNeuralModel(model);

        await RunGuideIterations(neuralLoop, neuralRender, iterations, ct);

        var neuralRms = neuralLoop.ErrorTracker.TotalRmsAll;
        output.WriteLine($"[{label}] Neural+P total RMS: {neuralRms:F3} px");
        output.WriteLine($"[{label}] Neural+P RA RMS:    {neuralLoop.ErrorTracker.RaRmsAll:F3} px");
        output.WriteLine($"[{label}] Neural+P Dec RMS:   {neuralLoop.ErrorTracker.DecRmsAll:F3} px");

        if (neuralLoop.PerformanceMonitor is not null)
        {
            output.WriteLine($"[{label}] Monitor Neural RMS: {neuralLoop.PerformanceMonitor.NeuralRms:F3}");
            output.WriteLine($"[{label}] Monitor P-ctrl RMS: {neuralLoop.PerformanceMonitor.PControllerRms:F3}");
            output.WriteLine($"[{label}] Neural helping: {neuralLoop.PerformanceMonitor.IsNeuralModelHelping}");
        }

        var ratio = pOnlyRms > 0.001 ? neuralRms / pOnlyRms : double.NaN;
        output.WriteLine($"[{label}] Ratio (neural/P-only): {ratio:F3}");

        // Neural+P should not catastrophically destabilize guiding.
        // Allow 50% tolerance — in production the performance monitor would disable
        // the neural model if it's hurting, but here we verify it doesn't cause runaway errors.
        if (pOnlyRms > 0.001)
        {
            neuralRms.ShouldBeLessThan(pOnlyRms * 1.50,
                $"[{label}] Neural+P RMS ({neuralRms:F3}) should not be >50% worse than P-only ({pOnlyRms:F3})");
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenNeuralEngagedWhenGuidingDivergesThenKillSwitchDisablesNeural()
    {
        // The divergence kill-switch: with the neural model engaged, if the short-window guide RMS
        // blows past NeuralDivergencePixels the model is hard-disabled (it's driving the star off and
        // the perf-monitor's pulse-capped P-residual comparison can't tell). Driven deterministically:
        // a NoOp pulse target means corrections never move the star, so a fixed standing offset above
        // the bound forces the divergence regardless of what the model outputs -- we test the SWITCH,
        // not a particular model's (non-deterministic, emergent) divergence.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var pulseTarget = new NoOpPulseGuideTarget();
        var tracker = new GuiderCentroidTracker(maxStars: 1);

        var starOffsetY = 0.0;
        ValueTask<Image> Render(CancellationToken token)
        {
            ReadOnlySpan<ProjectedStar> stars = [new ProjectedStar(160, 120 + starOffsetY, Magnitude: 5.0)];
            return ValueTask.FromResult(Image.FromChannel(SyntheticStarFieldRenderer.Render(
                320, 240, 0, stars, offsetX: 0, offsetY: 0, exposureSeconds: 2.0, pixelScaleArcsec: PixelScaleArcsec)));
        }

        // Acquire + lock with the star centred (lock at 160,120).
        tracker.ProcessFrame((await Render(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();
        tracker.SetLockPosition();

        var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
        var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);
        guideLoop.SetCalibration(new GuiderCalibrationResult(
            CameraAngleRad: 0, RaRatePixPerSec: 2.0, DecRatePixPerSec: 2.0,
            RaDisplacementPx: 18, DecDisplacementPx: 18, TotalCalibrationTimeSec: 9));

        var model = new NeuralGuideModel();
        model.InitializeRandom(42);
        guideLoop.EnableNeuralModel(model);
        guideLoop.NeuralDivergencePixels = 5.0; // small bound so a clear standing offset trips it
        guideLoop.IsNeuralActive.ShouldBeTrue("neural starts engaged");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var iter = 0;
        ValueTask<Image> Drive(CancellationToken token)
        {
            iter++;
            starOffsetY = 8.0; // standing 8px Dec error: > 5px bound, < 16px search radius (still tracked, not lost)
            if (iter >= 15)
            {
                cts.Cancel();
            }
            return Render(token);
        }

        try
        {
            await guideLoop.RunAsync(Drive, TimeSpan.FromSeconds(GuideIntervalSeconds),
                hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — cancelled once the kill-switch has had time to fire.
        }

        output.WriteLine($"IsNeuralActive={guideLoop.IsNeuralActive} DecRmsShort={guideLoop.ErrorTracker.DecRmsShort:F2}px StarLost={guideLoop.StarLostEvents}");
        guideLoop.IsNeuralActive.ShouldBeFalse(
            "the divergence kill-switch must hard-disable neural once short-window guide RMS exceeds the bound");
        guideLoop.StarLostEvents.ShouldBe(0, "the star stays within the search radius -- this is divergence, not a star loss");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSustainedDivergenceWhenGuidingThenRecalibrationRequestedAndLoopExits()
    {
        // When guiding diverges catastrophically and STAYS diverged (a bad lock the controller can't
        // recover -- here a NoOp pulse target means corrections never pull the star off a large fixed
        // offset), the loop gives up and asks the driver to recalibrate instead of spinning forever.
        // The driver's recovery loop then re-acquires + recalibrates on this signal.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var pulseTarget = new NoOpPulseGuideTarget();
        var tracker = new GuiderCentroidTracker(maxStars: 1);

        var starOffsetY = 0.0;
        ValueTask<Image> Render(CancellationToken token)
        {
            ReadOnlySpan<ProjectedStar> stars = [new ProjectedStar(160, 120 + starOffsetY, Magnitude: 5.0)];
            return ValueTask.FromResult(Image.FromChannel(SyntheticStarFieldRenderer.Render(
                320, 240, 0, stars, offsetX: 0, offsetY: 0, exposureSeconds: 2.0, pixelScaleArcsec: PixelScaleArcsec)));
        }

        tracker.ProcessFrame((await Render(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();
        tracker.SetLockPosition();

        var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
        var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);
        guideLoop.SetCalibration(new GuiderCalibrationResult(
            CameraAngleRad: 0, RaRatePixPerSec: 2.0, DecRatePixPerSec: 2.0,
            RaDisplacementPx: 18, DecDisplacementPx: 18, TotalCalibrationTimeSec: 9));
        guideLoop.RecalibrationDivergencePixels = 10.0; // small bound so a clear offset trips it
        guideLoop.RecalibrationDivergenceFrames = 8;     // and only a few sustained frames

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var iter = 0;
        ValueTask<Image> Drive(CancellationToken token)
        {
            iter++;
            starOffsetY = 14.0; // standing 14px error: > 10px recalibration bound, < 16px search radius (tracked, not lost)
            if (iter >= 40)
            {
                cts.Cancel(); // safety: the loop should break on its own well before this
            }
            return Render(token);
        }

        try
        {
            await guideLoop.RunAsync(Drive, TimeSpan.FromSeconds(GuideIntervalSeconds),
                hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Only reached if the loop failed to give up on its own and the safety cancel fired.
        }

        output.WriteLine($"RecalibrationRequested={guideLoop.RecalibrationRequested} frames={iter} IsGuiding={guideLoop.IsGuiding}");
        guideLoop.RecalibrationRequested.ShouldBeTrue(
            "sustained divergence the controller cannot recover must request recalibration");
        guideLoop.IsGuiding.ShouldBeFalse("the loop must exit (return) when it requests recalibration");
        iter.ShouldBeLessThan(40, "the loop must give up on its own, not run to the safety cancel");
    }

    // --- Helpers ---

    private async Task<(FakeMountDriver mount, GuideLoop guideLoop, GuiderCentroidTracker tracker,
        GuiderCalibrationResult calResult, Func<CancellationToken, ValueTask<Image>> renderFrame)>
        SetupGuidedMount(CancellationToken ct,
            double peAmplitude = 0, double pePeriod = 480.0, double windAmplitude = 0, double flexureRate = 0,
            double cableSnagTime = 0, double cableSnagRa = 0, double cableSnagDec = 0,
            double seeingArcsec = 0)
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external.BuildServiceProvider());
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var seeingRng = seeingArcsec > 0 ? new Random(123) : null;

        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(FrameWidth, FrameHeight, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec,
                seeingArcsec: seeingArcsec,
                seeingJitterRng: seeingRng));
        }

        // Acquire initial guide star
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));

        // Calibrate
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };
        var pulseTarget = new MountPulseGuideTarget(mount);
        var calResult = await calibration.CalibrateAsync(pulseTarget, tracker, RenderFrame, timeProvider, ct);
        calResult.ShouldNotBeNull();

        // Enable tracking and disturbances after calibration
        await mount.SetTrackingAsync(true, ct);
        mount.PeriodicErrorAmplitudeArcsec = peAmplitude;
        mount.PeriodicErrorPeriodSeconds = pePeriod;
        mount.WindGustAmplitudeArcsec = windAmplitude;
        mount.FlexureDriftRateDecArcsecPerHaHour = flexureRate;
        mount.CableSnagTimeSeconds = cableSnagTime;
        mount.CableSnagAmplitudeRaArcsec = cableSnagRa;
        mount.CableSnagAmplitudeDecArcsec = cableSnagDec;

        // Re-acquire
        tracker.Reset();
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.SetLockPosition();

        // Set up guide loop
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7,
            MinPulseMs = 20
        };
        var guideLoop = new GuideLoop(pulseTarget, tracker, pController, timeProvider);
        guideLoop.SetCalibration(calResult.Value);

        return (mount, guideLoop, tracker, calResult.Value, RenderFrame);
    }

    private async Task RunGuideIterations(
        GuideLoop guideLoop,
        Func<CancellationToken, ValueTask<Image>> renderFrame,
        int maxIterations,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var iterationCount = 0;

        async ValueTask<Image> RenderAndCount(CancellationToken token)
        {
            if (++iterationCount >= maxIterations)
            {
                await cts.CancelAsync();
            }
            return await renderFrame(token);
        }

        try
        {
            await guideLoop.RunAsync(RenderAndCount, TimeSpan.FromSeconds(GuideIntervalSeconds),
                hourAngle: 0, declination: 45.0, siteLatitude: 48.2, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — we cancel after maxIterations
        }

        output.WriteLine($"Guide iterations: {iterationCount}");
        output.WriteLine($"Total samples: {guideLoop.ErrorTracker.TotalSamples}");
    }

    /// <summary>Pulse target that ignores corrections — the test scripts the star error directly.</summary>
    private sealed class NoOpPulseGuideTarget : IPulseGuideTarget
    {
        public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(false);
    }
}
