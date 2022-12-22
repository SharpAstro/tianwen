using Astap.Lib.Astrometry.Focus;
using Astap.Lib.Devices;
using Astap.Lib.Imaging;
using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class ImageAnalyserTests
{
    const string PlateSolveTestFile = nameof(PlateSolveTestFile);
    private readonly Image _plateSolveTestImage;

    private static Image? _plateSolveTestImageCache;

    public ImageAnalyserTests()
    {
        _plateSolveTestImage = (_plateSolveTestImageCache ??= SharedTestData.ExtractGZippedFitsImage(PlateSolveTestFile));
    }

    [Theory]
    [InlineData(10f)]
    [InlineData(15f)]
    public void GivenFileNameWhenWritingImageAndReadingBackThenItIsIdentical(float snrMin)
    {
        // given
        var fullPath = Path.Combine(Path.GetTempPath(), $"roundtrip_{Guid.NewGuid():D}.fits");
        IImageAnalyser imageAnalyser = new ImageAnalyser();
        var expectedStars = imageAnalyser.FindStars(_plateSolveTestImage, snrMin: snrMin);

        try
        {
            // when
            _plateSolveTestImage.WriteToFitsFile(fullPath);

            // then
            File.Exists(fullPath).ShouldBeTrue();
            Image.TryReadFitsFile(fullPath, out var readoutImage).ShouldBeTrue();
            readoutImage.Width.ShouldBe(_plateSolveTestImage.Width);
            readoutImage.Height.ShouldBe(_plateSolveTestImage.Height);
            readoutImage.BitsPerPixel.ShouldBe(_plateSolveTestImage.BitsPerPixel);
            readoutImage.Instrument.ShouldBe(_plateSolveTestImage.Instrument);
            readoutImage.MaxValue.ShouldBe(_plateSolveTestImage.MaxValue);
            readoutImage.ExposureStartTime.ShouldBe(_plateSolveTestImage.ExposureStartTime);
            readoutImage.ExposureDuration.ShouldBe(_plateSolveTestImage.ExposureDuration);
            var starsFromImage = imageAnalyser.FindStars(_plateSolveTestImage, snrMin: snrMin);

            starsFromImage.ShouldBeEquivalentTo(expectedStars);
        }
        finally
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
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
            SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords).ShouldBeTrue();

            (dim, _, _) = dimAndCoords;

            // when
            var actualSuccess = Image.TryReadFitsFile(extractedFitsFile, out var image);

            // then
            image.ShouldNotBeNull();
            image.Width.ShouldBe(dim.Width);
            image.Height.ShouldBe(dim.Height);
            actualSuccess.ShouldBeTrue();
        }
        finally
        {
            File.Delete(extractedFitsFile);
        }
    }

    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 10f, 89)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 20f, 28)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 30f, 13)]
    public async Task GivenImageFileAndMinSNRWhenFindingStarsThenTheyAreFound(string name, float snrMin, int expectedStars)
    {
        // given
        var extractedFitsFile = await SharedTestData.ExtractGZippedFitsFileAsync(name);
        IImageAnalyser imageAnalyser = new ImageAnalyser();
        try
        {

            // when
            Image.TryReadFitsFile(extractedFitsFile, out var image).ShouldBeTrue();
            var actualStars = imageAnalyser.FindStars(image, snrMin);

            // then
            actualStars.ShouldNotBeEmpty();
            actualStars.Count.ShouldBe(expectedStars);
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
        var fileName = $"image_data_snr-{snr_min}_stars-{expectedStars}";
        var imageData = await SharedTestData.ExtractGZippedImageData(fileName, Width, Height);

        // when
        var image = ICameraDriver.DataToImage(imageData, BitDepth, fileName, DateTime.UtcNow, TimeSpan.FromSeconds(42));
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
    [InlineData(SampleKind.HFD, 28208, 28211, 1, 1, 1, 10f, 20, 2, 130)]
    [InlineData(SampleKind.HFD, 28227, 28231, 1, 1, 1, 10f, 20, 2, 140)]
    public void GivenFocusSamplesWhenSolvingAHyperboleIsFound(SampleKind kind, int focusStart, int focusEndIncl, int focusStepSize, int sampleCount, int filterNo, float snrMin, int maxIterations, int expectedSolutionAfterSteps, int expectedMinStarCount)
    {
        // given
        var sampleMap = new MetricSampleMap(kind);
        IImageAnalyser imageAnalyser = new ImageAnalyser();

        // when
        for (int fp = focusStart; fp <= focusEndIncl; fp += focusStepSize)
        {
            for (int cs = 1; cs <= sampleCount; cs++)
            {
                var image = SharedTestData.ExtractGZippedFitsImage($"fp{fp}-cs{cs}-ms{sampleCount}-fw{filterNo}");

                var (median, solution, minPos, maxPos, count) = imageAnalyser.SampleStarsAtFocusPosition(image, fp, sampleMap, snrMin: snrMin, maxFocusIterations: maxIterations);

                median.ShouldNotBeNull().ShouldBeGreaterThan(1f);
                count.ShouldBeGreaterThan(expectedMinStarCount);

                if (fp - focusStart >= expectedSolutionAfterSteps)
                {
                    (_, _, _, double error, int iterations) = solution.ShouldNotBeNull();
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
