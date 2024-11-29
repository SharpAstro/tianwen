using Shouldly;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace TianWen.Lib.Tests;

public class FindStarsFromFitsFileTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
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
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name);

        // when
        var sw = Stopwatch.StartNew();
        var actualStars = await image.FindStarsAsync(snrMin, maxStars ?? 500);
        _testOutputHelper.WriteLine("Testing image {0} took {1} ms", name, sw.ElapsedMilliseconds);

        // then
        actualStars.ShouldNotBeEmpty();
        actualStars.Count.ShouldBe(expectedStars);
    }
}
