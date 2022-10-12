using Astap.Lib.Astrometry.Focus;
using Astap.Lib.Imaging;
using Shouldly;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class ImageAnalysisTests
{
    const string PlateSolveTestFile = nameof(PlateSolveTestFile);
    private readonly Image _plateSolveTestImage;

    public ImageAnalysisTests()
    {
        if (SharedTestData.ExtractTestFitsImage(PlateSolveTestFile) is Image image)
        {
            _plateSolveTestImage = image;
        }
        else
        {
            Assert.Fail("Could not load " + PlateSolveTestFile + " into memory");
        }
    }

    [Theory]
    [InlineData(PlateSolveTestFile)]
    public async Task GivenOnDiskFitsFileWithImageWhenTryingReadImageItSucceeds(string name)
    {
        // given
        var extractedFitsFile = await SharedTestData.ExtractTestFitsFileAsync(name);
        if (extractedFitsFile is null)
        {
            Assert.Fail($"Could not extract test image data of {name}");
            return;
        }

        try
        {
            ImageDim dim;
            if (SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords))
            {
                (dim, _, _) = dimAndCoords;

                // when
                var actualSuccess = Image.TryReadFitsFile(extractedFitsFile, out var image);

                // then
                image.ShouldNotBeNull();
                image.Width.ShouldBe(dim.Width);
                image.Height.ShouldBe(dim.Height);
                actualSuccess.ShouldBeTrue();
            }
            else
            {
                Assert.Fail($"Could not extract test image dimensions for {name}");
            }
        }
        finally
        {
            File.Delete(extractedFitsFile);
        }
    }


    [Theory]
    [InlineData(5, 3, 11)]
    [InlineData(10, 3, 6)]
    [InlineData(20, 3, 2)]
    [InlineData(30, 3, 1)]
    public void GivenFitsFileWhenAnalysingThenMedianHFDAndFWHMIsCalculated(double snr_min, int max_retries, int expected_stars)
    {
        var analyser = new ImageAnalyser();

        // when
        var result = analyser.FindStars(_plateSolveTestImage, snr_min: snr_min, max_retries: max_retries);

        // then
        result.ShouldNotBeEmpty();
        result.Count.ShouldBe(expected_stars);
        result.ShouldAllBe(p => p.SNR >= snr_min);
        result.ShouldContain(p => p.XCentroid > 1241 && p.XCentroid < 1242 && p.YCentroid > 220 && p.YCentroid < 221 && p.SNR > 39);
    }
}
