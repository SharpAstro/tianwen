using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Shouldly;
using Astap.Lib.Imaging;

namespace Astap.Lib.Tests;

public class ImageAnalysisTests
{
    [Fact]
    public async Task GivenFitsFileWhenAnalysingThenMedianHFDAndFWHMIsCalculated()
    {
        // given
        var fileAndDim = await SharedTestData.ExtractTestFitsFileAsync("PlateSolveTestFile");
        if (!fileAndDim.HasValue)
        {
            Assert.Fail("Could not extract test image data");
        }
        var (extractedFitsFile, imageDim, expectedRa, expectedDec) = fileAndDim.Value;

        try
        {
            // when
            if (ImageAnalysis.TryReadFitsFile(extractedFitsFile, out var image))
            {

                var result = ImageAnalysis.FindStars(image, snr_min: 5, max_retries: 3);

                // then
                result.ShouldNotBeEmpty();
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
