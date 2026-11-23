using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.StarRemoval;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>What the operations several images go through in one run share.</summary>
internal static class JointCommandLine
{
    /// <summary>The start of such a command: the verb, every image, and <c>-o</c> once for each image's main result, in
    /// the images' order.</summary>
    public static ImmutableArray<string>.Builder Start(string verb, IReadOnlyList<string> members, IReadOnlyList<StepOutputs> outputs)
    {
        var line = ImmutableArray.CreateBuilder<string>();
        line.Add(StepCommandLine.Program);
        line.Add("image");
        line.Add(verb);
        line.AddRange(members);
        foreach (var output in outputs)
        {
            line.Add("-o");
            line.Add(output.Path);
        }

        return line;
    }

    /// <summary>Says why the images and the places for their results do not pair up, or null when they do.</summary>
    public static string? Mismatch(string id, IReadOnlyList<OperationInput> members, IReadOnlyList<StepOutputs> outputs)
        => members.Count == 0 ? $"'{id}' was handed no image"
            : members.Count != outputs.Count ? $"'{id}' was handed {members.Count} image(s) and {outputs.Count} place(s) for their results"
            : null;
}

/// <summary>
/// Blurs every sharper master to the widest one's star width (<see cref="PsfMatch.ToWidestAsync"/>), the fallback
/// where no deblurrer serves. <c>image match-psf</c> calls this.
/// </summary>
/// <remarks>Use the results for the colour and the measurements, never for a luminance.</remarks>
public sealed class MatchPsfOperation : JointOperation<NoSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "match-psf";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Match PSF", Verb: "image match-psf", OperationOutputKind.Stored, ExtraInputs: [], Settings: [])
    {
        Joint = true,
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<NoSettings> SettingsInfo => OperationSettingsJsonContext.Default.NoSettings;

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(NoSettings settings, IReadOnlyList<string> members, IReadOnlyList<StepOutputs> outputs)
        => JointCommandLine.Start("match-psf", members, outputs).ToImmutable();

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunJointAsync(NoSettings settings, IReadOnlyList<OperationInput> members, IReadOnlyList<StepOutputs> outputs,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (JointCommandLine.Mismatch(Id, members, outputs) is { } mismatch)
        {
            return OperationOutcome.BadInput(mismatch);
        }

        if (!OperationInputs.TryReadMasters(members, out var masters, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        var (matched, report, target) = await PsfMatch.ToWidestAsync(masters, cancellationToken);
        for (var i = 0; i < matched.Length; i++)
        {
            say?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"{members[i].Name}: FWHM {report[i].FwhmBefore:F2} px, blurred by sigma {report[i].Sigma:F2} to {report[i].FwhmAfter:F2} (target {target:F2})"));
            OperationOutput.Write(matched[i], outputs[i].Path, wcs);
            say?.Invoke($"wrote {outputs[i].Path}");
        }

        return OperationOutcome.Done;
    }
}

