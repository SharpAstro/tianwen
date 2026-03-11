using Shouldly;
using System.Diagnostics;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

public class FindStarsFromFitsFileTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 10f, 89)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 20f, 28)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 30f, 13)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 30f, 2786, 5000)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 10f, 3046, 5000)]
    public async Task GivenImageFileAndMinSNRWhenFindingStarsThenTheyAreFound(string name, float snrMin, int expectedStars, int? maxStars = null)
    {
        // given
        const int channel = 0;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: TestContext.Current.CancellationToken);

        // when
        var sw = Stopwatch.StartNew();
        var actualStars = await image.FindStarsAsync(channel, snrMin, maxStars ?? 500, cancellationToken: TestContext.Current.CancellationToken);
        testOutputHelper.WriteLine("Testing image {0} took {1} ms", name, sw.ElapsedMilliseconds);

        // then
        actualStars.ShouldNotBeEmpty();
        actualStars.Count.ShouldBe(expectedStars);
    }

    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", "None", 28)]
    [InlineData("RGGB_frame_bx0_by0_top_down", "AHD", 100)]
    public async Task GivenFitsDocumentWhenDetectingStarsThenStarsAreFound(string name, string algorithmStr, int minExpectedStars)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var algorithm = System.Enum.Parse<DebayerAlgorithm>(algorithmStr);

        // given — load via FitsDocument (same path as the viewer)
        var filePath = await SharedTestData.ExtractGZippedFitsFileAsync(name, cancellationToken);
        var document = await FitsDocument.OpenAsync(filePath, algorithm, cancellationToken);
        document.ShouldNotBeNull();

        // when
        var sw = Stopwatch.StartNew();
        await document.DetectStarsAsync(cancellationToken);
        testOutputHelper.WriteLine("DetectStarsAsync on {0} took {1:F0} ms, found {2} stars (HFR={3:F2}, FWHM={4:F2})",
            name, sw.Elapsed.TotalMilliseconds, document.Stars?.Count, document.AverageHFR, document.AverageFWHM);

        // then
        document.Stars.ShouldNotBeNull();
        document.Stars.Count.ShouldBeGreaterThanOrEqualTo(minExpectedStars);
        document.AverageHFR.ShouldBeGreaterThan(0f);
        document.AverageFWHM.ShouldBeGreaterThan(0f);
    }
}
