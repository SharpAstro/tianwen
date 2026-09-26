using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the JPEG render of the frame a source shows (<c>GET /api/v1/preview/{otaIndex}</c>, <c>/preview/guider</c> and the
/// ninaAPI <c>prepared-image</c>): the frame is someone else's, so it is leased for the encode and given back; a client
/// that has it is answered before it is touched; and a frame already given back is a miss, never recycled pixels (P0b
/// item 15 of docs/plans/hardware-in-the-server.md, #752). The token is the node's (<see cref="NodeFrames"/>), pinned
/// in <see cref="NodeFramesTests"/>.
/// </summary>
public class FramePreviewTests
{
    private static Task<PreviewRender> RenderAsync(Image? frame, int number, double scale = 1.0, int? ifNoneMatch = null)
        => FramePreview.RenderAsync(new NodeFrames.Shown(frame, number), "OTA 1", PreviewEncoder.DefaultQuality, scale, ifNoneMatch,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task AFrameEncodesUnderTheTokenItIsShownWith()
    {
        var render = await RenderAsync(TestFrames.BufferedMono(out _), number: 5);

        render.Failure.ShouldBeNull();
        render.FrameNumber.ShouldBe(5);
        // Decode rather than assert a byte count: the point of the endpoint is a viewable picture.
        Image.TryDecodeRaster(render.Jpeg.ShouldNotBeNull(), out var decoded).ShouldBeTrue();
        decoded.ShouldNotBeNull().Width.ShouldBe(48);
        decoded.Height.ShouldBe(32);
    }

    [Fact]
    public async Task TheBorrowGoesBackSoTheCameraCanStillRecycleTheBuffer()
    {
        var frame = TestFrames.BufferedMono(out var buffer);

        await RenderAsync(frame, 1);

        buffer.RefCount.ShouldBe(1, "only the frame's own ref is left; a leaked lease would pin a camera buffer per poll");
        buffer.IsReleased.ShouldBeFalse();
    }

    [Fact]
    public async Task TheFrameIsHeldAcrossTheEncodeEvenIfItsPublisherReplacesItMidRequest()
    {
        var frame = TestFrames.BufferedMono(out var buffer, clobberOnRecycle: true);

        // The publisher's swap, mid-request: it releases the frame it showed. The encode must still read pixels nobody
        // has reclaimed, which a bare read does not guarantee.
        var rendering = RenderAsync(frame, 3);
        frame.Release();
        var render = await rendering;

        // Recycling flattens the array to one value, so a preview encoded from a reclaimed buffer decodes as a uniform
        // grey rectangle: a perfectly valid JPEG of nothing, which is exactly why this asserts contrast and not success.
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
    public async Task AClientThatHasTheFrameIsAnsweredWithoutTheFrameBeingLeased()
    {
        // A frame already given back: a lease would be refused and answered 404, so an Unchanged proves none was tried.
        // Every poll used to cost a full encode, compared only afterwards.
        var frame = TestFrames.BufferedMono(out _);
        frame.Release();

        var render = await RenderAsync(frame, number: 8, ifNoneMatch: 8);

        render.IsUnchanged.ShouldBeTrue();
        render.FrameNumber.ShouldBe(8, "a 304 still names the frame, so the client keeps its token");
        render.Jpeg.ShouldBeNull();
    }

    [Fact]
    public async Task AClientWithAnOlderFrameGetsTheCurrentOne()
    {
        var render = await RenderAsync(TestFrames.BufferedMono(out _), number: 9, ifNoneMatch: 8);

        render.IsUnchanged.ShouldBeFalse();
        render.Jpeg.ShouldNotBeNull();
        render.FrameNumber.ShouldBe(9);
    }

    [Fact]
    public async Task NoFrameIsAMissNotAFault()
    {
        var render = await RenderAsync(null, number: 0);

        render.Jpeg.ShouldBeNull();
        render.IsUnchanged.ShouldBeFalse();
        render.Failure.ShouldNotBeNull().ShouldContain("has no frame");
        render.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task AFrameAlreadyGivenBackIsAMissNotRecycledPixels()
    {
        var frame = TestFrames.BufferedMono(out var buffer);
        frame.Release();
        buffer.IsReleased.ShouldBeTrue();

        var render = await RenderAsync(frame, number: 4);

        render.Jpeg.ShouldBeNull();
        render.StatusCode.ShouldBe(404, "replaced mid-read: the next poll finds its successor");
    }

    [Fact]
    public async Task TheOutputScalesSoAPhoneCanPollASmallerPicture()
    {
        var render = await RenderAsync(TestFrames.BufferedMono(out _, width: 64, height: 64), number: 1, scale: 0.5);

        render.Failure.ShouldBeNull();
        Image.TryDecodeRaster(render.Jpeg.ShouldNotBeNull(), out var decoded).ShouldBeTrue();
        decoded.ShouldNotBeNull().Width.ShouldBe(32);
        decoded.Height.ShouldBe(32);
    }
}