/// <summary>
/// Whole-frame deconvolution (RC-Astro BlurXTerminator) and nothing else, run before the stars come out
/// (<see cref="NarrowbandCombination.DeblurAsync"/>): linear in, linear out, each result on its input's scale, several
/// masters on one scale with the headroom below the deblurrer's ceiling. <c>image deblur</c> calls this.
/// </summary>
public sealed class DeblurOperation(IImageDeblurrer? deblurrer = null) : JointOperation<DeblurSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "deblur";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Deblur", Verb: "image deblur", OperationOutputKind.Stored, ExtraInputs: [],
        Settings:
        [
            new OperationSetting("headroom", "The brightest input pixel goes to the deblurrer at 1/headroom of its ceiling, so no sharpened star clips.",
                "BlurX lifted LDN 1622's brightest star to 3.4 times its input peak; at 4 no pixel came back clipped, and the result agreed "
                + "with 8's to 0.7 percent on star pixels (NarrowbandCombination.DefaultDeblurHeadroom)."),
        ])
    {
        Joint = true,
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<DeblurSettings> SettingsInfo => OperationSettingsJsonContext.Default.DeblurSettings;

    /// <inheritdoc/>
    public override string? Refusal(DeblurSettings settings)
        => float.IsFinite(settings.Headroom) && settings.Headroom >= 1f ? null : "headroom is 1 or more";

    /// <summary>The deblurrer registered here; empty where there is none.</summary>
    public override string ServedBy(DeblurSettings settings) => deblurrer?.Name ?? string.Empty;

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(DeblurSettings settings, IReadOnlyList<string> members, IReadOnlyList<StepOutputs> outputs)
    {
        var line = JointCommandLine.Start("deblur", members, outputs);
        line.Add("--headroom");
        line.Add(StepCommandLine.Number(settings.Headroom));
        return line.ToImmutable();
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunJointAsync(DeblurSettings settings, IReadOnlyList<OperationInput> members, IReadOnlyList<StepOutputs> outputs,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (JointCommandLine.Mismatch(Id, members, outputs) is { } mismatch)
        {
            return OperationOutcome.BadInput(mismatch);
        }

        if (!IEnhancerAvailability.Serves(deblurrer, 1, EnhanceOptions.Default))
        {
            return OperationOutcome.NotServed(
                "no deblurrer serves here. It comes from RC-Astro: install the rc-astro CLI and license BlurXTerminator, or set RC_ASTRO_CLI.");
        }

        if (!OperationInputs.TryReadMasters(members, out var masters, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        var (deblurred, atCeiling) = await NarrowbandCombination.DeblurAsync(masters, deblurrer, settings.Headroom, cancellationToken);
        for (var i = 0; i < deblurred.Length; i++)
        {
            var before = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(masters[i], cancellationToken));
            var after = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(deblurred[i], cancellationToken));
            say?.Invoke(string.Create(CultureInfo.InvariantCulture, $"{members[i].Name}: FWHM {before:F2} px -> {after:F2} ({deblurrer.Name})"));
            OperationOutput.Write(deblurred[i], outputs[i].Path, wcs);
            say?.Invoke($"wrote {outputs[i].Path}");
        }

        if (atCeiling > 0)
        {
            say?.Invoke($"{atCeiling} pixels reached the deblurrer's ceiling and are clipped: raise the headroom.");
        }

        return OperationOutcome.Done;
    }
}

/// <summary>
/// Noise reduction and nothing else (<see cref="NarrowbandCombination.DenoiseAsync"/>: RC-Astro NoiseXTerminator
/// where licensed, else the in-house denoiser, which takes colour only), each result on its input's scale.
/// <c>image denoise</c> calls this.
/// </summary>
/// <remarks>
/// Hand it STARLESS planes: the stars are never denoised. The verb's <c>--colour</c> (three mono planes denoised as
/// one colour image) is not this operation: there the order of the planes is their colour, and a run here must not
/// depend on the order its images are handed in.
/// </remarks>
public sealed class DenoiseOperation(IDenoiseEnhancer? denoiser = null) : JointOperation<DenoiseSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "denoise";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Denoise", Verb: "image denoise", OperationOutputKind.Stored, ExtraInputs: [],
        Settings:
        [
            new OperationSetting("strength", "Strength in 0 to 1. Left out: the denoiser's own (NoiseXTerminator's is set from each plate's noise)."),
        ])
    {
        Joint = true,
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<DenoiseSettings> SettingsInfo => OperationSettingsJsonContext.Default.DenoiseSettings;

    /// <inheritdoc/>
    public override string? Refusal(DenoiseSettings settings)
        => settings.Strength is null or (>= 0f and <= 1f) ? null : "strength is 0 to 1, or left out for the denoiser's own";

    /// <summary>The denoiser registered here; empty where there is none.</summary>
    public override string ServedBy(DenoiseSettings settings) => denoiser?.Name ?? string.Empty;

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(DenoiseSettings settings, IReadOnlyList<string> members, IReadOnlyList<StepOutputs> outputs)
    {
        var line = JointCommandLine.Start("denoise", members, outputs);
        if (settings.Strength is { } strength)
        {
            line.Add("--strength");
            line.Add(StepCommandLine.Number(strength));
        }

        return line.ToImmutable();
    }

    /// <summary>The denoiser's options for these settings.</summary>
    public static EnhanceOptions OptionsOf(DenoiseSettings settings)
        => settings.Strength is { } strength
            ? new EnhanceOptions(Tuning: new EnhanceTuning(DenoiseStrength: Math.Clamp(strength, 0f, 1f)))
            : EnhanceOptions.Default;

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunJointAsync(DenoiseSettings settings, IReadOnlyList<OperationInput> members, IReadOnlyList<StepOutputs> outputs,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (JointCommandLine.Mismatch(Id, members, outputs) is { } mismatch)
        {
            return OperationOutcome.BadInput(mismatch);
        }

        if (!OperationInputs.TryReadMasters(members, out var masters, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        var options = OptionsOf(settings);
        if (!IEnhancerAvailability.Serves(denoiser, masters[0].ChannelCount, options))
        {
            return OperationOutcome.NotServed(
                "no denoiser serves this input here. Register one with AddRcAstroAi() (NoiseXTerminator, else TianWen's own N2N model on colour data).");
        }

        var denoised = await NarrowbandCombination.DenoiseAsync(masters, denoiser, options, cancellationToken);
        for (var i = 0; i < denoised.Length; i++)
        {
            say?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"{members[i].Name}: noise {SyntheticLuminance.BlockNoise(masters[i]):G4} -> {SyntheticLuminance.BlockNoise(denoised[i]):G4} over {SyntheticLuminance.NoiseBlockPx} px blocks ({denoiser.Name}, strength {ColourComposition.DescribeStrength(options)})"));
            OperationOutput.Write(denoised[i], outputs[i].Path, wcs);
            say?.Invoke($"wrote {outputs[i].Path}");
        }

        return OperationOutcome.Done;
    }
}

