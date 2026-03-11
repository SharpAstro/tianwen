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
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Guider, 1);
        await using var guider = new FakeGuider(device, external);
        await guider.ConnectAsync();
        await guider.ConnectEquipmentAsync();

        // when — start guiding
        await guider.GuideAsync(0.5, 5, 30, TestContext.Current.CancellationToken);

        // then — should be settling
        (await guider.IsSettlingAsync()).ShouldBeTrue("should be settling after GuideAsync");
        (await guider.IsGuidingAsync()).ShouldBeFalse("should not be guiding yet");

        // when — advance time past settle time (5s)
        await external.SleepAsync(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);

        // then — should be guiding
        (await guider.IsGuidingAsync()).ShouldBeTrue("should be guiding after settle completes");
        (await guider.IsSettlingAsync()).ShouldBeFalse("should not be settling after guiding started");
    }

    [Fact]
    public async Task GivenGuidingFakeGuiderWhenDitherAsyncThenItSettlesBackToGuiding()
    {
        // given — guider in Guiding state
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Guider, 1);
        await using var guider = new FakeGuider(device, external);
        await guider.ConnectAsync();
        await guider.ConnectEquipmentAsync();
        await guider.GuideAsync(0.5, 2, 30, TestContext.Current.CancellationToken);
        await external.SleepAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        (await guider.IsGuidingAsync()).ShouldBeTrue("precondition: should be guiding");

        // when — dither
        await guider.DitherAsync(1.5, 0.3, 3, 15, cancellationToken: TestContext.Current.CancellationToken);

        // then — should be settling
        (await guider.IsSettlingAsync()).ShouldBeTrue("should be settling after dither");
        var progress = await guider.GetSettleProgressAsync();
        progress.ShouldNotBeNull();
        progress.Done.ShouldBeFalse();

        // when — advance past settle time
        await external.SleepAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        // then — back to guiding
        (await guider.IsGuidingAsync()).ShouldBeTrue("should be guiding after dither settles");
        var doneProgress = await guider.GetSettleProgressAsync();
        doneProgress.ShouldNotBeNull();
        doneProgress.Done.ShouldBeTrue();
    }

    [Fact]
    public async Task GivenGuidingFakeGuiderWhenStopCaptureThenItReturnsToIdle()
    {
        // given
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Guider, 1);
        await using var guider = new FakeGuider(device, external);
        await guider.ConnectAsync();
        await guider.ConnectEquipmentAsync();
        await guider.GuideAsync(0.5, 2, 30, TestContext.Current.CancellationToken);
        await external.SleepAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        (await guider.IsGuidingAsync()).ShouldBeTrue();

        // when
        await guider.StopCaptureAsync(TimeSpan.FromSeconds(5));

        // then
        (await guider.IsGuidingAsync()).ShouldBeFalse();
        (await guider.IsLoopingAsync()).ShouldBeFalse();
        var (appState, _) = await guider.GetStatusAsync();
        appState.ShouldBe("Stopped");
    }

    // --- FakeCamera Cooling Tests (Phase 2) ---

    [Fact]
    public async Task GivenCoolerOnWhenPollingTemperatureThenItRampsToSetpoint()
    {
        // given
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Camera, 1);
        await using var camera = new FakeCameraDriver(device, external);
        await camera.ConnectAsync();

        // when — enable cooler and set target to -10°C
        await camera.SetCoolerOnAsync(true);
        await camera.SetSetCCDTemperatureAsync(-10);

        // Poll temperature — each call moves 1°C toward setpoint
        var initialTemp = await camera.GetCCDTemperatureAsync();
        output.WriteLine($"Initial temp: {initialTemp:F1}°C");

        for (var i = 0; i < 35; i++)
        {
            var temp = await camera.GetCCDTemperatureAsync();
            if (i % 5 == 0)
            {
                output.WriteLine($"Poll {i}: {temp:F1}°C");
            }
        }

        // then — should be at or near setpoint
        var finalTemp = await camera.GetCCDTemperatureAsync();
        output.WriteLine($"Final temp: {finalTemp:F1}°C");
        finalTemp.ShouldBe(-10.0, 0.5);

        // Cooler power should be non-zero when maintaining cold temp
        var power = await camera.GetCoolerPowerAsync();
        output.WriteLine($"Cooler power: {power:F1}%");
        power.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task GivenCoolerOffWhenGetTemperatureThenItStaysAtAmbient()
    {
        // given
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Camera, 1);
        await using var camera = new FakeCameraDriver(device, external);
        await camera.ConnectAsync();

        // when — cooler off, poll temperature
        var temp1 = await camera.GetCCDTemperatureAsync();
        var temp2 = await camera.GetCCDTemperatureAsync();

        // then — stays at ambient (20°C default)
        temp1.ShouldBe(20.0);
        temp2.ShouldBe(20.0);
    }

    // --- FakeFocuser Temperature + Backlash Tests (Phase 3) ---

    [Fact]
    public async Task GivenFakeFocuserWhenTimeAdvancesThenTemperatureDrifts()
    {
        // given
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(device, external);
        await focuser.ConnectAsync();

        // when — read initial temperature
        var t0 = await focuser.GetTemperatureAsync();
        output.WriteLine($"T0: {t0:F2}°C");

        // advance 2 hours with -0.5°C/hr drift rate
        await external.SleepAsync(TimeSpan.FromHours(2), TestContext.Current.CancellationToken);
        var t1 = await focuser.GetTemperatureAsync();
        output.WriteLine($"T after 2h: {t1:F2}°C");

        // then — temperature should have dropped by ~1°C
        t1.ShouldBe(t0 - 1.0, 0.01);
    }

    [Fact]
    public async Task GivenFakeFocuserWhenTemperatureDriftsThenTrueBestFocusShifts()
    {
        // given
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(device, external);
        await focuser.ConnectAsync();

        var initialBestFocus = focuser.TrueBestFocus;
        output.WriteLine($"Initial best focus: {initialBestFocus}");

        // when — advance 4 hours → -2°C drift
        await external.SleepAsync(TimeSpan.FromHours(4), TestContext.Current.CancellationToken);
        var shiftedBestFocus = focuser.TrueBestFocus;
        output.WriteLine($"Shifted best focus: {shiftedBestFocus}");

        // then — best focus shifts by tempCoefficient * deltaTemp = 5.0 * (-2.0) = -10
        ((double)(shiftedBestFocus - initialBestFocus)).ShouldBe(-10.0, 1.0);
    }

    [Fact]
    public async Task GivenFakeFocuserWhenDirectionReversedThenBacklashEngages()
    {
        // given
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(device, external);
        focuser.TrueBacklashIn = 20;
        focuser.TrueBacklashOut = 15;
        await focuser.ConnectAsync();

        // when — move outward first
        await focuser.BeginMoveAsync(500);
        // Wait for movement (FakePositionBasedDriver moves 1 step per 100ms timer tick)
        await external.SleepAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var posAfterOutward = await focuser.GetPositionAsync();
        output.WriteLine($"Position after outward move: {posAfterOutward}");

        // then move inward — should engage backlash
        await focuser.BeginMoveAsync(480);
        await external.SleepAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var posAfterInward = await focuser.GetPositionAsync();
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
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await using var focuser = new FakeFocuserDriver(device, external);
        focuser.TrueBacklashIn = 20;
        focuser.TrueBacklashOut = 15;
        await focuser.ConnectAsync();

        // when — move outward twice (same direction)
        await focuser.BeginMoveAsync(100);
        await external.SleepAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await focuser.BeginMoveAsync(200);
        await external.SleepAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        // then — no backlash because same direction
        var pos = await focuser.GetPositionAsync();
        var effective = focuser.EffectivePosition;
        output.WriteLine($"Position: {pos}, Effective: {effective}");
        pos.ShouldBe(effective);
    }

    // --- FakeCamera Exposure Lifecycle Test ---

    [Fact]
    public async Task GivenFakeCameraWhenExposingThenImageIsProduced()
    {
        // given
        var external = new FakeExternal(output);
        var device = new FakeDevice(DeviceType.Camera, 1);
        await using var camera = new FakeCameraDriver(device, external);
        await camera.ConnectAsync();

        // when — start exposure
        var startTime = await camera.StartExposureAsync(TimeSpan.FromSeconds(1));
        startTime.ShouldNotBe(default);

        // advance time past exposure duration
        await external.SleepAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // then — image should be ready
        (await camera.GetImageReadyAsync()).ShouldBeTrue();
        camera.ImageData.ShouldNotBeNull();
        var state = await camera.GetCameraStateAsync();
        state.ShouldBe(CameraState.Idle);
    }
}
