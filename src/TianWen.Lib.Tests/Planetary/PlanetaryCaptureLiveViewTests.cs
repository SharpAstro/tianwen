using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The capture loop as a live view (#1111, P4 of docs/plans/live-session-preview.md): it keeps none of the frames it
/// streams, a camera without native video takes a new exposure from its next frame, and the whole-sensor window bins only
/// as far as the camera can. The camera streams through the universal rapid-exposure fallback (a substitute camera that
/// is no <see cref="IVideoCameraDriver"/>), stepped frame by frame.
/// </summary>
[Collection("Session")]
public class PlanetaryCaptureLiveViewTests
{
    private static Image Frame()
    {
        var a = new float[16, 24];
        a[8, 12] = 0.5f;
        return Image.FromChannel(a, 1f, 0f);
    }

    // A camera without native video, recording the exposure each frame was started at.
    private static (ICameraDriver Camera, ConcurrentQueue<TimeSpan> Exposures) Camera()
    {
        var exposures = new ConcurrentQueue<TimeSpan>();
        var camera = Substitute.For<ICameraDriver>();
        camera.StartExposureAsync(Arg.Any<TimeSpan>(), Arg.Any<FrameType>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                exposures.Enqueue(call.Arg<TimeSpan>());
                return ValueTask.FromResult(DateTimeOffset.UnixEpoch);
            });
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult<Image?>(Frame()));
        return (camera, exposures);
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

    [Fact(Timeout = 30_000)]
    public async Task ALiveViewKeepsNoneOfItsFramesAndItsHostSeesEachOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, _) = Camera();
        var seen = 0;
        await using var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance,
            onFrame: frame => { frame.Width.ShouldBe(24); Interlocked.Increment(ref seen); }, kind: LiveCaptureKind.LiveView);
        capture.ArmFrameGate();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();

        await StepAsync(capture, 3, ct);

        capture.FramesReceived.ShouldBe(3);
        Volatile.Read(ref seen).ShouldBe(3);
        capture.Stream.ShouldBeNull("a live view fills no ring: nothing stacks what it streams");
        capture.Name.ShouldBe(PlanetaryCapture.LiveViewLeaseOwner);
    }

    [Fact(Timeout = 30_000)]
    public async Task ACameraWithoutVideoTakesANewExposureFromItsNextFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, exposures) = Camera();
        await using var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance, kind: LiveCaptureKind.LiveView);
        capture.ArmFrameGate();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();
        await StepAsync(capture, 1, ct);

        // Staged between frames, applied once the next frame is in (every control's rule): the third exposure is the new one.
        capture.SetExposure(TimeSpan.FromMilliseconds(250));
        await StepAsync(capture, 3, ct);

        exposures.ToArray().ShouldBe(
            [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)],
            "the fallback once took the start's exposure for as long as it ran");
    }

    [Fact(Timeout = 30_000)]
    public async Task ALensStepReachesACameraThatDrivesItsLensAfterTheNextFrameInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stills, _) = Camera();
        // One camera with both faces: the stills of the short-exposure loop and its own lens drive (a Canon's, #681).
        var camera = Substitute.For<ICameraDriver, ILensFocusCamera>();
        camera.StartExposureAsync(Arg.Any<TimeSpan>(), Arg.Any<FrameType>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(DateTimeOffset.UnixEpoch));
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(_ => stills.GetImageAsync(CancellationToken.None));
        var lens = (ILensFocusCamera)camera;
        lens.CanDriveLens.Returns(true);
        var driven = new ConcurrentQueue<LensFocusStep>();
        lens.DriveLensAsync(Arg.Any<LensFocusStep>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            driven.Enqueue(call.Arg<LensFocusStep>());
            return ValueTask.FromResult(true);
        });

        await using var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance, kind: LiveCaptureKind.LiveView);
        capture.ArmFrameGate();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();
        await StepAsync(capture, 1, ct);
        capture.CanDriveLens.ShouldBeTrue();

        capture.DriveLens(LensFocusStep.NearLarge);
        capture.DriveLens(LensFocusStep.FarSmall);
        driven.ShouldBeEmpty("a step waits for the next frame, as every control does");
        await StepAsync(capture, 1, ct);

        driven.ToArray().ShouldBe([LensFocusStep.NearLarge, LensFocusStep.FarSmall]);
    }

    [Theory]
    [InlineData(2, 2, 2000, 1500)]
    [InlineData(4, 1, 4000, 3000)]
    public void TheWholeSensorBinsOnlyAsFarAsTheCameraCan(short asked, short maxBin, int width, int height)
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.CameraXSize.Returns(4000);
        camera.CameraYSize.Returns(3000);
        camera.MaxBinX.Returns(maxBin);
        camera.MaxBinY.Returns(maxBin);
        camera.BinX.Returns((short)1);
        camera.BinY.Returns((short)1);
        camera.RoiConstraints.Returns(RoiConstraints.ForSensor(4000, 3000));

        PlanetaryCapture.ConfigureRoi(camera, 0, 0, asked).ShouldBe((width, height));

        var bin = Math.Min(asked, maxBin);
        if (bin > 1)
        {
            camera.Received().BinX = bin;
            camera.Received().BinY = bin;
        }
        else
        {
            camera.DidNotReceiveWithAnyArgs().BinX = default;
        }
        camera.Received().StartX = 0;
        camera.Received().StartY = 0;
    }

    [Fact]
    public void APlanetaryWindowIsUnbinnedWhateverIsAsked()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.CameraXSize.Returns(4000);
        camera.CameraYSize.Returns(3000);
        camera.MaxBinX.Returns((short)4);
        camera.MaxBinY.Returns((short)4);
        camera.BinX.Returns((short)2);
        camera.BinY.Returns((short)2);
        camera.RoiConstraints.Returns(RoiConstraints.ForSensor(4000, 3000));

        PlanetaryCapture.ConfigureRoi(camera, 640, 320, bin: 4).ShouldBe((640, 320));

        camera.Received().BinX = 1;
        camera.Received().BinY = 1;
    }
}
