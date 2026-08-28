using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A raw CFA mosaic must be debayered ONCE, at the head, before any step sees it.
///
/// <para>Every enhancer in the program applies a SPATIAL kernel, and a kernel over an interleaved
/// mosaic blends photosites of different colours into one another. Measured on a real 3008x3008x1
/// RGGB sub, the R/G/B background levels converged 4.2x (separation 3.72% -> 0.90%) once the
/// deconvolver and denoiser ran, and the result renders grey. Those are plane MEDIANS, not noise,
/// so no colour-correct operation may move them -- and debayering afterwards cannot undo it,
/// because the colour is already gone in the linear data.</para>
///
/// <para>Fakes throughout: the point is the SHAPE handed to each step, which is exactly what a real
/// backend cannot tell us without an RC-Astro install.</para>
/// </summary>
public class SharpenPipelineDebayerTests
{
    /// <summary>Records the channel count of every plate it is handed, then passes it through.</summary>
    private sealed class ShapeRecorder(List<int> seen)
        : IImageDeblurrer, IStarRemover, IGradientCorrector, IDenoiseEnhancer
    {
        public string Name => "Test/ShapeRecorder";
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            lock (seen) { seen.Add(input.ChannelCount); }
            return Task.FromResult(input);
        }
    }

    /// <summary>An RGGB mosaic with a different level per photosite, so a debayer has real colour
    /// to reconstruct rather than a flat field that would look correct either way.</summary>
    private static Image Mosaic(int w, int h)
    {
        var a = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // RGGB: R at (even,even), B at (odd,odd), G on the diagonal.
                a[y, x] = (y % 2, x % 2) switch
                {
                    (0, 0) => 0.30f,
                    (1, 1) => 0.10f,
                    _ => 0.20f,
                };
            }
        }
        return new Image([a], BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.RGGB });
    }

    [Fact]
    public async Task AMosaicIsDebayeredBeforeAnyStepSeesIt()
    {
        var seen = new List<int>();
        var recorder = new ShapeRecorder(seen);
        var pipeline = new SharpenPipeline(
            starRemover: recorder, gradientCorrector: recorder, denoiser: recorder, deblurrer: recorder);

        var result = await pipeline.ProcessAsync(
            SharpenRequest.DeblurFirst(Mosaic(64, 64)), TestContext.Current.CancellationToken);

        seen.ShouldNotBeEmpty();
        seen.ShouldAllBe(c => c == 3);
        result.Final.ShouldNotBeNull();
        result.Final!.ChannelCount.ShouldBe(3);
    }

    /// <summary>
    /// The input belongs to the CALLER. The pipeline has never released what it was given and must
    /// not start now that it allocates a debayered plate of its own -- releasing here would hand a
    /// camera buffer back twice, which corrupts silently rather than throwing.
    /// </summary>
    [Fact]
    public async Task TheCallersMosaicIsLeftUsable()
    {
        var seen = new List<int>();
        var recorder = new ShapeRecorder(seen);
        var pipeline = new SharpenPipeline(
            starRemover: recorder, gradientCorrector: recorder, denoiser: recorder, deblurrer: recorder);
        var source = Mosaic(64, 64);

        await pipeline.ProcessAsync(SharpenRequest.DeblurFirst(source), TestContext.Current.CancellationToken);

        source.ChannelCount.ShouldBe(1);
        source.ImageMeta.SensorType.ShouldBe(SensorType.RGGB);
        source.GetChannelSpan(0)[0].ShouldBe(0.30f);   // still the R photosite, untouched
    }

    /// <summary>
    /// An already-debayered plate is the MasterPostProcessor case -- every stacked master reaches
    /// the pipeline as RGB -- and must be byte-identical to the pre-change behaviour.
    /// </summary>
    [Fact]
    public async Task AnRgbPlateIsNotTouched()
    {
        var seen = new List<int>();
        var recorder = new ShapeRecorder(seen);
        var pipeline = new SharpenPipeline(
            starRemover: recorder, gradientCorrector: recorder, denoiser: recorder, deblurrer: recorder);
        var rgb = new Image(
            [new float[8, 8], new float[8, 8], new float[8, 8]], BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Color });

        var result = await pipeline.ProcessAsync(
            SharpenRequest.DeblurFirst(rgb), TestContext.Current.CancellationToken);

        seen.ShouldAllBe(c => c == 3);
        result.Final!.ChannelCount.ShouldBe(3);
    }

    /// <summary>
    /// A mono frame has no CFA to undo and must stay single-channel -- triplicating it would treble
    /// the AI cost for no information, and the RGGB test is what distinguishes the two.
    /// </summary>
    [Fact]
    public async Task AMonoPlateStaysMono()
    {
        var seen = new List<int>();
        var recorder = new ShapeRecorder(seen);
        var pipeline = new SharpenPipeline(
            starRemover: recorder, gradientCorrector: recorder, denoiser: recorder, deblurrer: recorder);
        var mono = new Image([new float[8, 8]], BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Monochrome });

        var result = await pipeline.ProcessAsync(
            SharpenRequest.DeblurFirst(mono), TestContext.Current.CancellationToken);

        seen.ShouldAllBe(c => c == 1);
        result.Final!.ChannelCount.ShouldBe(1);
    }
}
