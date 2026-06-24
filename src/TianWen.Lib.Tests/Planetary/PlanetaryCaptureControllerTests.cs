using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase B: <see cref="PlanetaryCaptureController"/> drives a live capture from a camera into the live
/// rolling-window stack and exposes a render-thread preview source. Verified end to end against the fake
/// camera's <see cref="IVideoCameraDriver"/> path (the test thread plays the render thread, calling
/// <see cref="PlanetaryCaptureController.Tick"/>; the controller's capture loop runs in the background).
/// </summary>
[Collection("Session")]
public class PlanetaryCaptureControllerTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task Capture_streams_into_the_live_stack_and_builds_a_master()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), external.BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.NumX = 128;
        camera.NumY = 128;
        camera.PlanetRadiusPixels = 40;

        var state = new ViewerState();
        // Small rolling window so each stack is cheap and the master builds quickly under test.
        var options = new RollingWindowOptions { FallbackWindowFrames = 10, MaxWindowFrames = 16 };
        await using var controller = new PlanetaryCaptureController(
            state, external.TimeProvider, NullLogger<PlanetaryCaptureController>.Instance, options);

        controller.IsCapturing.ShouldBeFalse();
        controller.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct);
        controller.IsCapturing.ShouldBeTrue();

        // Play the render thread: tick until the first stacked master is published (or time out).
        var iterations = 0;
        while (!controller.HasMaster && iterations++ < 5000 && !ct.IsCancellationRequested)
        {
            controller.Tick();
            await Task.Delay(2, ct);
        }

        output.WriteLine($"frames received={controller.FramesReceived}, iterations={iterations}, fps={controller.MeasuredFps:F0}");
        controller.HasMaster.ShouldBeTrue();
        controller.FramesReceived.ShouldBeGreaterThan(0);
        controller.DroppedFrames.ShouldBe(0);

        var source = controller.Source;
        source.ShouldNotBeNull();
        source.Width.ShouldBe(128);
        source.Height.ShouldBe(128);
        source.ChannelCount.ShouldBe(1);                 // mono planetary sensor
        state.FrameCount.ShouldBeGreaterThan(0);

        await controller.StopAsync();
        controller.IsCapturing.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task Color_sensor_with_mono_video_frames_still_stacks()
    {
        // Regression: the stream layout must be derived from the ACTUAL frames, not the camera's
        // SensorType. A colour sensor (RGGB) whose video frames are mono (1 channel, as the fake emits)
        // used to size the stream as RGB (expects 3 channels) and drop every frame -> "nothing shows up".
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        // id 5 = IMX585C, an RGGB (colour) planetary sensor.
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 5), external.BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.SensorType.ShouldBe(SensorType.RGGB); // the camera reports colour...
        camera.NumX = 128;
        camera.NumY = 128;
        camera.PlanetRadiusPixels = 40;

        var state = new ViewerState();
        await using var controller = new PlanetaryCaptureController(
            state, external.TimeProvider, NullLogger<PlanetaryCaptureController>.Instance,
            new RollingWindowOptions { FallbackWindowFrames = 10, MaxWindowFrames = 16 });

        controller.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct);

        var iterations = 0;
        while (!controller.HasMaster && iterations++ < 5000 && !ct.IsCancellationRequested)
        {
            controller.Tick();
            await Task.Delay(2, ct);
        }

        output.WriteLine($"frames received={controller.FramesReceived}, iterations={iterations}");
        controller.HasMaster.ShouldBeTrue();           // ...but the mono video frames still flow + stack
        controller.FramesReceived.ShouldBeGreaterThan(0);
        controller.Source.ShouldNotBeNull();
        controller.Source.ChannelCount.ShouldBe(1);    // stream layout came from the mono frames

        await controller.StopAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task Start_is_idempotent_and_stop_is_safe_when_idle()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), external.BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.NumX = 96;
        camera.NumY = 96;

        var state = new ViewerState();
        await using var controller = new PlanetaryCaptureController(
            state, external.TimeProvider, NullLogger<PlanetaryCaptureController>.Instance,
            new RollingWindowOptions { FallbackWindowFrames = 8, MaxWindowFrames = 12 });

        // Stop when never started is a no-op.
        await controller.StopAsync();

        controller.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct);
        var firstReceived = controller.FramesReceived;

        // A second Start while running is ignored (no second capture loop).
        controller.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct);
        controller.IsCapturing.ShouldBeTrue();

        await controller.StopAsync();
        controller.IsCapturing.ShouldBeFalse();
        state.IsSequence.ShouldBeFalse();
    }

    [Fact]
    public void StartVideoCaptureSignal_default_construction_applies_the_declared_defaults()
    {
        // Regression: StartVideoCaptureSignal MUST be a record class, not a record struct. On a struct,
        // `new StartVideoCaptureSignal()` invokes the implicit parameterless constructor that zero-inits
        // every field and SILENTLY ignores the primary-ctor defaults -- yielding a 0 ms exposure and a 0x0
        // (-> clamped 16x16) ROI, which is exactly the bug the strip's Start button hit. As a class, new()
        // runs the primary ctor, so the sensible planetary defaults below actually apply.
        var sig = new StartVideoCaptureSignal();

        sig.ExposureMs.ShouldBe(10.0);
        sig.RoiWidth.ShouldBe(640);
        sig.RoiHeight.ShouldBe(320);
        sig.Gain.ShouldBeNull();
        sig.OtaIndex.ShouldBe(0);
    }
}
