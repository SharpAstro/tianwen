using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;
using static TianWen.Lib.Tests.SharedTestData;

namespace TianWen.Lib.Tests;

public class PlateSolverRoundTripTests
{
    [Theory]
    [InlineData(PlateSolveTestFile, 10f)]
    [InlineData(PlateSolveTestFile, 15f)]
    public async Task GivenFileNameWhenWritingImageAndReadingBackThenItIsIdentical(string name, float snrMin)
    {
        // given
        const int channel = 0;
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);
        var fullPath = Path.Combine(Path.GetTempPath(), $"roundtrip_{Guid.NewGuid():D}.fits");
        var expectedStars = await image.FindStarsAsync(channel, snrMin: snrMin, cancellationToken: cancellationToken);

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
            var starsFromImage = await image.FindStarsAsync(channel, snrMin: snrMin, cancellationToken: cancellationToken);

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
