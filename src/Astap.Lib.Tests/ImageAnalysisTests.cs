using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using Xunit;

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
            var result = ImageAnalysis.AnalyseFITS(extractedFitsFile, snr: 1);

            // then
        }
        finally
        {
            File.Delete(extractedFitsFile);
        }
    }
}
