using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Workbench;

// Every settings property is settable and carries its default (see OperationSettings.cs).

/// <summary>
/// The settings of <see cref="EnhanceOperation"/>: which steps of the canonical enhance run, and at what strength.
/// A step is switched on here and still runs only where an enhancer serves it.
/// </summary>
public sealed record EnhanceSettings
{
    /// <summary>The enhancer backend for the roles RC-Astro can serve (<c>--ai-backend</c>).</summary>
    public EnhanceBackend Backend { get; set; } = EnhanceBackend.Auto;

    /// <summary>The gradient correction that heads the program (off: <c>--no-gradient</c>).</summary>
    public bool Gradient { get; set; } = true;

    /// <summary>The extractor the gradient step runs (<c>--gradient</c>).</summary>
    public GradientBackend GradientBackend { get; set; } = GradientBackend.Auto;

    /// <summary>The split and recombine math (<c>--mode</c>).</summary>
    public RecombineMode Mode { get; set; } = RecombineMode.Additive;

    /// <summary>A stellar-sharpening pass on the extracted stars (<c>--stellar-sharpen</c>).</summary>
    public bool StellarSharpen { get; set; }

    /// <summary>Its strength, 0 to 1 (<c>--stellar-blend</c>).</summary>
    public float StellarBlend { get; set; } = 1f;

    /// <summary>The deconvolution of the starless plate (off: <c>--no-deconv</c>).</summary>
    public bool Deconvolve { get; set; } = true;

    /// <summary>Its strength, 0 to 1 (<c>--deconv-blend</c>). Left out it is 1, and the step is dropped where a
    /// deblurrer serves; stated, the step is kept even then.</summary>
    public float? DeconvolveBlend { get; set; }

    /// <summary>The noise reduction (off: <c>--no-denoise</c>).</summary>
    public bool Denoise { get; set; } = true;

    /// <summary>Its strength, 0 to 1 (<c>--denoise-blend</c>).</summary>
    public float DenoiseBlend { get; set; } = 1f;

    /// <summary>Its weight bundle (<c>--denoise-variant</c>).</summary>
    public DenoiseVariant DenoiseVariant { get; set; } = DenoiseVariant.Default;

    /// <summary>Chromatic noise reduction on the stars plate (<c>--scnr</c>). Left out, it follows the program: on where
    /// a deblurrer serves, off otherwise.</summary>
    public ScnrMode? Scnr { get; set; }

    /// <summary>Its strength, 0 to 1 (<c>--scnr-amount</c>).</summary>
    public float ScnrAmount { get; set; } = 1f;

    /// <summary>The deblurrer's own sharpening, 0 to 1 (<c>--deblur-sharpen</c>); left out, the enhancer's default.</summary>
    public float? DeblurSharpen { get; set; }

    /// <summary>The denoiser's own strength, 0 to 1 (<c>--denoise-strength</c>); left out, the enhancer's default.</summary>
    public float? DenoiseStrength { get; set; }

    /// <summary>Denoise passes, run ungated (<c>--denoise-iterations</c>); left out, gated.</summary>
    public int? DenoiseIterations { get; set; }

    /// <summary>The blur TianWen's own deconvolver removes (<c>--deconv-kernel</c>), as that option takes it.</summary>
    public string? DeconvKernel { get; set; }

    /// <summary>The Moffat beta of <see cref="DeconvKernel"/> (<c>--deconv-beta</c>).</summary>
    public double? DeconvBeta { get; set; }

    /// <summary>The scale the frame is deconvolved at (<c>--deconv-resample</c>).</summary>
    public double? DeconvResample { get; set; }

    /// <summary>An SPCC white balance for the input as red, green, blue (<c>--white-balance</c>), winning over one the
    /// input states; empty for the input's own.</summary>
    public ImmutableArray<float> WhiteBalance { get; set; } = [];
}

/// <summary>What an enhance starts from, before its program is chosen.</summary>
/// <param name="Normalised">The input on the unit scale, as every enhancer wants it.</param>
/// <param name="Options">The backend, the tuning and the gradient extractor.</param>
/// <param name="Capabilities">Which roles serve THIS image under these options.</param>
public sealed record EnhanceStart(Image Normalised, EnhanceOptions Options, EnhanceCapabilities Capabilities);

