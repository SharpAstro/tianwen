using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A DAL camera whose body streams serves a live view as video (#813, docs/plans/planetary-native-video.md Phase D),
/// rather than one exposure started, awaited and downloaded per frame: the path that held an ASI462MC at 5.8 frames a
/// second at full frame, and gave it 102 at 640 x 320 once it streamed.
/// </summary>
/// <remarks>
/// Against a scripted SDK that behaves as ZWO's does where it matters: a window's width in steps of 8, its size set
/// only while stopped, its origin movable mid-stream, a frame call that blocks and may answer Timeout.
/// </remarks>
[Collection("Devices")]
public class DALCameraVideoTests(ITestOutputHelper output)
{
    private static readonly VideoCaptureOptions OneMillisecond = new VideoCaptureOptions(TimeSpan.FromMilliseconds(1));

    private async Task<(TestDalCameraDriver Camera, FakeCmosState State)> StreamingCameraAsync(
        int width = 64, int height = 32, BayerPattern bayerPattern = BayerPattern.Monochrome)
    {
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, canStream: true, bayerPattern: bayerPattern);
        camera.NumX = width;
        camera.NumY = height;
        return (camera, state);
    }

    private static async Task<Image> NextAsync(IAsyncEnumerator<Image> frames)
    {
        (await frames.MoveNextAsync()).ShouldBeTrue("the stream goes on until it is stopped");
        return frames.Current;
    }

    [Fact(Timeout = 30_000)]
    public async Task AStreamingBodyServesTheLiveViewAsVideoNotOneExposureAtATime()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();
        camera.CanVideoCapture.ShouldBeTrue();

        await using (var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct))
        {
            var last = 0f;
            for (var i = 0; i < 5; i++)
            {
                var frame = await NextAsync(frames);
                (frame.Width, frame.Height).ShouldBe((64, 32));
                frame.MaxValue.ShouldBeGreaterThan(last, "each frame is a newer one from the body");
                last = frame.MaxValue;
                frame.ImageMeta.FrameType.ShouldBe(FrameType.Light);
                frame.Release();
            }
            camera.CanJogRoi.ShouldBeTrue("a streaming body that pans mid-stream can be jogged");
        }

        state.StartCount.ShouldBe(0, "no single exposure was started for a streamed frame");
        (state.VideoStarts, state.VideoStops).ShouldBe((1, 1), "the stream started once and stopped when its reader let it go");
        state.Streaming.ShouldBeFalse();
        camera.CanJogRoi.ShouldBeFalse("nothing streams now");
    }

    [Fact(Timeout = 30_000)]
    public async Task ACameraStreamsOrExposesNeverBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using (var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct))
        {
            (await NextAsync(frames)).Release();
            await Should.ThrowAsync<InvalidOperationException>(async () => await camera.StartExposureAsync(TimeSpan.FromSeconds(1), cancellationToken: ct));
        }

        await camera.StartExposureAsync(TimeSpan.FromSeconds(10), cancellationToken: ct);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var frame in camera.CaptureVideoAsync(OneMillisecond, ct))
            {
                frame.Release();
            }
        });
        state.VideoStarts.ShouldBe(1, "the stream that was refused never reached the body");
    }

    [Fact(Timeout = 30_000)]
    public async Task AResizeStopsTheStreamSetsTheWindowAndStartsItAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();

        // The body refuses a new size while it streams (as ZWO's does), so the stream must stop, resize and start.
        camera.NumX = 32;
        camera.NumY = 16;
        Image frame;
        while ((frame = await NextAsync(frames)).Width != 32)
        {
            frame.Release();
        }
        (frame.Width, frame.Height).ShouldBe((32, 16));
        frame.Release();
        state.VideoStarts.ShouldBe(2);
        state.VideoStops.ShouldBe(1);
        camera.VideoRoi.Width.ShouldBe(32);
    }

    [Fact(Timeout = 30_000)]
    public async Task AJogMovesTheWindowWithoutARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        var before = camera.VideoRoi;
        before.ShouldBe(new RoiRect(18, 34, 64, 32), "the window starts centred on the 100 x 100 sensor");

        await camera.JogRoiAsync(8, -4, ct);
        while (camera.VideoRoi == before)
        {
            (await NextAsync(frames)).Release();
        }

        camera.VideoRoi.ShouldBe(before with { X = before.X + 8, Y = before.Y - 4 });
        (state.StartX, state.StartY).ShouldBe((26, 30));
        state.VideoStarts.ShouldBe(1, "a pan needs no restart");
    }

    [Fact(Timeout = 30_000)]
    public async Task AColourSensorsWindowKeepsAnEvenOriginSoItsColoursStayWhereTheyWere()
    {
        var ct = TestContext.Current.CancellationToken;
        // 100 - 30 = 70, so a 30-high window centred would start at 35: odd, which swaps a Bayer sensor's rows.
        var (camera, _) = await StreamingCameraAsync(width: 64, height: 30, BayerPattern.RGGB);
        camera.RoiConstraints.ShouldBe(camera.RoiConstraints with { WidthStep = 8, HeightStep = 2, OriginStepX = 2, OriginStepY = 2 });

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        (camera.VideoRoi.X % 2, camera.VideoRoi.Y % 2).ShouldBe((0, 0));
    }

    [Fact(Timeout = 30_000)]
    public async Task ExposureAndGainChangeBetweenFramesWithoutARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        await camera.ApplyVideoControlsAsync(new VideoCaptureOptions(TimeSpan.FromMilliseconds(5), Gain: 42), ct);
        while (state.Controls[CMOSControlType.Gain] != 42)
        {
            (await NextAsync(frames)).Release();
        }

        state.Controls[CMOSControlType.Exposure].ShouldBe(5000, "the exposure control is in microseconds");
        state.VideoStarts.ShouldBe(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task ATimeoutIsAFrameNotYetThereNotTheEndOfTheStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();
        state.TimeoutEvery = 2;

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        for (var i = 0; i < 5; i++)
        {
            (await NextAsync(frames)).Release();
        }
        state.VideoStarts.ShouldBe(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABodyThatFailsEndsTheStreamWithItsReasonAndIsStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        state.FailNextFrameWith = CMOSErrorCode.CameraRemoved;

        var failed = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            while (await frames.MoveNextAsync())
            {
                frames.Current.Release();
            }
        });
        failed.Message.ShouldContain("CameraRemoved");
        state.VideoStops.ShouldBe(1, "the stream was stopped on the way out");
    }

    [Fact(Timeout = 30_000)]
    public async Task ADisconnectStopsTheStreamBeforeItClosesTheBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        await camera.DisconnectAsync(ct);

        state.ClosedWhileStreaming.ShouldBeFalse("a frame call must never be under way as the body closes");
        state.VideoStops.ShouldBe(1);
        while (await frames.MoveNextAsync())
        {
            frames.Current.Release();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ASteadyStreamRecyclesItsPlanes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, _) = await StreamingCameraAsync();

        await using (var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct))
        {
            for (var i = 0; i < 50; i++)
            {
                (await NextAsync(frames)).Release();
            }
        }

        // One the reader holds, one in the hand-off, one being filled: never one per frame.
        camera.VideoPlanesAllocated.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStreamReadsOutInTheDepthItAskedForAndDeclaresThatDepthsFullScale()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, formats: [PixelDataFormat.RAW8, PixelDataFormat.RAW16], canStream: true);
        camera.NumX = 64;
        camera.NumY = 32;
        camera.VideoBitDepths.ShouldBe([BitDepth.Int8, BitDepth.Int16]);

        await using (var frames = camera.CaptureVideoAsync(OneMillisecond with { BitDepth = BitDepth.Int8 }, ct).GetAsyncEnumerator(ct))
        {
            var frame = await NextAsync(frames);
            state.Format.ShouldBe(PixelDataFormat.RAW8);
            frame.BitDepth.ShouldBe(BitDepth.Int8);
            // The camera's own 16-bit full scale on an 8-bit frame made the live stack divide it down to nothing.
            frame.ImageMeta.SensorFullScaleAdu.ShouldBe(255f, "an 8-bit frame declares an 8-bit full scale");
            frame.Release();
        }

        (await camera.GetBitDepthAsync(ct)).ShouldBe(BitDepth.Int16, "the stream's depth is its own: the camera's single exposures stay in 16 bits");
    }

    [Fact(Timeout = 30_000)]
    public async Task AStreamAskingForADepthTheBodyHasNotStreamsInItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();
        camera.VideoBitDepths.ShouldBe([BitDepth.Int16]);

        await using var frames = camera.CaptureVideoAsync(OneMillisecond with { BitDepth = BitDepth.Int8 }, ct).GetAsyncEnumerator(ct);
        var frame = await NextAsync(frames);
        frame.BitDepth.ShouldBe(BitDepth.Int16);
        state.Format.ShouldBe(PixelDataFormat.RAW16);
        frame.Release();
    }

    [Fact(Timeout = 30_000)]
    public async Task AStreamTakesTheWholeUsbBandwidthAndTheHighSpeedReadoutAndGivesBothBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, canStream: true, hasHighSpeed: true, bandwidthMax: 100);
        camera.NumX = 64;
        camera.NumY = 32;
        camera.CanFastReadout.ShouldBeTrue();
        state.Controls[CMOSControlType.BandwidthOverload].ShouldBe(50, "what a connect sets");

        await using (var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct))
        {
            (await NextAsync(frames)).Release();
            state.Controls[CMOSControlType.BandwidthOverload].ShouldBe(100, "a stream takes the whole bandwidth: half of it halves the frame rate");
            state.Controls[CMOSControlType.HighSpeedMode].ShouldBe(1, "a stream runs the high-speed readout unless asked otherwise");
        }

        state.Controls[CMOSControlType.BandwidthOverload].ShouldBe(50, "given back as the stream ends");
        state.Controls[CMOSControlType.HighSpeedMode].ShouldBe(0, "back to what the camera's single exposures ask");
    }

    [Fact(Timeout = 30_000)]
    public async Task ANewDepthOrReadoutModeRestartsTheStreamInIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, formats: [PixelDataFormat.RAW8, PixelDataFormat.RAW16], canStream: true,
            hasHighSpeed: true);
        camera.NumX = 64;
        camera.NumY = 32;

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        var first = await NextAsync(frames);
        first.BitDepth.ShouldBe(BitDepth.Int16, "the camera's own depth when the stream names none");
        first.Release();

        await camera.ApplyVideoControlsAsync(new VideoCaptureOptions(TimeSpan.Zero, HighSpeedMode: false, BitDepth: BitDepth.Int8), ct);
        Image frame;
        while ((frame = await NextAsync(frames)).BitDepth != BitDepth.Int8)
        {
            frame.Release();
        }
        frame.ImageMeta.SensorFullScaleAdu.ShouldBe(255f);
        frame.Release();

        // The body refuses a new format while it streams, as ZWO's does, so the stream stops, changes and starts.
        state.Format.ShouldBe(PixelDataFormat.RAW8);
        state.Controls[CMOSControlType.HighSpeedMode].ShouldBe(0);
        state.VideoStarts.ShouldBe(2);
        state.VideoStops.ShouldBe(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABodyThatDoesNotStreamStaysOnSingleExposures()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, _, _) = await ScriptedDalCamera.ConnectAsync(output);

        camera.CanVideoCapture.ShouldBeFalse("PlanetaryCapture then falls back to its short-exposure loop");
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var frame in camera.CaptureVideoAsync(OneMillisecond, ct))
            {
                frame.Release();
            }
        });
    }
}
