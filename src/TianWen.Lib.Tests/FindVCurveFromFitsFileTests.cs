using Shouldly;
using System.Diagnostics;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Stat;
using Xunit;
using Xunit.Abstractions;

namespace TianWen.Lib.Tests;

public class FindVCurveFromFitsFileTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
{
    [Theory]
    [InlineData(SampleKind.HFD, AggregationMethod.Average, 28208, 28211, 1, 1, 1, 10f, 20, 2, 130)]
    [InlineData(SampleKind.HFD, AggregationMethod.Average, 28227, 28231, 1, 1, 1, 10f, 20, 2, 140)]
    [InlineData(SampleKind.HFD, AggregationMethod.Average, 28208, 28231, 1, 1, 1, 10f, 20, 2, 130)]
    public async Task GivenFocusSamplesWhenSolvingAHyperboleIsFound(SampleKind kind, AggregationMethod aggregationMethod, int focusStart, int focusEndIncl, int focusStepSize, int sampleCount, int filterNo, float snrMin, int maxIterations, int expectedSolutionAfterSteps, int expectedMinStarCount)
    {
        // given
        var sampleMap = new MetricSampleMap(kind, aggregationMethod);

        // when
        for (int fp = focusStart; fp <= focusEndIncl; fp += focusStepSize)
        {
            for (int cs = 1; cs <= sampleCount; cs++)
            {
                var sw = Stopwatch.StartNew();
                var image = await SharedTestData.ExtractGZippedFitsImageAsync($"fp{fp}-cs{cs}-ms{sampleCount}-fw{filterNo}");
                var extractImageElapsed = sw.ElapsedMilliseconds;
                var stars = await image.FindStarsAsync(snrMin: snrMin);
                var findStarsElapsed = sw.ElapsedMilliseconds - extractImageElapsed;
                var median = stars.MapReduceStarProperty(sampleMap.Kind, AggregationMethod.Median);
                var calcMedianElapsed = sw.ElapsedMilliseconds - findStarsElapsed;
                var (solution, maybeMinPos, maybeMaxPos) = sampleMap.AddSampleAtFocusPosition(fp, median, maxFocusIterations: maxIterations);
                var addSampleElapsed = sw.ElapsedMilliseconds - calcMedianElapsed;

                _testOutputHelper.WriteLine($"focuspos={fp} stars={stars.Count} median={median} solution={solution} minPos={maybeMinPos} maxPos={maybeMaxPos} time (ms): image={extractImageElapsed} find stars={findStarsElapsed} median={calcMedianElapsed} sample={addSampleElapsed}");

                median.ShouldBeGreaterThan(1f);
                stars.Count.ShouldBeGreaterThan(expectedMinStarCount);

                if (fp - focusStart >= expectedSolutionAfterSteps)
                {
                    (_, _, _, double error, int iterations) = solution.ShouldNotBeNull();
                    var minPos = maybeMinPos.ShouldNotBeNull();
                    var maxPos = maybeMaxPos.ShouldNotBeNull();

                    maxPos.ShouldBeGreaterThan(minPos);
                    minPos.ShouldBe(focusStart);
                    iterations.ShouldBeLessThanOrEqualTo(maxIterations);
                    error.ShouldBeLessThan(1);
                }
                else
                {
                    solution.ShouldBeNull();
                }
            }
        }
    }
}
