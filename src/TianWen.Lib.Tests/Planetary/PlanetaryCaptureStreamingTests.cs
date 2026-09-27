using System;
using System.Threading;
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
/// The planetary capture loop, <see cref="PlanetaryCapture"/>, streaming a camera into a rolling-window stack: the loop and
/// the stack every host runs, the node's planetary run included (<c>NodePlanetaryRun</c> stacks the loop's stream as these
/// tests do). Verified end to end against the fake camera's <see cref="IVideoCameraDriver"/> path, the test thread stepping
/// the loop one frame at a time. What the GUI adds over the node (its controls sent on change, the node's masters shown) is
/// <c>PlanetaryThroughTheNodeTests</c>'s.
/// </summary>
[Collection("Session")]
public class PlanetaryCaptureStreamingTests(ITestOutputHelper output)
{
    /// <summary>
    /// Steps the capture loop in LOCK-STEP, with TWO waits, one in each direction: the loop's frame gate (armed here, before
    /// <c>Start</c>) holds the loop until the pump steps it, and <see cref="PlanetaryCapture.WaitForNextFrameAsync"/> holds
    /// the pump until that frame is fully processed. So each step is exactly one frame, captured when the test says and
    /// observed before the next, and every quantity asserted on here (planet drift, stack depth, ROI chase) is a
    /// deterministic function of the step count and of nothing else, however slow the runner.
    /// <para>
    /// The frame wait alone was called lock-step here, but only the test waited: the loop signalled a frame and captured the
    /// next at once (<c>FakeTimeProviderWrapper.SleepAsync</c> advances fake time synchronously, so it never yields), and a
    /// test thread that fell behind found it thousands of frames on. That ran the recenter chase's window to the sensor edge
    /// on an ARM runner (issue 539). The shape before that, up to 5000 iterations of a real <c>Task.Delay(2)</c>, measured
    /// the wall clock.
    /// </para>
    /// </summary>
    private sealed class LockStepPump
    {
        private readonly PlanetaryCapture _capture;

        public LockStepPump(PlanetaryCapture capture)
        {
            _capture = capture;
            capture.ArmFrameGate();
        }

        /// <summary>Frames this pump has let the capture loop take, over every <see cref="PumpAsync"/> call.</summary>
        public int Stepped { get; private set; }

        public async Task<bool> PumpAsync(Func<bool> until, CancellationToken ct, int maxFrames = 2000)
        {
            for (var frame = 0; frame < maxFrames; frame++)
            {
                AssertNotAhead();
                if (until())
                {
                    return true;
                }

                // Take the wait BEFORE releasing the gate: the loop could otherwise finish the stepped frame and re-arm
                // the signal first, leaving this waiting on a frame that is never stepped. Completes on that settled
                // frame, or immediately once the capture loop has ended, so a producer that died bounds out through
                // maxFrames and fails the caller's assertion rather than hanging to the [Fact] timeout with nothing to say.
                var next = _capture.WaitForNextFrameAsync(ct);
                _capture.StepFrame();
                Stepped++;
                await next;
            }

            AssertNotAhead();
            return until();
        }

        // The loop may never hold a frame the pump did not step. Equality is the steady state; fewer is a stopped loop,
        // which the caller's predicate reports.
        private void AssertNotAhead()
            => _capture.FramesReceived.ShouldBeLessThanOrEqualTo(Stepped, "the capture loop ran ahead of the pump");
    }

    private static readonly RollingWindowOptions SmallWindow = new RollingWindowOptions { FallbackWindowFrames = 10, MaxWindowFrames = 16 };

