using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Coverage for <c>--save-calibrated</c> / <c>--save-normalized</c>: the per-frame intermediates
/// Astro Pixel Processor and PixInsight let you keep, so a bad master can be attributed to a stage
/// instead of guessed at.
///
/// <para>Each case asserts the same three things, because a diagnostic dump has three ways to be
/// useless: absent, unattributable, or re-ingestible. So the files must EXIST, must name the frame
/// they came from, and must carry the provenance that keeps the scan from eating them as lights on
/// the next run.</para>
/// </summary>
[Collection("Imaging")]
public class StackingIntermediateDumpTests(ITestOutputHelper output)
{
    private static async Task<(GroupResult Result, string StagingDir)> RunAsync(
        TempStackingWorkspace workspace, StackingOptions options, ITestOutputHelper output)
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null);
        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        results.Count.ShouldBe(1, "expected a single integrated light group");
        results[0].SkipReason.ShouldBeEmpty($"group should not have skipped: '{results[0].SkipReason}'");
        var staging = Path.Combine(workspace.OutputDir, "_staging", results[0].GroupSlug);
        return (results[0], staging);
    }

    private static TempStackingWorkspace NewWorkspace()
    {
        var workspace = new TempStackingWorkspace();
        var darksDir = Path.Combine(workspace.RootDir, "DARK");
        Directory.CreateDirectory(darksDir);
        RgbBayerSyntheticFixture.WriteSyntheticLights(workspace.LightsDir);
        RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);
        return workspace;
    }

    [Fact]
    public async Task NeitherFlagWritesNothingAtAll()
    {
        // The off path is the one every existing run takes, so it must not so much as create a
        // directory: an empty 'calibrated' folder beside a master implies a dump that failed.
        using var workspace = NewWorkspace();
        var (_, staging) = await RunAsync(workspace, new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir,
            ForcedStrategy: IntegrationStrategyKind.InRamAllFrames), output);

        Directory.Exists(Path.Combine(staging, "calibrated")).ShouldBeFalse();
        Directory.Exists(Path.Combine(staging, "normalized")).ShouldBeFalse();
    }

    [Fact]
    public async Task SaveCalibratedWritesOnePerFrameNamedAfterItsLight()
    {
        using var workspace = NewWorkspace();
        var (result, staging) = await RunAsync(workspace, new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir,
            ForcedStrategy: IntegrationStrategyKind.InRamAllFrames,
            SaveCalibrated: true), output);

        var files = Directory.GetFiles(Path.Combine(staging, "calibrated"), "*_calibrated.fits");
        files.Length.ShouldBe(result.FramesMatched,
            "one calibrated dump per matched frame, or the dump cannot be reasoned about as a set");
        AssertAttributable(files[0]);
    }

    [Fact]
    public async Task SaveNormalizedWritesThePixelsTheCombineActuallySaw()
    {
        // Normalization only exists on the non-drizzle path (drizzle accumulates in ADU and divides
        // once), so this pins the strategy rather than letting the selector choose.
        using var workspace = NewWorkspace();
        var (result, staging) = await RunAsync(workspace, new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir,
            ForcedStrategy: IntegrationStrategyKind.InRamAllFrames,
            SaveNormalized: true), output);

        var files = Directory.GetFiles(Path.Combine(staging, "normalized"), "*_normalized.fits");
        files.Length.ShouldBe(result.FramesMatched);
        AssertAttributable(files[0]);

        // The whole point of the normalized dump is that every frame was brought to a common level,
        // so the medians must agree far more tightly than the calibrated frames' do.
        var medians = new List<float>();
        foreach (var f in files)
        {
            TianWen.Lib.Imaging.Image.TryReadFitsFile(f, out var img).ShouldBeTrue();
            img.ShouldNotBeNull();
            var (_, median, _) = img.GetPedestralMedianAndMADScaledToUnit(0);
            medians.Add(median);
        }
        medians.Count.ShouldBe(result.FramesMatched);
        var (lo, hi) = (float.MaxValue, float.MinValue);
        foreach (var m in medians)
        {
            lo = System.MathF.Min(lo, m);
            hi = System.MathF.Max(hi, m);
        }
        (hi - lo).ShouldBeLessThan(0.05f,
            $"normalized frames should share a level; spread was {lo}..{hi}");
    }

    /// <summary>
    /// A dump has to say where it came from and must never be mistakable for a light.
    /// </summary>
    private static void AssertAttributable(string path)
    {
        using var bf = new nom.tam.util.BufferedFile(path, System.IO.FileAccess.Read, System.IO.FileShare.Read, 1024);
        using var fits = new nom.tam.fits.Fits(bf, false);
        var hdu = fits.ReadHDUHeaderOnly();
        hdu.ShouldNotBeNull();

        hdu.Header.GetStringValue("SRCPATH").ShouldNotBeNullOrEmpty("a dump that cannot name its source frame is not evidence");
        hdu.Header.GetStringValue("TWSTAGE").ShouldNotBeNullOrEmpty();
        hdu.Header.GetStringValue(FrameProvenance.SourceDigestKeyword).ShouldNotBeNullOrEmpty();

        // This is the predicate the scan's provenance skip actually asks. Without it these dumps
        // would be re-ingested as lights the next time the folder is stacked.
        IntegrationFitsWriter.IsTianWenProduct(hdu.Header.GetStringValue("SWCREATE"))
            .ShouldBeTrue("the dump must read as a TianWen product so the scan drops it");
    }
}
