using TianWen.Lib.Imaging;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class GuiderCalibrationTests(ITestOutputHelper output)
{
    private const double PixelScaleArcsec = 1.5;

    [Fact(Timeout = 60_000)]
    public async Task GivenFakeMountWhenCalibrateThenRatesAndAngleCorrect()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external.BuildServiceProvider());
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        // Record initial position — guide camera sees star shift relative to this
        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };

        // Render based on mount position change (pulse guides move the mount)
        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            // Convert position delta to pixel offset
            // RA: hours → arcsec → pixels; Dec: degrees → arcsec → pixels
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0; // hours to arcsec
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec));
        }

        // Initial frame — acquire guide star
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        // Calibrate
        var pulseTarget = new MountPulseGuideTarget(mount);
        var result = await calibration.CalibrateAsync(
            pulseTarget, tracker, RenderFrame, timeProvider, ct);

        result.ShouldNotBeNull();

        output.WriteLine($"Camera angle: {result.Value.CameraAngleDeg:F1}°");
        output.WriteLine($"RA rate: {result.Value.RaRatePixPerSec:F3} px/s");
        output.WriteLine($"Dec rate: {result.Value.DecRatePixPerSec:F3} px/s");
        output.WriteLine($"RA displacement: {result.Value.RaDisplacementPx:F2} px");
        output.WriteLine($"Dec displacement: {result.Value.DecDisplacementPx:F2} px");

        // Rates should be positive
        result.Value.RaRatePixPerSec.ShouldBeGreaterThan(0);
        result.Value.DecRatePixPerSec.ShouldBeGreaterThan(0);

        // Camera angle should be near 0 (guide camera aligned with RA axis)
        Math.Abs(result.Value.CameraAngleDeg).ShouldBeLessThan(30,
            "camera angle should be near 0 for aligned camera");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSavedCalibrationWhenValidateThenValid()
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
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };

        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec));
        }

        // Full calibration first
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        var pulseTarget = new MountPulseGuideTarget(mount);
        var calResult = await calibration.CalibrateAsync(pulseTarget, tracker, RenderFrame, timeProvider, ct);
        calResult.ShouldNotBeNull();

        output.WriteLine($"Calibrated: angle={calResult.Value.CameraAngleDeg:F1}°, RA rate={calResult.Value.RaRatePixPerSec:F3}");

        // Re-acquire for validation
        tracker.Reset();
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));

        // Validate with same mount/conditions — should be Valid
        var result = await calibration.ValidateAsync(calResult.Value, pulseTarget, tracker, RenderFrame, timeProvider, ct);
        output.WriteLine($"Validation result: {result}");
        result.ShouldBe(CalibrationValidationResult.Valid);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCalibrationResultWhenTransformThenMountAxesSeparated()
    {
        // Given a 45° camera rotation
        var cal = new GuiderCalibrationResult(
            CameraAngleRad: Math.PI / 4, // 45°
            DecAngleRad: Math.PI / 4 + Math.PI / 2, // Dec orthogonal at +90deg
            RaRatePixPerSec: 5.0,
            DecRatePixPerSec: 5.0,
            RaDisplacementPx: 15.0,
            DecDisplacementPx: 15.0,
            TotalCalibrationTimeSec: 6.0);

        // Pure X error (1 pixel right)
        var (ra, dec) = cal.TransformToMountAxes(1.0, 0.0);
        // At 45°: ra = cos(45) = 0.707, dec = -sin(45) = -0.707
        ra.ShouldBe(Math.Cos(Math.PI / 4), 0.01);
        dec.ShouldBe(-Math.Sin(Math.PI / 4), 0.01);

        // Pure Y error (1 pixel down)
        var (ra2, dec2) = cal.TransformToMountAxes(0.0, 1.0);
        // At 45°: ra = sin(45) = 0.707, dec = cos(45) = 0.707
        ra2.ShouldBe(Math.Sin(Math.PI / 4), 0.01);
        dec2.ShouldBe(Math.Cos(Math.PI / 4), 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenDecBacklashWhenAdaptiveClearThenDetectsMovementAfterBacklashConsumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        // Guide rate is ~10 arcsec/s, so 25 arcsec backlash needs ~3 pulses to clear
        var mount = new FakeMountDriver(device, external.BuildServiceProvider()) { DecBacklashArcsec = 25.0 };
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        // Pre-pulse South to arm backlash on reversal to North
        var pulseTarget = new MountPulseGuideTarget(mount);
        await pulseTarget.PulseGuideAsync(GuideDirection.South, TimeSpan.FromSeconds(1), ct);
        await timeProvider.SleepAsync(TimeSpan.FromSeconds(2), ct);

        // Update initial position after pre-pulse
        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            BacklashClearingEnabled = true,
            MaxBacklashClearingSteps = 15,
            BacklashMovementThresholdPx = 0.3
        };

        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec));
        }

        // Acquire guide star
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        // Run adaptive backlash clearing in North direction (reversal from South)
        var result = await calibration.ClearBacklashAsync(
            pulseTarget, tracker, RenderFrame, timeProvider, GuideDirection.North, ct);

        output.WriteLine($"Backlash clearing: StepsUsed={result.StepsUsed}, MovementDetected={result.MovementDetected}");

        result.MovementDetected.ShouldBeTrue("movement should be detected after backlash is consumed");
        result.StepsUsed.ShouldBeGreaterThan(1, "should need more than 1 step to consume 25 arcsec backlash");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenNoBacklashWhenAdaptiveClearThenDetectsMovementImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external.BuildServiceProvider()) { DecBacklashArcsec = 0 };
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        // Pre-pulse South then update baseline
        var pulseTarget = new MountPulseGuideTarget(mount);
        await pulseTarget.PulseGuideAsync(GuideDirection.South, TimeSpan.FromSeconds(1), ct);
        await timeProvider.SleepAsync(TimeSpan.FromSeconds(2), ct);

        initialRa = await mount.GetRightAscensionAsync(ct);
        initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            BacklashClearingEnabled = true,
            MaxBacklashClearingSteps = 10,
            BacklashMovementThresholdPx = 0.3
        };

        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec));
        }

        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        var result = await calibration.ClearBacklashAsync(
            pulseTarget, tracker, RenderFrame, timeProvider, GuideDirection.North, ct);

        output.WriteLine($"Backlash clearing: StepsUsed={result.StepsUsed}, MovementDetected={result.MovementDetected}");

        result.MovementDetected.ShouldBeTrue("movement should be detected immediately with no backlash");
        result.StepsUsed.ShouldBe(1, "should detect movement on the very first step");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMaxStepsExceededWhenClearThenReturnsMovementNotDetected()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external.BuildServiceProvider()) { DecBacklashArcsec = 1000.0 };
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        // Pre-pulse South to arm backlash
        var pulseTarget = new MountPulseGuideTarget(mount);
        await pulseTarget.PulseGuideAsync(GuideDirection.South, TimeSpan.FromSeconds(1), ct);
        await timeProvider.SleepAsync(TimeSpan.FromSeconds(2), ct);

        initialRa = await mount.GetRightAscensionAsync(ct);
        initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            BacklashClearingEnabled = true,
            MaxBacklashClearingSteps = 3,
            BacklashMovementThresholdPx = 0.3
        };

        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec));
        }

        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        var result = await calibration.ClearBacklashAsync(
            pulseTarget, tracker, RenderFrame, timeProvider, GuideDirection.North, ct);

        output.WriteLine($"Backlash clearing: StepsUsed={result.StepsUsed}, MovementDetected={result.MovementDetected}");

        result.MovementDetected.ShouldBeFalse("pathological backlash should not be cleared in 3 steps");
        result.StepsUsed.ShouldBe(3);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenDecBacklashWhenFullCalibrationThenRatesAccurate()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Mount, 1);
        // Guide rate is ~10 arcsec/s, so 25 arcsec backlash needs ~3 pulses to clear
        var mount = new FakeMountDriver(device, external.BuildServiceProvider()) { DecBacklashArcsec = 25.0 };
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        // Pre-pulse South to arm Dec backlash on reversal
        var pulseTarget = new MountPulseGuideTarget(mount);
        await pulseTarget.PulseGuideAsync(GuideDirection.South, TimeSpan.FromSeconds(1), ct);
        await timeProvider.SleepAsync(TimeSpan.FromSeconds(2), ct);

        var initialRa = await mount.GetRightAscensionAsync(ct);
        var initialDec = await mount.GetDeclinationAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3,
            BacklashClearingEnabled = true,
            MaxBacklashClearingSteps = 15,
            BacklashMovementThresholdPx = 0.3
        };

        async ValueTask<Image> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0;
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return Image.FromChannel(SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec));
        }

        // Acquire guide star
        tracker.ProcessFrame((await RenderFrame(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        var result = await calibration.CalibrateAsync(
            pulseTarget, tracker, RenderFrame, timeProvider, ct);

        result.ShouldNotBeNull();

        output.WriteLine($"Camera angle: {result.Value.CameraAngleDeg:F1}°");
        output.WriteLine($"RA rate: {result.Value.RaRatePixPerSec:F3} px/s");
        output.WriteLine($"Dec rate: {result.Value.DecRatePixPerSec:F3} px/s");
        output.WriteLine($"RA displacement: {result.Value.RaDisplacementPx:F2} px");
        output.WriteLine($"Dec displacement: {result.Value.DecDisplacementPx:F2} px");
        output.WriteLine($"Backlash steps RA: {result.Value.BacklashClearingStepsRa}");
        output.WriteLine($"Backlash steps Dec: {result.Value.BacklashClearingStepsDec}");

        // Rates should be positive
        result.Value.RaRatePixPerSec.ShouldBeGreaterThan(0);
        result.Value.DecRatePixPerSec.ShouldBeGreaterThan(0);

        // Camera angle should be near 0 (guide camera aligned with RA axis)
        Math.Abs(result.Value.CameraAngleDeg).ShouldBeLessThan(30,
            "camera angle should be near 0 for aligned camera");

        // Dec backlash clearing should have used some steps
        result.Value.BacklashClearingStepsDec.ShouldBeGreaterThan(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSavedCalibrationAndWeightsWhenLoadedThenMatchOriginal()
    {
        var ct = TestContext.Current.CancellationToken;

        // Save calibration + neural model to a temp directory
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"tianwen_cal_test_{Guid.NewGuid():N}"));
        try
        {
            var originalCalibration = new GuiderCalibrationResult(
                CameraAngleRad: -Math.PI,
                DecAngleRad: -Math.PI / 2.0,
                RaRatePixPerSec: 1.95,
                DecRatePixPerSec: 1.97,
                RaDisplacementPx: 17.5,
                DecDisplacementPx: 17.7,
                TotalCalibrationTimeSec: 18.0);

            var originalModel = new NeuralGuideModel();
            originalModel.InitializeRandom(123); // non-default seed
            var originalWeights = originalModel.ExportParameters();

            await NeuralGuideModelPersistence.SaveAsync(originalModel, originalCalibration, tempDir, ct);

            // Verify exactly one .ngm file was created
            var ngmFiles = new DirectoryInfo(Path.Combine(tempDir.FullName, "NeuralGuider")).GetFiles("*.ngm");
            ngmFiles.Length.ShouldBe(1);

            // Load into a fresh model
            var loadedModel = new NeuralGuideModel();
            var loadedCalibration = await NeuralGuideModelPersistence.TryLoadAsync(loadedModel, tempDir, ct);

            loadedCalibration.ShouldNotBeNull();

            // Calibration should match
            loadedCalibration.Value.CameraAngleRad.ShouldBe(originalCalibration.CameraAngleRad, 0.001);
            loadedCalibration.Value.DecAngleRad.ShouldBe(originalCalibration.DecAngleRad, 0.001);
            loadedCalibration.Value.RaRatePixPerSec.ShouldBe(originalCalibration.RaRatePixPerSec, 0.001);
            loadedCalibration.Value.DecRatePixPerSec.ShouldBe(originalCalibration.DecRatePixPerSec, 0.001);
            loadedCalibration.Value.RaDisplacementPx.ShouldBe(originalCalibration.RaDisplacementPx, 0.001);
            loadedCalibration.Value.DecDisplacementPx.ShouldBe(originalCalibration.DecDisplacementPx, 0.001);

            // Weights should match
            var loadedWeights = loadedModel.ExportParameters();
            loadedWeights.Length.ShouldBe(originalWeights.Length);
            for (var i = 0; i < loadedWeights.Length; i++)
            {
                loadedWeights[i].ShouldBe(originalWeights[i], 1e-6f,
                    $"Weight [{i}] mismatch");
            }

            output.WriteLine($"Calibration round-trip: angle={loadedCalibration.Value.CameraAngleDeg:F1}°, " +
                $"RA rate={loadedCalibration.Value.RaRatePixPerSec:F3}, {loadedWeights.Length} weights verified");

            // Save again — should replace the old file, not accumulate
            await NeuralGuideModelPersistence.SaveAsync(loadedModel, loadedCalibration.Value, tempDir, ct);
            ngmFiles = new DirectoryInfo(Path.Combine(tempDir.FullName, "NeuralGuider")).GetFiles("*.ngm");
            ngmFiles.Length.ShouldBe(1, "old .ngm files should be cleaned up after save");
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(recursive: true);
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenNonOrthogonalAxesWhenCalibrateThenRejected()
    {
        // Reject-if-dodgy (fresh calibration): a sweep where North moves the star the SAME way as
        // West is degenerate -- RA and Dec are mechanically perpendicular, so near-parallel axes
        // signal a bad sweep (backlash, a star swap, a non-responding axis) that would produce
        // garbage corrections. CalibrateAsync must return null, not hand back a usable result.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var rig = new DirectionalStarRig();

        ValueTask<Image> Render(CancellationToken token)
        {
            ReadOnlySpan<ProjectedStar> stars = [new ProjectedStar(160 + rig.X, 120 + rig.Y, Magnitude: 5.0)];
            return ValueTask.FromResult(Image.FromChannel(SyntheticStarFieldRenderer.Render(
                320, 240, 0, stars, offsetX: 0, offsetY: 0, exposureSeconds: 2.0, pixelScaleArcsec: PixelScaleArcsec)));
        }

        tracker.ProcessFrame((await Render(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        var calibration = new GuiderCalibration { CalibrationPulseDuration = TimeSpan.FromSeconds(1), CalibrationSteps = 3 };

        // Degenerate: North responds along the SAME sensor direction as West (axes ~parallel).
        rig.WestResponse = (-1.0, 0.0);
        rig.NorthResponse = (-1.0, 0.0);
        var degenerate = await calibration.CalibrateAsync(rig, tracker, Render, timeProvider, ct);
        degenerate.ShouldBeNull("a non-orthogonal (degenerate) calibration must be rejected");

        // Sanity: with perpendicular axes the very same machinery accepts the calibration, so the
        // gate is not just refusing everything.
        rig.X = 0;
        rig.Y = 0;
        tracker.Reset();
        tracker.ProcessFrame((await Render(ct)).GetChannelArray(0));
        rig.NorthResponse = (0.0, -1.0);
        var ok = await calibration.CalibrateAsync(rig, tracker, Render, timeProvider, ct);
        ok.ShouldNotBeNull("a perpendicular calibration must still be accepted (the gate must not false-reject)");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenConeErrorRigWhenValidateSavedCalibrationThenValid()
    {
        // A rig with real cone error / camera tilt shows the RA and Dec sweep directions 5-30deg
        // off perpendicular on the sensor. Fresh calibration accepts that (the degeneracy gate is
        // deliberately generous), so re-validation of the SAVED calibration on the same rig must
        // accept it too -- using the tight session-drift tolerance here meant every session
        // re-calibrated from scratch and threw away the neural model continuity.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var rig = new DirectionalStarRig();

        ValueTask<Image> Render(CancellationToken token)
        {
            ReadOnlySpan<ProjectedStar> stars = [new ProjectedStar(160 + rig.X, 120 + rig.Y, Magnitude: 5.0)];
            return ValueTask.FromResult(Image.FromChannel(SyntheticStarFieldRenderer.Render(
                320, 240, 0, stars, offsetX: 0, offsetY: 0, exposureSeconds: 2.0, pixelScaleArcsec: PixelScaleArcsec)));
        }

        tracker.ProcessFrame((await Render(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        var calibration = new GuiderCalibration { CalibrationPulseDuration = TimeSpan.FromSeconds(1), CalibrationSteps = 3 };

        // West sweeps along 180deg; North along 105deg -> 75deg axis separation = 15deg from
        // perpendicular. Inside the 30deg fresh tolerance, outside the old 5deg validation gate.
        var northAngleRad = 105.0 * Math.PI / 180.0;
        rig.WestResponse = (-1.0, 0.0);
        rig.NorthResponse = (Math.Cos(northAngleRad), Math.Sin(northAngleRad));

        var calResult = await calibration.CalibrateAsync(rig, tracker, Render, timeProvider, ct);
        calResult.ShouldNotBeNull("15deg non-orthogonality is within the fresh calibration tolerance");

        // Re-acquire and validate the just-saved calibration on the unchanged rig.
        tracker.Reset();
        tracker.ProcessFrame((await Render(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        var result = await calibration.ValidateAsync(calResult.Value, rig, tracker, Render, timeProvider, ct);
        output.WriteLine($"Validation result: {result}");
        result.ShouldBe(CalibrationValidationResult.Valid,
            "a calibration that passed the fresh orthogonality gate must re-validate on the same rig");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)] // measured Dec angle (PHD2 default)
    [InlineData(true)]  // assume Dec orthogonal (sense still from measurement)
    public async Task GivenNorthClockwiseFromWestWhenCalibrateThenDecSenseFromMeasurement(bool assumeOrthogonal)
    {
        // Southern-hemisphere / flipped-sensor rig: the North sweep moves the star CLOCKWISE from
        // the West sweep on the sensor (West = -X, North = +Y). The old fixed "+90deg from RA"
        // transform would invert Dec here and run the axis away (errDec -31 -> -92px in the field).
        // The fix takes the Dec SENSE from the measured North sweep -- in BOTH modes -- so a drift
        // toward North is corrected with a South pulse, and guiding converges.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var rig = new DirectionalStarRig { WestResponse = (-1.0, 0.0), NorthResponse = (0.0, 1.0) };

        ValueTask<Image> Render(CancellationToken token)
        {
            ReadOnlySpan<ProjectedStar> stars = [new ProjectedStar(160 + rig.X, 120 + rig.Y, Magnitude: 5.0)];
            return ValueTask.FromResult(Image.FromChannel(SyntheticStarFieldRenderer.Render(
                320, 240, 0, stars, offsetX: 0, offsetY: 0, exposureSeconds: 2.0, pixelScaleArcsec: PixelScaleArcsec)));
        }

        tracker.ProcessFrame((await Render(ct)).GetChannelArray(0));
        tracker.IsAcquired.ShouldBeTrue();

        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3,
            AssumeDecOrthogonal = assumeOrthogonal,
        };

        var result = await calibration.CalibrateAsync(rig, tracker, Render, timeProvider, ct);
        result.ShouldNotBeNull();

        // The measured Dec sense is clockwise from West: sin(DecAngle - CameraAngle) < 0.
        var cross = Math.Sin(result.Value.DecAngleRad - result.Value.CameraAngleRad);
        output.WriteLine($"camAngle={result.Value.CameraAngleDeg:F1} decAngle={result.Value.DecAngleDeg:F1} cross={cross:F3}");
        cross.ShouldBeLessThan(0, "the Dec sense must come from the measured (clockwise) North sweep, not an assumed +90deg");

        // A star that drifted toward North (+Y on this rig) must be corrected with a SOUTH pulse
        // (negative DecPulseMs); the old inverted sense would have commanded North and diverged.
        var controller = new ProportionalGuideController { MinPulseMs = 0 };
        var corr = controller.Compute(result.Value, 0, 5.0);
        corr.DecPulseMs.ShouldBeLessThan(0, "a drift toward North must be corrected toward South -- not amplified");
    }

    [Fact]
    public void SweepLinearityScoresMonotonicHighAndWanderingLow()
    {
        // The fresh-calibration linearity gate: a clean monotonic sweep scores ~1; a star that
        // zig-zagged the same net distance scores low (unstable rate) and would be rejected.
        var origin = new CalibrationStep(0, 0);

        var monotonic = ImmutableArray.Create(
            new CalibrationStep(3, 0), new CalibrationStep(6, 0), new CalibrationStep(9, 0));
        GuiderCalibration.SweepLinearity(origin, monotonic, netDisplacementPx: 9.0)
            .ShouldBeGreaterThan(0.95, "a straight monotonic sweep is ~perfectly linear");

        var wandering = ImmutableArray.Create(
            new CalibrationStep(8, 0), new CalibrationStep(2, 0), new CalibrationStep(9, 0));
        GuiderCalibration.SweepLinearity(origin, wandering, netDisplacementPx: 9.0)
            .ShouldBeLessThan(0.6, "a wandering sweep (unstable rate) must score below the reject threshold");
    }

    /// <summary>Pulse target that moves a single tracked star by a per-axis response vector (camera px).</summary>
    private sealed class DirectionalStarRig : IPulseGuideTarget
    {
        public double X;
        public double Y;
        public double RatePxPerSec { get; set; } = 3.0;
        public (double Dx, double Dy) WestResponse { get; set; } = (-1.0, 0.0);
        public (double Dx, double Dy) NorthResponse { get; set; } = (0.0, -1.0);

        public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
        {
            var px = RatePxPerSec * duration.TotalSeconds;
            switch (direction)
            {
                case GuideDirection.West: X += WestResponse.Dx * px; Y += WestResponse.Dy * px; break;
                case GuideDirection.East: X -= WestResponse.Dx * px; Y -= WestResponse.Dy * px; break;
                case GuideDirection.North: X += NorthResponse.Dx * px; Y += NorthResponse.Dy * px; break;
                case GuideDirection.South: X -= NorthResponse.Dx * px; Y -= NorthResponse.Dy * px; break;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
