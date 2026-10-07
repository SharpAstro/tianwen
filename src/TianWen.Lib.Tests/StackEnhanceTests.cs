using System;
using System.Collections.Concurrent;
using TianWen.Lib.Geometry;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using Microsoft.Extensions.Logging;
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
public class StackEnhanceTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

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

    /// <summary>
    /// Returns a COPY, as a real enhancer does (a step owns what it produces), so the whole-frame
    /// program's release of the plate it consumed can never reach its own result.
    /// </summary>
    private sealed class CopyEnhancer(string name) : IDenoiseEnhancer, IGradientCorrector
    {
        public string Name => name;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var (channels, w, h) = input.Shape;
            var data = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var src = input.GetChannelSpan(c);
                var plane = new float[h, w];
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        plane[y, x] = src[y * w + x];
                    }
                }
                data[c] = plane;
            }
            return Task.FromResult(new Image(data, input.BitDepth, input.MaxValue, input.MinValue, input.Pedestal, input.ImageMeta));
        }
    }

    /// <summary>Keeps every entry logged: a flag that writes nothing has only the log to say why.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new ConcurrentQueue<(LogLevel Level, string Message)>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
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

    private static string? ReadHeaderString(string fitsPath, string card)
    {
        using var bf = new nom.tam.util.BufferedFile(fitsPath, FileAccess.Read, FileShare.Read, 1024);
        using var fits = new nom.tam.fits.Fits(bf, false);
        var hdu = fits.ReadHDUHeaderOnly();
        hdu.ShouldNotBeNull();
        return hdu.Header.GetStringValue(card);
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
        var tmp = _folders.Create("StackEnhanceTests_");
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
        var autocrop = new PixelRect(4, 4, 56, 56);

        var postResult = await processor.WriteMasterAsync(
            result, masterPath, searchHint: null, imageDim: null, refMeta: master.ImageMeta,
            autocropRect: autocrop, strategy: IntegrationStrategyKind.InRamAllFrames,
            enhance: true, enhanceBlend: 1.0f, splitPlates: false, enhanceOptions: EnhanceOptions.Default,
            outputs: MasterRenderOutputs.None, ct: TestContext.Current.CancellationToken);

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

        // The sharpened sibling names its modifier (SWMODIFY, the MaxIm DL card); the raw
        // master, which nothing modified, must not.
        ReadHeaderString(masterPath, "SWMODIFY").ShouldBeNull();
        ReadHeaderString(Path.Combine(tmp.FullName, "master_test_sharpened.fits"), "SWMODIFY")
            .ShouldBe(SharpenPipeline.SoftwareModifier);
        ReadHeaderString(Path.Combine(tmp.FullName, "master_test_sharpened_autocrop.fits"), "SWMODIFY")
            .ShouldBe(SharpenPipeline.SoftwareModifier);
    }

    [Fact]
    public async Task WriteMasterAsync_WithoutEnhance_OmitsSharpenedSiblings()
    {
        // Default path (enhance=false) must be byte-identical to the
        // pre-PR behaviour: only the master + autocrop FITS appear, no
        // sharpened siblings even when a SharpenPipeline is supplied.
        var tmp = _folders.Create("StackEnhanceTests_");
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

        var autocrop = new PixelRect(4, 4, 56, 56);

        var postResult = await processor.WriteMasterAsync(
            result, masterPath, searchHint: null, imageDim: null, refMeta: master.ImageMeta,
            autocropRect: autocrop, strategy: IntegrationStrategyKind.InRamAllFrames,
            enhance: false, enhanceBlend: 1.0f, splitPlates: false, enhanceOptions: EnhanceOptions.Default,
            outputs: MasterRenderOutputs.None, ct: TestContext.Current.CancellationToken);
        postResult.SolvedWcs.ShouldBeNull();

        File.Exists(masterPath).ShouldBeTrue();
        File.Exists(Path.Combine(tmp.FullName, "master_test_autocrop.fits")).ShouldBeTrue();
        File.Exists(Path.Combine(tmp.FullName, "master_test_sharpened.fits")).ShouldBeFalse(
            "sharpened sibling must NOT appear when enhance=false");
        File.Exists(Path.Combine(tmp.FullName, "master_test_sharpened_autocrop.fits")).ShouldBeFalse(
            "sharpened autocrop sibling must NOT appear when enhance=false");
    }

    /// <summary>
    /// <c>--split-plates</c> exports the split program's stars / starless lineage, and without a star
    /// remover the program is whole-frame and has none. The enhanced master is still written, and the
    /// log SAYS why there are no plates: before, the flag wrote nothing and said nothing, which on a
    /// host without RC-Astro (every host, since the SAS tier went) reads as a bug.
    /// </summary>
    [Fact]
    public async Task WriteMasterAsync_SplitPlatesWithoutAStarRemover_WritesTheWholeFrameMasterAndSaysWhy()
    {
        var tmp = _folders.Create("StackEnhanceTests_");
        var masterPath = Path.Combine(tmp.FullName, "master_test.fits");
        var master = SyntheticRgb(64, 64, 0.05f);
        var result = MakeResult(master);

        // Gradient + denoise and nothing else: what a host without RC-Astro serves.
        var sharpenPipeline = new SharpenPipeline(
            denoiser: new CopyEnhancer("denoise"),
            gradientCorrector: new CopyEnhancer("gradient"));
        var logger = new RecordingLogger();
        var processor = new MasterPostProcessor(logger, catalogDb: null, sharpenPipeline: sharpenPipeline);

        await processor.WriteMasterAsync(
            result, masterPath, searchHint: null, imageDim: null, refMeta: master.ImageMeta,
            autocropRect: new PixelRect(4, 4, 56, 56), strategy: IntegrationStrategyKind.InRamAllFrames,
            enhance: true, enhanceBlend: 1.0f, splitPlates: true, enhanceOptions: EnhanceOptions.Default,
            outputs: MasterRenderOutputs.None, ct: TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(tmp.FullName, "master_test_sharpened.fits"))
            .ShouldBeTrue("the whole-frame enhance still writes its master");
        logger.Entries.ShouldContain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("[split-plates] skipped") && e.Message.Contains("no star remover"),
            "a --split-plates run with no star remover must say why it wrote no plates");
        logger.Entries.ShouldNotContain(e => e.Message.Contains("[enhance] failed"),
            "the whole-frame program must run, not fail over the missing split");
    }
}
