using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Stat;
using Xunit;
using Xunit.Abstractions;

namespace TianWen.Lib.Tests;

public class FindVCurveFromFitsFileTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
{
    record NinaTestResult(
        int Version,
        NinaSamplingMethod Method,
        NinaFittingMethod Fitting,
        FocusPoint CalculatedFocusPoint,
        FocusPoint PreviousFocusPoint,
        IReadOnlyList<FocusPoint> MeasurePoints,
        IReadOnlyDictionary<string, string> Fittings
    );

    enum NinaSamplingMethod
    {
        Unknown,
        StarHFR
    }

    enum NinaFittingMethod
    {
        Unknown,
        Hyperbolic
    }
    record FocusPoint(double Position, double Value, double Error);

    [Theory]
    [InlineData("2025-12-17--23-06-56--9d0e769c-5847-470c-a25e-0e1de367e31e.json")]
    [InlineData("2025-12-17--23-27-02--9d0e769c-5847-470c-a25e-0e1de367e31e.json")]
    [InlineData("2025-12-18--01-36-05--9d0e769c-5847-470c-a25e-0e1de367e31e.json")]
    [InlineData("2025-12-18--02-26-57--9d0e769c-5847-470c-a25e-0e1de367e31e.json")]
    public async Task ReachSameResultFromSuccessfulNINARun(string file)
    {
        var ninaResult = await JsonSerializer.DeserializeAsync<NinaTestResult>(SharedTestData.OpenEmbeddedFileStream(file).ShouldNotBeNull(), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        ninaResult.ShouldNotBeNull();
        ninaResult.Fitting.ShouldBe(NinaFittingMethod.Hyperbolic);
        ninaResult.CalculatedFocusPoint.ShouldNotBeNull();
        ninaResult.MeasurePoints.ShouldNotBeEmpty();

        var ninaFitting = ninaResult.Fittings[ninaResult.Fitting.ToString()];

        var sampleKind = ninaResult.Method switch
        {
            NinaSamplingMethod.StarHFR => SampleKind.HFD,
            var x => throw new InvalidOperationException($"Sampling method {x} is not supported")
        };

        var sampleMap = new MetricSampleMap(sampleKind, AggregationMethod.Average);

        foreach (var point in ninaResult.MeasurePoints)
        {
            sampleMap.AddSampleAtFocusPosition((int)Math.Round(point.Position), (float)point.Value);
        }
        sampleMap.TryGetBestFocusSolution(out var solution, out var minPos, out var maxPos).ShouldBeTrue();
    
        ninaResult.CalculatedFocusPoint.Position.ShouldBeInRange(minPos, maxPos);
        solution.ShouldNotBeNull().P.ShouldBeInRange(minPos, maxPos);
    }

    [Theory]
    [InlineData(SampleKind.HFD, AggregationMethod.Average, 28208, 28211, 1, 1, 1, 10f, 20, 2, 130)]
    [InlineData(SampleKind.HFD, AggregationMethod.Average, 28227, 28231, 1, 1, 1, 10f, 20, 2, 140)]
    [InlineData(SampleKind.HFD, AggregationMethod.Average, 28208, 28231, 1, 1, 1, 10f, 20, 2, 130)]
    public async Task HyperboleIsFoundFromActualImageRun(SampleKind kind, AggregationMethod aggregationMethod, int focusStart, int focusEndIncl, int focusStepSize, int sampleCount, int filterNo, float snrMin, int maxIterations, int expectedSolutionAfterSteps, int expectedMinStarCount)
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
