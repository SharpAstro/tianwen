using Shouldly;
using System;
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
        var stars = await image.FindStarsAsync(snrMin: snr_min, cancellationToken: cancellationToken);

        // then
        image.ShouldNotBeNull();
        image.Height.ShouldBe(Height);
        image.Width.ShouldBe(Width);
        image.BitDepth.ShouldBe(BitDepth);
        stars.ShouldNotBeNull().Count.ShouldBe(expectedStars);
    }
}
