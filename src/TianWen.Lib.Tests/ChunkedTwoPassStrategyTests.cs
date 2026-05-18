using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Cross-N sigma-clip rejection in <see cref="ChunkedTwoPassStrategy"/>.
/// Mono synthetic frames with one obvious outlier; the strategy must reject
/// it on the strength of all N frames' distribution, not just a chunk's
/// 80-frame slice. The old per-chunk variant would have placed the outlier
/// + good frames into the same chunk and computed a local sigma that still
/// included the outlier in the median, so a 5-frame outlier vs 80 inliers
/// would not be cleanly rejected -- the new global threshold should kill
/// it cleanly.
/// </summary>
[Collection("Imaging")]
public class ChunkedTwoPassStrategyTests
{
    [Fact]
    public async Task CrossNRejection_PicksOffSingleOutlier()
    {
        var ct = TestContext.Current.CancellationToken;
        // 10 inlier frames at 0.50, one outlier at 0.95. Sigma-clip at
        // kappa=3 should reject the outlier on a clean Gaussian-ish set;
        // here we use constants so sd is exactly 0 around the inliers and
        // the outlier sits at "infinitely many" sigma -- guaranteed reject.
        var values = new[]
        {
            0.50f, 0.50f, 0.50f, 0.50f, 0.50f,
            0.95f,                                  // outlier
            0.50f, 0.50f, 0.50f, 0.50f, 0.50f,
        };
        const int width = 8;
        const int height = 6;

        var frames = new List<Image>(values.Length);
        foreach (var v in values)
        {
            var arr = new float[height, width];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    arr[y, x] = v;
            frames.Add(Image.FromChannel(arr, maxValue: 1f, minValue: 0f));
        }

        var strategy = new ChunkedTwoPassStrategy();
        var job = new IntegrationJob(
            WarpedFrames: _ => EnumerateOnce(frames),
            ExpectedFrameCount: frames.Count,
            Options: new IntegrationOptions(
                Rejector: new SigmaClipRejector(LowSigma: 3f, HighSigma: 3f),
                ApplyNormalization: false),
            StagingDir: Path.GetTempPath(),
            StatsRect: Rectangle.Empty);

        var result = await strategy.RunAsync(job, ct);

        result.FrameCount.ShouldBe(frames.Count);
        result.Master.ChannelCount.ShouldBe(1);
        var masterCh = result.Master.GetChannelArray(0);
        // After rejecting the 0.95 outlier, master pixels should be exactly
        // 0.50 (the mean of the 10 inliers).
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                masterCh[y, x].ShouldBe(0.50f, tolerance: 1e-6f,
                    $"master[{y}, {x}] = {masterCh[y, x]} -- outlier wasn't rejected");
            }
        }
        // The 0.95 frame contributed width*height rejected pixels per channel.
        result.TotalRejections.ShouldBe((long)width * height);
        result.MeanRejectionRate.ShouldBeInRange(0.08, 0.10);  // 1/11 ≈ 0.0909
    }

    [Fact]
    public async Task NoRejector_AllFramesContribute_MeanCombine()
    {
        // Without a rejector, pass-B's clip path falls back to kappa=3 default
        // BUT no rejector means the kappa gate is still applied. For constant
        // inliers (sd=0), kappa*sd=0 and the gate would reject anything not
        // exactly equal to the mean -- which is exactly what we want from a
        // "rejector-aware" strategy. Test confirms inliers pass.
        var ct = TestContext.Current.CancellationToken;
        var values = new[] { 0.20f, 0.30f, 0.40f };
        const int width = 4;
        const int height = 4;
        var frames = new List<Image>(values.Length);
        foreach (var v in values)
        {
            var arr = new float[height, width];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    arr[y, x] = v;
            frames.Add(Image.FromChannel(arr, maxValue: 1f, minValue: 0f));
        }

        var strategy = new ChunkedTwoPassStrategy();
        var job = new IntegrationJob(
            WarpedFrames: _ => EnumerateOnce(frames),
            ExpectedFrameCount: frames.Count,
            Options: new IntegrationOptions(Rejector: null, ApplyNormalization: false),
            StagingDir: Path.GetTempPath(),
            StatsRect: Rectangle.Empty);

        var result = await strategy.RunAsync(job, ct);

        // Expected mean = (0.2 + 0.3 + 0.4) / 3 = 0.30
        result.FrameCount.ShouldBe(3);
        var masterCh = result.Master.GetChannelArray(0);
        masterCh[0, 0].ShouldBe(0.30f, tolerance: 1e-6f);
    }

    private static async IAsyncEnumerable<Image> EnumerateOnce(List<Image> frames)
    {
        await Task.CompletedTask;
        foreach (var f in frames) yield return f;
    }
}