/// <summary>
/// The canonical enhance (<see cref="SharpenPipeline"/>, <see cref="LinearEnhanceProgram"/>): with a star remover,
/// deblur, gradient, remove stars, denoise (and deconvolve) the starless plate, recombine; without one, whole-frame
/// deblur, gradient correction and denoise. Linear in, linear out. <c>image sharpen</c> calls <see cref="Start"/> and
/// <see cref="ProgramAsync"/>, and adds only its own stretches after them.
/// </summary>
/// <remarks>
/// <para><b>The step order is not written here.</b> <see cref="LinearEnhanceProgram.For"/> is the one place it lives;
/// the settings only say which steps are kept and at what strength, and none of them can switch on a step no role
/// serves.</para>
/// <para>What serves it is in the step's key: the enhancer registered for each role
/// (<see cref="SharpenPipeline.DescribeRoles"/>).</para>
/// <para>The per-plate stretches of <c>image sharpen</c> (<c>--dual-stretch</c>, <c>--stretch-mode</c>) and its
/// <c>--no-recombine</c> are not this operation: a stretch is an operation of its own.</para>
/// </remarks>
public sealed class EnhanceOperation(SharpenPipeline pipeline, ILogger<EnhanceOperation>? logger = null) : Operation<EnhanceSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "enhance";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Enhance",
        Verb: "image sharpen",
        OperationOutputKind.Stored,
        ExtraInputs: [],
        Settings:
        [
            new OperationSetting("backend", "The enhancer backend for the roles RC-Astro can serve: auto, forceRcAstro or tianWen."),
            new OperationSetting("gradient", "The gradient correction that heads the program."),
            new OperationSetting("gradientBackend", "The extractor the gradient step runs: auto, classical or graxpert."),
            new OperationSetting("mode", "The split and recombine math: additive (linear-light correct) or screen."),
            new OperationSetting("stellarSharpen", "A stellar-sharpening pass on the extracted stars, where a stellar sharpener serves and no deblurrer does."),
            new OperationSetting("stellarBlend", "Its strength, 0 to 1."),
            new OperationSetting("deconvolve", "The deconvolution of the starless plate."),
            new OperationSetting("deconvolveBlend", "Its strength, 0 to 1. Stated, the step is kept even where a deblurrer serves."),
            new OperationSetting("denoise", "The noise reduction: on the starless plate, or on the whole frame when no star remover serves."),
            new OperationSetting("denoiseBlend", "Its strength, 0 to 1."),
            new OperationSetting("denoiseVariant", "Its weight bundle. Only the default one is served today."),
            new OperationSetting("scnr", "Chromatic noise reduction on the stars plate: none, average or maximum. Left out, it follows the program.",
                "On where a deblurrer serves: BlurX tightens every star to near the sampling limit, and the faint ones then carry a green "
                + "fringe (docs/plans/ai-enhancement.md)."),
            new OperationSetting("scnrAmount", "Its strength, 0 to 1."),
            new OperationSetting("deblurSharpen", "The deblurrer's own sharpening, 0 to 1. Left out: the enhancer's default."),
            new OperationSetting("denoiseStrength", "The denoiser's own strength, 0 to 1. Left out: the enhancer's default."),
            new OperationSetting("denoiseIterations", "Denoise passes, run ungated. Left out: gated, up to three."),
            new OperationSetting("deconvKernel", "The blur TianWen's own deconvolver removes, as a Moffat FWHM in pixels: one value or three."),
            new OperationSetting("deconvBeta", "The Moffat beta of the kernel."),
            new OperationSetting("deconvResample", "The scale the frame is deconvolved at."),
            new OperationSetting("whiteBalance", "An SPCC white balance for the input as red, green, blue; it wins over one the input states."),
        ]);

    /// <inheritdoc/>
    protected override JsonTypeInfo<EnhanceSettings> SettingsInfo => OperationSettingsJsonContext.Default.EnhanceSettings;

    /// <inheritdoc/>
    public override string? Refusal(EnhanceSettings settings)
    {
        if (!TryOptions(settings, out _, out var why))
        {
            return why;
        }

        if (!settings.Gradient && settings.GradientBackend is not GradientBackend.Auto)
        {
            return "gradientBackend chooses the extractor of a gradient step that gradient leaves out: give one or the other";
        }

        if (settings.WhiteBalance.IsDefault || (settings.WhiteBalance.Length is not (0 or 3)) || settings.WhiteBalance.Any(static gain => !float.IsFinite(gain) || gain <= 0f))
        {
            return "whiteBalance is three positive numbers (red, green, blue), or left out";
        }

        return IsFraction(settings.StellarBlend) && IsFraction(settings.DeconvolveBlend ?? 1f) && IsFraction(settings.DenoiseBlend) && IsFraction(settings.ScnrAmount)
            ? null
            : "a blend and the scnr amount are 0 to 1";
    }

    private static bool IsFraction(float value) => value is >= 0f and <= 1f;

    /// <summary>
    /// The enhance options the settings mean: the backend, the tuning (none where nothing overrides an enhancer's own
    /// default) and the gradient extractor. False with the reason when the kernel's text is refused.
    /// </summary>
    public static bool TryOptions(EnhanceSettings settings, out EnhanceOptions options, [NotNullWhen(false)] out string? refusal)
    {
        if (!DeconvolutionKernel.TryParse(settings.DeconvKernel, settings.DeconvBeta, settings.DeconvResample, out var kernel, out refusal))
        {
            options = EnhanceOptions.Default;
            return false;
        }

        // No override at all is "each enhancer's own default", which is no tuning (EnhanceOptions.TryParse's rule).
        var tuning = settings.DeblurSharpen.HasValue || settings.DenoiseStrength.HasValue || settings.DenoiseIterations.HasValue || kernel is not null
            ? new EnhanceTuning(settings.DeblurSharpen, settings.DenoiseStrength, settings.DenoiseIterations, kernel)
            : null;
        options = new EnhanceOptions(settings.Backend, tuning, settings.GradientBackend);
        return true;
    }

    /// <summary>The enhancer registered for each role: a result made with one set is not a result made with another.</summary>
    public override string ServedBy(EnhanceSettings settings) => pipeline.DescribeRoles();

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(EnhanceSettings settings, IReadOnlyList<string> inputs, string output)
    {
        var line = ImmutableArray.CreateBuilder<string>();
        line.AddRange(StepCommandLine.Program, "image", "sharpen", inputs[0], "-o", output);
        line.AddRange("--ai-backend", settings.Backend switch
        {
            EnhanceBackend.ForceRcAstro => "rc",
            EnhanceBackend.TianWen => "tianwen",
            _ => "auto",
        });
        if (!settings.Gradient)
        {
            line.Add("--no-gradient");
        }
        else
        {
            line.AddRange("--gradient", settings.GradientBackend.ToString().ToLowerInvariant());
        }

        line.AddRange("--mode", settings.Mode.ToString().ToLowerInvariant());
        if (settings.StellarSharpen)
        {
            line.AddRange("--stellar-sharpen", "--stellar-blend", StepCommandLine.Number(settings.StellarBlend));
        }

        if (!settings.Deconvolve)
        {
            line.Add("--no-deconv");
        }
        else if (settings.DeconvolveBlend is { } deconvolveBlend)
        {
            line.AddRange("--deconv-blend", StepCommandLine.Number(deconvolveBlend));
        }

        if (!settings.Denoise)
        {
            line.Add("--no-denoise");
        }
        else
        {
            line.AddRange("--denoise-blend", StepCommandLine.Number(settings.DenoiseBlend), "--denoise-variant", settings.DenoiseVariant.ToString().ToLowerInvariant());
        }

        if (settings.Scnr is { } scnr)
        {
            line.AddRange("--scnr", scnr.ToString().ToLowerInvariant(), "--scnr-amount", StepCommandLine.Number(settings.ScnrAmount));
        }

        Add(line, "--deblur-sharpen", settings.DeblurSharpen is { } deblurSharpen ? StepCommandLine.Number(deblurSharpen) : null);
        Add(line, "--denoise-strength", settings.DenoiseStrength is { } denoiseStrength ? StepCommandLine.Number(denoiseStrength) : null);
        Add(line, "--denoise-iterations", settings.DenoiseIterations is { } iterations ? StepCommandLine.Number(iterations) : null);
        Add(line, "--deconv-kernel", settings.DeconvKernel);
        Add(line, "--deconv-beta", settings.DeconvBeta is { } beta ? StepCommandLine.Number(beta) : null);
        Add(line, "--deconv-resample", settings.DeconvResample is { } resample ? StepCommandLine.Number(resample) : null);
        Add(line, "--white-balance", settings.WhiteBalance.IsDefaultOrEmpty ? null : string.Join(',', settings.WhiteBalance.Select(StepCommandLine.Number)));
        return line.ToImmutable();
    }

    private static void Add(ImmutableArray<string>.Builder line, string option, string? value)
    {
        if (value is not null)
        {
            line.Add(option);
            line.Add(value);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(EnhanceSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (!inputs[0].TryRead(out var source, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        if (!TryOptions(settings, out var options, out refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        var start = Start(source, options);
        var program = await ProgramAsync(start, settings, stretchedInPipeline: false, recombine: true, say, cancellationToken);
        SharpenResult result;
        try
        {
            // Composite only: every intermediate plate is released as soon as the chain has consumed it.
            result = await pipeline.ProcessAsync(new SharpenRequest(start.Normalised, program.ToSteps(), SharpenIntermediates.None), options, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An enhancer is a model that can fail on a frame, or turn out not to be licensed at its first use; the step
            // says so and a run goes on to the next image. A cancellation is not a failure and is not caught.
            logger?.LogError(ex, "The enhance failed for {Input}", inputs[0].Name);
            return OperationOutcome.Failed(ex.Message);
        }

        if (result.Final is not { } final)
        {
            return OperationOutcome.Failed("the enhance gave no image");
        }

        OperationOutput.Write(final, outputPath, wcs);
        final.Release();
        say?.Invoke($"wrote {outputPath}");
        return OperationOutcome.Done;
    }

    /// <summary>The input on the unit scale, and which roles serve it under these options. The RC-Astro licence probe
    /// runs here, at the first enhance, never when the services are built.</summary>
    public EnhanceStart Start(Image source, EnhanceOptions options)
    {
        // The enhancers need [0, 1]. A no-op for an image that is there already; the source is unchanged.
        var normalised = source.ScaleFloatValuesToUnit();
        return new EnhanceStart(normalised, options, pipeline.CapabilitiesFor(normalised, options));
    }

    /// <summary>
    /// The program for these settings on this image: <see cref="LinearEnhanceProgram.For"/> with the settings' deviations
    /// from it, each ANDed with what serves.
    /// </summary>
    /// <param name="start">Where the enhance starts (<see cref="Start"/>).</param>
    /// <param name="settings">The settings.</param>
    /// <param name="stretchedInPipeline">True when the caller stretches the plates inside the pipeline
    /// (<c>--dual-stretch</c>): the composite is then a screen of stretched plates, and green stars, a stretched-space
    /// artefact, are taken out unless the settings say otherwise.</param>
    /// <param name="recombine">False when the caller writes each plate by itself (<c>--no-recombine</c>).</param>
    /// <param name="say">Takes each line that says a setting was ignored and why, and the white balance applied.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    public async Task<LinearEnhanceProgram> ProgramAsync(EnhanceStart start, EnhanceSettings settings, bool stretchedInPipeline, bool recombine, Action<string>? say,
        CancellationToken cancellationToken)
    {
        var capabilities = start.Capabilities;
        var deblurLive = capabilities.Deblur;

        // Stellar-sharpen is opt-in (default OFF), and only where a stellar sharpener serves: none ships today (the SETI
        // Astro one went with the SAS tier on 2026-09-26, and it hardened bright cores into square white blocks, ~89k
        // clipped px measured on a dense field). Hard override: when a BlurX deblurrer is live it is skipped even if
        // opted in -- the BlurX-first (PixInsight OSC) flow deblurs whole-frame before star extraction, so
        // re-sharpening the split stars double-dips.
        var stellar = settings.StellarSharpen && !deblurLive && capabilities.StellarSharpen && capabilities.StarRemoval;
        if (settings.StellarSharpen && deblurLive)
        {
            say?.Invoke("stellar sharpening ignored: a deblurrer is live (deblur is whole-frame upstream; re-sharpening extracted stars over-sharpens).");
        }
        else if (settings.StellarSharpen && !stellar)
        {
            say?.Invoke("stellar sharpening ignored: no stellar sharpener is available (none ships today; RC-Astro BlurXTerminator's deblur tightens stars).");
        }

        // SCNR on the stars plate follows whichever canonical program applies, and only a stated mode overrides it.
        // SharpenRequest.DeblurFirst carries ScnrStarsStep (Average) and SharpenRequest.Canonical does not, which is
        // the split honoured here: BlurX tightens every star to near the sampling limit, and the faint ones then carry
        // a green fringe where the G channel outpaces R and B, so the BlurX-first flow neutralises it and the split
        // program without a deblurrer has nothing to neutralise. A program that stretches in the pipeline
        // (--dual-stretch) keeps its own reason on top: green stars are a stretched-space artefact, so it wants SCNR
        // whichever deblurrer is live.
        var scnr = settings.Scnr ?? (deblurLive || stretchedInPipeline ? ScnrMode.Average : ScnrMode.None);
        if (settings.Scnr is not (null or ScnrMode.None) && !capabilities.StarRemoval)
        {
            say?.Invoke("scnr ignored: it works on the stars plate, and with no star remover the program is whole-frame.");
        }

        // A broadband SPCC balance the input states, or the one the settings give, goes into the linear data first, as
        // stack --enhance and the viewer apply theirs.
        var (stated, note) = ResolveWhiteBalance(start.Normalised.ImageMeta.ColourCalibration, settings.WhiteBalance);
        if (note is not null)
        {
            say?.Invoke(note);
        }

        var whiteBalance = await MasterPreviewRenderer.StatedToApplyAsync(stated, start.Normalised.ImageMeta, cancellationToken);
        if (whiteBalance is { } applying)
        {
            say?.Invoke($"{ColourCalibration.CardOf(applying.Source)} white balance ({applying.R:F3}, {applying.G:F3}, {applying.B:F3}) applied to the linear input before the enhance");
        }

        // THE STEP ORDER IS NOT WRITTEN HERE. LinearEnhanceProgram.For is the one place it lives, and the viewer's
        // Enhance button, the hosted endpoint and MasterPostProcessor all read it from there too; this only says which
        // steps the settings keep and at what blend. `tianwen image sharpen` used to carry a hand-assembled list that
        // began at RemoveStarsStep, so it ran neither the whole-frame deblur nor the gradient correction the other
        // callers have always run: the same master enhanced in the viewer and on the command line came out visibly
        // different, an uncorrected background with its colour cast intact, and nothing said so.
        //
        // Deviations from the canonical defaults, each with its own reason (and none of them can switch on a step no
        // role serves: every step flag is ANDed with its capability):
        //  - StellarSharpen is opt-in here, where a sharpener serves at all (see above).
        //  - DeconvolveStarless survives a live deblurrer only if asked for outright.
        //  - Scnr follows whichever canonical program applies unless a mode is stated, and a program that stretches in
        //    the pipeline wants it either way (green stars are a stretched-space artefact).
        //  - Recombine is cleared by a caller that writes each plate separately (--no-recombine).
        //  - WhiteBalance multiplies a broadband SPCC balance the input states (a stacked master's WB cards), or the
        //    one the settings give for an input that states none, into the linear data first, as `stack --enhance`
        //    and the viewer apply theirs.
        return LinearEnhanceProgram.For(capabilities) with
        {
            WhiteBalance = whiteBalance,
            // The deblur's blend is left at the step default, as the canonical program has it; deblurSharpen tunes the
            // deblurrer's own sharpening through the options and is a different dial.
            GradientCorrection = settings.Gradient && capabilities.GradientCorrection,
            SplitMode = settings.Mode,
            StellarSharpen = stellar,
            StellarBlend = settings.StellarBlend,
            // The starless deconvolution survives a live deblurrer only if its blend was stated outright.
            DeconvolveStarless = settings.Deconvolve && capabilities.StarRemoval && capabilities.Deconvolve && (!deblurLive || settings.DeconvolveBlend is not null),
            DeconvolveBlend = settings.DeconvolveBlend ?? 1f,
            Denoise = settings.Denoise && capabilities.Denoise,
            DenoiseBlend = settings.DenoiseBlend,
            DenoiseVariant = settings.DenoiseVariant,
            Scnr = scnr,
            ScnrAmount = settings.ScnrAmount,
            Recombine = recombine,
            // Dual-stretch produces plates already in [0, 1] stretched space. Additive sum saturates at 1.0; screen is
            // the natural bounded composite: Final = 1 - (1-bg)(1-fg). Split stays in linear (mode controls that).
            RecombineMode = stretchedInPipeline ? RecombineMode.Screen : settings.Mode,
        };
    }

    /// <summary>
    /// Which balance an enhance works from: a stated triple WINS over the input's own WB cards (it is the person's
    /// intent, and the cards used to win silently, so an override was lost without a word), and the note names both
    /// triples when it replaced the file's. Null note when nothing was replaced.
    /// </summary>
    public static (ColourCalibration? Chosen, string? Note) ResolveWhiteBalance(ColourCalibration? fromFile, ImmutableArray<float> stated)
    {
        if (stated.IsDefaultOrEmpty)
        {
            return (fromFile, null);
        }

        var (r, g, b) = (stated[0], stated[1], stated[2]);
        var chosen = new ColourCalibration(r, g, b, ColourCalibrationSource.Spcc);
        var note = fromFile is { } file
            ? $"the white balance given ({r:F3}, {g:F3}, {b:F3}) replaces the input's own {ColourCalibration.CardOf(file.Source)} white balance ({file.R:F3}, {file.G:F3}, {file.B:F3})"
            : null;
        return (chosen, note);
    }
}
