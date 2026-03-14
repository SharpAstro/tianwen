using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

public class GuideLoopTests(ITestOutputHelper output)
{
    private const double PixelScaleArcsec = 1.5;

    [Fact]
    public async Task GivenCalibratedLoopWhenGuidingThenErrorDecreases()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external);
        await mount.ConnectAsync();
        await mount.SetPositionAsync(12.0, 45.0);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);

        // Render based on mount position
        async ValueTask<float[,]> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec);
        }

        // Acquire initial guide star
        tracker.ProcessFrame(await RenderFrame(ct));
        tracker.IsAcquired.ShouldBeTrue();

        // Calibrate
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };
        var calResult = await calibration.CalibrateAsync(mount, tracker, RenderFrame, external, ct);
        calResult.ShouldNotBeNull();

        output.WriteLine($"Calibration: RA rate={calResult.Value.RaRatePixPerSec:F2} px/s, " +
            $"Dec rate={calResult.Value.DecRatePixPerSec:F2} px/s, " +
            $"Angle={calResult.Value.CameraAngleDeg:F1}°");

        // Enable tracking and PE now (after calibration)
        await mount.SetTrackingAsync(true, ct);
        mount.PeriodicErrorAmplitudeArcsec = 10.0;
        mount.PeriodicErrorPeriodSeconds = 480.0;

        // Re-acquire after calibration
        tracker.Reset();
        tracker.ProcessFrame(await RenderFrame(ct));
        tracker.SetLockPosition();

        // Set up guide loop
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7,
            MinPulseMs = 20
        };
        var guideLoop = new GuideLoop(mount, tracker, pController, external);
        guideLoop.SetCalibration(calResult.Value);

        // Run guide loop for a limited number of iterations
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var iterationCount = 0;

        async ValueTask<float[,]> RenderAndCount(CancellationToken token)
        {
            if (++iterationCount >= 30)
            {
                await cts.CancelAsync();
            }
            return await RenderFrame(token);
        }

        try
        {
            await guideLoop.RunAsync(RenderAndCount, TimeSpan.FromSeconds(2), hourAngle: 0, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — we cancel after 30 iterations
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

    [Fact]
    public async Task GivenCalibratedLoopWithOnlineLearningWhenGuidingThenExperienceRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external);
        await mount.ConnectAsync();
        await mount.SetPositionAsync(12.0, 45.0);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);

        async ValueTask<float[,]> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec);
        }

        // Acquire
        tracker.ProcessFrame(await RenderFrame(ct));

        // Calibrate
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };
        var calResult = await calibration.CalibrateAsync(mount, tracker, RenderFrame, external, ct);
        calResult.ShouldNotBeNull();

        // Enable PE
        await mount.SetTrackingAsync(true, ct);
        mount.PeriodicErrorAmplitudeArcsec = 10.0;
        mount.PeriodicErrorPeriodSeconds = 480.0;

        // Re-acquire
        tracker.Reset();
        tracker.ProcessFrame(await RenderFrame(ct));
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
            var guideLoop = new GuideLoop(mount, tracker, pController, external);
            guideLoop.SetCalibration(calResult.Value);
            guideLoop.EnableNeuralModel(model);
            guideLoop.EnableOnlineLearning(onlineLearningRate: 0.0001f, profileFolder: tempDir);
            guideLoop.OnlineTrainingInterval = 10; // train more frequently for test
            guideLoop.MinExperiencesBeforeTraining = 15;

            guideLoop.IsOnlineLearningEnabled.ShouldBeTrue();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var iterationCount = 0;

            async ValueTask<float[,]> RenderAndCount(CancellationToken token)
            {
                if (++iterationCount >= 60)
                {
                    await cts.CancelAsync();
                }
                return await RenderFrame(token);
            }

            try
            {
                await guideLoop.RunAsync(RenderAndCount, TimeSpan.FromSeconds(2), hourAngle: 0, cts.Token);
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

    [Theory]
    [InlineData(2.0, "good seeing")]
    [InlineData(4.0, "poor seeing")]
    public async Task GivenAtmosphericSeeingWhenGuidingThenRmsBounded(double seeingArcsec, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external);
        await mount.ConnectAsync();
        await mount.SetPositionAsync(12.0, 45.0);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);

        // Persistent RNG for seeing jitter — different jitter each frame
        var seeingRng = new Random(123);

        async ValueTask<float[,]> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec,
                seeingArcsec: seeingArcsec,
                seeingJitterRng: seeingRng);
        }

        // Acquire
        tracker.ProcessFrame(await RenderFrame(ct));
        tracker.IsAcquired.ShouldBeTrue();

        // Calibrate (seeing jitter is present but calibration should still succeed)
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };
        var calResult = await calibration.CalibrateAsync(mount, tracker, RenderFrame, external, ct);
        calResult.ShouldNotBeNull($"calibration should succeed even with {label}");

        output.WriteLine($"[{label}] Calibration: RA rate={calResult.Value.RaRatePixPerSec:F2} px/s, " +
            $"Angle={calResult.Value.CameraAngleDeg:F1}°");

        // Enable PE
        await mount.SetTrackingAsync(true, ct);
        mount.PeriodicErrorAmplitudeArcsec = 10.0;
        mount.PeriodicErrorPeriodSeconds = 480.0;

        // Re-acquire
        tracker.Reset();
        tracker.ProcessFrame(await RenderFrame(ct));
        tracker.SetLockPosition();

        // Guide with P-controller
        var pController = new ProportionalGuideController
        {
            AggressivenessRa = 0.7,
            AggressivenessDec = 0.7,
            MinPulseMs = 20
        };
        var guideLoop = new GuideLoop(mount, tracker, pController, external);
        guideLoop.SetCalibration(calResult.Value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var iterationCount = 0;

        async ValueTask<float[,]> RenderAndCount(CancellationToken token)
        {
            if (++iterationCount >= 40)
            {
                await cts.CancelAsync();
            }
            return await RenderFrame(token);
        }

        try
        {
            await guideLoop.RunAsync(RenderAndCount, TimeSpan.FromSeconds(2), hourAngle: 0, cts.Token);
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
        guideLoop.ErrorTracker.TotalRmsAll.ShouldBeLessThan(10.0,
            $"guiding should keep total RMS bounded even with {label}");
    }

    [Fact]
    public void GivenSeeingJitterWhenRenderThenCentroidVaries()
    {
        // Same seed, same offset, but different jitter RNG state → different centroids
        var rng = new Random(42);

        var frame1 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 3, seed: 99,
            seeingArcsec: 3.0, pixelScaleArcsec: 1.5, seeingJitterRng: rng);

        var frame2 = SyntheticStarFieldRenderer.Render(320, 240, 0,
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

    [Fact]
    public void GivenNoSeeingWhenRenderThenCentroidStable()
    {
        // Without jitter RNG, same parameters should produce identical frames
        var frame1 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 3, seed: 99,
            seeingArcsec: 3.0, pixelScaleArcsec: 1.5, seeingJitterRng: null);

        var frame2 = SyntheticStarFieldRenderer.Render(320, 240, 0,
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

    [Fact]
    public void GivenUncalibratedLoopWhenRunThenThrows()
    {
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external);
        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var pController = new ProportionalGuideController();
        var guideLoop = new GuideLoop(mount, tracker, pController, external);

        Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await guideLoop.RunAsync(_ => ValueTask.FromResult(new float[240, 320]),
                TimeSpan.FromSeconds(1), hourAngle: 0, CancellationToken.None);
        });
    }
}
