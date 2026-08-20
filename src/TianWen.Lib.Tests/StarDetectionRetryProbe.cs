using System;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Diagnostic probe: dumps the per-pass table of the FindStarsAsync retry loop for the real pinned
/// fixtures, so the cost of a retry chain that cannot reach its target is a measurement rather than
/// an inference. Not an assertion -- read the output.
/// </summary>
[Collection("Imaging")]
public class StarDetectionRetryProbe(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 10f, 500)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 10f, 200)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 10f, 2000)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 10f, 5000)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 10f, 200)]
    [InlineData("PlateSolveTestFile", 10f, 2000)]
    [InlineData("PlateSolveTestFile", 10f, 200)]
    public async Task DumpPassTable(string name, float snrMin, int target)
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: ct);
        var (channels, width, height) = image.Shape;
        testOutputHelper.WriteLine($"--- {name} {width}x{height} ch={channels} bitDepth={image.BitDepth} target={target} snrMin={snrMin}");

        var logger = new XunitLogger(testOutputHelper);
        var stars = await image.FindStarsAsync(image.ReferenceStarChannel, snrMin, maxStars: target, logger: logger, cancellationToken: ct);
        testOutputHelper.WriteLine($"--- final: {stars.Count} stars");
    }
}
