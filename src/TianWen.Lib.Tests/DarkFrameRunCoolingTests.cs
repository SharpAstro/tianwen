using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A dark run's setpoint (<see cref="DarkFrameRunOptions.SetpointC"/>, <c>tianwen darks --setpoint</c>): the sensor is cooled
/// through the session's ramp and READ BACK before the first frame, no frame is taken short of it, the cooler is held
/// afterwards unless the run was asked to warm, and a stopped run never warms. Every cooler change goes through the hub, so
/// the camera's cooler intent (what a node's crash journal re-establishes) says what the run left it doing. The fake sensor
/// starts at 20 °C beside a 20 °C heat sink; the ramps run on the fake clock.
/// </summary>
public class DarkFrameRunCoolingTests(ITestOutputHelper output)
{
    private (FakeExternal External, IDeviceHub Hub, DarkFrameRun Run) Build()
    {
        var external = new FakeExternal(output);
        var hub = external.BuildServiceProvider().GetRequiredService<IDeviceHub>();
        return (external, hub, new DarkFrameRun(external, external.TimeProvider, NullLogger<DarkFrameRun>.Instance));
    }

    private static DarkFrameRunOptions Darks(int count, double? setpointC, bool warmWhenFinished = false)
        => new DarkFrameRunOptions(TimeSpan.FromSeconds(1), count, SetpointC: setpointC, WarmWhenFinished: warmWhenFinished);

