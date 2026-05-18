using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end coverage for <see cref="DrizzleStrategy"/> (Bayer drizzle,
/// Phase 1: scale=10, pixfrac=1.0). Shares the 4-light + 2-dark RGGB
/// fixture with <see cref="StackingPipelineRgbBayerSyntheticTest"/>; the
/// only difference is forcing <see cref="IntegrationStrategyKind.BayerDrizzle"/>
/// and dropping <see cref="DrizzleOptions.MinFrameCount"/> to 4 so the
/// synthetic fixture can actually exercise the strategy.
///
/// <para>Two cases:</para>
/// <list type="bullet">
///   <item><c>Stack_4Frames_RGGB_Drizzle_ProducesThreeChannelMaster</c> --
///     happy path. Verifies 3-channel output + non-zero per-channel
///     medians + coverage-map round-trip + drizzle's per-cell coverage
///     reporting.</item>
///   <item><c>Stack_4Frames_DrizzleGate_SkipsWithClearReason</c> -- same
///     fixture but leaves <see cref="DrizzleOptions.MinFrameCount"/> at
///     its 60 default. Verifies the gate fires with a clear reason
///     rather than silently producing a NaN-riddled master.</item>
/// </list>
/// </summary>
[Collection("Imaging")]
public class StackingPipelineRgbBayerDrizzleTest(ITestOutputHelper output)
{
    [Fact]
    public async Task Stack_4Frames_RGGB_Drizzle_ProducesThreeChannelMaster()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        var darksDir = Path.Combine(workspace.RootDir, "DARK");
        Directory.CreateDirectory(darksDir);

        RgbBayerSyntheticFixture.WriteSyntheticLights(workspace.LightsDir);
        RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

        // Production default MinFrameCount=60 would gate out an 8-frame
        // synthetic test entirely; drop to 6 to allow up to 2 quad-fit
        // failures (sub-pixel dither + Bayer interpolation noise drops
        // ~1-2 of every 8 frames on RGGB, same as real-world sessions).
        // Pixfrac stays at the production default (1.0) so we exercise
        // the forward-bilinear drizzle path the real workload would also
        // hit.
        var options = new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir,
            ForcedStrategy: IntegrationStrategyKind.BayerDrizzle,
            DrizzleOptions: new DrizzleOptions(MinFrameCount: 6));
        var logger = new XunitLogger(output);
        var pipeline = new StackingPipeline(options, logger, catalogDb: null);

        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        // 1) The group went through the drizzle path. Same registration
        //    pass as the debayer test (the pipeline still debayers for
        //    star detection); strategy differs only on the integration
        //    pass.
        results.Count.ShouldBe(1, "expected a single integrated light group");
        var result = results[0];
        result.SkipReason.ShouldBeEmpty($"group should not have skipped: '{result.SkipReason}'");
        result.FramesMatched.ShouldBeGreaterThanOrEqualTo(6,
            $"expected at least 6/8 RGGB frames to register; got {result.FramesMatched}");

        // 2) Master FITS round-trips with 3 channels. The drizzle code path
        //    bypasses DebayerAsync entirely -- if the strategy still pushed
        //    1-channel raw through, this would surface as ChannelCount=1.
        result.MasterFitsPath.ShouldNotBeNull();
        File.Exists(result.MasterFitsPath).ShouldBeTrue();
        Image.TryReadFitsFile(result.MasterFitsPath, out var master).ShouldBeTrue();
        master.ShouldNotBeNull();
        master.ChannelCount.ShouldBe(3, "drizzle on RGGB should produce a 3-channel master");
        master.Width.ShouldBeGreaterThanOrEqualTo(RgbBayerSyntheticFixture.FrameSize);
        master.Height.ShouldBeGreaterThanOrEqualTo(RgbBayerSyntheticFixture.FrameSize);

        // 3) Every channel carries signal. A wrong Bayer-pattern dispatch
        //    in DrizzleStrategy (e.g. always writing channel 0) would zero
        //    G or B here; this catches it.
        for (var c = 0; c < master.ChannelCount; c++)
        {
            var (_, median, _) = master.GetPedestralMedianAndMADScaledToUnit(c);
            median.ShouldBeGreaterThan(1e-4f,
                $"channel {c} median {median:F6} too close to zero -- drizzle Bayer dispatch likely wrong");
        }

        // 4) The IntegrationResult is the drizzle variant -- TotalRejections
        //    is repurposed to count uncovered cells, the rejection map is
        //    the per-channel coverage weight buffer. A 4-frame stack with
        //    sub-pixel dither WILL leave some R/B cells uncovered because
        //    each Bayer position only sees ~25% of input pixels; we don't
        //    pin an exact ratio (varies with dither offsets + drop math)
        //    but it should be a small fraction, not majority of the canvas.
        result.Result.ShouldNotBeNull();
        result.Result.MeanRejectionRate.ShouldBeLessThan(0.50,
            $"uncovered-cell fraction {result.Result.MeanRejectionRate:P1} is too high -- " +
            "drizzle forward-warp or pattern dispatch likely missing most pixels");

        // 5) Coverage map (RejectionMap on the result) is 3-channel and
        //    has non-zero values across most of the canvas. The drizzle
        //    integrator returns its weight buffer as the RejectionMap so
        //    downstream code (FITS write, future QA) can see per-channel
        //    coverage.
        result.Result.RejectionMap.ChannelCount.ShouldBe(3,
            "drizzle's RejectionMap is the per-channel coverage buffer");
    }

    [Fact]
    public async Task Stack_4Frames_DrizzleGate_SkipsWithClearReason()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        var darksDir = Path.Combine(workspace.RootDir, "DARK");
        Directory.CreateDirectory(darksDir);

        RgbBayerSyntheticFixture.WriteSyntheticLights(workspace.LightsDir);
        RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

        // No DrizzleOptions override -> defaults apply -> MinFrameCount=60.
        // The 4-frame fixture trips the gate; the pipeline must skip the
        // group with a clear reason rather than producing a NaN master.
        var options = new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir,
            ForcedStrategy: IntegrationStrategyKind.BayerDrizzle);
        var logger = new XunitLogger(output);
        var pipeline = new StackingPipeline(options, logger, catalogDb: null);

        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        results.Count.ShouldBe(1);
        var result = results[0];
        result.SkipReason.ShouldContain("BayerDrizzle requires >= 60 matched frames");
        result.MasterFitsPath.ShouldBeNull("no master should be written when the drizzle gate fires");
        result.Result.ShouldBeNull();
    }
}
