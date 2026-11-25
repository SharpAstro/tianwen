using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The acceptance probe for comet stacking on a real night (#2489): 10P/Tempel 2, 2026-08-16, 135 x 60 s, stacked on
/// the comet with each frame's stars removed, as the owner's August composite was made, and held to what that
/// composite showed: no dark streak and no coloured ring round the coma (<see cref="CometResidue"/>), a star layer the
/// model was subtracted from, and a composite written.
/// </summary>
/// <remarks>
/// <para><b>Env-gated and Heavy:</b> it needs the night, a track file and RC-Astro StarXTerminator licensed, and takes
/// half an hour or more. <c>TIANWEN_COMET_10P_ROOT</c> is the night's folder (lights and calibration),
/// <c>TIANWEN_COMET_10P_TRACK</c> the file <c>tianwen comet track</c> wrote for it, and <c>TIANWEN_COMET_10P_OUT</c>
/// an output folder to keep between runs: the starless plates are cached under its <c>starless/</c>, so a re-run costs
/// the integration and not the 49 minutes of per-frame removal (on the Snapdragon laptop's GPU).</para>
/// <para><b>Why it exists.</b> On 2026-11-25 three runs of the same night failed three ways, each into a plausible
/// master: a model holding trail flux dug dark streaks, a drizzled starless comet layer left a coloured ring, and a star
/// layer that picked TilePipelined was never subtracted at all and left no composite. The thresholds sit between what
/// each failure read and what a clean run reads; the comet plan has the table ("The first run through the workbench
/// step").</para>
/// </remarks>
[Collection("Imaging")]
public sealed class Comet10PAcceptanceProbe(ITestOutputHelper output)
{
    /// <summary>How far round the body the residue is read: 10P's coma reaches about this far in green.</summary>
    private const float ReachPx = 350f;

    [Fact(Timeout = 7_200_000)]
    [Trait("Category", "Heavy")]
    public async Task TenPsCompositeHasNoStreakAndNoRingAndIsWritten()
    {
        var root = Environment.GetEnvironmentVariable("TIANWEN_COMET_10P_ROOT") ?? "";
        var trackPath = Environment.GetEnvironmentVariable("TIANWEN_COMET_10P_TRACK") ?? "";
        var outDir = Environment.GetEnvironmentVariable("TIANWEN_COMET_10P_OUT") ?? "";
        Assert.SkipWhen(root.Length == 0 || trackPath.Length == 0 || outDir.Length == 0,
            "set TIANWEN_COMET_10P_ROOT, TIANWEN_COMET_10P_TRACK and TIANWEN_COMET_10P_OUT to run the 10P acceptance probe");
        Assert.SkipUnless(RcAstroTestSupport.ProductAvailable("sxt", out var skip), skip);
        var ct = TestContext.Current.CancellationToken;
        CometTrack.TryRead(trackPath, out var track, out var refusal).ShouldBeTrue(refusal);

        var options = new StackingOptions(DataRoot: root, OutputDir: outDir)
        {
            CometTrack = track,
            RemoveStarsPerFrame = true,
        };
        var remover = new RcAstroStarRemover(new RcAstroCli());
        var pipeline = new StackingPipeline(options, new XunitLogger(output), await SharedCatalogDB.InitAsync(ct), starRemover: remover);
        var results = new List<GroupResult>();
        await foreach (var result in pipeline.RunAsync(ct))
        {
            results.Add(result);
        }

        var group = results.ShouldHaveSingleItem("one night, one group");
        var starLayer = group.StarLayerFitsPath.ShouldNotBeNull("the star layer is written beside the comet master");
        var composite = Directory.EnumerateFiles(outDir, "master_*_composite.fits").ShouldHaveSingleItem(
            "the composite is written: it is not where the star layer was never subtracted");

        var reading = CometResidue.MeasureFiles(starLayer, composite, ReachPx);

        output.WriteLine($"10P: {reading}");
        reading.StarCorePixels.ShouldBeGreaterThan(0, "premise: the stars inside the coma were found and masked");
        // Run 1's dark streaks read -3.69 (no per-frame removal, a model with trail flux in it); a clean run -0.92.
        reading.StarLayer.LuminanceLow.ShouldBeGreaterThan(-2f, "a dark streak beside the bright stars: the model held trail flux");
        // Run 2's ring read +4.88 in the star layer (green) and -7.94 in the model (magenta); a clean run +1.32 and -1.82.
        reading.StarLayer.ChromaHigh.ShouldBeLessThan(2.5f, "green trails beside the bright stars: the comet layer kept the remover's residue");
        reading.Model.ChromaLow.ShouldBeGreaterThan(-3.5f, "magenta trails added back with the body: the comet layer kept the remover's residue");
    }
}
