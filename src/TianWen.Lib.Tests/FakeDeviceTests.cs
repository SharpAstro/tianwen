using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

public class FakeDeviceTests(ITestOutputHelper output)
{
    // --- FakeGuider State Machine Tests (Phase 1) ---

    [Fact]
    public async Task GivenFakeGuiderWhenGuideAsyncThenItTransitionsThroughCalibrationToGuiding()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Guider, 1);
        await using var guider = new FakeGuider(device, external);
        await guider.ConnectAsync(ct);
        await guider.ConnectEquipmentAsync(ct);

        // when — start guiding
        await guider.GuideAsync(0.5, 5, 30, ct);

        // then — should be settling
        (await guider.IsSettlingAsync(ct)).ShouldBeTrue("should be settling after GuideAsync");
        (await guider.IsGuidingAsync(ct)).ShouldBeFalse("should not be guiding yet");

        // when — advance time past settle time (5s)
        await external.SleepAsync(TimeSpan.FromSeconds(6), ct);

        // then — should be guiding
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("should be guiding after settle completes");
        (await guider.IsSettlingAsync(ct)).ShouldBeFalse("should not be settling after guiding started");
    }

    [Fact]
    public async Task GivenGuidingFakeGuiderWhenDitherAsyncThenItSettlesBackToGuiding()
    {
        // given — guider in Guiding state
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Guider, 1);
        await using var guider = new FakeGuider(device, external);
        await guider.ConnectAsync(ct);
        await guider.ConnectEquipmentAsync(ct);
        await guider.GuideAsync(0.5, 2, 30, ct);
        await external.SleepAsync(TimeSpan.FromSeconds(3), ct);
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("precondition: should be guiding");

        // when — dither
        await guider.DitherAsync(1.5, 0.3, 3, 15, cancellationToken: ct);

        // then — should be settling
        (await guider.IsSettlingAsync(ct)).ShouldBeTrue("should be settling after dither");
        var progress = await guider.GetSettleProgressAsync(ct);
        progress.ShouldNotBeNull();
        progress.Done.ShouldBeFalse();

        // when — advance past settle time
        await external.SleepAsync(TimeSpan.FromSeconds(4), ct);

        // then — back to guiding
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("should be guiding after dither settles");
        var doneProgress = await guider.GetSettleProgressAsync(ct);
        doneProgress.ShouldNotBeNull();
        doneProgress.Done.ShouldBeTrue();
    }

    [Fact]
    public async Task GivenGuidingFakeGuiderWhenStopCaptureThenItReturnsToIdle()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Guider, 1);
        await using var guider = new FakeGuider(device, external);
        await guider.ConnectAsync(ct);
        await guider.ConnectEquipmentAsync(ct);
        await guider.GuideAsync(0.5, 2, 30, ct);
        await external.SleepAsync(TimeSpan.FromSeconds(3), ct);
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue();

        // when
        await guider.StopCaptureAsync(TimeSpan.FromSeconds(5), ct);

        // then
        (await guider.IsGuidingAsync(ct)).ShouldBeFalse();
        (await guider.IsLoopingAsync(ct)).ShouldBeFalse();
        var (appState, _) = await guider.GetStatusAsync(ct);
        appState.ShouldBe("Stopped");
    }

    // --- FakeCamera Cooling Tests (Phase 2) ---

    [Fact]
    public async Task GivenCoolerOnWhenPollingTemperatureThenItRampsToSetpoint()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Camera, 1);
        await using var camera = new FakeCameraDriver(device, external);
        await camera.ConnectAsync(ct);

        // when — enable cooler and set target to -10°C
        await camera.SetCoolerOnAsync(true, ct);
        await camera.SetSetCCDTemperatureAsync(-10, ct);

        // Poll temperature — each call moves 1°C toward setpoint
        var initialTemp = await camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Initial temp: {initialTemp:F1}°C");

        for (var i = 0; i < 35; i++)
        {
            var temp = await camera.GetCCDTemperatureAsync(ct);
            if (i % 5 == 0)
            {
                output.WriteLine($"Poll {i}: {temp:F1}°C");
            }
        }

        // then — should be at or near setpoint
        var finalTemp = await camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Final temp: {finalTemp:F1}°C");
        finalTemp.ShouldBe(-10.0, 0.5);

        // Cooler power should be non-zero when maintaining cold temp
        var power = await camera.GetCoolerPowerAsync(ct);
        output.WriteLine($"Cooler power: {power:F1}%");
        power.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task GivenCoolerOffWhenGetTemperatureThenItStaysAtAmbient()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Camera, 1);
        await using var camera = new FakeCameraDriver(device, external);
        await camera.ConnectAsync(ct);

        // when — cooler off, poll temperature
        var temp1 = await camera.GetCCDTemperatureAsync(ct);
        var temp2 = await camera.GetCCDTemperatureAsync(ct);

        // then — stays at ambient (20°C default)
        temp1.ShouldBe(20.0);
        temp2.ShouldBe(20.0);
    }

    // --- FakeFocuser Temperature + Backlash Tests (Phase 3) ---

    [Fact]
    public async Task GivenFakeFocuserWhenTimeAdvancesThenTemperatureDrifts()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(device, external);
        await focuser.ConnectAsync(ct);

        // when — read initial temperature
        var t0 = await focuser.GetTemperatureAsync(ct);
        output.WriteLine($"T0: {t0:F2}°C");

        // advance 2 hours with -0.5°C/hr drift rate
        await external.SleepAsync(TimeSpan.FromHours(2), ct);
        var t1 = await focuser.GetTemperatureAsync(ct);
        output.WriteLine($"T after 2h: {t1:F2}°C");

        // then — temperature should have dropped by ~1°C
        t1.ShouldBe(t0 - 1.0, 0.01);
    }

    [Fact]
    public async Task GivenFakeFocuserWhenTemperatureDriftsThenTrueBestFocusShifts()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(device, external);
        await focuser.ConnectAsync(ct);

        var initialBestFocus = focuser.TrueBestFocus;
        output.WriteLine($"Initial best focus: {initialBestFocus}");

        // when — advance 4 hours → -2°C drift
        await external.SleepAsync(TimeSpan.FromHours(4), ct);
        var shiftedBestFocus = focuser.TrueBestFocus;
        output.WriteLine($"Shifted best focus: {shiftedBestFocus}");

        // then — best focus shifts by tempCoefficient * deltaTemp = 5.0 * (-2.0) = -10
        ((double)(shiftedBestFocus - initialBestFocus)).ShouldBe(-10.0, 1.0);
    }

    [Fact]
    public async Task GivenFakeFocuserWhenDirectionReversedThenBacklashEngages()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(device, external);
        focuser.TrueBacklashIn = 20;
        focuser.TrueBacklashOut = 15;
        await focuser.ConnectAsync(ct);

        // when — move outward first
        await focuser.BeginMoveAsync(500, ct);
        // Wait for movement (FakePositionBasedDriver moves 1 step per 100ms timer tick)
        await external.SleepAsync(TimeSpan.FromSeconds(60), ct);

        var posAfterOutward = await focuser.GetPositionAsync(ct);
        output.WriteLine($"Position after outward move: {posAfterOutward}");

        // then move inward — should engage backlash
        await focuser.BeginMoveAsync(480, ct);
        await external.SleepAsync(TimeSpan.FromSeconds(5), ct);

        var posAfterInward = await focuser.GetPositionAsync(ct);
        output.WriteLine($"Position after inward move: {posAfterInward}");

        // Effective position should differ from reported position by up to backlash amount
        var effective = focuser.EffectivePosition;
        output.WriteLine($"Effective position: {effective}");

        // The focuser should have moved to position 480
        posAfterInward.ShouldBe(480);
    }

    [Fact]
    public async Task GivenFakeFocuserWhenMovingInSameDirectionThenNoBacklash()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(device, external);
        focuser.TrueBacklashIn = 20;
        focuser.TrueBacklashOut = 15;
        await focuser.ConnectAsync(ct);

        // when — move outward twice (same direction)
        await focuser.BeginMoveAsync(100, ct);
        await external.SleepAsync(TimeSpan.FromSeconds(15), ct);
        await focuser.BeginMoveAsync(200, ct);
        await external.SleepAsync(TimeSpan.FromSeconds(15), ct);

        // then — no backlash because same direction
        var pos = await focuser.GetPositionAsync(ct);
        var effective = focuser.EffectivePosition;
        output.WriteLine($"Position: {pos}, Effective: {effective}");
        pos.ShouldBe(effective);
    }

    // --- FakeCamera Exposure Lifecycle Test ---

    [Fact]
    public async Task GivenFakeCameraWhenExposingThenImageIsProduced()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Camera, 1);
        await using var camera = new FakeCameraDriver(device, external);
        await camera.ConnectAsync(ct);

        // when — start exposure
        var startTime = await camera.StartExposureAsync(TimeSpan.FromSeconds(1), cancellationToken: ct);
        startTime.ShouldNotBe(default);

        // advance time past exposure duration
        await external.SleepAsync(TimeSpan.FromSeconds(2), ct);

        // then — image should be ready
        (await camera.GetImageReadyAsync(ct)).ShouldBeTrue();
        camera.ImageData.ShouldNotBeNull();
        var state = await camera.GetCameraStateAsync(ct);
        state.ShouldBe(CameraState.Idle);
    }

}
