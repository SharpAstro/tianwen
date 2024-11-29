using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace TianWen.Lib.Tests;

public class PlateSolverRoundTripTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
{
    [Theory]
    [InlineData(PlateSolveTestFile, 10f)]
    [InlineData(PlateSolveTestFile, 15f)]
    public async Task GivenFileNameWhenWritingImageAndReadingBackThenItIsIdentical(string name, float snrMin)
    {
        // given
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name);
        var fullPath = Path.Combine(Path.GetTempPath(), $"roundtrip_{Guid.NewGuid():D}.fits");
        var expectedStars = await image.FindStarsAsync(snrMin: snrMin);

        try
        {
            // when
            image.WriteToFitsFile(fullPath);

            // then
            File.Exists(fullPath).ShouldBeTrue();
            Image.TryReadFitsFile(fullPath, out var readoutImage).ShouldBeTrue();
            readoutImage.Width.ShouldBe(image.Width);
            readoutImage.Height.ShouldBe(image.Height);
            readoutImage.BitDepth.ShouldBe(image.BitDepth);
            readoutImage.ImageMeta.Instrument.ShouldBe(image.ImageMeta.Instrument);
            readoutImage.MaxValue.ShouldBe(image.MaxValue);
            readoutImage.ImageMeta.ExposureStartTime.ShouldBe(image.ImageMeta.ExposureStartTime);
            readoutImage.ImageMeta.ExposureDuration.ShouldBe(image.ImageMeta.ExposureDuration);
            var starsFromImage = await image.FindStarsAsync(snrMin: snrMin);

            starsFromImage.ShouldBe(expectedStars, ignoreOrder: true);
        }
        finally
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
    }
}
