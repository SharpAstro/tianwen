using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A light group with a FLAT but no matching DARK must still get its pedestal removed, from the
/// BIAS.
///
/// <para>With a dark, passing a bias too would double-subtract: the master dark is built from raw
/// darks with no bias pre-subtraction, so the pedestal is already inside it. Without a dark, that
/// same reasoning left nothing to remove the pedestal at all, and the flat then divided a frame
/// still carrying it. The order is what makes it damaging rather than merely incomplete --
/// <c>(signal + pedestal) / flat</c> imprints the flat's inverse shape onto a constant offset.</para>
///
/// <para>Measured on a real SVBONY SV605CC set whose 30 s lights have no dark at any temperature:
/// an 804 ADU pedestal divided by a flat spanning 0.950-1.019 spreads to 789-846 ADU. That is a
/// 57 ADU gradient where the real sky signal is 948 ADU -- six percent, shaped exactly like inverse
/// vignetting, so it reads as light pollution.</para>
/// </summary>
[Collection("Imaging")]
public class BiasPedestalFallbackTests(ITestOutputHelper output)
{
    /// <summary>
    /// Stack, dumping the CALIBRATED frames, and return the median of the first one.
    /// <para>The MASTER cannot answer this question: the non-drizzle path normalises every frame to
    /// a common median (<c>IntegrationOptions.NormalizationTarget</c> = 0.5) before combining, so a
    /// pedestal difference is scaled away and both arms land on 0.4996. The calibrated frame is the
    /// last point at which the pedestal still exists as a level.</para>
    /// </summary>
    private static async Task<float> CalibratedMedianAsync(TempStackingWorkspace ws, ITestOutputHelper output)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new StackingOptions(
            DataRoot: ws.RootDir,
            OutputDir: ws.OutputDir,
            ForcedStrategy: IntegrationStrategyKind.InRamAllFrames,
            RenderOutputs: MasterRenderOutputs.None,
            SaveCalibrated: true);

        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null);
        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }
        results.Count.ShouldBe(1, "expected one light group");
        results[0].SkipReason.ShouldBeEmpty();

        var dir = Path.Combine(ws.OutputDir, "_staging", results[0].GroupSlug, "calibrated");
        var dumps = Directory.GetFiles(dir, "*_calibrated.fits");
        dumps.Length.ShouldBeGreaterThan(0, "no calibrated frames were dumped");
        System.Array.Sort(dumps);

        Image.TryReadFitsFile(dumps[0], out var frame).ShouldBeTrue();
        frame.ShouldNotBeNull();
        var span = frame!.GetChannelSpan(0);
        var values = new float[span.Length];
        span.CopyTo(values);
        System.Array.Sort(values);
        return values[values.Length / 2];
    }

    [Fact]
    public async Task WithNoMatchingDarkTheBiasStillRemovesThePedestal()
    {
        // An A/B on the CALIBRATED frame, not an absolute threshold on the master. The first version
        // of this test converted a master median back to ADU by guesswork and PASSED with the fix
        // disabled; the second measured the master, which normalisation had already flattened. Same
        // data, one difference -- whether a bias is present to remove the pedestal.
        using var withBias = new TempStackingWorkspace();
        var biasDir = Path.Combine(withBias.RootDir, "BIAS");
        Directory.CreateDirectory(biasDir);
        RgbBayerSyntheticFixture.WriteSyntheticLights(withBias.LightsDir);
        RgbBayerSyntheticFixture.WriteSyntheticBiases(biasDir);

        using var noBias = new TempStackingWorkspace();
        RgbBayerSyntheticFixture.WriteSyntheticLights(noBias.LightsDir);

        var withMedian = await CalibratedMedianAsync(withBias, output);
        var withoutMedian = await CalibratedMedianAsync(noBias, output);
        output.WriteLine($"calibrated median with bias {withMedian:F3}, without {withoutMedian:F3}, "
            + $"difference {withoutMedian - withMedian:F3} (bias level is {RgbBayerSyntheticFixture.BiasLevel})");

        withMedian.ShouldBeGreaterThan(0f, "a calibrated frame of pure zero means calibration ate the signal");
        (withoutMedian - withMedian).ShouldBeGreaterThan(RgbBayerSyntheticFixture.BiasLevel * 0.8f,
            "with no dark to carry it, the BIAS has to remove the pedestal. An unchanged level means "
            + "nothing did -- and a flat would then divide a frame still holding it, imprinting the "
            + "flat's inverse shape on what should be a constant offset.");
    }
}
