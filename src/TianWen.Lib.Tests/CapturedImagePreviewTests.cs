using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Hosting.Api;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <c>GET /api/v1/preview/{otaIndex}</c>'s render step, the per-OTA twin of <see cref="GuidePreviewTests"/>:
/// the frame is the session's, so it is leased for the encode; the token is the slot's own number, never the
/// camera's exposure counter; and a client that has the frame is answered before the frame is touched (P0b
/// item 15 of docs/plans/hardware-in-the-server.md, #752).
/// </summary>
public class CapturedImagePreviewTests
{
    private static ISessionTelemetry TelemetryWith(Image? frame, int frameNumber)
    {
        var telemetry = Substitute.For<ISessionTelemetry>();
        telemetry.LastCapturedImages.Returns([frame]);
        telemetry.LastCapturedImageNumber(0).Returns(frameNumber);
        return telemetry;
    }

    private static Task<PreviewRender> RenderAsync(ISessionTelemetry telemetry, int otaIndex = 0, int? ifNoneMatch = null)
        => CapturedImagePreview.RenderAsync(
            telemetry, otaIndex, PreviewEncoder.DefaultQuality, 1.0, ifNoneMatch, TestContext.Current.CancellationToken);

    [Fact]
    public async Task APublishedFrameEncodesUnderTheSlotsOwnNumberNotTheCamerasExposureCounter()
    {
        var telemetry = TelemetryWith(TestFrames.BufferedMono(out _), frameNumber: 5);

        // The camera is exposing the NEXT frame, which its counter already names. The endpoint stamped that
        // counter, so it served frame 5 as 6 and then skipped the real frame 6 when it landed.
        telemetry.CameraStates.Returns(ImmutableArray.Create(
            new CameraExposureState(0, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(120), FrameNumber: 6, "L", 1000, CameraState.Exposing)));

        var render = await RenderAsync(telemetry);

        render.Failure.ShouldBeNull();
        render.FrameNumber.ShouldBe(5);
        Image.TryDecodeRaster(render.Jpeg.ShouldNotBeNull(), out var decoded).ShouldBeTrue();
        decoded.ShouldNotBeNull().Width.ShouldBe(48);
    }

    [Fact]
    public async Task TheBorrowGoesBackSoTheCameraCanStillRecycleTheBuffer()
    {
        var frame = TestFrames.BufferedMono(out var buffer);

        await RenderAsync(TelemetryWith(frame, 1));

        buffer.RefCount.ShouldBe(1, "only the frame's own ref is left; a leaked lease would pin a camera buffer per poll");
        buffer.IsReleased.ShouldBeFalse();
    }

    [Fact]
    public async Task TheFrameIsHeldAcrossTheEncodeEvenIfTheSessionReplacesItMidRequest()
    {
        var frame = TestFrames.BufferedMono(out var buffer, clobberOnRecycle: true);

        // The slot's swap, mid-request: it releases the frame it showed. The encode must still read pixels
        // nobody has reclaimed, which the old bare read did not guarantee.
        var rendering = RenderAsync(TelemetryWith(frame, 3));
        frame.Release();
        var render = await rendering;

        Image.TryDecodeRaster(render.Jpeg.ShouldNotBeNull(), out var decoded).ShouldBeTrue();
        var span = decoded.ShouldNotBeNull().GetChannelSpan(0);
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var value in span)
        {
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        (max - min).ShouldBeGreaterThan(0.1f * decoded.MaxValue, "a recycled buffer decodes as flat grey, not a star field");
        buffer.IsReleased.ShouldBeTrue("the lease was given back, so the camera has it now and not a poll earlier");
    }

    [Fact]
    public async Task AClientThatHasTheFrameIsAnsweredWithoutTheFrameBeingRead()
    {
        var telemetry = TelemetryWith(TestFrames.BufferedMono(out _), frameNumber: 8);

        var render = await RenderAsync(telemetry, ifNoneMatch: 8);

        render.IsUnchanged.ShouldBeTrue();
        render.FrameNumber.ShouldBe(8, "a 304 still names the frame, so the client keeps its token");
        render.Jpeg.ShouldBeNull();

        // Never reading the slot is what proves nothing was leased or encoded: every poll used to cost a full
        // encode, compared only afterwards.
        _ = telemetry.DidNotReceive().LastCapturedImages;
    }

    [Fact]
    public async Task AClientWithAnOlderFrameGetsTheCurrentOne()
    {
        var render = await RenderAsync(TelemetryWith(TestFrames.BufferedMono(out _), frameNumber: 9), ifNoneMatch: 8);

        render.IsUnchanged.ShouldBeFalse();
        render.Jpeg.ShouldNotBeNull();
        render.FrameNumber.ShouldBe(9);
    }

    [Fact]
    public async Task AnEmptySlotIsAMissNotAFault()
    {
        var render = await RenderAsync(TelemetryWith(null, frameNumber: 0));

        render.Jpeg.ShouldBeNull();
        render.IsUnchanged.ShouldBeFalse();
        render.Failure.ShouldNotBeNull();
        render.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task AFrameAlreadyGivenBackIsAMissNotRecycledPixels()
    {
        var frame = TestFrames.BufferedMono(out var buffer);
        frame.Release();
        buffer.IsReleased.ShouldBeTrue();

        var render = await RenderAsync(TelemetryWith(frame, frameNumber: 4));

        render.Jpeg.ShouldBeNull();
        render.StatusCode.ShouldBe(404, "replaced mid-read: the next poll finds its successor");
    }

    [Fact]
    public async Task AnIndexPastTheRigIsTheCallersMistake()
    {
        var render = await RenderAsync(TelemetryWith(TestFrames.BufferedMono(out _), frameNumber: 1), otaIndex: 1);

        render.Jpeg.ShouldBeNull();
        render.StatusCode.ShouldBe(400);
    }
}
