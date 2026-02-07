using Shouldly;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Xunit;

namespace TianWen.Lib.Tests;

public class ImageTransformTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
{
    [Theory]
    [InlineData(PlateSolveTestFile, 45, +10, -45)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 45, +10, -45)]
    public async Task GivenFitsFileWhenFirstRotateThenTranslateItAppliesTransformationInCorrectOrder(string name, float rotationDegrees, float x_off, float y_off)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);
        var matrix = Matrix3x2.CreateRotation(float.DegreesToRadians(rotationDegrees)) * Matrix3x2.CreateTranslation(x_off, y_off);
        var starsBefore = await image.FindStarsAsync(snrMin: 20, cancellationToken: cancellationToken);

        // when
        var transformed = image.Transform(matrix);

        var outFile = Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{Path.GetFileNameWithoutExtension(name)}_transformed_r{rotationDegrees}_x{x_off}_y{y_off}.fits");
        transformed.WriteToFitsFile(outFile);

        // then
        var starsAfter = await transformed.FindStarsAsync(snrMin: 20, cancellationToken: cancellationToken);
        starsAfter.Count.ShouldBeGreaterThanOrEqualTo(starsBefore.Count);
        transformed.ShouldNotBeNull();

        _testOutputHelper.WriteLine($"Transformed image written to: {outFile}");
    }
}