    private async Task<FakeCameraDriver> CameraAsync(int sensorId, int size, CancellationToken ct)
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, sensorId), external.BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.NumX = size;
        camera.NumY = size;
        return camera;
    }

    private static PlanetaryCapture NewCapture() => new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance, SmallWindow);

    // The stack of the loop's stream up to its latest frame, as the node's run takes it.
    private static Task<Image> StackLatestAsync(PlanetaryCapture capture, CancellationToken ct)
    {
        var stream = capture.Stream.ShouldNotBeNull("the loop streams once a frame has arrived");
        return new RollingWindowStacker(stream, SmallWindow).StackToAsync(stream.LatestIndex, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACaptureStreamsIntoTheStackAndBuildsAMaster()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(8, 128, ct);
        camera.PlanetRadiusPixels = 40;
        await using var capture = NewCapture();

        capture.IsCapturing.ShouldBeFalse();
        var pump = new LockStepPump(capture);
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        capture.IsCapturing.ShouldBeTrue();

        (await pump.PumpAsync(() => capture.Stream is { FrameCount: >= 10 }, ct)).ShouldBeTrue();
        var master = await StackLatestAsync(capture, ct);

        output.WriteLine($"frames received={capture.FramesReceived}, fps={capture.MeasuredFps:F0}");
        capture.FramesReceived.ShouldBeGreaterThan(0);
        capture.DroppedFrames.ShouldBe(0);
        (master.Width, master.Height, master.ChannelCount).ShouldBe((128, 128, 1)); // a mono planetary sensor

        await capture.StopAsync(ct);
        capture.IsCapturing.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task AColourSensorEmitsBayerVideoAndStacksToAColourMaster()
    {
        // The fake's RGGB sensor delivers a raw Bayer mosaic in video mode. The loop derives the stream layout from the
        // ACTUAL frame (1 channel + SensorType.RGGB -> SplitCfa, NOT an assumed RGB that drops every mono-shaped frame),
        // splits each frame into four half-res CFA sub-planes (mirroring SerFrameStream), and the stack stacks
        // per-photosite and demosaics ONCE -> a COLOUR master. This is the path that lets the wavelet deblur run on real
        // colour data.
        var ct = TestContext.Current.CancellationToken;
        // id 5 = IMX585C, an RGGB (colour) planetary sensor.
        var camera = await CameraAsync(5, 128, ct);
        camera.SensorType.ShouldBe(SensorType.RGGB);
        camera.PlanetRadiusPixels = 40;
        await using var capture = NewCapture();

        var pump = new LockStepPump(capture);
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        (await pump.PumpAsync(() => capture.Stream is { FrameCount: >= 10 }, ct)).ShouldBeTrue();
        var master = await StackLatestAsync(capture, ct);

        output.WriteLine($"frames received={capture.FramesReceived}");
        capture.Stream.ShouldNotBeNull().Layout.ShouldBe(PlanetaryFrameLayout.SplitCfa);
        master.ChannelCount.ShouldBe(3);   // colour master: SplitCfa -> merge -> single demosaic
        master.Width.ShouldBe(128);        // the half-res (64) CFA planes demosaic back to the full mosaic res
        master.Height.ShouldBe(128);

        // The master is actually COLOURED, not a grey disk: Jupiter's tan/brown disk carries more red than blue, so the
        // red-channel mean across the central disk exceeds the blue-channel mean (the master is linear; WB and stretch are
        // render-time uniforms, so channel ratios survive).
        var (rMean, bMean) = ChannelMeansInCentre(master);
        output.WriteLine($"central disk mean R={rMean:F4} B={bMean:F4}");
        rMean.ShouldBeGreaterThan(bMean);

        await capture.StopAsync(ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task ALiveRoiResizeRebuildsTheStreamAtTheNewSize()
    {
        // A live ROI resize (the panel's Size stepper mid-capture) stages a NumX/NumY change; the loop applies it, the fake
        // yields a smaller frame, and the loop rebuilds the frame stream at the new size, on which a stack starts again
        // (the node's run does) without a Stop/Start.
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(8, 128, ct); // mono
        camera.PlanetRadiusPixels = 40;
        await using var capture = NewCapture();

        var pump = new LockStepPump(capture);
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        (await pump.PumpAsync(() => capture.Stream is { FrameCount: > 0 }, ct)).ShouldBeTrue();
        var first = capture.Stream.ShouldNotBeNull();
        first.Width.ShouldBe(128);

        // Resize the ROI live; the stream should track down to the new size within a bounded number of frames.
        capture.SetRoiSize(96, 96);
        var resized = await pump.PumpAsync(() => capture.Stream is { Width: 96, FrameCount: > 0 }, ct);

        output.WriteLine($"after resize: stream={capture.Stream?.Width}x{capture.Stream?.Height}, resized={resized}");
        resized.ShouldBeTrue();
        capture.Stream.ShouldNotBeSameAs(first, "a new window size is a new stream");
        (await StackLatestAsync(capture, ct)).Width.ShouldBe(96);

        await capture.StopAsync(ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task AutoRecenterJogsTheRoiWindowToFollowADriftingPlanet()
    {
        // With auto-recenter on, the loop measures the disk COM each frame and jogs the readout window to follow the
        // planet's drift across the sensor (the fast, mount-free path).
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(8, 200, ct); // mono
        camera.PlanetRadiusPixels = 40;
        camera.PlanetDriftPixelsPerSecX = 120.0;  // strong rightward drift, X-only for a clean assertion
        camera.PlanetDriftPixelsPerSecY = 0.0;
        await using var capture = NewCapture();

        capture.ConfigureRecenter(auto: true, mountJog: false, deadbandPixels: 2, gain: 0.5);
        var pump = new LockStepPump(capture);
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();

        // Capture the ROI origin once streaming has produced a frame, then run until the window has followed the drift to
        // the right (the recenter loop panned it). The planet drifts 120 px/s against a 5 ms per-frame fake clock, 0.6 px
        // per FRAME, so the chase is deterministic in frames.
        (await pump.PumpAsync(() => capture.FramesReceived > 0, ct)).ShouldBeTrue();

        var startX = camera.VideoRoi.X;
        var startY = camera.VideoRoi.Y;
        var chaseStart = pump.Stepped;
        var chased = await pump.PumpAsync(() => camera.VideoRoi.X >= startX + 10, ct);
        var chaseFrames = pump.Stepped - chaseStart;

        var endX = camera.VideoRoi.X;
        output.WriteLine($"ROI X: start={startX} end={endX} Y={camera.VideoRoi.Y}, chase frames={chaseFrames}, frames={capture.FramesReceived}");
        chased.ShouldBeTrue();
        endX.ShouldBeGreaterThan(startX + 8);          // the window chased the drifting disk right
        // 10 px at 0.6 px per frame is ~17 frames of drift. Measured: 15 (the window starts 2 px past centre and moves in
        // even-pixel jogs), the same with the pump slowed by a real 250 ms per step, which is the point. The bound leaves
        // room for a retuned seeing model; a count in the hundreds is a loop that ran ahead of the pump (2927 frames in the
        // recorded failure).
        chaseFrames.ShouldBeLessThanOrEqualTo(25);
        // Y drift was 0 and the deadband is per-axis, so the centred Y axis isn't dragged by the large X offset; at most
        // one rare single-frame COM-noise excursion past the deadband nudges it a pixel. (The one time this failed, the
        // window had run to the sensor's right edge, and the centroid of the half-visible disk dragged Y: the runaway the
        // pump's gate now prevents.)
        Math.Abs(camera.VideoRoi.Y - startY).ShouldBeLessThanOrEqualTo(2);

        await capture.StopAsync(ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task AutoRecenterOffLeavesTheRoiWindowFixed()
    {
        // The control case: same drift, auto-recenter OFF -> the loop never jogs, so the window stays put even as the
        // planet drifts off it. Proves the jog in the test above is the recenter, not capture itself.
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(8, 200, ct);
        camera.PlanetRadiusPixels = 40;
        camera.PlanetDriftPixelsPerSecX = 120.0;
        await using var capture = NewCapture();

        capture.ConfigureRecenter(auto: false, mountJog: false, deadbandPixels: 2, gain: 0.5);
        var pump = new LockStepPump(capture);
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();

        (await pump.PumpAsync(() => capture.FramesReceived > 0, ct)).ShouldBeTrue();

        var startX = camera.VideoRoi.X;
        // Run well past the point the recenter would have moved the window (the test above needs 15 frames to chase 10 px
        // at the same drift); the planet drifts but the window must not move. Gated, 300 steps are exactly 300 frames.
        var target = capture.FramesReceived + 300;
        (await pump.PumpAsync(() => capture.FramesReceived >= target, ct, maxFrames: 300)).ShouldBeTrue();
        capture.FramesReceived.ShouldBe(target);

        output.WriteLine($"ROI X: start={startX} end={camera.VideoRoi.X}, frames={capture.FramesReceived}");
        camera.VideoRoi.X.ShouldBe(startX);   // no recenter -> unmoved

        await capture.StopAsync(ct);
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
    public async Task AStartWhileCapturingIsRefusedAndAStopWhenIdleIsSafe()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(8, 96, ct);
        await using var capture = NewCapture();

        // Stop when never started is a no-op.
        await capture.StopAsync(ct);

        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();

        // A second Start while running is refused (no second capture loop).
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeFalse();
        capture.IsCapturing.ShouldBeTrue();

        await capture.StopAsync(ct);
        capture.IsCapturing.ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task PumpingPastTheEndOfACaptureBoundsOutInsteadOfHanging()
    {
        // Guards the frame-arrival seam PumpAsync rides on. The loop completes the signal when it ends, but if it also
        // RE-ARMED it there, the very next wait would park on a frame that can never arrive, so a stopped or faulted
        // producer would show up as a 60 s [Fact] timeout with nothing to say instead of as a failed predicate. The
        // terminal completion is deliberately sticky.
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(8, 64, ct);
        await using var capture = NewCapture();

        var pump = new LockStepPump(capture);
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        (await pump.PumpAsync(() => capture.FramesReceived > 0, ct)).ShouldBeTrue();
        await capture.StopAsync(ct);
        capture.IsCapturing.ShouldBeFalse();

        // A predicate that can never hold: this must exhaust its frame budget and return false promptly, NOT block. (The
        // [Fact] timeout is the backstop that fails the test if the seam regresses.)
        var never = await pump.PumpAsync(() => false, ct, maxFrames: 8);
        never.ShouldBeFalse();
    }

    [Fact]
    public void StartVideoCaptureSignal_default_construction_applies_the_declared_defaults()
    {
        // Regression: StartVideoCaptureSignal MUST be a record class, not a record struct. On a struct,
        // `new StartVideoCaptureSignal()` invokes the implicit parameterless constructor that zero-inits every field and
        // SILENTLY ignores the primary-ctor defaults, yielding a 0 ms exposure and a 0x0 (-> clamped 16x16) ROI, which is
        // exactly the bug the strip's Start button hit. As a class, new() runs the primary ctor, so the sensible planetary
        // defaults below actually apply.
        var sig = new StartVideoCaptureSignal();

        sig.ExposureMs.ShouldBe(10.0);
        sig.RoiWidth.ShouldBe(640);
        sig.RoiHeight.ShouldBe(320);
        sig.Gain.ShouldBeNull();
        sig.OtaIndex.ShouldBe(0);
    }
}
