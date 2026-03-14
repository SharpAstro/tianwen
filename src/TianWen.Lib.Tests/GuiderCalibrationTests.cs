using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

public class GuiderCalibrationTests(ITestOutputHelper output)
{
    private const double PixelScaleArcsec = 1.5;

    [Fact]
    public async Task GivenFakeMountWhenCalibrateThenRatesAndAngleCorrect()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external);
        await mount.ConnectAsync();
        await mount.SetPositionAsync(12.0, 45.0);

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
        async ValueTask<float[,]> RenderFrame(CancellationToken token)
        {
            var ra = await mount.GetRightAscensionAsync(token);
            var dec = await mount.GetDeclinationAsync(token);
            // Convert position delta to pixel offset
            // RA: hours → arcsec → pixels; Dec: degrees → arcsec → pixels
            var deltaRaArcsec = (ra - initialRa) * 15.0 * 3600.0; // hours to arcsec
            var deltaDecArcsec = (dec - initialDec) * 3600.0;
            var offsetX = deltaRaArcsec / PixelScaleArcsec;
            var offsetY = deltaDecArcsec / PixelScaleArcsec;
            return SyntheticStarFieldRenderer.Render(320, 240, 0,
                offsetX: offsetX, offsetY: offsetY,
                starCount: 5, seed: 42,
                pixelScaleArcsec: PixelScaleArcsec);
        }

        // Initial frame — acquire guide star
        tracker.ProcessFrame(await RenderFrame(ct));
        tracker.IsAcquired.ShouldBeTrue();

        // Calibrate
        var result = await calibration.CalibrateAsync(
            mount, tracker, RenderFrame, external, ct);

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

    [Fact]
    public async Task GivenSavedCalibrationWhenValidateThenValid()
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
        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3
        };

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

        // Full calibration first
        tracker.ProcessFrame(await RenderFrame(ct));
        var calResult = await calibration.CalibrateAsync(mount, tracker, RenderFrame, external, ct);
        calResult.ShouldNotBeNull();

        output.WriteLine($"Calibrated: angle={calResult.Value.CameraAngleDeg:F1}°, RA rate={calResult.Value.RaRatePixPerSec:F3}");

        // Re-acquire for validation
        tracker.Reset();
        tracker.ProcessFrame(await RenderFrame(ct));

        // Validate with same mount/conditions — should be Valid
        var result = await calibration.ValidateAsync(calResult.Value, mount, tracker, RenderFrame, external, ct);
        output.WriteLine($"Validation result: {result}");
        result.ShouldBe(CalibrationValidationResult.Valid);
    }

    [Fact]
    public async Task GivenCalibrationResultWhenTransformThenMountAxesSeparated()
    {
        // Given a 45° camera rotation
        var cal = new GuiderCalibrationResult(
            CameraAngleRad: Math.PI / 4, // 45°
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

}