/// <summary>
/// Star removal (<see cref="NarrowbandCombination"/>'s split, with its core protection and dig fill): the starless
/// plate, and as an extra result the stars it took out, each on its input's scale, so
/// <see cref="AddStarsOperation"/> puts them back exactly. <c>image remove-stars</c> calls <see cref="SplitAsync"/>.
/// </summary>
/// <remarks>
/// <para>Several masters go through on one scale, so a scale measured between two of them still holds between their
/// plates.</para>
/// <para>What serves it is in the step's key: the remover's name and, with core protection on, the star catalogue
/// that would answer for the stars inside a core.</para>
/// </remarks>
public sealed class RemoveStarsOperation(IStarRemover? starRemover = null, ICelestialObjectDB? objectDb = null, ILogger<RemoveStarsOperation>? logger = null)
    : JointOperation<RemoveStarsSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "remove-stars";

    /// <summary>The name of its extra result: the stars it took out.</summary>
    public const string Stars = "stars";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Remove stars", Verb: "image remove-stars", OperationOutputKind.Stored, ExtraInputs: [],
        Settings:
        [
            new OperationSetting("protectCores", "Keep the brightest part of each galaxy or nebula out of the stars, whichever remover runs: a nucleus "
                + "or a bright knot a remover takes as a star stays in the starless plate (#1658)."),
            new OperationSetting("catalogue", "The star catalogue asked for the stars inside a protected core, which then stay removed (a plate-solved "
                + "master only): auto, embedded or none."),
            new OperationSetting("fillDigs", "Fill the holes TianWen's own models dig under the stars they take (#2204). RC-Astro's plate is never touched."),
            new OperationSetting("keepOvershoot", "Leave the plate as the remover wrote it. By default it is held at or under its input, so the stars are "
                + "never negative and plate plus stars is the input (#2433)."),
        ])
    {
        Joint = true,
        ExtraResults = [new OperationResultRole(Stars, "The stars it took out: the input less its starless plate.")],
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<RemoveStarsSettings> SettingsInfo => OperationSettingsJsonContext.Default.RemoveStarsSettings;

    /// <summary>The remover registered here and, with core protection on, the catalogue that would answer.</summary>
    public override string ServedBy(RemoveStarsSettings settings)
        => (starRemover?.Name ?? string.Empty) + (settings.ProtectCores ? "; cores by " + CoreCatalogue.Source(settings.Catalogue) : string.Empty);

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(RemoveStarsSettings settings, IReadOnlyList<string> members, IReadOnlyList<StepOutputs> outputs)
    {
        var line = JointCommandLine.Start("remove-stars", members, outputs);
        if (outputs.All(static output => output.Extra.ContainsKey(Stars)))
        {
            foreach (var output in outputs)
            {
                line.Add("--stars-output");
                line.Add(output.Extra[Stars]);
            }
        }
        else
        {
            line.Add("--no-stars");
        }

        if (!settings.ProtectCores)
        {
            line.Add("--no-protect-cores");
        }
        else
        {
            line.Add("--core-catalogue");
            line.Add(CoreCatalogue.Word(settings.Catalogue));
        }

        if (!settings.FillDigs)
        {
            line.Add("--no-fill-digs");
        }

        if (settings.KeepOvershoot)
        {
            line.Add("--keep-overshoot");
        }

        return line.ToImmutable();
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunJointAsync(RemoveStarsSettings settings, IReadOnlyList<OperationInput> members, IReadOnlyList<StepOutputs> outputs,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (starRemover is null)
        {
            return OperationOutcome.NotServed("no star remover serves here (RC-Astro StarXTerminator).");
        }

        try
        {
            return await SplitAsync(starRemover, settings, members, outputs, say, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A remover is a model that can fail on a frame, or turn out not to be licensed at its first use; the step
            // says so and a run goes on to the next image. A cancellation is not a failure and is not caught.
            logger?.LogError(ex, "Star removal failed for {Input}", members[0].Name);
            return OperationOutcome.Failed(ex.Message);
        }
    }

    /// <summary>
    /// The split through <paramref name="remover"/>: what <c>image remove-stars</c> calls, with the registered remover
    /// or with one of its own (weights named on its command line). A remover's own failure is thrown.
    /// </summary>
    public async ValueTask<OperationOutcome> SplitAsync(IStarRemover remover, RemoveStarsSettings settings, IReadOnlyList<OperationInput> members,
        IReadOnlyList<StepOutputs> outputs, Action<string>? say, CancellationToken cancellationToken)
    {
        if (JointCommandLine.Mismatch(Id, members, outputs) is { } mismatch)
        {
            return OperationOutcome.BadInput(mismatch);
        }

        if (!OperationInputs.TryReadMasters(members, out var masters, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        say?.Invoke($"{members.Count} master(s) through {remover.Name}");
        var coreOptions = settings.ProtectCores ? ExtendedCoreMask.Options.Default : null;
        var catalogStars = coreOptions is null
            ? null
            : await CoreCatalogue.ReadAsync(settings.Catalogue, wcs, masters[0].Width, masters[0].Height, objectDb, say, cancellationToken);
        var (starless, stars, cores, filled) = await NarrowbandCombination.SplitStarsAsync(
            masters, remover, coreOptions, fillDigs: settings.FillDigs, catalogStars, holdAtOrUnderPlane: !settings.KeepOvershoot, cancellationToken);
        for (var i = 0; i < starless.Length; i++)
        {
            if (filled is not null)
            {
                say?.Invoke(string.Create(CultureInfo.InvariantCulture, $"{members[i].Name}: dig-fill step covered {filled[i]:P1} of the pixels"));
            }

            if (cores?[i] is { } mask)
            {
                CoreCatalogue.Describe(members[i].Name, mask, say);
            }

            OperationOutput.Write(starless[i], outputs[i].Path, wcs);
            say?.Invoke($"wrote {outputs[i].Path}");
            if (outputs[i].Extra.TryGetValue(Stars, out var starsPath))
            {
                OperationOutput.Write(stars[i], starsPath, wcs);
                say?.Invoke($"wrote {starsPath}");
            }
        }

        return OperationOutcome.Done;
    }
}
