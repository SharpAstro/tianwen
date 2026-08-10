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

    /// <summary>
    /// Byte-pins detector output per image + SNR floor.
    ///
    /// <para><b>The RGGB counts dropped by ~1.1 % on 2026-08-11</b> (3,046 -> 3,014 at SNR 10, 2,786 ->
    /// 2,753 at SNR 30) when detection stopped reporting the same star more than once. A saturated
    /// star's above-threshold halo extends past the <c>HfdFactor * HFD</c> star-area mask, so halo
    /// pixels re-ran <c>AnalyseStar</c>, whose centre of gravity landed back on the same core, and
    /// every copy was counted. The removed entries were duplicates, not stars: the accompanying
    /// duplicate-pair pin is what holds that line, since a count pin cannot (duplicates only push a
    /// count up, and up is the direction an expectation drifts to).</para>
    ///
    /// <para><b><paramref name="expectedDuplicatePairs"/> is 1 for the dense RGGB frame, and that is a
    /// measurement, not a tolerance.</b> One pair survives at (2943.11, 761.56) / (2943.19, 761.55),
    /// HFD 3.60 / 3.57, out of 3,014 stars. It is NOT a parallelism race: 12 consecutive runs over the
    /// identical input produced the same count and the same coordinates, and it sits mid-band
    /// (row 14 of the 44-row chunk), not on the interleaved even/odd chunk seam. The exact geometry
    /// that lets that one centroid escape a mask which should cover it is not yet explained, so it is
    /// pinned rather than rounded away: an increase means the duplicate class is back, and a drop to 0
    /// means someone fixed this and should say so here.</para>
    /// </summary>
    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 10f, 89, null, 0)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 20f, 28, null, 0)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 30f, 13, null, 0)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 30f, 2753, 5000, 1)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 10f, 3014, 5000, 1)]
    public async Task GivenImageFileAndMinSNRWhenFindingStarsThenTheyAreFound(string name, float snrMin, int expectedStars, int? maxStars = null, int expectedDuplicatePairs = 0)
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

        // No star reported twice. On a real dense frame this is the assertion that gives the count
        // above its meaning: 3,014 distinct stars, not 3,046 detections of 3,014 stars.
        var all = actualStars.ToArray();
        var duplicates = 0;
        var detail = "";
        const int chunkSize = 2 * ((int)(Image.HfdFactor * Image.BoxRadius) + 1);
        for (var i = 0; i < all.Length; i++)
        {
            for (var j = i + 1; j < all.Length; j++)
            {
                if (MathF.Abs(all[i].XCentroid - all[j].XCentroid) < 1f
                    && MathF.Abs(all[i].YCentroid - all[j].YCentroid) < 1f)
                {
                    duplicates++;
                    if (detail.Length == 0)
                    {
                        // Chunk-relative row says whether this is the interleaved-parallelism seam
                        // (a star straddling two row bands) rather than an uncovered-mask case.
                        var rowInChunk = (int)MathF.Round(all[i].YCentroid) % chunkSize;
                        detail = $" first at ({all[i].XCentroid:F2}, {all[i].YCentroid:F2}) hfd={all[i].HFD:F2}" +
                                 $" / ({all[j].XCentroid:F2}, {all[j].YCentroid:F2}) hfd={all[j].HFD:F2}," +
                                 $" rowInChunk={rowInChunk} of {chunkSize}";
                    }
                }
            }
        }
        testOutputHelper.WriteLine("{0} @ SNR {1}: {2} stars, {3} duplicate pair(s){4}", name, snrMin, all.Length, duplicates, detail);
        duplicates.ShouldBe(expectedDuplicatePairs,
            $"{duplicates} duplicate detection pair(s) in {name} at SNR {snrMin}, expected {expectedDuplicatePairs}.{detail}");
    }

    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", "None", 28)]
    [InlineData("RGGB_frame_bx0_by0_top_down", "AHD", 100)]
    public async Task GivenAstroImageDocumentWhenDetectingStarsThenStarsAreFound(string name, string algorithmStr, int minExpectedStars)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var algorithm = System.Enum.Parse<DebayerAlgorithm>(algorithmStr);

        // given: load via AstroImageDocument (same path as the viewer)
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

        // Detect stars: mask is built during detection
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

        // Scan background without mask (32×32): same as initial load
        var (bgNoMask, lumaBgNoMask) = scaledImage.ScanBackgroundRegion(pedestals, squareSize: 32);

        // Scan background with star mask (48×48): post star detection
        var (bgWithMask, lumaBgWithMask) = scaledImage.ScanBackgroundRegion(pedestals, squareSize: 48, starMask);

        // Log both for comparison
        for (var c = 0; c < bgNoMask.Length; c++)
        {
            testOutputHelper.WriteLine("Ch{0}: bg_no_mask={1:F6}, bg_with_mask={2:F6}, diff={3:F6}",
                c, bgNoMask[c], bgWithMask[c], bgWithMask[c] - bgNoMask[c]);
        }
        testOutputHelper.WriteLine("Luma: bg_no_mask={0:F6}, bg_with_mask={1:F6}, diff={2:F6}",
            lumaBgNoMask, lumaBgWithMask, lumaBgWithMask - lumaBgNoMask);

        // Masked background should be <= unmasked, but the two calls use different
        // squareSize (32 vs 48) and each picks its own darkest-luma patch -- so the
        // patches sampled aren't the same image region. Tolerance absorbs that
        // patch-choice noise (observed up to ~1.5e-4 on heavy-gradient frames);
        // it's wide enough that a real mask defect (stars not excluded -> bg
        // inflated by star flux, typically O(1e-2)) would still trip the check.
        for (var c = 0; c < bgNoMask.Length; c++)
        {
            bgWithMask[c].ShouldBeLessThanOrEqualTo(bgNoMask[c] + 5e-4f,
                $"Ch{c}: masked background should not exceed unmasked");
        }
    }
}
