using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class ImageTransformTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
{
    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 45, +10, -45, 20, "0.204", -2)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 0, 0, 0, 20, "1e-3", -2)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 0, -12, 13, 15, "1e-3", -2)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 0.75, 0, 0, 10, "0.026", -2)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 0.75, +5, -7, 10, "0.026", -2)]
    public async Task GivenFitsFileWhenTransformingThenTheyCanStillBeAligned(string name, float rotationDegrees, float x_off, float y_off, int snrMin, string quadToleranceStr, int solutionToleranceE10)
    {
        // given
        const int channel = 0;
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);
        var matrix = Matrix3x2.CreateRotation(float.DegreesToRadians(rotationDegrees)) * Matrix3x2.CreateTranslation(x_off, y_off);
        var starsBefore = await image.FindStarsAsync(channel, snrMin: snrMin, cancellationToken: cancellationToken);
        var outFolder = SharedTestData.CreateTempTestOutputDir(nameof(GivenFitsFileWhenTransformingThenTheyCanStillBeAligned));
        var varFileNamePart = $"r{rotationDegrees}_x{x_off}_y{y_off}_snr{snrMin}_qt{quadToleranceStr}";

        // when
        var transformed = image.Transform(matrix);

        var transformedFile = Path.Combine(outFolder, $"{Path.GetFileNameWithoutExtension(name)}_transformed_{varFileNamePart}.fits");
        transformed.WriteToFitsFile(transformedFile);

        _testOutputHelper.WriteLine($"Transformed image written to: {transformedFile}");

        // then
        var starsAfter = await transformed.FindStarsAsync(channel, snrMin: snrMin, cancellationToken: cancellationToken);
        starsAfter.Count.ShouldBeGreaterThanOrEqualTo(starsBefore.Count);
        transformed.ShouldNotBeNull();

        var sw = Stopwatch.StartNew();
        var solutionTolerance = MathF.Pow(10, solutionToleranceE10);
        var solutionSpecQuad = await new SortedStarList(starsBefore).FindOffsetAndRotationAsync(starsAfter, quadTolerance: float.Parse(quadToleranceStr), solutionTolerance: solutionTolerance);
        var ms = sw.ElapsedMilliseconds;

        solutionSpecQuad.ShouldNotBeNull();
        _testOutputHelper.WriteLine($"Found solution with given tolerance ({quadToleranceStr}) {solutionSpecQuad} in {ms} ms");
        solutionSpecQuad.Value.Decompose().Rotation.ShouldBe(float.DegreesToRadians(rotationDegrees), tolerance: solutionTolerance);

        sw.Restart();
        var (solutionWithRetry, retriedQuadTolerance) = await new SortedStarList(starsBefore).FindOffsetAndRotationWithRetryAsync(new SortedStarList(starsAfter), solutionTolerance: solutionTolerance);
        ms = sw.ElapsedMilliseconds;

        solutionWithRetry.ShouldNotBeNull();
        _testOutputHelper.WriteLine($"Found solution with retry ({retriedQuadTolerance}) {solutionWithRetry} in {ms} ms");
        solutionWithRetry.Value.Decompose().Rotation.ShouldBe(float.DegreesToRadians(rotationDegrees), tolerance: solutionTolerance);

        var realigned = image.Transform(solutionSpecQuad.Value);
        var realignedFile = Path.Combine(outFolder, $"{Path.GetFileNameWithoutExtension(name)}_realigned_{varFileNamePart}.fits");
        realigned.WriteToFitsFile(realignedFile);

        _testOutputHelper.WriteLine($"Realigned image written to: {realignedFile}");
    }
}
