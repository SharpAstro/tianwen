using Shouldly;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class FindStarsFromFitsFileTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void StarMasksCoverFullHfdRange()
    {
        // The maximum HFD accepted by FindStarsAsync is BoxRadius * 2.
        // The scaled radius used for masking is Round(HfdFactor * HFD).
        // StarMasks must have an entry for every possible radius index.
        var maxHfd = Image.BoxRadius * 2;
        var maxScaledRadius = (int)MathF.Round(Image.HfdFactor * maxHfd);
        Image.StarMasks.Length.ShouldBeGreaterThanOrEqualTo(maxScaledRadius);
    }

    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 10f, 89)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 20f, 28)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 30f, 13)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 30f, 2786, 5000)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 10f, 3046, 5000)]
    public async Task GivenImageFileAndMinSNRWhenFindingStarsThenTheyAreFound(string name, float snrMin, int expectedStars, int? maxStars = null)
    {
        // given
        const int channel = 0;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: TestContext.Current.CancellationToken);

        // when
        var sw = Stopwatch.StartNew();
        var actualStars = await image.FindStarsAsync(channel, snrMin, maxStars ?? 500, cancellationToken: TestContext.Current.CancellationToken);
        testOutputHelper.WriteLine("Testing image {0} took {1} ms", name, sw.ElapsedMilliseconds);

        // then
        actualStars.ShouldNotBeEmpty();
        actualStars.Count.ShouldBe(expectedStars);
    }

    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", "None", 28)]
    [InlineData("RGGB_frame_bx0_by0_top_down", "AHD", 100)]
    public async Task GivenAstroImageDocumentWhenDetectingStarsThenStarsAreFound(string name, string algorithmStr, int minExpectedStars)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var algorithm = System.Enum.Parse<DebayerAlgorithm>(algorithmStr);

        // given — load via AstroImageDocument (same path as the viewer)
        var filePath = await SharedTestData.ExtractGZippedFitsFileAsync(name, cancellationToken);
        var document = await AstroImageDocument.OpenAsync(filePath, algorithm, cancellationToken);
        document.ShouldNotBeNull();

        // when
        var sw = Stopwatch.StartNew();
        await document.DetectStarsAsync(cancellationToken);
        testOutputHelper.WriteLine("DetectStarsAsync on {0} took {1:F0} ms, found {2} stars (HFR={3:F2}, FWHM={4:F2})",
            name, sw.Elapsed.TotalMilliseconds, document.Stars?.Count ?? -1, document.AverageHFR, document.AverageFWHM);

        // then
        document.Stars.ShouldNotBeNull();
        document.Stars.Count.ShouldBeGreaterThanOrEqualTo(minExpectedStars);
        document.AverageHFR.ShouldBeGreaterThan(0f);
        document.AverageFWHM.ShouldBeGreaterThan(0f);
    }

    [Theory]
    [InlineData("RGGB_frame_bx0_by0_top_down")]
    [InlineData("image_file-snr-20_stars-28_1280x960x16")]
    public async Task GivenImageWithStarsWhenScanningBackgroundWithMaskThenStarPixelsAreExcluded(string name)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);
        var scaledImage = image.ScaleFloatValuesToUnit();

        // Detect stars — mask is built during detection
        var stars = await scaledImage.FindStarsAsync(channel: 0, snrMin: 10f, maxStars: 2000, cancellationToken: cancellationToken);
        stars.Count.ShouldBeGreaterThan(0);
        stars.StarMask.ShouldNotBeNull();
        var starMask = stars.StarMask;

        // Compute pedestals
        var pedestals = new float[scaledImage.ChannelCount];
        for (var c = 0; c < scaledImage.ChannelCount; c++)
        {
            var (ped, _, _) = scaledImage.GetPedestralMedianAndMADScaledToUnit(c);
            pedestals[c] = ped;
        }

        // Scan background without mask (32×32) — same as initial load
        var (bgNoMask, lumaBgNoMask) = scaledImage.ScanBackgroundRegion(pedestals, squareSize: 32);

        // Scan background with star mask (48×48) — post star detection
        var (bgWithMask, lumaBgWithMask) = scaledImage.ScanBackgroundRegion(pedestals, squareSize: 48, starMask);

        // Log both for comparison
        for (var c = 0; c < bgNoMask.Length; c++)
        {
            testOutputHelper.WriteLine("Ch{0}: bg_no_mask={1:F6}, bg_with_mask={2:F6}, diff={3:F6}",
                c, bgNoMask[c], bgWithMask[c], bgWithMask[c] - bgNoMask[c]);
        }
        testOutputHelper.WriteLine("Luma: bg_no_mask={0:F6}, bg_with_mask={1:F6}, diff={2:F6}",
            lumaBgNoMask, lumaBgWithMask, lumaBgWithMask - lumaBgNoMask);

        // Masked background should be <= unmasked (stars only add flux)
        for (var c = 0; c < bgNoMask.Length; c++)
        {
            bgWithMask[c].ShouldBeLessThanOrEqualTo(bgNoMask[c] + 1e-4f,
                $"Ch{c}: masked background should not exceed unmasked");
        }
    }
}
