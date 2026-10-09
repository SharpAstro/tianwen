using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A broadband SPCC balance is multiplied into the LINEAR master before the enhance, PixInsight's order (SPCC, then the
/// deblur, the gradient, the stars and the denoise), and every file and document that carries it says so
/// (<see cref="ColourCalibration.Applied"/>, FITS <c>WBAPPLD</c>), so nothing applies it twice or solves SPCC again on
/// stars the enhance has reshaped. Solved on the enhanced master instead, the fit read 5 to 26 percent bluer on Centaurus A
/// (BlurX and NoiseX moving the stars it measures). The sky-background estimate and a fit through a line-selective filter
/// stay display multipliers, as before.
/// </summary>
[Collection("Stacking")]
public sealed class SpccBeforeEnhanceTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static readonly ColourCalibration Spcc = new(1.5f, 1f, 0.75f, ColourCalibrationSource.Spcc);

    /// <summary>Returns a copy, as a real enhancer does, carrying its input's metadata.</summary>
    private sealed class CopyEnhancer : IStarRemover, IDenoiseEnhancer, IGradientCorrector
    {
        public string Name => "Test/Copy";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input.Affine(1.0, 0.0));
    }

    /// <summary>Returns a copy WITHOUT its input's metadata, as RC-Astro does: it reads its output back from a file of its
    /// own, so nothing it writes says the balance is in the pixels.</summary>
    private sealed class MetadataDroppingEnhancer : IDenoiseEnhancer, IGradientCorrector
    {
        public string Name => "Test/DropsMetadata";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input.Affine(1.0, 0.0, new ImageMeta { SensorType = input.ImageMeta.SensorType }));
    }

    private static Image Rgb(float r, float g, float b, float pedestal = 0f, ImageMeta? meta = null, int size = 32)
    {
        var planes = new float[3][,];
        ReadOnlySpan<float> levels = [r, g, b];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // A little texture, so a statistic over the plane is not degenerate.
                    plane[y, x] = levels[c] + (0.002f * ((x + y + c) % 5));
                }
            }
            planes[c] = plane;
        }
        return new Image(planes, BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal,
            meta ?? new ImageMeta { Instrument = "synth", SensorType = SensorType.Color });
    }

    /// <summary>An OSC capture whose filter the curve database can read, broadband or line-selective.</summary>
    private static ImageMeta Osc(string filterName) => new()
    {
        Instrument = "SVBONY SV605CC",
        SensorType = SensorType.Color,
        Filter = Filter.FromName(filterName),
    };

    private const string Broadband = "Optolong L-Quad Enhance";
    private const string LineSelective = "Optolong L-Ultimate 3nm";

    [Fact]
    public void TheBalanceGoesInAboutThePedestalAndNoChannelIsLifted()
    {
        var frame = Rgb(0.30f, 0.20f, 0.10f, pedestal: 0.05f);

        var balanced = frame.WithWhiteBalanceApplied(Spcc);

        // Divided by the largest gain: (1.5, 1, 0.75) is applied as (1, 2/3, 1/2).
        float[] gains = [1f, 2f / 3f, 0.5f];
        for (var c = 0; c < 3; c++)
        {
            var before = frame.GetChannelSpan(c);
            var after = balanced.GetChannelSpan(c);
            for (var i = 0; i < before.Length; i++)
            {
                after[i].ShouldBe(((before[i] - 0.05f) * gains[c]) + 0.05f, 1e-6f);
            }
        }
        balanced.Pedestal.ShouldBe(frame.Pedestal);
        balanced.MaxValue.ShouldBeLessThanOrEqualTo(frame.MaxValue, "no channel may be lifted past its own level");
        balanced.ImageMeta.ColourCalibration.ShouldBe(new ColourCalibration(1f, 2f / 3f, 0.5f, ColourCalibrationSource.Spcc, Applied: true));
        frame.ImageMeta.ColourCalibration.ShouldBeNull("the source is never touched");
    }

    [Fact]
    public void AFrameOfOneChannelHasNoBalanceToTake()
    {
        var mono = new Image([new float[4, 4]], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });

        Should.Throw<ArgumentException>(() => mono.WithWhiteBalanceApplied(Spcc));
    }

    /// <summary>The flag is a FITS card, read in the one place a header becomes an <see cref="ImageMeta"/>, so both read
    /// paths agree on it; absent, a calibration is a multiplier still to apply, as every master written before reads.</summary>
    [Fact]
    public void TheAppliedFlagRoundTripsThroughBothReadPaths()
    {
        var dir = _folders.Create("spcc-applied-").FullName;
        var applied = Path.Combine(dir, "applied.fits");
        var pending = Path.Combine(dir, "pending.fits");
        Rgb(0.3f, 0.2f, 0.1f).WithWhiteBalanceApplied(Spcc).WriteToFitsFile(applied);
        Rgb(0.3f, 0.2f, 0.1f, meta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Color, ColourCalibration = Spcc })
            .WriteToFitsFile(pending);

        Image.TryReadFitsFile(applied, out var appliedImage).ShouldBeTrue();
        Image.TryReadFitsHeader(applied, out var appliedHeader).ShouldBeTrue();
        appliedImage.ImageMeta.ColourCalibration.ShouldBe(new ColourCalibration(1f, 2f / 3f, 0.5f, ColourCalibrationSource.Spcc, Applied: true));
        appliedHeader.Meta.ColourCalibration.ShouldBe(appliedImage.ImageMeta.ColourCalibration);

        Image.TryReadFitsFile(pending, out var pendingImage).ShouldBeTrue();
        Image.TryReadFitsHeader(pending, out var pendingHeader).ShouldBeTrue();
        pendingImage.ImageMeta.ColourCalibration.ShouldBe(Spcc);
        pendingHeader.Meta.ColourCalibration.ShouldBe(Spcc);
    }

    /// <summary>
    /// The head step: the enhancers see the balanced pixels, and every plate the run returns says the balance is in them,
    /// INCLUDING one from an enhancer that does not carry its input's metadata through (RC-Astro), which the pipeline
    /// restamps. Without that, a renderer would read the plate as unbalanced and multiply the balance in a second time.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryPlateOfABalancedRunSaysTheBalanceIsInItsPixels(bool enhancerDropsMetadata)
    {
        IGradientCorrector corrector = enhancerDropsMetadata ? new MetadataDroppingEnhancer() : new CopyEnhancer();
        IDenoiseEnhancer denoiser = enhancerDropsMetadata ? new MetadataDroppingEnhancer() : new CopyEnhancer();
        var pipeline = new SharpenPipeline(denoiser: denoiser, gradientCorrector: corrector);
        var source = Rgb(0.30f, 0.20f, 0.10f);
        var program = pipeline.CanonicalProgram(source, EnhanceOptions.Default) with { WhiteBalance = Spcc };

        program.ToSteps()[0].ShouldBe(new WhiteBalanceStep(Spcc), "the balance goes in before any enhancer runs");
        var result = await pipeline.ProcessAsync(new SharpenRequest(source, program.ToSteps()), EnhanceOptions.Default,
            cancellationToken: TestContext.Current.CancellationToken);

        var final = result.Final.ShouldNotBeNull();
        var applied = new ColourCalibration(1f, 2f / 3f, 0.5f, ColourCalibrationSource.Spcc, Applied: true);
        result.AppliedWhiteBalance.ShouldBe(applied);
        final.ImageMeta.ColourCalibration.ShouldBe(applied);
        // The copy enhancers change nothing, so the result IS the balanced source.
        final.GetChannelSpan(1)[0].ShouldBe(source.GetChannelSpan(1)[0] * (2f / 3f), 1e-6f);
        final.GetChannelSpan(2)[0].ShouldBe(source.GetChannelSpan(2)[0] * 0.5f, 1e-6f);
        source.ImageMeta.ColourCalibration.ShouldBeNull("the caller's source is never touched");
    }

    [Fact]
    public async Task AWhiteBalanceAnywhereButFirstIsRefused()
    {
        var pipeline = new SharpenPipeline(gradientCorrector: new CopyEnhancer());
        var source = Rgb(0.3f, 0.2f, 0.1f);

        await Should.ThrowAsync<ArgumentException>(() => pipeline.ProcessAsync(
            new SharpenRequest(source, [new GradientCorrectionStep(), new WhiteBalanceStep(Spcc)]),
            TestContext.Current.CancellationToken));
    }

    /// <summary>The deblur is the first ENHANCER, so it may follow the balance and nothing else.</summary>
    [Fact]
    public void TheProgramPutsTheBalanceAheadOfTheDeblur()
    {
        var program = LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur: true)) with { WhiteBalance = Spcc };

        var steps = program.ToSteps();
        steps[0].ShouldBeOfType<WhiteBalanceStep>();
        steps[1].ShouldBeOfType<DeblurStep>();
        LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur: true)).ToSteps()[0].ShouldBeOfType<DeblurStep>(
            "a program is never balanced by itself: only a host that solved SPCC sets it");
    }

    /// <summary>A frame whose balance is in its pixels is shown as it is: an identity balance (still a calibration, so Auto
    /// renders Linked) and no fit of its own, which would be SPCC on enhanced stars.</summary>
    [Fact]
    public async Task AFrameWhoseBalanceIsInItsPixelsRendersWithNoneOfItsOwn()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        var frame = Rgb(0.30f, 0.20f, 0.10f, meta: Osc(Broadband), size: 64).WithWhiteBalanceApplied(Spcc);
        var renderer = new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance);

        var render = await renderer.RenderAsync(frame, frame.ImageMeta, wcs: null, statsSource: null, outputPath: "",
            ct: TestContext.Current.CancellationToken);

        render.WhiteBalance.ShouldBe((1f, 1f, 1f));
        render.Spcc.ShouldBeNull();
        render.Uniforms.Mode.ShouldBe(StretchMode.Linked);
    }

    /// <summary>
    /// A balance a file STATES goes into the pixels only when it is an SPCC fit, not yet applied, through a throughput
    /// the metadata shows to be broadband, which is the throughput SPCC integrates: a colour sensor always has one (its
    /// CFA curves), so a colour frame naming no filter reads as unfiltered, as its SPCC fit did; a frame no curve
    /// describes at all (the last row: a monochrome sensor the database does not know) has none to show.
    /// </summary>
    [Theory]
    [InlineData(ColourCalibrationSource.Spcc, false, Broadband, true)]
    [InlineData(ColourCalibrationSource.Spcc, false, LineSelective, false)]
    [InlineData(ColourCalibrationSource.SkyBackground, false, Broadband, false)]
    [InlineData(ColourCalibrationSource.Spcc, true, Broadband, false)]
    [InlineData(ColourCalibrationSource.Spcc, false, "", true)]
    [InlineData(ColourCalibrationSource.Spcc, false, null, false)]
    public async Task AStatedBalanceIsAppliedOnlyWhenItIsABroadbandSpccFit(
        ColourCalibrationSource source, bool applied, string? filterName, bool expectApplied)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        var stated = new ColourCalibration(1.5f, 1f, 0.75f, source, applied);
        var meta = filterName switch
        {
            null => new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome },
            "" => new ImageMeta { Instrument = "synth", SensorType = SensorType.Color },
            _ => Osc(filterName),
        };

        var toApply = await MasterPreviewRenderer.StatedToApplyAsync(stated, meta, TestContext.Current.CancellationToken);

        (toApply is not null).ShouldBe(expectApplied);
    }

    /// <summary>
    /// The stacking path end to end, with the caller's triple (<c>--inherit-wb</c>, the one way to have SPCC without a
    /// catalogue in a test): a broadband master's ENHANCED files carry the balance in their pixels and say so, while the
    /// raw master keeps its unbalanced pixels and the triple still to apply.
    /// </summary>
    [Theory]
    [InlineData(MasterRenderOutputs.None)]
    [InlineData(MasterRenderOutputs.PreviewPng)]
    public async Task ABroadbandMastersEnhancedFilesCarryTheBalanceAndTheRawMasterKeepsItsOwn(MasterRenderOutputs outputs)
    {
        var (raw, sharpened) = await StackAndEnhanceAsync(Broadband, outputs);

        raw.ImageMeta.ColourCalibration.ShouldBe(Spcc);
        raw.GetChannelSpan(2)[0].ShouldBe(0.10f, 1e-6f, "the raw master's pixels stay as integrated");

        sharpened.ImageMeta.ColourCalibration.ShouldBe(new ColourCalibration(1f, 2f / 3f, 0.5f, ColourCalibrationSource.Spcc, Applied: true));
        sharpened.GetChannelSpan(2)[0].ShouldBe(0.05f, 1e-6f);
    }

    /// <summary>The control: through a line-selective filter SPCC's triple is a fit of nothing, so it never enters the
    /// linear data and the enhanced master carries it as a display multiplier, as before. With a preview too, since the
    /// render is HANDED the linear solve's triple and its own diagnostics cannot say it was SPCC: the enhanced master's
    /// cards once read the source back off the render and called it the sky-background estimate.</summary>
    [Theory]
    [InlineData(MasterRenderOutputs.None)]
    [InlineData(MasterRenderOutputs.PreviewPng)]
    public async Task ALineSelectiveMastersBalanceStaysADisplayMultiplier(MasterRenderOutputs outputs)
    {
        var (raw, sharpened) = await StackAndEnhanceAsync(LineSelective, outputs);

        raw.ImageMeta.ColourCalibration.ShouldBe(Spcc);
        sharpened.ImageMeta.ColourCalibration.ShouldBe(Spcc);
        sharpened.GetChannelSpan(2)[0].ShouldBe(0.10f, 1e-6f);
    }

    private async Task<(Image Raw, Image Sharpened)> StackAndEnhanceAsync(string filterName, MasterRenderOutputs outputs)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        var dir = _folders.Create("spcc-before-enhance-").FullName;
        var masterPath = Path.Combine(dir, "master_test.fits");
        // A flat frame, so a pixel read anywhere is the channel's level.
        var master = new Image([Flat(0.30f), Flat(0.20f), Flat(0.10f)], BitDepth.Float32, 1f, 0f, 0f, Osc(filterName));
        var rejection = new Image([new float[32, 32]], BitDepth.Float32, 1f, 0f, 0f, master.ImageMeta);
        var copy = new CopyEnhancer();
        var processor = new MasterPostProcessor(NullLogger.Instance, catalogDb: null,
            new SharpenPipeline(starRemover: copy, denoiser: copy, gradientCorrector: copy));

        await processor.WriteMasterAsync(
            new IntegrationResult(master, rejection, FrameCount: 1, TotalRejections: 0, MeanRejectionRate: 0.0),
            masterPath, searchHint: null, imageDim: null, refMeta: master.ImageMeta,
            autocropRect: new PixelRect(0, 0, 32, 32), strategy: IntegrationStrategyKind.InRamAllFrames,
            enhance: true, enhanceBlend: 1f, splitPlates: false, enhanceOptions: EnhanceOptions.Default,
            outputs: outputs, inheritedWhiteBalance: Spcc, ct: TestContext.Current.CancellationToken);

        Image.TryReadFitsFile(masterPath, out var raw).ShouldBeTrue();
        Image.TryReadFitsFile(Path.Combine(dir, "master_test_sharpened.fits"), out var sharpened).ShouldBeTrue();
        return (raw, sharpened);

        static float[,] Flat(float level)
        {
            var plane = new float[32, 32];
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    plane[y, x] = level;
                }
            }
            return plane;
        }
    }

    /// <summary>The viewer opens a file whose balance is in its pixels with an identity calibration, which also stops
    /// the calibration pass fitting one, and the enhance has nothing left to apply to it.</summary>
    [Fact]
    public async Task AFileWhoseBalanceIsInItsPixelsOpensWithNoneOfItsOwn()
    {
        var document = await AstroImageDocument.AdoptImageAsync(Rgb(0.3f, 0.2f, 0.1f).WithWhiteBalanceApplied(Spcc),
            DebayerAlgorithm.None, wcs: null, filePath: "master_sharpened.fits", cancellationToken: TestContext.Current.CancellationToken);

        document.IsColourInPixels.ShouldBeTrue();
        document.ColorCalibration.ShouldBe((1f, 1f, 1f));
        document.PhotometricColorCalibration.ShouldBeNull();
    }

    /// <summary>What the viewer's enhance multiplies in first: the document's own SPCC fit through a broadband filter,
    /// and never the sky-background estimate nor a line-selective fit.</summary>
    [Theory]
    [InlineData(AstroImageDocument.SpccMethod, false, true)]
    [InlineData(AstroImageDocument.SpccMethod, true, false)]
    [InlineData("Sky background", false, false)]
    public async Task TheViewerAppliesOnlyABroadbandSpccFit(string method, bool narrowband, bool expectApplied)
    {
        var document = await AstroImageDocument.AdoptImageAsync(Rgb(0.3f, 0.2f, 0.1f), DebayerAlgorithm.None,
            wcs: null, filePath: "master.fits", cancellationToken: TestContext.Current.CancellationToken);
        document.InheritColorCalibration((1.5f, 1f, 0.75f), new ColorCalibrationSummary(method, 1.5f, 1f, 0.75f, 100, null), narrowband);

        (document.PhotometricColorCalibration is { } wb ? wb : (ColourCalibration?)null)
            .ShouldBe(expectApplied ? Spcc : null);
    }

    /// <summary>The viewer's enhance end to end: a document calibrated by a broadband SPCC fit comes back with the
    /// balance in its pixels and no multiplier of its own.</summary>
    [Fact]
    public async Task TheViewersEnhanceAppliesTheDocumentsSpccFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var copy = new CopyEnhancer();
        var pipeline = new SharpenPipeline(starRemover: copy, denoiser: copy, gradientCorrector: copy);
        var source = await AstroImageDocument.AdoptImageAsync(Rgb(0.3f, 0.2f, 0.1f), DebayerAlgorithm.None,
            wcs: null, filePath: "master.fits", cancellationToken: ct);
        source.InheritColorCalibration((1.5f, 1f, 0.75f), new ColorCalibrationSummary(AstroImageDocument.SpccMethod, 1.5f, 1f, 0.75f, 100, null));

        var enhanced = (await EnhanceActions.EnhanceAsync(source, new ViewerState(), pipeline, EnhanceOptions.Default,
            DebayerAlgorithm.None, crop: null, ct)).ShouldNotBeNull();

        enhanced.IsColourInPixels.ShouldBeTrue();
        enhanced.ColorCalibration.ShouldBe((1f, 1f, 1f));
    }

    /// <summary>The enhanced master beside its raw master in one folder matches it on every other count; sharing the
    /// raw master's calibration would multiply it in a second time, so the two never share a display anchor.</summary>
    [Fact]
    public void AFrameWithItsBalanceInItsPixelsNeverSharesACalibrationWithOneWithout()
    {
        var raw = Rgb(0.3f, 0.2f, 0.1f);
        var balanced = raw.WithWhiteBalanceApplied(Spcc);

        FrameShape.Of(raw).IsComparableTo(FrameShape.Of(balanced)).ShouldBeFalse();
        FrameShape.Of(balanced).IsComparableTo(FrameShape.Of(balanced)).ShouldBeTrue();
    }
}
