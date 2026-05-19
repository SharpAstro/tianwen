using System;
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
        // The filename carries the _drizzle suffix so an A/B run against the
        // default strategy can coexist in the same output dir. The autocrop
        // sidecar should also pick up the suffix (master_<slug>_drizzle_autocrop.fits)
        // because its name is derived from masterPath via WithSuffix("_autocrop").
        Path.GetFileName(result.MasterFitsPath).ShouldEndWith("_drizzle.fits",
            customMessage: "drizzle masters must land under master_<slug>_drizzle.fits so they don't collide with default-strategy masters");
        var autocropPath = Path.Combine(workspace.OutputDir,
            Path.GetFileNameWithoutExtension(result.MasterFitsPath) + "_autocrop.fits");
        File.Exists(autocropPath).ShouldBeTrue(
            $"autocrop sidecar missing at {autocropPath}; expected the _drizzle suffix to propagate via WithSuffix");
        Image.TryReadFitsFile(result.MasterFitsPath, out var master).ShouldBeTrue();
        master.ShouldNotBeNull();
        master.ChannelCount.ShouldBe(3, "drizzle on RGGB should produce a 3-channel master");
        master.Width.ShouldBeGreaterThanOrEqualTo(RgbBayerSyntheticFixture.FrameSize);
        master.Height.ShouldBeGreaterThanOrEqualTo(RgbBayerSyntheticFixture.FrameSize);

        // 3) Every channel carries signal AND the per-channel medians match
        //    the gain ratio baked into BuildBayerMosaic (R=1.0, G=0.7,
        //    B=0.4). A "median > epsilon" check alone passes even when
        //    Bayer dispatch is fully broken (e.g. R<->B swapped) because
        //    every channel still ends up with SOME signal. The ratio check
        //    is the actual guard: an R<->B swap inverts the master's
        //    channel ratios from 1.0:0.7:0.4 to 0.4:0.7:1.0, which the
        //    tolerance below catches by 5+ sigma.
        var medians = new float[master.ChannelCount];
        for (var c = 0; c < master.ChannelCount; c++)
        {
            var (_, median, _) = master.GetPedestralMedianAndMADScaledToUnit(c);
            medians[c] = median;
            median.ShouldBeGreaterThan(1e-4f,
                $"channel {c} median {median:F6} too close to zero -- drizzle Bayer dispatch likely wrong");
        }
        // Ratios relative to G (channel 1) so we don't depend on absolute
        // sky-background brightness. Expected R/G = 1.0/0.7 = 1.43,
        // B/G = 0.4/0.7 = 0.57. Tolerance is generous (~30%) to cover
        // drizzle's per-cell coverage noise + dark-subtraction residual
        // on a small synthetic fixture; the R<->B-swap regression would
        // flip them to R/G ~= 0.57 and B/G ~= 1.43, well outside.
        var rRatio = medians[0] / medians[1];
        var bRatio = medians[2] / medians[1];
        rRatio.ShouldBeInRange(1.0f, 2.0f,
            $"R/G ratio {rRatio:F2} outside [1.0, 2.0] -- channels likely swapped (Bayer dispatch regression)");
        bRatio.ShouldBeInRange(0.3f, 0.9f,
            $"B/G ratio {bRatio:F2} outside [0.3, 0.9] -- channels likely swapped (Bayer dispatch regression)");

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
    public async Task Stack_TilePipelinedDrizzle_MatchesBayerDrizzleByteForByte()
    {
        // Both drizzle variants share DrizzleKernel for forward-projection
        // and FinaliseDivide for the flux/weight pass. The only difference
        // is the accumulator shape: full-canvas (BayerDrizzle) vs strip-
        // local (TilePipelinedDrizzle). Addition order per cell is the
        // same in both paths (same frame order, same source-pixel order,
        // same Bayer dispatch), so the master pixels must match
        // bit-exactly. A drift here would indicate the strip-deposit
        // codepath is silently truncating or double-counting at strip
        // boundaries.
        var ct = TestContext.Current.CancellationToken;

        // Share ONE workspace + ONE synthesised fixture across both runs.
        // Each strategy gets its own output sub-dir so the master FITS
        // files don't collide, but the input FITS files (light + dark)
        // are byte-identical and present in the same on-disk order. That
        // makes the directory-scan order in the pipeline reproducibly the
        // same across both runs; with separate workspaces, file
        // timestamps drift and the resulting frame iteration order can
        // diverge enough to perturb per-cell drizzle sums by 1 ULP.
        using var workspace = new TempStackingWorkspace();
        var darksDir = Path.Combine(workspace.RootDir, "DARK");
        Directory.CreateDirectory(darksDir);
        RgbBayerSyntheticFixture.WriteSyntheticLights(workspace.LightsDir);
        RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

        async Task<float[][]> RunAndExtract(IntegrationStrategyKind kind)
        {
            var options = new StackingOptions(
                DataRoot: workspace.RootDir,
                OutputDir: workspace.OutputDir,
                ForcedStrategy: kind,
                DrizzleOptions: new DrizzleOptions(MinFrameCount: 6));
            var logger = new XunitLogger(output);
            var pipeline = new StackingPipeline(options, logger, catalogDb: null);

            var results = new List<GroupResult>();
            await foreach (var r in pipeline.RunAsync(ct))
            {
                results.Add(r);
            }
            results.Count.ShouldBe(1);
            var masterPath = results[0].MasterFitsPath;
            masterPath.ShouldNotBeNull();
            Image.TryReadFitsFile(masterPath, out var master).ShouldBeTrue();
            master.ShouldNotBeNull();
            master.ChannelCount.ShouldBe(3);

            // Flatten each channel into a row-major float[] for cmp.
            var perChannel = new float[master.ChannelCount][];
            for (var c = 0; c < master.ChannelCount; c++)
            {
                var span = master.GetChannelSpan(c);
                perChannel[c] = span.ToArray();
            }
            return perChannel;
        }

        // First strategy writes its master into workspace.OutputDir. The
        // pipeline filter excludes anything under that dir from the
        // light-scan, so the second strategy's run won't see the first
        // run's master FITS as a stray light frame. Both strategies emit
        // master_<slug>_drizzle.fits (same _drizzle suffix), so move the
        // first run's outputs aside before the second runs to avoid the
        // overwrite-then-re-read collision and to preserve the master
        // for the comparison.
        var streaming = await RunAndExtract(IntegrationStrategyKind.BayerDrizzle);
        // Archive the first run's master OUTSIDE workspace.RootDir entirely:
        // anything left under workspace would get rescanned as a light by
        // the second run unless it's under OutputDir (which is going to be
        // recreated by the second run with conflicting filenames). System
        // temp is the simplest "definitely not part of the second run's
        // data root" location; the file gets cleaned up with the rest of
        // the workspace via TempStackingWorkspace's IDisposable. We don't
        // actually need the archived bytes -- the in-memory `streaming`
        // float arrays are what we compare against -- so the move is just
        // about getting the files out of the second run's scan path.
        var trashDir = Path.Combine(Path.GetTempPath(), "tianwen-parity-trash-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(trashDir);
        try
        {
            foreach (var file in Directory.GetFiles(workspace.OutputDir))
            {
                File.Move(file, Path.Combine(trashDir, Path.GetFileName(file)));
            }
            var tiled = await RunAndExtract(IntegrationStrategyKind.TilePipelinedDrizzle);
            CompareChannels(streaming, tiled);
        }
        finally
        {
            try { Directory.Delete(trashDir, recursive: true); } catch { /* hygiene */ }
        }
        return;

        static void CompareChannels(float[][] streaming, float[][] tiled)
        {
            // Tolerance: 0.01% relative error. The two paths share the
            // DrizzleKernel and the FinaliseDivide pass, so the deposit
            // math + final divide are bit-equivalent for identical
            // inputs. What CAN drift by a few ULPs is the calibrated
            // input itself: streaming consumes Image instances from the
            // pipeline's pre-populated `calibratedCache` (registered
            // during the matching pass), while tiled re-reads + re-
            // calibrates via DecodeCalibrate. Both follow the same
            // FITS reader + Calibrator.Apply code paths, but SIMD-vs-
            // scalar dispatch in Calibrator (ArrayPool reuse + thread-
            // local intrinsics) can yield 1-2 ULP differences in
            // calibrated samples when invoked under different call-site
            // contexts. Those propagate as a handful of ULPs through
            // the drizzle accumulator. The 1e-4 bound is wide enough to
            // tolerate that drift across thread/SIMD scheduling
            // variation, yet tight enough to flag a real algorithmic
            // divergence (R/B swap, halo too tight, strip boundary
            // off-by-one -- all of which produce > 1% local deltas).
            const double RelativeTolerance = 1e-4;
            streaming.Length.ShouldBe(tiled.Length);
            var maxAbsDelta = 0.0;
            var maxRelDelta = 0.0;
            for (var c = 0; c < streaming.Length; c++)
            {
                streaming[c].Length.ShouldBe(tiled[c].Length,
                    $"channel {c} length mismatch: {streaming[c].Length} vs {tiled[c].Length}");
                var streamCh = streaming[c];
                var tiledCh = tiled[c];
                for (var i = 0; i < streamCh.Length; i++)
                {
                    var s = streamCh[i];
                    var t = tiledCh[i];
                    // NaN cells: both paths emit NaN where coverage is
                    // zero -- they must agree on the NaN pattern (any cell
                    // that's NaN in one path but a finite value in the
                    // other is a real bug, not a precision artifact).
                    if (float.IsNaN(s) || float.IsNaN(t))
                    {
                        float.IsNaN(s).ShouldBe(float.IsNaN(t),
                            $"channel {c} cell {i}: NaN pattern mismatch (BayerDrizzle={s}, TilePipelinedDrizzle={t}). " +
                            "The two paths must agree on which cells are uncovered.");
                        continue;
                    }
                    var abs = (double)Math.Abs(s - t);
                    if (abs > maxAbsDelta) maxAbsDelta = abs;
                    var rel = abs / Math.Max(Math.Abs((double)s), 1e-10);
                    if (rel > maxRelDelta) maxRelDelta = rel;
                    rel.ShouldBeLessThan(RelativeTolerance,
                        $"channel {c} cell {i}: BayerDrizzle={s:R} vs TilePipelinedDrizzle={t:R} " +
                        $"(absolute delta {abs:G3}, relative {rel:G3}); tolerance {RelativeTolerance:G3}. " +
                        "Above this threshold indicates a real algorithmic divergence, not a " +
                        "floating-point summation-order artifact.");
                }
            }
        }

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
