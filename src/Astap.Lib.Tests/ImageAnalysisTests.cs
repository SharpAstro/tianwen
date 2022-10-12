using Astap.Lib.Astrometry;
using Astap.Lib.Imaging;
using Shouldly;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class ImageAnalysisTests
{
    [Theory]
    [InlineData(5, 3, 11)]
    [InlineData(10, 3, 6)]
    [InlineData(20, 3, 2)]
    [InlineData(30, 3, 1)]
    public async Task GivenFitsFileWhenAnalysingThenMedianHFDAndFWHMIsCalculated(double snr_min, int max_retries, int expected_stars)
    {
        // given
        var fileAndDim = await SharedTestData.ExtractTestFitsFileAsync("PlateSolveTestFile");
        if (!fileAndDim.HasValue)
        {
            Assert.Fail("Could not extract test image data");
        }
        var (extractedFitsFile, imageDim, expectedRa, expectedDec) = fileAndDim.Value;
        var analyser = new ImageAnalyser();

        try
        {
            // when
            if (Image.TryReadFitsFile(extractedFitsFile, out var image))
            {

                var result = analyser.FindStars(image, snr_min: snr_min, max_retries: max_retries);
                // then
                result.ShouldNotBeEmpty();
                result.Count.ShouldBe(expected_stars);
                result.ShouldAllBe(p => p.SNR >= snr_min);
                result.ShouldContain(p => p.XCentroid > 1241 && p.XCentroid < 1242 && p.YCentroid > 220 && p.YCentroid < 221 && p.SNR > 39);
            }
            else
            {
                Assert.Fail("Could not read fits file");
            }
        }
        finally
        {
            File.Delete(extractedFitsFile);
        }
    }
}