    [Fact(Timeout = 60_000)]
    public async Task EveryFrameIsTakenAtTheSetpointAndTheCoolerIsHeldAfterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, hub, run) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);

        var frames = await run.RunAsync(hub, device.DeviceUri, Darks(2, setpointC: -5.4), cancellationToken: ct);

        frames.Count.ShouldBe(2);
        frames.ShouldAllBe(static f => Math.Abs(f.SensorTemperatureC - -5) <= DarkFrameRun.SetpointToleranceC,
            "the ramp's whole-degree target was reached before the first frame");
        (await camera.GetCoolerOnAsync(ct)).ShouldBeTrue("not asked to warm, the run leaves the cooler held");
        hub.TryGetCoolerIntent(device.DeviceUri, out var intent).ShouldBeTrue();
        intent.ShouldBe(CoolerIntent.CoolTo(-5), "the node knows what the cooler is doing, and a crash journal holds it there");
    }

    [Fact(Timeout = 60_000)]
    public async Task AskedToWarmItWarmsThroughTheRampOnceTheFramesAreTaken()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, hub, run) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);
        var intents = new List<CoolerIntent>();
        hub.CoolerIntentChanged += (_, _) =>
        {
            if (hub.TryGetCoolerIntent(device.DeviceUri, out var now))
            {
                intents.Add(now);
            }
        };

        var frames = await run.RunAsync(hub, device.DeviceUri, Darks(2, setpointC: -5, warmWhenFinished: true), cancellationToken: ct);

        frames.ShouldAllBe(static f => Math.Abs(f.SensorTemperatureC - -5) <= DarkFrameRun.SetpointToleranceC, "the frames were taken cold");
        intents.ShouldBe([CoolerIntent.CoolTo(-5), CoolerIntent.Warm, CoolerIntent.Off], "cooled, then a warm-up ramp, then off: never a jump");
        (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse();
        (await camera.GetCCDTemperatureAsync(ct)).ShouldBeGreaterThanOrEqualTo(20 - CameraReading.WarmUpToleranceC, "back at its heat sink");
        hub.IsConnected(device.DeviceUri).ShouldBeTrue("warming leaves the camera connected");
    }

    [Fact(Timeout = 60_000)]
    public async Task ARunWarmsTheCameraItHoldsButNeverOneAnotherRunHolds()
    {
        // The hub's warm-up refuses a held camera, so a run that holds it names its lease; the node's dark library holds its
        // camera for its whole run and so warms it as the holder.
        var ct = TestContext.Current.CancellationToken;
        var (_, hub, run) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);

        DeviceLeaseSet.TryAcquire(hub, [device.DeviceUri], "dark library", out var held, out _).ShouldBeTrue();
        using (held)
        {
            await run.RunAsync(hub, device.DeviceUri, Darks(1, setpointC: -5, warmWhenFinished: true), heldAs: "dark library", cancellationToken: ct);
            (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse("its holder warmed it");
        }

        DeviceLeaseSet.TryAcquire(hub, [device.DeviceUri], "a session", out var other, out _).ShouldBeTrue();
        using (other)
        {
            await Should.ThrowAsync<DeviceLeasedException>(async () =>
                await run.RunAsync(hub, device.DeviceUri, Darks(1, setpointC: -5, warmWhenFinished: true), heldAs: "dark library", cancellationToken: ct));
            (await camera.GetCoolerOnAsync(ct)).ShouldBeTrue("a camera another run holds is that run's to warm");
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AStoppedRunTakesNoMoreFramesAndLeavesTheCoolerHeldEvenWhenAskedToWarm()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, hub, run) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new StopAfterFirst(stop);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await run.RunAsync(hub, device.DeviceUri, Darks(5, setpointC: -5, warmWhenFinished: true), progress, cancellationToken: stop.Token));

        progress.Frames.ShouldBe(1);
        (await camera.GetCoolerOnAsync(ct)).ShouldBeTrue("a stop never switches a cooler off: held is safe, and the caller decides");
        hub.TryGetCoolerIntent(device.DeviceUri, out var intent).ShouldBeTrue();
        intent.ShouldBe(CoolerIntent.CoolTo(-5));
    }

    [Fact(Timeout = 30_000)]
    public async Task ASensorThatStopsShortOfTheSetpointTakesNoFrame()
    {
        // The ramp gives up after twice its time and says so only in its return, so the run reads the sensor itself.
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var uri = new FakeDevice(DeviceType.Camera, 1).DeviceUri;
        var camera = Substitute.For<ICameraDriver>();
        camera.Name.Returns("Stuck camera");
        camera.CanSetCCDTemperature.Returns(true);
        camera.CanGetCCDTemperature.Returns(true);
        camera.GetCCDTemperatureAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(15.0));
        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetConnectedDriver(uri, out Arg.Any<ICameraDriver?>()).Returns(call =>
        {
            call[1] = camera;
            return true;
        });
        var run = new DarkFrameRun(external, external.TimeProvider, NullLogger<DarkFrameRun>.Instance);

        var refused = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await run.RunAsync(hub, uri, Darks(2, setpointC: -10), cancellationToken: ct));

        refused.Message.ShouldContain("did not reach -10 °C");
        refused.Message.ShouldContain("reads 15.0 °C");
        await camera.DidNotReceiveWithAnyArgs().StartExposureAsync(default, default, default);
    }

    [Fact(Timeout = 30_000)]
    public async Task ACameraThatCannotRegulateItsSensorIsRefusedASetpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var uri = new FakeDevice(DeviceType.Camera, 1).DeviceUri;
        var camera = Substitute.For<ICameraDriver>();
        camera.Name.Returns("Uncooled camera");
        camera.CanSetCCDTemperature.Returns(false);
        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetConnectedDriver(uri, out Arg.Any<ICameraDriver?>()).Returns(call =>
        {
            call[1] = camera;
            return true;
        });
        var run = new DarkFrameRun(external, external.TimeProvider, NullLogger<DarkFrameRun>.Instance);

        (await Should.ThrowAsync<InvalidOperationException>(async () => await run.RunAsync(hub, uri, Darks(2, setpointC: -10), cancellationToken: ct)))
            .Message.ShouldContain("cannot regulate its sensor temperature");
        hub.DidNotReceiveWithAnyArgs().SetCoolerIntent(default!, default);
        await camera.DidNotReceiveWithAnyArgs().StartExposureAsync(default, default, default);
    }

    [Fact(Timeout = 30_000)]
    public async Task WithNoSetpointTheCoolerIsNotTouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, hub, run) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);

        await run.RunAsync(hub, device.DeviceUri, Darks(1, setpointC: null), cancellationToken: ct);

        (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse();
        hub.TryGetCoolerIntent(device.DeviceUri, out _).ShouldBeFalse("nothing asked the cooler for anything");
    }

    private sealed class StopAfterFirst(CancellationTokenSource stop) : IProgress<DarkFrameCaptured>
    {
        public int Frames { get; private set; }

        public void Report(DarkFrameCaptured value)
        {
            Frames++;
            stop.Cancel();
        }
    }
}
