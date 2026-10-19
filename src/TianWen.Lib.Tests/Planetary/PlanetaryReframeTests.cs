using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Reframing a running capture on the full sensor (#1033): one full-sensor still taken with the stream stopped, the stream back
/// at its window where it was panned; and a reframe, a pan of the running window at the same size, else a restart at the new
/// size with the window placed where it was proposed. Over the fake camera's video, stepped a frame at a time.
/// </summary>
[Collection("Session")]
public class PlanetaryReframeTests(ITestOutputHelper output)
{
    // One clock for the camera and the capture: a still's exposure ends on the camera's clock, which the capture's waits advance.
    private readonly FakeTimeProviderWrapper _clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 7, 11, 30, 0, TimeSpan.Zero));

    private async Task<FakeCameraDriver> CameraAsync(CancellationToken ct)
    {
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), new FakeExternal(output, _clock).BuildServiceProvider());
        await camera.ConnectAsync(ct);
        return camera;
    }

    private PlanetaryCapture NewCapture()
    {
        var capture = new PlanetaryCapture(_clock, NullLogger.Instance,
            new RollingWindowOptions { FallbackWindowFrames = 10, MaxWindowFrames = 16 });
        capture.ArmFrameGate();
        return capture;
    }

    private static async Task StepAsync(PlanetaryCapture capture, int frames, CancellationToken ct)
    {
        for (var i = 0; i < frames; i++)
        {
            var next = capture.WaitForNextFrameAsync(ct);
            capture.StepFrame();
            await next;
        }
    }

    private static async Task StepUntilAsync(PlanetaryCapture capture, Task done, CancellationToken ct)
    {
        for (var i = 0; i < 60 && !done.IsCompleted; i++)
        {
            await StepAsync(capture, 1, ct);
        }
    }

    private async Task<PlanetaryCapture> StreamingAsync(FakeCameraDriver camera, CancellationToken ct)
    {
        PlanetaryCapture.ConfigureRoi(camera, 640, 360);
        var capture = NewCapture();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        await StepAsync(capture, 2, ct);
        return capture;
    }

    [Fact(Timeout = 60_000)]
    public async Task AFullFrameIsTheWholeSensorAndTheStreamComesBackWhereItWasPanned()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        await using var capture = await StreamingAsync(camera, ct);
        capture.JogRoi(64, 32);
        await StepAsync(capture, 2, ct);
        var panned = capture.Window.ShouldNotBeNull();
        output.WriteLine($"panned to {panned}");

        var still = capture.TakeFullFrameAsync(ct);
        await StepUntilAsync(capture, still, ct);
        var (frame, refusal) = await still;
        await StepAsync(capture, 3, ct);

        refusal.ShouldBeNull();
        using (var _ = new ReleaseOnDispose(frame.ShouldNotBeNull()))
        {
            (frame.Width, frame.Height).ShouldBe((camera.CameraXSize, camera.CameraYSize), "the whole sensor, unbinned");
        }
        var back = capture.Window.ShouldNotBeNull();
        back.ShouldBe(panned, "the stream is back at its window, where it was panned");
        capture.IsCapturing.ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task AReframeOfTheSameSizePansTheRunningWindowWithoutARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        await using var capture = await StreamingAsync(camera, ct);
        var now = capture.Window.ShouldNotBeNull();
        var starts = camera.VideoStarts.Length;

        (await capture.ReframeAsync(now with { X = now.X + 200, Y = now.Y - 100 }, ct)).ShouldBeNull();
        await StepAsync(capture, 2, ct);

        var moved = capture.Window.ShouldNotBeNull();
        (moved.Width, moved.Height).ShouldBe((now.Width, now.Height));
        (moved.X, moved.Y).ShouldBe(((IVideoCameraDriver)camera).RoiConstraints.Snap(now with { X = now.X + 200, Y = now.Y - 100 }) is var snapped
            ? (snapped.X, snapped.Y) : default);
        camera.VideoStarts.Length.ShouldBe(starts, "a pan of the running window needs no restart");
    }

    [Fact(Timeout = 60_000)]
    public async Task AReframeOfAnotherSizeRestartsTheStreamWithItsWindowWhereItWasProposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        await using var capture = await StreamingAsync(camera, ct);
        var target = ((IVideoCameraDriver)camera).RoiConstraints.Snap(new RoiRect(400, 300, 320, 240));

        var reframe = capture.ReframeAsync(target, ct);
        await StepUntilAsync(capture, reframe, ct);
        (await reframe).ShouldBeNull();
        await StepAsync(capture, 3, ct);

        capture.Window.ShouldBe(target, "the window is the proposal, placed where it was drawn");
        camera.VideoStarts[^1].Width.ShouldBe(target.Width, "the stream started again at the new size");
    }

    private readonly struct ReleaseOnDispose(Imaging.Image image) : IDisposable
    {
        public void Dispose() => image.Release();
    }
}
