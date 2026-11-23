using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.StarRemoval;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>What <see cref="CombineOperation.ComposeAsync"/> made. The caller owns the images.</summary>
/// <param name="Rgb">The colour image: three planes, linear, on the unit scale.</param>
/// <param name="Luminance">The synthetic luminance, where one was asked for or the recipe made one for its own use.</param>
/// <param name="Wcs">The plate solution of the masters: the first of them that has one.</param>
public sealed record CombineResult(Image Rgb, Image? Luminance, WCS? Wcs);

/// <summary>
/// One colour image from red, green and blue mono masters on one grid, the recipe a PixInsight mono workflow follows
/// (<see cref="ColourComposition.RunAsync"/>): linear fit, deblur, stars out, the line in, the luminance, its detail
/// in every channel, stars back. <c>image combine</c> calls <see cref="ComposeAsync"/>.
/// </summary>
/// <remarks>
/// <para>The recipe runs each step's own routine in order and nothing between them, so the steps as edits of their own
/// give the same image. This is the recipe as ONE step, for a project that does not need to look at the steps.</para>
/// <para>The edit is on the RED master and takes green and blue, and an H-alpha master when its emission goes into
/// red. What serves it is in the step's key: the enhancer of each role the settings switch on.</para>
/// </remarks>
public sealed class CombineOperation(
    IStarRemover? starRemover = null,
    IImageDeblurrer? deblurrer = null,
    IDenoiseEnhancer? denoiser = null,
    ICelestialObjectDB? objectDb = null) : Operation<CombineSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "combine";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Combine",
        Verb: "image combine",
        OperationOutputKind.Stored,
        ExtraInputs:
        [
            new OperationInputRole("green", "The green master."),
            new OperationInputRole("blue", "The blue master."),
            new OperationInputRole("ha", "An H-alpha master whose emission goes into red, its continuum taken out against red.", Optional: true),
        ],
        Settings:
        [
            new OperationSetting("linearFit", "Put red, green and blue on this channel's scale before anything else (green, red, blue), or none."),
            new OperationSetting("deblur", "Deblur every master before its stars are removed, all on one scale."),
            new OperationSetting("matchPsf", "The fallback without a deblurrer: blur every sharper master to the widest one's star width, for the colour only."),
            new OperationSetting("starless", "Remove the stars from every master first, add the emission to the starless planes, and put the broadband stars back on top."),
            new OperationSetting("catalogue", "With starless: the star catalogue asked for the stars inside a protected core."),
            new OperationSetting("lineScale", "The continuum scale to subtract the H-alpha's continuum with. Left out: measured on the masters with their stars."),
            new OperationSetting("lineWeight", "How much of the pure H-alpha goes into red, in red's own units."),
            new OperationSetting("lrgb", "Give every channel its fine detail from the synthetic luminance."),
            new OperationSetting("colourSigma", "With lrgb: the blur in pixels the channels keep their own colour above."),
            new OperationSetting("denoise", "Denoise the starless planes; the stars are never denoised, so it needs starless."),
            new OperationSetting("denoiseStrength", "With denoise: the strength for the planes that carry the detail. Left out: the denoiser's own."),
            new OperationSetting("colourDenoiseStrength", "With denoise and lrgb: the strength for the colour, whose fine detail the luminance replaces."),
        ]);

    /// <inheritdoc/>
    protected override JsonTypeInfo<CombineSettings> SettingsInfo => OperationSettingsJsonContext.Default.CombineSettings;

    /// <inheritdoc/>
    public override string? Refusal(CombineSettings settings)
    {
        if (settings.Denoise && !settings.Starless)
        {
            return "denoise needs starless: the stars are never denoised";
        }

        if (settings.LineScale is { } scale && !double.IsFinite(scale))
        {
            return "lineScale is a number, or left out to be measured";
        }

        if (!double.IsFinite(settings.LineWeight))
        {
            return "lineWeight is a number";
        }

        if (!float.IsFinite(settings.ColourSigma) || settings.ColourSigma <= 0)
        {
            return "colourSigma is a blur in pixels, above 0";
        }

        return settings.DenoiseStrength is null or (>= 0f and <= 1f) && settings.ColourDenoiseStrength is >= 0f and <= 1f
            ? null
            : "a denoise strength is 0 to 1";
    }

    /// <summary>The enhancer of each role the settings switch on, and the catalogue core protection would ask.</summary>
    public override string ServedBy(CombineSettings settings)
    {
        var served = new List<string>(3);
        if (settings.Deblur)
        {
            served.Add("deblur by " + (deblurrer?.Name ?? "none"));
        }

        if (settings.Starless)
        {
            served.Add("stars by " + (starRemover?.Name ?? "none") + ", cores by " + CoreCatalogue.Source(settings.Catalogue));
        }

        if (settings.Denoise)
        {
            served.Add("denoise by " + (denoiser?.Name ?? "none"));
        }

        return string.Join("; ", served);
    }

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(CombineSettings settings, IReadOnlyList<string> inputs, string output)
    {
        var line = ImmutableArray.CreateBuilder<string>();
        line.AddRange(StepCommandLine.Program, "image", "combine", "--red", inputs[0], "--green", inputs[1], "--blue", inputs[2]);
        if (inputs.Count > 3)
        {
            line.AddRange("--ha", inputs[3]);
        }

        line.AddRange("-o", output, "--linear-fit", settings.LinearFit.ToString().ToLowerInvariant(), "--ha-weight", StepCommandLine.Number(settings.LineWeight));
        if (settings.LineScale is { } scale)
        {
            line.AddRange("--ha-scale", StepCommandLine.Number(scale));
        }

        if (settings.Deblur)
        {
            line.Add("--deblur");
        }

        if (settings.MatchPsf)
        {
            line.Add("--match-psf");
        }

        if (settings.Starless)
        {
            line.AddRange("--starless", "--core-catalogue", CoreCatalogue.Word(settings.Catalogue));
        }

        if (settings.Lrgb)
        {
            line.AddRange("--lrgb", "--colour-sigma", StepCommandLine.Number(settings.ColourSigma));
        }

        if (settings.Denoise)
        {
            line.AddRange("--denoise", "--colour-denoise-strength", StepCommandLine.Number(settings.ColourDenoiseStrength));
            if (settings.DenoiseStrength is { } strength)
            {
                line.AddRange("--denoise-strength", StepCommandLine.Number(strength));
            }
        }

        return line.ToImmutable();
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(CombineSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        var (result, outcome) = await ComposeAsync(settings, inputs, withLuminance: false, say, cancellationToken);
        if (result is null)
        {
            return outcome;
        }

        OperationOutput.Write(result.Rgb, outputPath, result.Wcs);
        say?.Invoke($"wrote {outputPath}");
        return OperationOutcome.Done;
    }

    /// <summary>
    /// The composition without the files: what <c>image combine</c> calls, and then writes.
    /// </summary>
    /// <param name="settings">The settings, already accepted (<see cref="Refusal"/>).</param>
    /// <param name="inputs">Red, green and blue, and H-alpha when there is a fourth.</param>
    /// <param name="withLuminance">Also make the synthetic luminance where the settings would not (<c>--luminance</c>).</param>
    /// <param name="say">Takes each line a step reports, or null.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    /// <returns>The result, or null with the outcome saying why there is none.</returns>
    public async Task<(CombineResult? Result, OperationOutcome Outcome)> ComposeAsync(CombineSettings settings, IReadOnlyList<OperationInput> inputs, bool withLuminance,
        Action<string>? say, CancellationToken cancellationToken)
    {
        var masters = new List<Image>(inputs.Count);
        WCS? wcs = null;
        foreach (var input in inputs)
        {
            if (!input.TryReadMaster(out var master, out var masterWcs, out var refusal))
            {
                return (null, OperationOutcome.BadInput(refusal));
            }

            wcs ??= masterWcs;
            masters.Add(master);
        }

        if (masters.Count is not (3 or 4))
        {
            return (null, OperationOutcome.BadInput("red, green and blue, and an optional H-alpha: three or four masters."));
        }

        var options = new ColourCompositionOptions
        {
            LinearFitTo = settings.LinearFit switch
            {
                CombineLinearFit.Red => ColourChannel.Red,
                CombineLinearFit.Blue => ColourChannel.Blue,
                CombineLinearFit.None => null,
                _ => ColourChannel.Green,
            },
            Deblur = settings.Deblur,
            MatchPsf = settings.MatchPsf,
            Starless = settings.Starless,
            // As image remove-stars: on a plate-solved set the catalogued stars inside a protected core stay removed.
            CatalogStars = settings.Starless
                ? await CoreCatalogue.ReadAsync(settings.Catalogue, wcs, masters[0].Width, masters[0].Height, objectDb, say, cancellationToken)
                : null,
            LineScale = settings.LineScale,
            LineWeight = settings.LineWeight,
            Luminance = withLuminance,
            Lrgb = settings.Lrgb,
            ColourSigma = settings.ColourSigma,
            Denoise = settings.Denoise,
            DetailDenoise = settings.DenoiseStrength is { } strength
                ? new EnhanceOptions(Tuning: new EnhanceTuning(DenoiseStrength: Math.Clamp(strength, 0f, 1f)))
                : EnhanceOptions.Default,
            ColourDenoise = new EnhanceOptions(Tuning: new EnhanceTuning(DenoiseStrength: Math.Clamp(settings.ColourDenoiseStrength, 0f, 1f))),
        };
        var roles = new ColourCompositionRoles(starRemover, deblurrer, denoiser);
        if (ColourComposition.Validate(masters, options, roles) is { } problem)
        {
            return (null, OperationOutcome.BadInput($"{problem}."));
        }

        ColourComposition.Result result;
        try
        {
            result = await ColourComposition.RunAsync(masters, options, roles, say ?? (static _ => { }), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The recipe's own way of saying a step could not measure what it needs, in words a person can act on.
            return (null, OperationOutcome.BadInput($"{ex.Message}."));
        }

        var rgb = NarrowbandCombination.Rgb(result.Red, result.Green, result.Blue).ScaleFloatValuesToUnit();
        return (new CombineResult(rgb, result.Luminance, wcs), OperationOutcome.Done);
    }
}
