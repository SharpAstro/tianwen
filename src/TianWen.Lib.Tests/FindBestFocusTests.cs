using ImageMagick;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

public class FindBestFocusTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
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

    private static readonly JsonSerializerOptions _stringEnums = new JsonSerializerOptions
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData("2025-12-17--23-06-56--9d0e769c-5847-470c-a25e-0e1de367e31e.json")]
    [InlineData("2025-12-17--23-27-02--9d0e769c-5847-470c-a25e-0e1de367e31e.json")]
    [InlineData("2025-12-18--01-36-05--9d0e769c-5847-470c-a25e-0e1de367e31e.json")]
    [InlineData("2025-12-18--02-26-57--9d0e769c-5847-470c-a25e-0e1de367e31e.json")]
    public async Task ReachSameResultFromSuccessfulNINARun(string file)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var ninaResult = await JsonSerializer.DeserializeAsync<NinaTestResult>(SharedTestData.OpenEmbeddedFileStream(file).ShouldNotBeNull(), _stringEnums, cancellationToken);
        ninaResult.ShouldNotBeNull();
        ninaResult.Fitting.ShouldBe(NinaFittingMethod.Hyperbolic);
        ninaResult.CalculatedFocusPoint.ShouldNotBeNull();
        ninaResult.MeasurePoints.ShouldNotBeEmpty();

        var ninaFitting = ninaResult.Fittings[ninaResult.Fitting.ToString()];
        _testOutputHelper.WriteLine("NINA fitting formula: " + ninaFitting);

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

        solution.ShouldNotBeNull().BestFocus.ShouldBeInRange(minPos, maxPos);

        var fileName = Path.ChangeExtension(file, ".png");
        await DrawSolution(sampleMap, solution.Value, minPos, maxPos, fileName);
    }

    [Theory]
    [InlineData(SampleKind.HFD, AggregationMethod.Average, 28227, 28231, 1, 1, 1, 10f, 20, 2, 140)]
    public async Task HyperboleIsFoundFromActualImageRun(SampleKind kind, AggregationMethod aggregationMethod, int focusStart, int focusEndIncl, int focusStepSize, int sampleCount, int filterNo, float snrMin, int maxIterations, int expectedSolutionAfterSteps, int expectedMinStarCount)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var sampleMap = new MetricSampleMap(kind, aggregationMethod);

        // when
        for (int fp = focusStart; fp <= focusEndIncl; fp += focusStepSize)
        {
            for (int cs = 1; cs <= sampleCount; cs++)
            {
                var sampleName = $"fp{fp}-cs{cs}-ms{sampleCount}-fw{filterNo}";
                var sw = Stopwatch.StartNew();
                var image = await SharedTestData.ExtractGZippedFitsImageAsync(sampleName, cancellationToken: cancellationToken);
                var extractImageElapsed = sw.ElapsedMilliseconds;
                var stars = await image.FindStarsAsync(snrMin: snrMin, cancellationToken: cancellationToken);
                var findStarsElapsed = sw.ElapsedMilliseconds - extractImageElapsed;
                var median = stars.MapReduceStarProperty(sampleMap.Kind, AggregationMethod.Median);
                var calcMedianElapsed = sw.ElapsedMilliseconds - findStarsElapsed;
                sampleMap.AddSampleAtFocusPosition(fp, median).ShouldBeTrue();
                var addSampleElapsed = sw.ElapsedMilliseconds - calcMedianElapsed;

                _testOutputHelper.WriteLine($"focuspos={fp} stars={stars.Count} median={median} time (ms): image={extractImageElapsed} find stars={findStarsElapsed} median={calcMedianElapsed} sample={addSampleElapsed}");

                median.ShouldBeGreaterThan(1f);
                stars.Count.ShouldBeGreaterThan(expectedMinStarCount);

                var hasSolution = sampleMap.TryGetBestFocusSolution(out var solution, out var minPos, out var maxPos, maxIterations);
                if (fp - focusStart >= expectedSolutionAfterSteps)
                {
                    hasSolution.ShouldBeTrue();
                    solution.HasValue.ShouldBeTrue();

                    maxPos.ShouldBeGreaterThan(minPos);
                    minPos.ShouldBe(focusStart);
                    solution.Value.Iterations.ShouldBeLessThanOrEqualTo(maxIterations);
                    solution.Value.Error.ShouldBeInRange(0, 1);

                    await DrawSolution(sampleMap, solution.Value, minPos, maxPos, sampleName + ".png");
                }
                else
                {
                    hasSolution.ShouldBeFalse();
                    solution.ShouldBeNull();
                }
            }
        }
    }

    private async Task DrawSolution(MetricSampleMap sampleMap, FocusSolution solution, int minPos, int maxPos, string fileName)
    {
        using var image = new MagickImage(MagickColors.Transparent, 1000, 1000)
        {
            Format = MagickFormat.Png
        };
        var xMargin = (int)Math.Ceiling(image.Width * 0.1);
        var yMargin = (int)Math.Ceiling(image.Height * 0.1);

        sampleMap.Draw(solution, minPos, maxPos, image, xMargin, yMargin);

        var outputDir = SharedTestData.CreateTempTestOutputDir();
        var fullPath = Path.Combine(outputDir, fileName);

        await image.WriteAsync(fullPath);

        _testOutputHelper.WriteLine("Result: " + solution);
        _testOutputHelper.WriteLine("Wrote hyperbola plot to: " + fileName + " in");
        _testOutputHelper.WriteLine(outputDir);
    }
}
