using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class FindStarsFromCameraImageTests
{
    [Theory]
    [InlineData(10, 22)]
    [InlineData(15, 6)]
    [InlineData(20, 3)]
    public async Task GivenCameraImageDataWhenConvertingToImageThenStarsCanBeFound(int snr_min, int expectedStars)
    {
        // given
        const int channel = 0;
        const int Width = 1280;
        const int Height = 960;
        const BitDepth BitDepth = BitDepth.Int16;
        const int Pedestal = 1;

        var cancellationToken = TestContext.Current.CancellationToken;
        var expTime = TimeSpan.FromSeconds(42);
        var fileName = $"image_data_snr-{snr_min}_stars-{expectedStars}";
        var int16WxHData = await SharedTestData.ExtractGZippedImageData(fileName, Width, Height);
        var imageMeta = new ImageMeta(fileName, DateTime.UtcNow, expTime, FrameType.Light, "", 2.4f, 2.4f, 190, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

        // when
        var imageData = Float32HxWImageData.FromWxHImageData(int16WxHData);
        var image = imageData.ToImage(BitDepth, Pedestal, imageMeta);
        var stars = await image.FindStarsAsync(channel, snrMin: snr_min, cancellationToken: cancellationToken);

        // then
        image.ShouldNotBeNull();
        image.Height.ShouldBe(Height);
        image.Width.ShouldBe(Width);
        image.BitDepth.ShouldBe(BitDepth);
        stars.ShouldNotBeNull().Count.ShouldBe(expectedStars);
    }

    [Theory]
    [InlineData(10, 22)]
    [InlineData(15, 6)]
    [InlineData(20, 3)]
    public async Task GivenCameraImageDataWhenConvertingToImageAndNormalizingThenStarsCanBeFound(int snr_min, int expectedStars)
    {
        // given
        const int channel = 0;
        const int Width = 1280;
        const int Height = 960;
        const int Pedestal = 1;

        var cancellationToken = TestContext.Current.CancellationToken;
        var expTime = TimeSpan.FromSeconds(42);
        var fileName = $"image_data_snr-{snr_min}_stars-{expectedStars}";
        var int16WxHData = await SharedTestData.ExtractGZippedImageData(fileName, Width, Height);
        var imageMeta = new ImageMeta(fileName, DateTime.UtcNow, expTime, FrameType.Light, "", 2.4f, 2.4f, 190, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

        // when
        var imageData = Float32HxWImageData.FromWxHImageData(int16WxHData);
        var denormalized = imageData.ToImage(BitDepth.Int16, Pedestal, imageMeta);
        var denormalizedStars = await denormalized.FindStarsAsync(channel, snrMin: snr_min, cancellationToken: cancellationToken);

        // SNR is now scale-invariant — same threshold works for normalized images
        var normalized = denormalized.ScaleFloatValuesToUnit();
        var normalizedStars = await normalized.FindStarsAsync(channel, snrMin: snr_min, cancellationToken: cancellationToken);

        // then
        denormalized.ShouldNotBeNull();
        denormalized.Height.ShouldBe(Height);
        denormalized.Width.ShouldBe(Width);
        denormalized.BitDepth.ShouldBe(BitDepth.Int16);
        denormalizedStars.ShouldNotBeNull().Count.ShouldBe(expectedStars);

        normalized.ShouldNotBeNull();
        normalized.Height.ShouldBe(Height);
        normalized.Width.ShouldBe(Width);
        normalized.BitDepth.ShouldBe(BitDepth.Float32);
        // Normalized path may find slightly more/fewer stars due to float rounding in Background(),
        // but should find at least as many as the denormalized path.
        normalizedStars.ShouldNotBeNull().Count.ShouldBeGreaterThanOrEqualTo(expectedStars);

        // Every star found in the denormalized path should also appear in the normalized path at the same position
        var denormalizedStarsOrdered = denormalizedStars.OrderBy(s => s.XCentroid).ThenBy(s => s.YCentroid).ToArray();
        foreach (var denormStar in denormalizedStarsOrdered)
        {
            normalizedStars.ShouldContain(s =>
                Math.Abs(s.XCentroid - denormStar.XCentroid) < 1.0f
                && Math.Abs(s.YCentroid - denormStar.YCentroid) < 1.0f,
                $"Star at ({denormStar.XCentroid:F1}, {denormStar.YCentroid:F1}) not found in normalized results");
        }
    }
}
