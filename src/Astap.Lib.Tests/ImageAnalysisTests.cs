using Astap.Lib.Astrometry.Focus;
using Astap.Lib.Devices;
using Astap.Lib.Imaging;
using Moq;
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
        _plateSolveTestImage = SharedTestData.ExtractGZippedFitsImage(PlateSolveTestFile);
    }

    [Theory]
    [InlineData(PlateSolveTestFile)]
    public async Task GivenOnDiskFitsFileWithImageWhenTryingReadImageItSucceeds(string name)
    {
        // given
        var extractedFitsFile = await SharedTestData.ExtractGZippedFitsFileAsync(name);

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
    [InlineData(10, 22)]
    [InlineData(15, 6)]
    [InlineData(20, 3)]
    public async Task GivenCameraImageDataWhenConvertingToImageThenStarsCanBeFound(int snr_min, int expectedStars)
    {
        // given
        const int Width = 1280;
        const int Height = 960;
        const int BitDepth = 16;
        var imageData = await SharedTestData.ExtractGZippedImageData($"image_data_snr-{snr_min}_stars-{expectedStars}", Width, Height);

        // when
        var image = ICameraDriver.DataToImage(imageData, BitDepth);
        var stars = image?.FindStars(snr_min: snr_min);

        // then
        image.ShouldNotBeNull();
        image.Height.ShouldBe(Height);
        image.Width.ShouldBe(Width);
        image.BitsPerPixel.ShouldBe(BitDepth);
        stars.ShouldNotBeNull().Count.ShouldBe(expectedStars);
    }


    [Theory]
    [InlineData(5, 3, 11)]
    [InlineData(9.5, 3, 6)]
    [InlineData(20, 3, 2)]
    [InlineData(30, 3, 1)]
    public void GivenFitsFileWhenAnalysingThenMedianHFDAndFWHMIsCalculated(float snr_min, int max_retries, int expected_stars)
    {
        var analyser = new ImageAnalyser();

        // when
        var result = analyser.FindStars(_plateSolveTestImage, snrMin: snr_min, maxIterations: max_retries);

        // then
        result.ShouldNotBeEmpty();
        result.Count.ShouldBe(expected_stars);
        result.ShouldAllBe(p => p.SNR >= snr_min);
        result.ShouldContain(p => p.XCentroid > 1241 && p.XCentroid < 1243 && p.YCentroid > 219 && p.YCentroid < 221 && p.SNR > 38);
    }

    [Theory]
    [InlineData(SampleKind.HFD, 28208, 28211, 1, 1, 10f, 20, 2)]
    [InlineData(SampleKind.HFD, 28228, 28232, 1, 1, 10f, 20, 2)]
    public void GivenFocusSamplesWhenSolvingAHyperboleIsFound(SampleKind kind, int focusStart, int focusEndIncl, int sampleCount, int filterNo, float snrMin, int maxIterations, int expectedSolutionAfterSteps)
    {
        // given
        var sampleMap = new MetricSampleMap(kind);
        IImageAnalyser imageAnalyser = new ImageAnalyser();

        // when
        for (int fp = focusStart; fp <= focusEndIncl; fp++)
        {
            for (int cs = 1; cs <= sampleCount; cs++)
            {
                var image = SharedTestData.ExtractGZippedFitsImage($"fp{fp}-cs{cs}-ms{sampleCount}-fw{filterNo}");

                var (median, solution, minPos, maxPos) = imageAnalyser.SampleStarsAtFocusPosition(image, fp, sampleMap, snrMin: snrMin, maxFocusIterations: maxIterations);

                median.ShouldNotBeNull().ShouldBeGreaterThan(1f);

                if (fp - focusStart >= expectedSolutionAfterSteps)
                {
                    (double p, _, _, double error, int iterations) = solution.ShouldNotBeNull();
                    var minPosD = (double)minPos.ShouldNotBeNull();
                    var maxPosD = (double)maxPos.ShouldNotBeNull();

                    maxPosD.ShouldBeGreaterThan(minPosD);
                    minPosD.ShouldBe(focusStart);
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
