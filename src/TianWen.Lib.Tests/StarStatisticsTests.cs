using Shouldly;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace TianWen.Lib.Tests;

public class StarStatisticsTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
{
    [Theory]
    [InlineData(PlateSolveTestFile, 5, 3, 11, 1242, 220, 38)]
    [InlineData(PlateSolveTestFile, 9.5, 3, 6, 1242, 220, 38)]
    [InlineData(PlateSolveTestFile, 20, 3, 2, 1242, 220, 38)]
    [InlineData(PlateSolveTestFile, 30, 3, 1, 1242, 220, 38)]
    [InlineData(PHD2SimGuider, 2, 3, 10)]
    [InlineData(PHD2SimGuider, 5, 3, 10)]
    [InlineData(PHD2SimGuider, 5, 10, 10)]
    [InlineData(PHD2SimGuider, 20, 3, 6)]
    [InlineData(PHD2SimGuider, 30, 3, 2)]
    [InlineData(PHD2SimGuider, 30, 10, 2)]
    public async Task GivenFitsFileWhenAnalysingThenMedianHFDAndFWHMIsCalculated(string name, float snrMin, int maxRetries, int expectedStars, params int[] sampleStar)
    {
        // when
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name);
        var result = await image.FindStarsAsync(snrMin: snrMin, maxRetries: maxRetries);

        // then
        result.ShouldNotBeEmpty();
        result.Count.ShouldBe(expectedStars);
        result.ShouldAllBe(p => p.SNR >= snrMin);

        if (sampleStar is { Length: 3 })
        {
            var x = sampleStar[0];
            var y = sampleStar[1];
            var snr = sampleStar[2];
            result.ShouldContain(p => p.XCentroid > x - 1 && p.XCentroid < x + 1 && p.YCentroid > y - 1 && p.YCentroid < y + 1 && p.SNR > snr);
        }
        else if (sampleStar is { Length: > 0 })
        {
            Assert.Fail($"Sample star needs to be exactly 3 elements (x, y, snr), but only {sampleStar.Length} where given.");
        }
    }
}
