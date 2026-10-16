using System;
using System.Collections.Generic;
using System.Linq;
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
        state.VideoStarts.ShouldBe(1, "a body gone from the bus is not a stall to recover from");
        camera.VideoStalls.ShouldBe(0);
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

    /// <summary>
    /// A stream reads its size at the start of each pass, and a disconnect marks the driver disconnecting BEFORE it
    /// stops the stream and waits for it. A pass that began just before a disconnect therefore read the size inside
    /// that window, through <c>NumX</c> / <c>NumY</c>, which throw there: the stream ended with "not connected"
    /// instead of stopping (once on CI, in <see cref="ADisconnectStopsTheStreamBeforeItClosesTheBody"/>). Held in
    /// that window on purpose: a staged gain's control write waits until the disconnect has begun.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task APassUnderWayAsTheDisconnectBeginsStillStopsCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.OnSetControl = control =>
        {
            if (control is CMOSControlType.Gain && held.TrySetResult())
            {
                SpinWait.SpinUntil(() => !camera.Connected, TimeSpan.FromSeconds(10));
            }
        };
        await camera.ApplyVideoControlsAsync(new VideoCaptureOptions(TimeSpan.Zero, Gain: 42), ct);
        await held.Task.WaitAsync(ct);

        await camera.DisconnectAsync(ct);

        state.ClosedWhileStreaming.ShouldBeFalse("a frame call must never be under way as the body closes");
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
            for (var i = 0; i < 150; i++)
            {
                (await NextAsync(frames)).Release();
            }
        }

        // One the reader holds, the hand-off's worth waiting (66 here), one being filled: never one per frame.
        camera.VideoPlanesAllocated.ShouldBeLessThanOrEqualTo(TestDalCameraDriver.HandOffFrames(camera.VideoRoi) + 2);
    }

    /// <summary>
    /// A reader that pauses is handed every frame the camera read meanwhile, in order, once it reads again: the one-frame
    /// hand-off this replaced lost most of a 197-frame-a-second recording on a busy machine (TIA2, 2026-10-15). The reader
    /// pauses for a frame count the camera enforces (<see cref="FrameCreditGate"/>), never for a stretch of wall clock,
    /// which a loaded runner outran: its reader woke 19 frames past the hand-off's limit (#1615).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AReaderThatPausesIsHandedEveryFrameItMissedInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        // Disposed before the stream, which a closed gate would hold for ever: a blocked frame call never sees the stop.
        using var gate = state.GateFrames(credits: 1);
        var first = (int)(await NextAsync(frames)).MaxValue;
        frames.Current.Release();

        // Twenty frames while the reader waits, well short of the hand-off's 64, and not one more until it has read them.
        await gate.Grant(20).WaitAsync(ct);
        for (var i = 1; i <= 20; i++)
        {
            var frame = await NextAsync(frames);
            ((int)frame.MaxValue).ShouldBe(first + i, "no frame is skipped while the reader catches up");
            frame.Release();
        }
        camera.VideoFramesReplaced.ShouldBe(0);
        camera.VideoHandOffHighWater.ShouldBe(20, "all twenty waited for the reader");
    }

    /// <summary>
    /// A reader further behind than the hand-off holds loses the OLDEST frames, and reads on from the oldest it kept. It falls
    /// behind by a frame count the camera enforces (<see cref="FrameCreditGate"/>), never by a stretch of wall clock.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AReaderFarBehindLosesTheOldestFramesNotTheNewest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        // Disposed before the stream, which a closed gate would hold for ever: a blocked frame call never sees the stop.
        using var gate = state.GateFrames(credits: 1);
        var first = (int)(await NextAsync(frames)).MaxValue;
        frames.Current.Release();
        var limit = TestDalCameraDriver.HandOffFrames(camera.VideoRoi);

        // Twenty frames more than the hand-off holds while the reader waits.
        await gate.Grant(limit + 20).WaitAsync(ct);
        var next = await NextAsync(frames);
        ((int)next.MaxValue).ShouldBe(first + 21, "the twenty oldest went first");
        next.Release();
        camera.VideoFramesReplaced.ShouldBe(20);
        camera.VideoHandOffHighWater.ShouldBe(limit);
    }

    /// <summary>
    /// Frames the body took under the old window and hands over after a pan are not under the new one, nor are frames read
    /// before the pan reached the body: the recenter loop must not measure the disk on them (it would correct the same
    /// offset twice). The first frame the body has to be waited for after the pan is the first under the new window.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task FramesTakenBeforeAPanAreNotUnderTheNewWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();
        state.PanBacklog = 3;

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        var first = await NextAsync(frames);
        camera.IsUnderVideoRoi(first).ShouldBeTrue("nothing has moved the window yet");
        first.Release();

        await camera.JogRoiAsync(8, 0, ct);
        Image frame;
        while (!camera.IsUnderVideoRoi(frame = await NextAsync(frames)))
        {
            frame.Release();
        }
        ((int)frame.MaxValue).ShouldBeGreaterThan(state.ServedAtPan + state.PanBacklog, "the backlog the pan left was taken under the old window");
        frame.Release();
        camera.VideoRoi.X.ShouldBe(26);
    }

    /// <summary>
    /// The settle after a pan asks for a frame without waiting, which a ZWO body (it pans mid-stream too) was never asked
    /// before. An answer it does not expect there ends the settle and is never a stall: were it one, every recentring
    /// pan would restart the stream.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnErrorToTheSettlesCallWithoutAWaitIsNotAStall()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();
        state.ErrorAtWaitZero = CMOSErrorCode.GeneralError;

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        await camera.JogRoiAsync(8, 0, ct);
        Image frame;
        while (!camera.IsUnderVideoRoi(frame = await NextAsync(frames)))
        {
            frame.Release();
        }
        frame.Release();

        camera.VideoStalls.ShouldBe(0);
        state.VideoStarts.ShouldBe(1, "the stream was never restarted for it");
        camera.VideoRoi.X.ShouldBe(26);
    }

    /// <summary>
    /// A body whose buffers never run dry after a pan (a reader that never catches up) would keep the settle going for
    /// ever, and the recenter loop, which measures only frames under the window, would never act again. So the settle
    /// is bounded in time (<c>VideoSettleLimit</c>), and ends as an empty buffer would end it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ASettleWhoseBufferNeverEmptiesEndsInTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, time) = await ScriptedDalCamera.ConnectAsync(output, canStream: true);
        camera.NumX = 64;
        camera.NumY = 32;
        state.PanBacklog = int.MaxValue;

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        await camera.JogRoiAsync(8, 0, ct);
        while (camera.VideoRoi.X != 26)
        {
            (await NextAsync(frames)).Release();
        }

        // The pan has reached the body, so its settle has started: past its limit, it ends.
        time.Advance(TestDalCameraDriver.VideoSettleLimit + TimeSpan.FromMilliseconds(1));
        Image frame;
        while (!camera.IsUnderVideoRoi(frame = await NextAsync(frames)))
        {
            frame.Release();
        }
        frame.Release();
        camera.VideoStalls.ShouldBe(0);
    }

    /// <summary>
    /// A body that goes silent mid-stream, answering only Timeout as ASIGetVideoData does with no frame, is a stall once
    /// the silence passes the stream's limit (<see cref="Devices.DAL.VideoSilenceWatch"/>), and the ladder brings it back as
    /// it would a stall the body reported.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ABodyThatGoesSilentMidStreamIsBroughtBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        state.StallCode = CMOSErrorCode.Timeout;
        state.Stall(FakeCmosState.Cure.Restart);

        await ReadUntilRecoveredAsync(camera, frames, 1);
        var recovery = camera.VideoRecoveryLog.Single();
        (recovery.Code, recovery.Rung).ShouldBe((CMOSErrorCode.Timeout, TestDalCameraDriver.VideoRecoveryRung.Restart));
        camera.VideoStalls.ShouldBe(1);
    }

    /// <summary>
    /// (a) A body that finishes the frame in flight at the OLD exposure after it is lowered, as a ZWO may: that frame comes
    /// the old exposure after the last, and the new exposure's limit called the wait a stall (review round 2 of #1605,
    /// question 1), a restart and a gap for nothing. The watch holds the longer exposure until two frames have arrived since.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task LoweringTheExposureMidStreamWaitsOutTheFrameInFlight()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();
        state.PacedByExposure = true;

        // 20 s frames, each 80 waits after the last: past the 2 s a 5 ms exposure is allowed, and past the 15 s the frames'
        // own silences are ever allowed, so only the held exposure covers the frame in flight.
        await using var frames = camera.CaptureVideoAsync(new VideoCaptureOptions(TimeSpan.FromSeconds(20)), ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        await camera.ApplyVideoControlsAsync(new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct);

        // The frame in flight, then frames at the new exposure.
        for (var frame = 0; frame < 4; frame++)
        {
            (await NextAsync(frames)).Release();
        }
        camera.VideoStalls.ShouldBe(0, "the frame in flight came at the exposure it began with, which is no stall");
        state.Controls[CMOSControlType.Exposure].ShouldBe(5_000, "the new exposure is on the body");
        state.VideoStarts.ShouldBe(1, "never restarted");
    }

    /// <summary>
    /// (b) A healthy stream whose frame period is its READOUT, over 2 s (a 60 MP ZWO at full frame, 16 bits, over USB 2): the
    /// exposure's limit called every frame's wait a stall and the ladder gave up on it, where the stream before the watchdog
    /// waited for ever (review round 2 of #1605, question 2). The limit also covers the stream's own recent frames.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AStreamWhoseFramePeriodIsItsReadoutKeepsStreaming()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();
        state.WaitsPerFrame = 12; // 3 s a frame at a 5 ms exposure

        await using var frames = camera.CaptureVideoAsync(new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).GetAsyncEnumerator(ct);
        for (var frame = 0; frame < 5; frame++)
        {
            (await NextAsync(frames)).Release();
        }
        camera.VideoStalls.ShouldBe(0, "a slow frame is no stall");
        state.VideoStarts.ShouldBe(1, "never restarted");
    }

    /// <summary>
    /// (b) A slow stream that then goes silent is still called stalled, within twice its longest recent silence plus 1 s, and
    /// a restart brings it back: the limit is the STREAM's, carried across the rung, so the restarted stream's first frame,
    /// as slow as the ones before, is no second stall.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ASlowStreamThatGoesSilentIsBroughtBackAndItsFirstFrameAfterIsNoStall()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();
        state.WaitsPerFrame = 12;

        await using var frames = camera.CaptureVideoAsync(new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).GetAsyncEnumerator(ct);
        for (var frame = 0; frame < 3; frame++)
        {
            (await NextAsync(frames)).Release();
        }
        state.StallCode = CMOSErrorCode.Timeout;
        state.Stall(FakeCmosState.Cure.Restart);

        await ReadUntilRecoveredAsync(camera, frames, 1);
        (await NextAsync(frames)).Release();
        camera.VideoRecoveryLog.Single().Rung.ShouldBe(TestDalCameraDriver.VideoRecoveryRung.Restart);
        camera.VideoStalls.ShouldBe(1, "the silence, and nothing after the restart");
    }

    /// <summary>
    /// A stall (ToupTek.SDK answers GeneralError once the body stops sending) is brought back by restarting the stream on
    /// the same handle, which undid 10 of 11 on a G3M678M; the stream goes on, and the stall is counted and logged.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AStallIsBroughtBackByARestartOfTheStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        var opens = state.Opens;
        state.Stall(FakeCmosState.Cure.Restart);

        await ReadUntilRecoveredAsync(camera, frames, 1);
        camera.VideoRecoveryLog.Single().Rung.ShouldBe(TestDalCameraDriver.VideoRecoveryRung.Restart);
        camera.VideoStalls.ShouldBe(1);
        (state.VideoStarts, state.VideoStops).ShouldBe((2, 1), "stopped as it stalled and started again");
        state.Opens.ShouldBe(opens, "a restart keeps the handle");
        state.ResetCount.ShouldBe(0);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStallARestartCannotUndoIsBroughtBackByClosingAndOpeningTheCameraWithEverySettingTheStreamHad()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, formats: [PixelDataFormat.RAW8, PixelDataFormat.RAW16], canStream: true,
            hasHighSpeed: true, bandwidthMax: 100);
        camera.NumX = 64;
        camera.NumY = 32;
        var options = new VideoCaptureOptions(TimeSpan.FromMilliseconds(5), Gain: 42, HighSpeedMode: true, BitDepth: BitDepth.Int8);

        await using var frames = camera.CaptureVideoAsync(options, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        await camera.JogRoiAsync(8, -4, ct);
        while (camera.VideoRoi.X != 26)
        {
            (await NextAsync(frames)).Release();
        }
        var window = camera.VideoRoi;
        var opens = state.Opens;
        state.ForgetSettingsOnOpen = true;
        state.Stall(FakeCmosState.Cure.Reopen);

        await ReadUntilRecoveredAsync(camera, frames, 1);
        camera.VideoRecoveryLog.Single().Rung.ShouldBe(TestDalCameraDriver.VideoRecoveryRung.Reopen);
        (state.Closes, state.Opens).ShouldBe((1, opens + 1), "closed once and opened again, the same body");
        state.ResetCount.ShouldBe(0, "a reopen was enough");

        // The body opened at its defaults; the stream put back everything it had.
        state.Controls[CMOSControlType.Exposure].ShouldBe(5000);
        state.Controls[CMOSControlType.Gain].ShouldBe(42);
        state.Controls[CMOSControlType.HighSpeedMode].ShouldBe(1);
        state.Controls[CMOSControlType.BandwidthOverload].ShouldBe(100, "the stream takes the whole bandwidth again");
        state.Format.ShouldBe(PixelDataFormat.RAW8);
        (state.Width, state.Height, state.StartX, state.StartY).ShouldBe((window.Width, window.Height, window.X, window.Y), "the window, pans and all");
        camera.VideoRoi.ShouldBe(window);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStallOnlyAResetUndoesIsBroughtBackByTheResetWithTheStreamsSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, canReset: true, canStream: true);
        camera.NumX = 64;
        camera.NumY = 32;

        await using var frames = camera.CaptureVideoAsync(new VideoCaptureOptions(TimeSpan.FromMilliseconds(5), Gain: 42), ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        state.Stall(FakeCmosState.Cure.Reset);

        await ReadUntilRecoveredAsync(camera, frames, 1);
        camera.VideoRecoveryLog.Single().Rung.ShouldBe(TestDalCameraDriver.VideoRecoveryRung.Reset);
        state.ResetCount.ShouldBe(1);
        state.Closes.ShouldBe(1, "the reopen came first, and did not undo it");
        state.Controls[CMOSControlType.Exposure].ShouldBe(5000, "a reset puts the body back to its power-on defaults, and the stream its own settings");
        state.Controls[CMOSControlType.Gain].ShouldBe(42);
        (state.Width, state.Height).ShouldBe((64, 32));
    }

    [Fact(Timeout = 30_000)]
    public async Task AStallNothingUndoesEndsTheStreamWithAPlainReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, canReset: true, canStream: true);
        camera.NumX = 64;
        camera.NumY = 32;

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        state.Stall(FakeCmosState.Cure.Never);

        var failed = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            while (await frames.MoveNextAsync())
            {
                frames.Current.Release();
            }
        });
        failed.Message.ShouldContain("stopped sending frames");
        failed.Message.ShouldContain("nor a reset brought it back");
        (camera.VideoStalls, camera.VideoRecoveries).ShouldBe((1, 0));
        state.ResetCount.ShouldBe(1, "the ladder went all the way up");
        state.Streaming.ShouldBeFalse("the stream was stopped on the way out");
    }

    [Fact(Timeout = 30_000)]
    public async Task ABodyThatRefusesItsResetGivesUpAndSaysTheResetWasRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, canReset: true, canStream: true);
        camera.NumX = 64;
        camera.NumY = 32;
        state.RefuseReset = true;

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        state.Stall(FakeCmosState.Cure.Never);

        var failed = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            while (await frames.MoveNextAsync())
            {
                frames.Current.Release();
            }
        });
        failed.Message.ShouldContain("refused the reset");
        failed.Message.ShouldNotContain("nor a reset brought it back", Case.Insensitive, "a reset that never ran brought nothing back");
    }

    [Fact(Timeout = 30_000)]
    public async Task ABodyThatCannotBeResetGivesUpAfterTheReopenAndSaysToReplugIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        state.Stall(FakeCmosState.Cure.Never);

        var failed = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            while (await frames.MoveNextAsync())
            {
                frames.Current.Release();
            }
        });
        failed.Message.ShouldContain("cannot be reset from software");
        state.ResetCount.ShouldBe(0);
        state.Closes.ShouldBe(1);
    }

    /// <summary>
    /// A restart that brings back a body which then sends nothing at all, not even the stall's answer, is caught by the
    /// silence watchdog (<see cref="Devices.DAL.VideoSilenceWatch"/>) and the next rung tried, rather than waited on for ever.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ABodyThatGoesSilentAfterARestartIsClosedAndOpenedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        state.Stall(FakeCmosState.Cure.Reopen);
        // The stall is reported once; the restarted stream then answers only Timeout.
        state.OnStopVideo = () => state.StallCode = CMOSErrorCode.Timeout;

        await ReadUntilRecoveredAsync(camera, frames, 1);
        camera.VideoRecoveryLog.Single().Rung.ShouldBe(TestDalCameraDriver.VideoRecoveryRung.Reopen);
        state.VideoStarts.ShouldBe(3, "the first start, the restart that stayed silent, and the start after the reopen");
    }

    [Fact(Timeout = 30_000)]
    public async Task AStallSoonAfterARecoveryClimbsTheLadderAndOneLongAfterStartsItAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, time) = await ScriptedDalCamera.ConnectAsync(output, canStream: true);
        camera.NumX = 64;
        camera.NumY = 32;

        await using var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        state.Stall(FakeCmosState.Cure.Restart);
        await ReadUntilRecoveredAsync(camera, frames, 1);

        // Stalled again before the stream had run long enough to count as healthy: the restart did not hold, so the next
        // rung brings this one back, though a restart would have done.
        state.Stall(FakeCmosState.Cure.Restart);
        await ReadUntilRecoveredAsync(camera, frames, 2);
        camera.VideoRecoveryLog[1].Rung.ShouldBe(TestDalCameraDriver.VideoRecoveryRung.Reopen);
        camera.VideoStalls.ShouldBe(2, "a stall after frames is the camera's own, whichever rung it gets");

        // Healthy for longer than VideoHealthyAfter: the next stall starts from a restart again.
        time.Advance(TestDalCameraDriver.VideoHealthyAfter + TimeSpan.FromSeconds(1));
        (await NextAsync(frames)).Release();
        state.Stall(FakeCmosState.Cure.Restart);
        await ReadUntilRecoveredAsync(camera, frames, 3);
        camera.VideoRecoveryLog[2].Rung.ShouldBe(TestDalCameraDriver.VideoRecoveryRung.Restart);
        camera.VideoStalls.ShouldBe(3);
    }

    /// <summary>
    /// A stop asked while a stalled stream is being stopped (5 s on a stalled ToupTek) ends the stream there: the next
    /// rung is never started.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AStopDuringARecoveryEndsTheStreamWithoutClimbingFurther()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state) = await StreamingCameraAsync();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        var frames = camera.CaptureVideoAsync(OneMillisecond, ct).GetAsyncEnumerator(ct);
        (await NextAsync(frames)).Release();
        var opens = state.Opens;
        state.OnStopVideo = () =>
        {
            stopping.TrySetResult();
            release.Wait(ct);
        };
        state.Stall(FakeCmosState.Cure.Reopen);
        await stopping.Task.WaitAsync(ct);

        var disposed = frames.DisposeAsync().AsTask();
        release.Set();
        await disposed;

        state.VideoStarts.ShouldBe(1, "no restart after the stop");
        state.Opens.ShouldBe(opens, "no reopen either");
        state.Streaming.ShouldBeFalse();
    }

    /// <summary>Reads until the stream has come back from its <paramref name="recoveries"/>th stall, releasing what it reads.</summary>
    private static async Task ReadUntilRecoveredAsync(TestDalCameraDriver camera, IAsyncEnumerator<Image> frames, int recoveries)
    {
        while (camera.VideoRecoveries < recoveries)
        {
            (await NextAsync(frames)).Release();
        }
        // The first frame after the recovery, and one more, so the stream is seen going on.
        (await NextAsync(frames)).Release();
        camera.VideoRecoveryLog.Length.ShouldBe(recoveries);
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
