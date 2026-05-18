using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// RGB Bayer counterpart to <see cref="StackingPipelineSyntheticTest"/>:
/// exercises the calibrate -> debayer -> register -> integrate chain end-
/// to-end on synthetic <see cref="SensorType.RGGB"/> data, plus a tiny
/// 2-frame master dark group so the Calibrator gets a real dark to
/// subtract. Catches regressions in the Bayer code path that the mono
/// test cannot see (debayer choice, RGGB Bayer pattern propagation,
/// 3-channel master output).
///
/// <para>Shares fixture data with <see cref="StackingPipelineRgbBayerDrizzleTest"/>
/// via <see cref="RgbBayerSyntheticFixture"/> -- the only variable between
/// the two tests is the integration strategy.</para>
/// </summary>
[Collection("Imaging")]
public class StackingPipelineRgbBayerSyntheticTest(ITestOutputHelper output)
{
    [Fact]
    public async Task Stack_4Frames_RGGB_WithDarkMaster_ProducesThreeChannelMaster()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        var darksDir = Path.Combine(workspace.RootDir, "DARK");
        Directory.CreateDirectory(darksDir);

        RgbBayerSyntheticFixture.WriteSyntheticLights(workspace.LightsDir);
        RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

        // No catalog DB -> plate-solve skipped. The Bayer/debayer path is
        // what we're verifying here, not the WCS pipeline.
        var options = new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir);
        var logger = new XunitLogger(output);
        var pipeline = new StackingPipeline(options, logger, catalogDb: null);

        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        // 1) One light group survived; all 4 RGGB frames registered against
        //    the reference. If FrameType.Dark frames had leaked into the
        //    light enumeration we'd see a second group or a registration
        //    skip; if SensorType.RGGB had been ignored we'd get a
        //    single-channel master.
        results.Count.ShouldBe(1, "expected a single integrated light group");
        var result = results[0];
        result.SkipReason.ShouldBeEmpty($"group should not have skipped: '{result.SkipReason}'");
        result.FramesAttempted.ShouldBe(RgbBayerSyntheticFixture.LightCount);
        // RGGB + sub-pixel dither stresses the quad-matcher more than the
        // mono path -- debayer interpolation at star edges drifts centroids
        // by ~0.1-0.3 px in a phase-dependent way that scales with dither.
        // The mono synthetic test matches 8/8 on the same dither magnitude
        // range; here we expect at least 3/4 (75%). A regression that broke
        // debayer or quad matching outright would drop below 2 and trip
        // the SkipReason check above.
        result.FramesMatched.ShouldBeGreaterThanOrEqualTo(6,
            $"expected at least 6/8 RGGB frames to register; got {result.FramesMatched}");

        // 2) Master FITS landed on disk + round-trips with a 3-channel
        //    shape. This is the key Bayer assertion: a missed debayer
        //    step would yield ChannelCount = 1.
        result.MasterFitsPath.ShouldNotBeNull();
        File.Exists(result.MasterFitsPath).ShouldBeTrue($"master FITS missing at {result.MasterFitsPath}");
        // Non-drizzle masters keep the canonical master_<slug>.fits name
        // (the _drizzle suffix is reserved for BayerDrizzle outputs so the
        // two strategies can A/B in the same output dir without collision).
        Path.GetFileName(result.MasterFitsPath).ShouldNotContain("_drizzle",
            customMessage: "default-strategy masters must not carry the _drizzle suffix");
        Image.TryReadFitsFile(result.MasterFitsPath, out var master).ShouldBeTrue();
        master.ShouldNotBeNull();
        master.ChannelCount.ShouldBe(3, "RGGB lights should produce a 3-channel debayered master");
        master.Width.ShouldBeGreaterThanOrEqualTo(RgbBayerSyntheticFixture.FrameSize);
        master.Height.ShouldBeGreaterThanOrEqualTo(RgbBayerSyntheticFixture.FrameSize);

        // 3) Every channel carries signal AND the per-channel medians
        //    match the gain ratio baked into BuildBayerMosaic (R=1.0,
        //    G=0.7, B=0.4). A "median > epsilon" check alone passes even
        //    when Bayer dispatch is fully broken (e.g. R<->B swapped)
        //    because every channel still ends up with SOME signal. The
        //    ratio check is the actual guard.
        //
        //    The debayer path interpolates the missing 2/3 of channels at
        //    every pixel, which softens the raw ratio toward 1.0 (each
        //    output pixel is a weighted blend of nearby R/G/B Bayer
        //    cells). Tolerance is therefore wider than the drizzle test's:
        //    expected R/G in ~[0.8, 1.7] and B/G in ~[0.5, 1.0], still
        //    plenty of margin to catch an R<->B swap.
        var medians = new float[master.ChannelCount];
        for (var c = 0; c < master.ChannelCount; c++)
        {
            var (_, median, _) = master.GetPedestralMedianAndMADScaledToUnit(c);
            medians[c] = median;
            median.ShouldBeGreaterThan(1e-4f,
                $"channel {c} median {median:F6} is too close to zero -- debayer / calibration likely zeroed it");
        }
        var rRatio = medians[0] / medians[1];
        var bRatio = medians[2] / medians[1];
        rRatio.ShouldBeInRange(0.8f, 1.7f,
            $"R/G ratio {rRatio:F2} outside [0.8, 1.7] -- channels likely swapped (debayer Bayer dispatch regression)");
        bRatio.ShouldBeInRange(0.5f, 1.0f,
            $"B/G ratio {bRatio:F2} outside [0.5, 1.0] -- channels likely swapped (debayer Bayer dispatch regression)");

        // 4) Calibration master cache populated. The 2-dark group should
        //    have built one master_*.fits under output/masters/. Calibration
        //    masters are written by MasterFrameBuilder, NOT IntegrationFitsWriter,
        //    so they're deliberately NOT stamped with SWCREATE -- they live
        //    under masters/ which the wipe scan never touches.
        var mastersDir = Path.Combine(workspace.OutputDir, "masters");
        Directory.Exists(mastersDir).ShouldBeTrue();
        var cachedMasters = Directory.GetFiles(mastersDir, "*.fits");
        cachedMasters.Length.ShouldBe(1, "exactly one dark master should have been cached");

        // 5) The integrated master FITS at outputDir top-level IS stamped
        //    and would survive the wipe-on-rerun scan as one of our own.
        IntegrationFitsWriter.IsTianWenMaster(result.MasterFitsPath).ShouldBeTrue(
            "integrated master should round-trip through IsTianWenMaster (SWCREATE stamping)");
    }
}
