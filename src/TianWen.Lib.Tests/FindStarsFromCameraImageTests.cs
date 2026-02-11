using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class FindStarsFromCameraImageTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
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
        const int BlackLevel = 1;

        var cancellationToken = TestContext.Current.CancellationToken;
        var expTime = TimeSpan.FromSeconds(42);
        var fileName = $"image_data_snr-{snr_min}_stars-{expectedStars}";
        var int16WxHData = await SharedTestData.ExtractGZippedImageData(fileName, Width, Height);
        var imageMeta = new ImageMeta(fileName, DateTime.UtcNow, expTime, FrameType.Light, "", 2.4f, 2.4f, 190, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

        // when
        var imageData = Float32HxWImageData.FromWxHImageData(int16WxHData);
        var image = imageData.ToImage(BitDepth, BlackLevel, imageMeta);
        var stars = await image.FindStarsAsync(channel, snrMin: snr_min, cancellationToken: cancellationToken);

        // then
        image.ShouldNotBeNull();
        image.Height.ShouldBe(Height);
        image.Width.ShouldBe(Width);
        image.BitDepth.ShouldBe(BitDepth);
        stars.ShouldNotBeNull().Count.ShouldBe(expectedStars);
    }

    [Theory]
    [InlineData(10, 22, "2")]
    [InlineData(15, 6, "3.0")]
    [InlineData(20, 3, "3.9")]
    public async Task GivenCameraImageDataWhenConvertingToImageAndNormalizingThenStarsCanBeFound(int denorm_snr_min, int expectedStars, string norm_snr_min_str)
    {
        // given
        const int channel = 0;
        const int Width = 1280;
        const int Height = 960;
        const int BlackLevel = 1;

        var cancellationToken = TestContext.Current.CancellationToken;
        var expTime = TimeSpan.FromSeconds(42);
        var fileName = $"image_data_snr-{denorm_snr_min}_stars-{expectedStars}";
        var int16WxHData = await SharedTestData.ExtractGZippedImageData(fileName, Width, Height);
        var imageMeta = new ImageMeta(fileName, DateTime.UtcNow, expTime, FrameType.Light, "", 2.4f, 2.4f, 190, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

        // when
        var imageData = Float32HxWImageData.FromWxHImageData(int16WxHData);
        var denormalized = imageData.ToImage(BitDepth.Int16, BlackLevel, imageMeta);
        var denormalizedStars = await denormalized.FindStarsAsync(channel, snrMin: denorm_snr_min, cancellationToken: cancellationToken);

        var normalized = denormalized.Normalize();
        var normalizedStars = await normalized.FindStarsAsync(channel, snrMin: float.Parse(norm_snr_min_str), cancellationToken: cancellationToken);

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
        normalizedStars.ShouldNotBeNull().Count.ShouldBe(expectedStars);

        // check if star is at same position it is of same size
        var denormalizedStarsOrdered = denormalizedStars.OrderBy(s => s.XCentroid).ThenBy(s => s.YCentroid).ToArray();
        var normalizedStarsOrdered = normalizedStars.OrderBy(s => s.XCentroid).ThenBy(s => s.YCentroid).ToArray();
        for (var i = 0; i < expectedStars; i++)
        {
            var denormStar = denormalizedStarsOrdered[i];
            var normStar = normalizedStarsOrdered[i];
            normStar.XCentroid.ShouldBe(denormStar.XCentroid, 0.2f);
            normStar.YCentroid.ShouldBe(denormStar.YCentroid, 0.2f);
            normStar.HFD.ShouldBe(denormStar.HFD, 0.001f);
        }
    }
}
