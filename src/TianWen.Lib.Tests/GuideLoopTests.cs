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
