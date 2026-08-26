using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Coverage for <c>--inherit-wb</c> surviving <c>--output-format none</c>.
///
/// <para>The white balance is stamped from the render result, and a render only happens when a
/// preview is asked for -- so asking for no companion output silently threw the inherited triple
/// away. That is the worst shape of bug this flag can have: it did nothing, said nothing, and did
/// it on exactly the cheapest kind of run.</para>
///
/// <para>It bit the comet work. A layer written with <c>--output-format none</c> carried no
/// <c>WBSOURCE</c> / <c>WBRED</c> / <c>WBGREEN</c> / <c>WBBLUE</c> at all, so anything rendering it
/// afterwards solved its own white balance from a starless plate instead of sharing the star
/// layer's -- which is the entire reason a triple gets passed in, since two layers that will be
/// combined have to agree on colour.</para>
/// </summary>
[Collection("Imaging")]
public class InheritedWhiteBalanceStampTests(ITestOutputHelper output)
{
    private static readonly ColourCalibration Donor = new(1.52208054f, 1.0f, 1.77573335f, "SPCC");

    private static async Task<string> RunAsync(MasterRenderOutputs renderOutputs, ITestOutputHelper output)
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = new TempStackingWorkspace();
        var darksDir = Path.Combine(workspace.RootDir, "DARK");
        Directory.CreateDirectory(darksDir);
        RgbBayerSyntheticFixture.WriteSyntheticLights(workspace.LightsDir);
        RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

        var options = new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir,
            ForcedStrategy: IntegrationStrategyKind.InRamAllFrames,
            RenderOutputs: renderOutputs,
            InheritedWhiteBalance: Donor);

        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null);
        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        results.Count.ShouldBe(1);
        results[0].MasterFitsPath.ShouldNotBeNull();
        return results[0].MasterFitsPath!;
    }

    [Theory]
    [InlineData(MasterRenderOutputs.None)]        // --output-format none: the regression
    [InlineData(MasterRenderOutputs.PreviewPng)]  // a preview IS rendered: the path that already worked
    public async Task AnInheritedTripleIsStampedWhetherOrNotAPreviewRenders(MasterRenderOutputs renderOutputs)
    {
        // Both cases, deliberately. No-preview is the regression; with-preview is there so a future
        // change that fixes one by breaking the other cannot pass.
        var masterPath = await RunAsync(renderOutputs, output);

        using var bf = new nom.tam.util.BufferedFile(masterPath, FileAccess.Read, FileShare.Read, 1024);
        using var fits = new nom.tam.fits.Fits(bf, false);
        var hdu = fits.ReadHDUHeaderOnly();
        hdu.ShouldNotBeNull();

        hdu.Header.GetStringValue("WBSOURCE").ShouldBe(Donor.Source,
            "an inherited triple keeps the DONOR's provenance -- how a white balance was derived is a " +
            "fact about the white balance, not about which process stamped it");
        hdu.Header.GetDoubleValue("WBRED", -1).ShouldBe(Donor.R, 1e-5);
        hdu.Header.GetDoubleValue("WBGREEN", -1).ShouldBe(Donor.G, 1e-5);
        hdu.Header.GetDoubleValue("WBBLUE", -1).ShouldBe(Donor.B, 1e-5);
    }
}
