using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Hosting.Api;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins <c>GET /api/v1/preview/guider</c>'s render step. The route itself is one line; what needs a
    /// test is the borrow, because the guide loop releases the previous frame on every exposure and the
    /// failure it produces is silent: a JPEG encoded from a buffer the camera has already reused decodes
    /// perfectly and simply shows the wrong frame. And the token check, which must come before the frame is
    /// touched at all, or every poll pays for an encode the client throws away.
    /// </summary>
    public class GuidePreviewTests
    {
        private static ISessionTelemetry TelemetryWith(Image? frame, int frameNumber)
        {
            var telemetry = Substitute.For<ISessionTelemetry>();
            telemetry.LastGuideFrame.Returns(frame);
            telemetry.LastGuideFrameNumber.Returns(frameNumber);
            return telemetry;
        }

        private static Task<PreviewRender> RenderAsync(ISessionTelemetry telemetry, double scale = 1.0, int? ifNoneMatch = null)
            => GuidePreview.RenderAsync(telemetry, PreviewEncoder.DefaultQuality, scale, ifNoneMatch, TestContext.Current.CancellationToken);

        [Fact]
        public async Task RenderAsync_WithALiveFrame_EncodesItAndReportsTheFrameNumber()
        {
            var frame = TestFrames.BufferedMono(out _);

            var render = await RenderAsync(TelemetryWith(frame, 4711));

            render.Failure.ShouldBeNull();
            render.FrameNumber.ShouldBe(4711);

            // Decode rather than assert a byte count: the point of the endpoint is a viewable picture.
            Image.TryDecodeRaster(render.Jpeg.ShouldNotBeNull(), out var decoded).ShouldBeTrue();
            decoded.ShouldNotBeNull();
            decoded.Width.ShouldBe(48);
            decoded.Height.ShouldBe(32);
        }

        [Fact]
        public async Task RenderAsync_GivesTheBorrowBack_SoTheGuiderCanStillRecycleTheBuffer()
        {
            var frame = TestFrames.BufferedMono(out var buffer);
            buffer.RefCount.ShouldBe(1); // the frame itself

            await RenderAsync(TelemetryWith(frame, 1));

            // Leaking the lease would pin a guide-camera buffer for the rest of the night, one per poll,
            // and starve the recycle loop it came from.
            buffer.RefCount.ShouldBe(1);
            buffer.IsReleased.ShouldBeFalse();
        }

        [Fact]
        public async Task RenderAsync_HoldsTheFrameAcrossTheEncode_EvenIfTheGuiderPublishesTheNextOne()
        {
            var frame = TestFrames.BufferedMono(out var buffer, clobberOnRecycle: true);

            // The guide loop's swap, mid-request: it releases the frame it published and moves on. The
            // encode must still complete against pixels nobody has reclaimed.
            var rendering = RenderAsync(TelemetryWith(frame, 7));
            frame.Release();

            var render = await rendering;

            render.Failure.ShouldBeNull();
            Image.TryDecodeRaster(render.Jpeg.ShouldNotBeNull(), out var decoded).ShouldBeTrue();
            decoded.ShouldNotBeNull();

            // The star must still be there. Recycling flattens the array to one value, so a preview
            // encoded from a reclaimed buffer decodes as a uniform grey rectangle: a perfectly valid
            // JPEG of nothing, which is exactly why this asserts contrast and not success.
            var span = decoded.GetChannelSpan(0);
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var i = 0; i < span.Length; i++)
            {
                min = System.Math.Min(min, span[i]);
                max = System.Math.Max(max, span[i]);
            }

            (max - min).ShouldBeGreaterThan(0.1f * decoded.MaxValue);

            // The frame's own ref is gone and the lease has been returned, so now it recycles - and not
            // one poll earlier.
            buffer.IsReleased.ShouldBeTrue();
        }

        [Fact]
        public async Task RenderAsync_WithNoFrame_ReportsAMissRatherThanFailing()
        {
            var render = await RenderAsync(TelemetryWith(null, 0));

            render.Jpeg.ShouldBeNull();
            render.IsUnchanged.ShouldBeFalse();
            render.Failure.ShouldBe(GuidePreview.NoFrameFailure);
            render.StatusCode.ShouldBe(404);
        }

        [Fact]
        public async Task RenderAsync_WhenTheFrameIsAlreadyGone_ReportsAMissInsteadOfEncodingRecycledPixels()
        {
            var frame = TestFrames.BufferedMono(out var buffer);

            // The guider published this frame and has since moved on: its buffer is back with the camera.
            frame.Release();
            buffer.IsReleased.ShouldBeTrue();

            var render = await RenderAsync(TelemetryWith(frame, 9));

            render.Jpeg.ShouldBeNull();
            render.Failure.ShouldBe(GuidePreview.NoFrameFailure);
        }

        [Fact]
        public async Task RenderAsync_ScalesTheOutput_SoAPhoneCanPollASmallerPicture()
        {
            var frame = TestFrames.BufferedMono(out _, width: 64, height: 64);

            var render = await RenderAsync(TelemetryWith(frame, 1), scale: 0.5);

            render.Failure.ShouldBeNull();
            Image.TryDecodeRaster(render.Jpeg.ShouldNotBeNull(), out var decoded).ShouldBeTrue();
            decoded.ShouldNotBeNull();
            decoded.Width.ShouldBe(32);
            decoded.Height.ShouldBe(32);
        }

        [Fact]
        public async Task RenderAsync_WhenTheClientHasTheFrame_AnswersUnchangedWithoutTouchingIt()
        {
            var frame = TestFrames.BufferedMono(out _);
            var telemetry = TelemetryWith(frame, 12);

            var render = await RenderAsync(telemetry, ifNoneMatch: 12);

            render.IsUnchanged.ShouldBeTrue();
            render.FrameNumber.ShouldBe(12, "a 304 still names the frame, so the client keeps its token");
            render.Jpeg.ShouldBeNull();

            // Never reading the frame is what proves nothing was leased or encoded: the render used to encode
            // first and leave the comparison to the client.
            _ = telemetry.DidNotReceive().LastGuideFrame;
        }

        [Fact]
        public async Task RenderAsync_WhenTheClientHasAnOlderFrame_EncodesTheCurrentOne()
        {
            var render = await RenderAsync(TelemetryWith(TestFrames.BufferedMono(out _), 13), ifNoneMatch: 12);

            render.IsUnchanged.ShouldBeFalse();
            render.Jpeg.ShouldNotBeNull();
            render.FrameNumber.ShouldBe(13);
        }
    }
}
