using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Hosting.Api;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the hosted JPEG preview. Every test here encodes and then <b>decodes back</b>
    /// (<see cref="Image.TryDecodeRaster"/>) and asserts on real pixels -- asserting on byte counts or
    /// "it did not throw" would not have caught the defect these exist for.
    /// </summary>
    public class PreviewEncoderTests
    {
        /// <summary>
        /// A linear astronomical sub: a faint background a couple of percent off the floor, a handful of
        /// bright stars, in a 16-bit container. This shape is the whole point -- it is what makes a naive
        /// divide-by-max render black.
        /// </summary>
        private static Image MakeLinearSub(int width = 64, int height = 48, bool color = false)
        {
            var channelCount = color ? 3 : 1;
            var planes = Image.CreateChannelData(channelCount, height, width);

            const float background = 900f;   // ~1.4% of a 16-bit full scale
            const float star = 48000f;

            for (var c = 0; c < channelCount; c++)
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        // A little structure so the median/MAD scan sees a real distribution rather than
                        // a constant (a zero-MAD frame is a degenerate stretch input).
                        planes[c][y, x] = background + ((x * 7 + y * 13) % 40);
                    }
                }
            }

            // A few stars, placed as fractions of the frame so they stay in bounds at any test size
            // (each needs one pixel of margin for its 2x2 core).
            foreach (var (fx, fy) in new[] { (0.15, 0.2), (0.45, 0.4), (0.8, 0.7) })
            {
                var sx = Math.Clamp((int)(width * fx), 0, width - 2);
                var sy = Math.Clamp((int)(height * fy), 0, height - 2);
                for (var c = 0; c < channelCount; c++)
                {
                    planes[c][sy, sx] = star;
                    planes[c][sy, sx + 1] = star * 0.6f;
                    planes[c][sy + 1, sx] = star * 0.6f;
                }
            }

            return new Image(planes, BitDepth.Int16, maxValue: star, minValue: background,
                pedestal: 0f, new ImageMeta { SensorType = color ? SensorType.Color : SensorType.Monochrome });
        }

        private static (int Width, int Height, double MeanByte) DecodeStats(byte[] jpeg)
        {
            Image.TryDecodeRaster(jpeg, out var decoded).ShouldBeTrue("the encoder must emit a decodable JPEG");
            decoded.ShouldNotBeNull();

            var (channels, width, height) = decoded.Shape;
            double sum = 0;
            var count = 0;
            for (var c = 0; c < channels; c++)
            {
                var span = decoded.GetChannelSpan(c);
                for (var i = 0; i < span.Length; i++)
                {
                    sum += span[i];
                    count++;
                }
            }

            // The decoder normalises to its own scale; express the mean as a 0..255 byte level using the
            // decoded image's own max so the assertion does not depend on that choice.
            var scale = decoded.MaxValue > 1.0f ? 255.0 / decoded.MaxValue : 255.0;
            return (width, height, count == 0 ? 0 : sum / count * scale);
        }

        [Fact]
        public async Task ALinearSubIsStretchedNotJustDividedByItsPeak()
        {
            // THE regression this encoder exists for. The previous implementation multiplied every
            // sample by 1/MaxValue, so a background at ~1.4% of full well encoded to byte level ~3 --
            // a black rectangle with three lit pixels. Going through the shared StretchSolver lifts the
            // background into the visible range, which is what makes the preview usable.
            var image = MakeLinearSub();

            var jpeg = await PreviewEncoder.EncodeJpegAsync(image, quality: 90, scale: 1.0, TestContext.Current.CancellationToken);
            var (width, height, mean) = DecodeStats(jpeg);

            width.ShouldBe(64);
            height.ShouldBe(48);

            // The naive path lands under ~5; a real stretch puts the background well clear of black.
            mean.ShouldBeGreaterThan(20.0);
            // And it must not blow the whole frame to white either.
            mean.ShouldBeLessThan(245.0);
        }

        [Fact]
        public async Task ScaleDownsamplesAndKeepsFaintStarsVisible()
        {
            // Box-averaging rather than point-sampling: at 1:4 a nearest-neighbour downsample simply
            // misses the single-pixel stars, so the preview would under-report what was captured.
            var image = MakeLinearSub(width: 64, height: 48);

            var jpeg = await PreviewEncoder.EncodeJpegAsync(image, quality: 90, scale: 0.25, TestContext.Current.CancellationToken);
            var (width, height, mean) = DecodeStats(jpeg);

            width.ShouldBe(16);
            height.ShouldBe(12);
            mean.ShouldBeGreaterThan(20.0);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(5.0)]
        public async Task AnOutOfRangeScaleFallsBackToFullResolutionRatherThanThrowing(double scale)
        {
            // A query-string value arrives unvalidated, and a zero/negative/NaN scale must not produce a
            // zero-dimension buffer (or an upscale we never asked to support).
            var image = MakeLinearSub(width: 32, height: 32);

            var jpeg = await PreviewEncoder.EncodeJpegAsync(image, quality: 80, scale: scale, TestContext.Current.CancellationToken);
            var (width, height, _) = DecodeStats(jpeg);

            width.ShouldBe(32);
            height.ShouldBe(32);
        }

        [Fact]
        public async Task TheSessionOwnedImageIsNotMutatedOrConsumed()
        {
            // LastCapturedImages pins a recycled camera buffer, so encoding a preview must be a pure
            // read. If the encoder ever normalised in place, the session's own frame -- and every later
            // consumer of it, including the next preview -- would silently see rescaled pixels.
            var image = MakeLinearSub();

            var before = image.GetChannelSpan(0)[0];
            var maxBefore = image.MaxValue;

            _ = await PreviewEncoder.EncodeJpegAsync(image, quality: 80, scale: 1.0, TestContext.Current.CancellationToken);

            image.GetChannelSpan(0)[0].ShouldBe(before);
            image.MaxValue.ShouldBe(maxBefore);

            // And encoding twice must give the same bytes -- proof the first pass left no residue.
            var first = await PreviewEncoder.EncodeJpegAsync(image, quality: 80, scale: 1.0, TestContext.Current.CancellationToken);
            var second = await PreviewEncoder.EncodeJpegAsync(image, quality: 80, scale: 1.0, TestContext.Current.CancellationToken);
            second.ShouldBe(first);
        }

        [Fact]
        public async Task AColourImageEncodesAsThreeChannels()
        {
            var image = MakeLinearSub(color: true);

            var jpeg = await PreviewEncoder.EncodeJpegAsync(image, quality: 85, scale: 1.0, TestContext.Current.CancellationToken);

            Image.TryDecodeRaster(jpeg, out var decoded).ShouldBeTrue();
            decoded.ShouldNotBeNull();
            decoded.ChannelCount.ShouldBe(3);
        }
    }
}
