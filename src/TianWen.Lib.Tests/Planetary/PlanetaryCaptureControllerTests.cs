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

        await controller.StopAsync(ct);
        controller.IsCapturing.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task Color_sensor_emits_bayer_video_and_stacks_to_a_colour_master()
    {
        // The fake's RGGB sensor delivers a raw Bayer mosaic in video mode. The capture controller derives
        // the stream layout from the ACTUAL frame (1 channel + SensorType.RGGB -> SplitCfa, NOT an assumed
        // RGB that drops every mono-shaped frame), splits each frame into four half-res CFA sub-planes
        // (mirroring SerFrameStream), stacks per-photosite, and demosaics ONCE -> a COLOUR master. This is
        // the path that lets the wavelet deblur run on real colour data.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        // id 5 = IMX585C, an RGGB (colour) planetary sensor.
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 5), external.BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.SensorType.ShouldBe(SensorType.RGGB);
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
        controller.HasMaster.ShouldBeTrue();
        controller.FramesReceived.ShouldBeGreaterThan(0);

        var source = controller.Source;
        source.ShouldNotBeNull();
        source.ChannelCount.ShouldBe(3);   // colour master: SplitCfa -> merge -> single demosaic
        source.Width.ShouldBe(128);        // the half-res (64) CFA planes demosaic back to the full mosaic res
        source.Height.ShouldBe(128);

        // The master is actually COLOURED, not a grey disk: Jupiter's tan/brown disk carries more red than
        // blue, so the red-channel mean across the central disk exceeds the blue-channel mean. (DisplayMaster
        // is the linear [0,1] master -- WB/stretch are render-time uniforms -- so channel ratios survive.)
        var master = controller.CurrentMaster;
        master.ShouldNotBeNull();
        master.ChannelCount.ShouldBe(3);
        var (rMean, bMean) = ChannelMeansInCentre(master);
        output.WriteLine($"central disk mean R={rMean:F4} B={bMean:F4}");
        rMean.ShouldBeGreaterThan(bMean);

        await controller.StopAsync(ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task Live_ROI_resize_rebuilds_the_stream_and_swaps_the_preview_source()
    {
        // A live ROI resize (the panel's Size stepper mid-capture) stages a NumX/NumY change; the capture loop
        // applies it, the fake yields a smaller frame, the loop rebuilds the frame stream at the new size, and
        // Tick swaps the preview source -- so Source.Width tracks the new ROI without a Stop/Start.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), external.BuildServiceProvider()); // mono
        await camera.ConnectAsync(ct);
        camera.NumX = 128;
        camera.NumY = 128;
        camera.PlanetRadiusPixels = 40;

        var state = new ViewerState();
        await using var controller = new PlanetaryCaptureController(
            state, external.TimeProvider, NullLogger<PlanetaryCaptureController>.Instance,
            new RollingWindowOptions { FallbackWindowFrames = 8, MaxWindowFrames = 16 });

        controller.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct);

        var iterations = 0;
        while (!controller.HasMaster && iterations++ < 5000 && !ct.IsCancellationRequested)
        {
            controller.Tick();
            await Task.Delay(2, ct);
        }

        controller.HasMaster.ShouldBeTrue();
        controller.Source.ShouldNotBeNull();
        controller.Source.Width.ShouldBe(128);

        // Resize the ROI live; the source should track down to the new size within a bounded number of ticks.
        controller.SetRoiSize(96, 96);
        var resized = false;
        var loops = 0;
        while (!resized && loops++ < 5000 && !ct.IsCancellationRequested)
        {
            controller.Tick();
            resized = controller.Source is { Width: 96 };
            await Task.Delay(2, ct);
        }

        output.WriteLine($"after resize: source={controller.Source?.Width}x{controller.Source?.Height}, loops={loops}");
        controller.Source.ShouldNotBeNull();
        controller.Source.Width.ShouldBe(96);
        controller.Source.Height.ShouldBe(96);

        await controller.StopAsync(ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task Auto_recenter_jogs_the_roi_window_to_follow_a_drifting_planet()
    {
        // Phase C end-to-end: with auto-recenter on, the capture loop measures the disk COM each frame and
        // jogs the readout window to follow the planet's drift across the sensor (the fast, mount-free path).
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), external.BuildServiceProvider()); // mono
        await camera.ConnectAsync(ct);
        camera.NumX = 200;
        camera.NumY = 200;
        camera.PlanetRadiusPixels = 40;
        camera.PlanetDriftPixelsPerSecX = 120.0;  // strong rightward drift, X-only for a clean assertion
        camera.PlanetDriftPixelsPerSecY = 0.0;

        var state = new ViewerState();
        await using var controller = new PlanetaryCaptureController(
            state, external.TimeProvider, NullLogger<PlanetaryCaptureController>.Instance,
            new RollingWindowOptions { FallbackWindowFrames = 8, MaxWindowFrames = 16 });

        controller.ConfigureRecenter(auto: true, mountJog: false, deadbandPixels: 2, gain: 0.5);
        controller.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct);

        // Capture the ROI origin once streaming has produced a frame, then run until the window has followed
        // the drift to the right (the recenter loop panned it).
        var iterations = 0;
        while (controller.FramesReceived == 0 && iterations++ < 5000 && !ct.IsCancellationRequested)
        {
            controller.Tick();
            await Task.Delay(2, ct);
        }

        var startX = camera.VideoRoi.X;
        var startY = camera.VideoRoi.Y;
        iterations = 0;
        while (camera.VideoRoi.X < startX + 10 && iterations++ < 5000 && !ct.IsCancellationRequested)
        {
            controller.Tick();
            await Task.Delay(2, ct);
        }

        var endX = camera.VideoRoi.X;
        output.WriteLine($"ROI X: start={startX} end={endX} Y={camera.VideoRoi.Y}, frames={controller.FramesReceived}");
        endX.ShouldBeGreaterThan(startX + 8);          // the window chased the drifting disk right
        // Y drift was 0 and the deadband is per-axis, so the centred Y axis isn't dragged by the large X
        // offset; at most one rare single-frame COM-noise excursion past the deadband nudges it a pixel.
        Math.Abs(camera.VideoRoi.Y - startY).ShouldBeLessThanOrEqualTo(2);

        await controller.StopAsync(ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task Auto_recenter_off_leaves_the_roi_window_fixed()
    {
        // The control case: same drift, auto-recenter OFF -> the loop never jogs, so the window stays put even
        // as the planet drifts off it. Proves the jog in the test above is the recenter loop, not capture itself.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), external.BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.NumX = 200;
        camera.NumY = 200;
        camera.PlanetRadiusPixels = 40;
        camera.PlanetDriftPixelsPerSecX = 120.0;

        var state = new ViewerState();
        await using var controller = new PlanetaryCaptureController(
            state, external.TimeProvider, NullLogger<PlanetaryCaptureController>.Instance,
            new RollingWindowOptions { FallbackWindowFrames = 8, MaxWindowFrames = 16 });

        controller.ConfigureRecenter(auto: false, mountJog: false, deadbandPixels: 2, gain: 0.5);
        controller.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct);

        var iterations = 0;
        while (controller.FramesReceived == 0 && iterations++ < 5000 && !ct.IsCancellationRequested)
        {
            controller.Tick();
            await Task.Delay(2, ct);
        }

        var startX = camera.VideoRoi.X;
        // Run a good while; the planet drifts but the window must not move.
        for (var i = 0; i < 600 && !ct.IsCancellationRequested; i++)
        {
            controller.Tick();
            await Task.Delay(2, ct);
        }

        output.WriteLine($"ROI X: start={startX} end={camera.VideoRoi.X}, frames={controller.FramesReceived}");
        camera.VideoRoi.X.ShouldBe(startX);   // no recenter -> unmoved

        await controller.StopAsync(ct);
    }

    // Mean of the red (channel 0) and blue (channel 2) planes over the central half of the image (the disk).
    private static (double R, double B) ChannelMeansInCentre(Image img)
    {
        int w = img.Width, h = img.Height;
        int x0 = w / 4, x1 = 3 * w / 4, y0 = h / 4, y1 = 3 * h / 4;
        double rSum = 0, bSum = 0;
        long n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                rSum += img[0, y, x];
                bSum += img[2, y, x];
                n++;
            }
        }

        return (rSum / n, bSum / n);
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
        await controller.StopAsync(ct);

        controller.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct);
        var firstReceived = controller.FramesReceived;

        // A second Start while running is ignored (no second capture loop).
        controller.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct);
        controller.IsCapturing.ShouldBeTrue();

        await controller.StopAsync(ct);
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
