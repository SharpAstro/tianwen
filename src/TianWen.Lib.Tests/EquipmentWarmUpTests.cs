using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The out-of-session warm-up (<c>IDeviceHub.WarmAndDisconnectAsync</c>) runs the session's own
/// <see cref="CameraCoolingRamp"/> and waits on the injected <see cref="ITimeProvider"/>, never on the wall clock. It used
/// to call <c>Task.Delay</c>, then to step 2 degrees every 30 s of its own, which was a second answer to how fast a sensor
/// may warm (#1840).
/// </summary>
public class EquipmentWarmUpTests(ITestOutputHelper output)
{
    private static readonly Uri CameraUri = new("Camera://FakeDevice/cam1");

    /// <summary>A clock whose every sleep returns at once and is recorded in <paramref name="sleeps"/>: what a ramp waited is their sum.</summary>
    internal static ITimeProvider RecordingClock(List<TimeSpan> sleeps)
    {
        var clock = Substitute.For<ITimeProvider>();
        clock.SleepAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => { sleeps.Add(call.ArgAt<TimeSpan>(0)); return ValueTask.CompletedTask; });
        return clock;
    }

    [Fact(Timeout = 30_000)]
    public async Task TheRampStepsEveryFifteenSecondsOnTheInjectedClockThenSettlesAndDisconnects()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var hub = external.BuildServiceProvider().GetRequiredService<IDeviceHub>();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);
        // the sensor is taken down to the setpoint first
        for (var i = 0; i < 40; i++) await camera.GetCCDTemperatureAsync(ct);

        var sleeps = new List<TimeSpan>();
        await hub.WarmAndDisconnectAsync(device.DeviceUri, RecordingClock(sleeps), FakeExternal.CreateLogger(output), force: false, ct);

        sleeps.Count.ShouldBeGreaterThan(1);
        sleeps[^1].ShouldBe(TimeSpan.FromSeconds(2), "the settle after the cooler goes off");
        sleeps[..^1].ShouldAllBe(s => s == CameraCoolingRamp.StepInterval, "the session's 15 s step, not a ramp of its own");
        hub.TryGetConnectedDriver<ICameraDriver>(device.DeviceUri, out _).ShouldBeFalse("it disconnects when asked");
        (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse();
    }

    /// <summary>
    /// The QHY294PROC case: no heat sink, a cooler at 0 % power below an unreachable +25 C. The ramp ends in a few steps
    /// rather than at the safety cap.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ACoolerStuckAtNoPowerBelowAnUnreachableTargetEndsInAFewSteps()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var hub = external.BuildServiceProvider().GetRequiredService<IDeviceHub>();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (FakeCameraDriver)await hub.ConnectAsync(device, ct);
        camera.MakeHeatsinkless(ambientC: 18, sensorCannotPassAmbient: true);
        // cool the sensor first, so the ramp has a walk up from -10 C and then stalls at the 18 C ambient
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);
        for (var i = 0; i < 40; i++) await camera.GetCCDTemperatureAsync(ct);

        var sleeps = new List<TimeSpan>();
        await hub.WarmAndCoolerOffAsync(device.DeviceUri, RecordingClock(sleeps), FakeExternal.CreateLogger(output), ct);

        // At the default 2 C/min a step is half a degree: the walk is about 56 steps; the cap is twice the planned 60 steps
        // plus the four confirming ones (124 sleeps). Ending on power adds a few.
        sleeps.Count.ShouldBeGreaterThan(40, "the ramp walked the sensor up");
        sleeps.Count.ShouldBeLessThanOrEqualTo(56 + 5 + 1,"the walk, a few steps at no power and the settle, not the 2x safety cap");
        (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse();
        hub.TryGetCoolerIntent(device.DeviceUri, out var intent).ShouldBeTrue();
        intent.ShouldBe(CoolerIntent.Off);
    }

    /// <summary>
    /// A cooled camera that cannot report its cooler power (an ASCOM or Alpaca camera with CanGetCoolerPower false) is still
    /// warmed: the ramp must not read the missing power as "no cooler working" and cut a cold sensor off with no ramp.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ACameraThatCannotReportCoolerPowerIsStillRampedWarm()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var hub = external.BuildServiceProvider().GetRequiredService<IDeviceHub>();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (FakeCameraDriver)await hub.ConnectAsync(device, ct);
        camera.MakeCoolerPowerUnreadable();
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);
        for (var i = 0; i < 40; i++) await camera.GetCCDTemperatureAsync(ct);

        var sleeps = new List<TimeSpan>();
        await hub.WarmAndCoolerOffAsync(device.DeviceUri, RecordingClock(sleeps), FakeExternal.CreateLogger(output), ct);

        sleeps.Count.ShouldBeGreaterThan(5, "it ramped on the sensor instead of cutting the cooler off cold");
        sleeps[..^1].ShouldAllBe(s => s == CameraCoolingRamp.StepInterval);
        (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task ACameraAnotherRunHoldsIsRefusedBeforeTheSetpointIsTouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var hub = external.BuildServiceProvider().GetRequiredService<IDeviceHub>();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);
        hub.TryAcquireLease(device.DeviceUri, "the imaging session", out _).ShouldBeTrue();

        var sleeps = new List<TimeSpan>();
        await Should.ThrowAsync<DeviceLeasedException>(async () =>
            await hub.WarmAndDisconnectAsync(device.DeviceUri, RecordingClock(sleeps), FakeExternal.CreateLogger(output), force: false, ct).AsTask());

        sleeps.ShouldBeEmpty();
        (await camera.GetSetCCDTemperatureAsync(ct)).ShouldBe(-10);
        (await camera.GetCoolerOnAsync(ct)).ShouldBeTrue();
    }

    private static (ICameraDriver Camera, IDeviceHub Hub, List<TimeSpan> Sleeps, ITimeProvider Clock) NoHeatSinkRig(double ccdC, SensorAmbientReading? preCool)
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.CanSetCCDTemperature.Returns(true);
        camera.CanGetCoolerOn.Returns(true);
        camera.CanGetHeatsinkTemperature.Returns(false);
        camera.GetCoolerOnAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetCCDTemperatureAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(ccdC));

        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetConnectedDriver<ICameraDriver>(CameraUri, out Arg.Any<ICameraDriver?>())
            .Returns(call => { call[1] = camera; return true; });
        hub.TryGetPreCoolReading(CameraUri, out Arg.Any<SensorAmbientReading>())
            .Returns(call => { call[1] = preCool ?? default; return preCool is not null; });

        var sleeps = new List<TimeSpan>();
        var clock = Substitute.For<ITimeProvider>();
        clock.SleepAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => { sleeps.Add(call.ArgAt<TimeSpan>(0)); return ValueTask.CompletedTask; });
        return (camera, hub, sleeps, clock);
    }

    /// <summary>
    /// The 2026-10-27 QHYminiCam8M: no heat sink, a fan that always runs, 12 C settled before cooling, and 11.5 C at 0 %
    /// power now. It waited out the 15 minute cap for a +25 C it could never reach (#1842).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ASettledReadingBeforeCoolingEndsTheRampAtOnce()
    {
        var (camera, hub, sleeps, clock) = NoHeatSinkRig(11.5, new SensorAmbientReading(12.0, true, DateTimeOffset.UnixEpoch));

        await hub.WarmAndDisconnectAsync(CameraUri, clock, FakeExternal.CreateLogger(output), force: false, TestContext.Current.CancellationToken);

        sleeps.ShouldBe([TimeSpan.FromSeconds(2)], "no step to wait for, only the settle after the cooler goes off");
        await camera.DidNotReceive().SetSetCCDTemperatureAsync(Arg.Any<double>(), Arg.Any<CancellationToken>());
        await camera.Received(1).SetCoolerOnAsync(false, Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 30_000)]
    public async Task AnUnsettledReadingStillRampsToTheFallback()
    {
        var (camera, hub, _, clock) = NoHeatSinkRig(11.5, new SensorAmbientReading(12.0, false, DateTimeOffset.UnixEpoch));

        await hub.WarmAndDisconnectAsync(CameraUri, clock, FakeExternal.CreateLogger(output), force: false, TestContext.Current.CancellationToken);

        await camera.Received().SetSetCCDTemperatureAsync(Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 30_000)]
    public async Task TheSourceOfTheTargetIsLogged()
    {
        var (_, hub, _, clock) = NoHeatSinkRig(11.5, new SensorAmbientReading(12.1, true, DateTimeOffset.UnixEpoch));
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);

        await hub.WarmAndDisconnectAsync(CameraUri, clock, logger, force: false, TestContext.Current.CancellationToken);

        var messages = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(Microsoft.Extensions.Logging.ILogger.Log))
            .Select(c => c.GetArguments()[2]?.ToString() ?? "")
            .ToList();
        messages.ShouldContain(m => m.Contains("12.1") && m.Contains("settled before cooling"));
    }

    /// <summary>
    /// A camera with no cooler (an ASI462MC) disconnects at once. It reads a sensor temperature and ignores a setpoint, so
    /// the ramp used to step toward the +25 °C fallback from a 20 °C room for its whole 15 minute cap, and a quit after a
    /// planetary capture waited for all of it (the ZWO live check, 2026-09-28).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ACameraWithNoCoolerDisconnectsWithoutARamp()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.CanGetCCDTemperature.Returns(true);
        camera.GetCCDTemperatureAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(20.0));

        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetConnectedDriver<ICameraDriver>(CameraUri, out Arg.Any<ICameraDriver?>())
            .Returns(call => { call[1] = camera; return true; });

        var sleeps = new List<TimeSpan>();
        await hub.WarmAndDisconnectAsync(CameraUri, RecordingClock(sleeps), FakeExternal.CreateLogger(output),
            force: false, TestContext.Current.CancellationToken);

        sleeps.ShouldBeEmpty("there is no cooler to step, so nothing to wait for");
        await camera.DidNotReceive().SetSetCCDTemperatureAsync(Arg.Any<double>(), Arg.Any<CancellationToken>());
        await hub.Received(1).DisconnectAsync(CameraUri, false, Arg.Any<CancellationToken>());
    }
}
