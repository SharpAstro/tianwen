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
    /// perfectly and simply shows the wrong frame.
    /// </summary>
    public class GuidePreviewTests
    {
        /// <summary>
        /// A mono guide frame carrying a recycled camera buffer, so the tests can watch the refcount
        /// rather than infer ownership. Shaped like a real guide frame: mostly background with a star,
        /// which is also what keeps the stretch's median/MAD scan out of its degenerate case.
        /// </summary>
        private static Image BufferedGuideFrame(out ChannelBuffer buffer, int width = 48, int height = 32,
            bool clobberOnRecycle = false)
        {
            var data = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    data[y, x] = 0.02f + (x * 7 + y * 13) % 17 * 0.0005f;
                }
            }

            data[height / 2, width / 2] = 0.9f;
            data[height / 2, width / 2 + 1] = 0.55f;
            data[height / 2 + 1, width / 2] = 0.55f;

            // Recycling is not a bookkeeping event: the camera writes the NEXT frame into this very array.
            // Modelling that as a clobber is what gives the across-the-encode test teeth, since the raw
            // float[,] stays readable after a release and a stale read would otherwise look like a pass.
            var owned = new ChannelBuffer(
                data,
                onRelease: recycled =>
                {
                    if (clobberOnRecycle)
                    {
                        for (var y = 0; y < recycled.GetLength(0); y++)
                        {
                            for (var x = 0; x < recycled.GetLength(1); x++)
                            {
                                recycled[y, x] = 0.5f;
                            }
                        }
                    }
                });
            buffer = owned;

            return new Image(
                [new Channel(data, default, 0f, 0.9f, 0) { Buffer = owned }],
                BitDepth.Float32,
                pedestal: 0f,
                new ImageMeta { SensorType = SensorType.Monochrome });
        }

        private static ISessionTelemetry TelemetryWith(Image? frame, int frameNumber)
        {
            var telemetry = Substitute.For<ISessionTelemetry>();
            telemetry.LastGuideFrame.Returns(frame);
            telemetry.LastGuideFrameNumber.Returns(frameNumber);
            return telemetry;
        }

        [Fact]
        public async Task RenderAsync_WithALiveFrame_EncodesItAndReportsTheFrameNumber()
        {
            var frame = BufferedGuideFrame(out _);

            var (jpeg, frameNumber, failure) = await GuidePreview.RenderAsync(
                TelemetryWith(frame, 4711), PreviewEncoder.DefaultQuality, 1.0, TestContext.Current.CancellationToken);

            failure.ShouldBeNull();
            jpeg.ShouldNotBeNull();
            frameNumber.ShouldBe(4711);

            // Decode rather than assert a byte count: the point of the endpoint is a viewable picture.
            Image.TryDecodeRaster(jpeg, out var decoded).ShouldBeTrue();
            decoded.ShouldNotBeNull();
            decoded.Width.ShouldBe(48);
            decoded.Height.ShouldBe(32);
        }

        [Fact]
        public async Task RenderAsync_GivesTheBorrowBack_SoTheGuiderCanStillRecycleTheBuffer()
        {
            var frame = BufferedGuideFrame(out var buffer);
            buffer.RefCount.ShouldBe(1); // the frame itself

            await GuidePreview.RenderAsync(
                TelemetryWith(frame, 1), PreviewEncoder.DefaultQuality, 1.0, TestContext.Current.CancellationToken);

            // Leaking the lease would pin a guide-camera buffer for the rest of the night, one per poll,
            // and starve the recycle loop it came from.
            buffer.RefCount.ShouldBe(1);
            buffer.IsReleased.ShouldBeFalse();
        }

        [Fact]
        public async Task RenderAsync_HoldsTheFrameAcrossTheEncode_EvenIfTheGuiderPublishesTheNextOne()
        {
            var frame = BufferedGuideFrame(out var buffer, clobberOnRecycle: true);
            var telemetry = TelemetryWith(frame, 7);

            // The guide loop's swap, mid-request: it releases the frame it published and moves on. The
            // encode must still complete against pixels nobody has reclaimed.
            var render = GuidePreview.RenderAsync(
                telemetry, PreviewEncoder.DefaultQuality, 1.0, TestContext.Current.CancellationToken);
            frame.Release();

            var (jpeg, _, failure) = await render;

            failure.ShouldBeNull();
            jpeg.ShouldNotBeNull();
            Image.TryDecodeRaster(jpeg, out var decoded).ShouldBeTrue();
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
            var (jpeg, frameNumber, failure) = await GuidePreview.RenderAsync(
                TelemetryWith(null, 0), PreviewEncoder.DefaultQuality, 1.0, TestContext.Current.CancellationToken);

            jpeg.ShouldBeNull();
            frameNumber.ShouldBe(0);
            failure.ShouldBe(GuidePreview.NoFrameFailure);
        }

        [Fact]
        public async Task RenderAsync_WhenTheFrameIsAlreadyGone_ReportsAMissInsteadOfEncodingRecycledPixels()
        {
            var frame = BufferedGuideFrame(out var buffer);

            // The guider published this frame and has since moved on: its buffer is back with the camera.
            frame.Release();
            buffer.IsReleased.ShouldBeTrue();

            var (jpeg, _, failure) = await GuidePreview.RenderAsync(
                TelemetryWith(frame, 9), PreviewEncoder.DefaultQuality, 1.0, TestContext.Current.CancellationToken);

            jpeg.ShouldBeNull();
            failure.ShouldBe(GuidePreview.NoFrameFailure);
        }

        [Fact]
        public async Task RenderAsync_ScalesTheOutput_SoAPhoneCanPollASmallerPicture()
        {
            var frame = BufferedGuideFrame(out _, width: 64, height: 64);

            var (jpeg, _, failure) = await GuidePreview.RenderAsync(
                TelemetryWith(frame, 1), PreviewEncoder.DefaultQuality, 0.5, TestContext.Current.CancellationToken);

            failure.ShouldBeNull();
            jpeg.ShouldNotBeNull();
            Image.TryDecodeRaster(jpeg, out var decoded).ShouldBeTrue();
            decoded.ShouldNotBeNull();
            decoded.Width.ShouldBe(32);
            decoded.Height.ShouldBe(32);
        }
    }
}
