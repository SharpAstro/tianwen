using Shouldly;
using System.Diagnostics;
using System.Threading.Tasks;
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
}
