using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Validates the <c>--enhance</c> integration in <see cref="MasterPostProcessor"/>:
/// when an AI <see cref="SharpenPipeline"/> is supplied and the flag is set, the
/// post-processor must write <c>_sharpened.fits</c> and
/// <c>_sharpened_autocrop.fits</c> sibling FITS files alongside the canonical
/// linear masters. The raw <c>master.fits</c> + <c>master_autocrop.fits</c>
/// must remain untouched.
/// </summary>
[Collection("Stacking")]
public class StackEnhanceTests
{
    /// <summary>
    /// Identity enhancer that returns its input unchanged. Stand-in for any
    /// IImageEnhancer role; lets us drive MasterPostProcessor's enhance path
    /// without needing real ONNX model files. Output equals input -> the
    /// sharpened FITS should byte-equal the master FITS for this test.
    /// </summary>
    private sealed class IdentityEnhancer(string name) : IStarRemover, IStellarSharpener, INonStellarDeconvolver, IDenoiseEnhancer, IGradientCorrector
    {
        public string Name => name;
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }

    private static Image SyntheticRgb(int w, int h, float fill)
    {
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                r[y, x] = fill;
                g[y, x] = fill;
                b[y, x] = fill;
            }
        var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(60),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([r, g, b], BitDepth.Float32, 1.0f, 0f, 0f, meta);
    }

    private static IntegrationResult MakeResult(Image master)
    {
        // RejectionMap is a single-channel non-null Image per the
        // IntegrationResult contract; build a trivial one matching shape.
        var (_, w, h) = master.Shape;
        var meta = master.ImageMeta;
        var rejection = new Image([new float[h, w]], BitDepth.Float32, 1.0f, 0f, 0f, meta);
        return new IntegrationResult(master, rejection, FrameCount: 1, TotalRejections: 0, MeanRejectionRate: 0.0);
    }

    [Fact]
    public async Task WriteMasterAsync_WithEnhance_ProducesSharpenedSiblings()
    {
        // Identity enhancers + canonical step list = sharpened FITS is a
        // structural copy of the master (same pixels in linear space). We
        // verify the FILES exist and load back to the same shape -- byte
        // equality is not asserted because IntegrationFitsWriter normalises
        // headers + the recombine math (additive) is bit-stable but not
        // necessarily byte-identical to the source.
        var tmp = Directory.CreateTempSubdirectory("StackEnhanceTests_");
        try
        {
            var masterPath = Path.Combine(tmp.FullName, "master_test.fits");
            var master = SyntheticRgb(64, 64, 0.05f);
            var result = MakeResult(master);

            var sharpenPipeline = new SharpenPipeline(
                starRemover: new IdentityEnhancer("star"),
                stellarSharpener: new IdentityEnhancer("stellar"),
                nonStellarDeconvolver: new IdentityEnhancer("deconv"),
                denoiser: new IdentityEnhancer("denoise"),
                gradientCorrector: new IdentityEnhancer("gradient"));

            var processor = new MasterPostProcessor(
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                catalogDb: null,
                sharpenPipeline: sharpenPipeline);

            // Autocrop = inset 4 px on each side -> proper sub-rectangle so
            // the autocrop-sibling path runs.
            var autocrop = new Rectangle(4, 4, 56, 56);

            var postResult = await processor.WriteMasterAsync(
                result, masterPath, searchHint: null, imageDim: null, refMeta: master.ImageMeta,
                autocropRect: autocrop, strategy: IntegrationStrategyKind.InRamAllFrames,
                enhance: true, enhanceBlend: 1.0f, splitPlates: false, enhanceOptions: EnhanceOptions.Default,
                renderPreviewPng: false, ct: TestContext.Current.CancellationToken);

            // The post-processor returns the same Master back (potentially
            // with MaxValue patched). SolvedWcs is null here because no
            // catalog DB was supplied -> no plate-solve.
            postResult.SolvedWcs.ShouldBeNull();
            postResult.Result.Master.ShouldNotBeNull();
            postResult.Result.Master.Shape.ShouldBe(master.Shape);

            // 4 files: raw master + raw autocrop + sharpened master + sharpened autocrop.
            File.Exists(masterPath).ShouldBeTrue($"raw master at {masterPath}");
            File.Exists(Path.Combine(tmp.FullName, "master_test_autocrop.fits"))
                .ShouldBeTrue("raw autocrop sibling");
            File.Exists(Path.Combine(tmp.FullName, "master_test_sharpened.fits"))
                .ShouldBeTrue("sharpened master sibling -- --enhance wiring is broken");
            File.Exists(Path.Combine(tmp.FullName, "master_test_sharpened_autocrop.fits"))
                .ShouldBeTrue("sharpened autocrop sibling -- crop-of-enhanced-master path is broken");

            // Round-trip the sharpened FITS to verify dimensions match the
            // canonical sibling. Identity enhancer => content matches master.
            Image.TryReadFitsFile(Path.Combine(tmp.FullName, "master_test_sharpened.fits"), out var sharpened, out _)
                .ShouldBeTrue();
            sharpened!.Shape.ShouldBe(master.Shape);

            Image.TryReadFitsFile(Path.Combine(tmp.FullName, "master_test_sharpened_autocrop.fits"), out var sharpenedCrop, out _)
                .ShouldBeTrue();
            sharpenedCrop!.Width.ShouldBe(autocrop.Width);
            sharpenedCrop.Height.ShouldBe(autocrop.Height);
        }
        finally
        {
            try { tmp.Delete(recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task WriteMasterAsync_WithoutEnhance_OmitsSharpenedSiblings()
    {
        // Default path (enhance=false) must be byte-identical to the
        // pre-PR behaviour: only the master + autocrop FITS appear, no
        // sharpened siblings even when a SharpenPipeline is supplied.
        var tmp = Directory.CreateTempSubdirectory("StackEnhanceTests_");
        try
        {
            var masterPath = Path.Combine(tmp.FullName, "master_test.fits");
            var master = SyntheticRgb(64, 64, 0.05f);
            var result = MakeResult(master);

            var sharpenPipeline = new SharpenPipeline(
                starRemover: new IdentityEnhancer("star"),
                stellarSharpener: new IdentityEnhancer("stellar"),
                nonStellarDeconvolver: new IdentityEnhancer("deconv"),
                denoiser: new IdentityEnhancer("denoise"),
                gradientCorrector: new IdentityEnhancer("gradient"));

            var processor = new MasterPostProcessor(
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                catalogDb: null,
                sharpenPipeline: sharpenPipeline);

            var autocrop = new Rectangle(4, 4, 56, 56);

            var postResult = await processor.WriteMasterAsync(
                result, masterPath, searchHint: null, imageDim: null, refMeta: master.ImageMeta,
                autocropRect: autocrop, strategy: IntegrationStrategyKind.InRamAllFrames,
                enhance: false, enhanceBlend: 1.0f, splitPlates: false, enhanceOptions: EnhanceOptions.Default,
                renderPreviewPng: false, ct: TestContext.Current.CancellationToken);
            postResult.SolvedWcs.ShouldBeNull();

            File.Exists(masterPath).ShouldBeTrue();
            File.Exists(Path.Combine(tmp.FullName, "master_test_autocrop.fits")).ShouldBeTrue();
            File.Exists(Path.Combine(tmp.FullName, "master_test_sharpened.fits")).ShouldBeFalse(
                "sharpened sibling must NOT appear when enhance=false");
            File.Exists(Path.Combine(tmp.FullName, "master_test_sharpened_autocrop.fits")).ShouldBeFalse(
                "sharpened autocrop sibling must NOT appear when enhance=false");
        }
        finally
        {
            try { tmp.Delete(recursive: true); } catch { /* best-effort */ }
        }
    }
}
