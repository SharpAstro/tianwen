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
    { Summary = "Blurs each image to the star size of the softest one, so the images share one star shape.",
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
    /// <remarks>Version 2 (2026-11-26, #2504): the result says it is on a unit scale (<c>UNITREF</c>,
    /// <see cref="ImageMeta.UnitReferred"/>), so a star the deblur lifted past 2.0 no longer makes every reader take the
    /// file for ADU. A version 1 result, which every step after it divided by its lifted star, runs again.</remarks>
    public override OperationDescription Description { get; } = new(
        Id, Version: 2, Title: "Deblur", Verb: "image deblur", OperationOutputKind.Stored, ExtraInputs: [],
        Settings:
        [
            new OperationSetting("headroom", "The brightest input pixel goes to the deblurrer at 1/headroom of its ceiling, so no sharpened star clips.",
                "BlurX lifted LDN 1622's brightest star to 3.4 times its input peak; at 4 no pixel came back clipped, and the result agreed "
                + "with 8's to 0.7 percent on star pixels (NarrowbandCombination.DefaultDeblurHeadroom)."),
        ])
    { Summary = "Sharpens the image by taking out the blur of the telescope and the seeing.",
        Joint = true,
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<DeblurSettings> SettingsInfo => OperationSettingsJsonContext.Default.DeblurSettings;

    /// <inheritdoc/>
    public override string? Refusal(DeblurSettings settings)
        => float.IsFinite(settings.Headroom) && settings.Headroom >= 1f ? null : "headroom is 1 or more";

    /// <summary>The deblurrer registered here; empty where there is none.</summary>
    public override string ServedBy(DeblurSettings settings) => Served(settings).By;

    /// <inheritdoc/>
    public override string ServedTag(DeblurSettings settings) => Served(settings).Tag;

    /// <summary>The name and the origin word from one resolution of the role (<see cref="IEnhancerAvailability.ServedOf"/>).</summary>
    public override (string By, string Tag) Served(DeblurSettings settings)
    {
        var (name, tag) = IEnhancerAvailability.ServedOf(deblurrer, EnhanceOptions.Default);
        return (name, tag);
    }

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
    { Summary = "Lowers the noise of the image while it keeps the detail.",
        Joint = true,
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<DenoiseSettings> SettingsInfo => OperationSettingsJsonContext.Default.DenoiseSettings;

    /// <inheritdoc/>
    public override string? Refusal(DenoiseSettings settings)
        => settings.Strength is null or (>= 0f and <= 1f) ? null : "strength is 0 to 1, or left out for the denoiser's own";

    /// <summary>The denoiser registered here; empty where there is none.</summary>
    public override string ServedBy(DenoiseSettings settings) => Served(settings).By;

    /// <inheritdoc/>
    public override string ServedTag(DenoiseSettings settings) => Served(settings).Tag;

    /// <summary>The name and the origin word from one resolution of the role (<see cref="IEnhancerAvailability.ServedOf"/>).</summary>
    public override (string By, string Tag) Served(DenoiseSettings settings)
    {
        var (name, tag) = IEnhancerAvailability.ServedOf(denoiser, EnhanceOptions.Default);
        return (name, tag);
    }

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
/// Three mono planes denoised together as ONE colour image, so the denoiser sees their colour
/// (<see cref="NarrowbandCombination.DenoiseColourAsync"/>), and handed back as three, each on its own scale.
/// <c>image denoise --colour</c> calls this.
/// </summary>
/// <remarks>
/// <para>Its own image is red, and it takes green and blue: which plane is which colour is its place among the
/// inputs. That is why this is not <see cref="DenoiseOperation"/>, whose images go through in any order.</para>
/// <para>Red's plane is the step's own result; green's and blue's are its extra results, each an image of the project
/// that starts from the step (<see cref="ProjectImage.Result"/>).</para>
/// <para>Hand it STARLESS planes: the stars are never denoised.</para>
/// </remarks>
public sealed class DenoiseColourOperation(IDenoiseEnhancer? denoiser = null) : MultiResultOperation<DenoiseSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "denoise-colour";

    /// <summary>The name of the extra result that is the green plane.</summary>
    public const string Green = "green";

    /// <summary>The name of the extra result that is the blue plane.</summary>
    public const string Blue = "blue";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Denoise as colour", Verb: "image denoise --colour", OperationOutputKind.Stored,
        ExtraInputs:
        [
            new OperationInputRole("green", "The green plane."),
            new OperationInputRole("blue", "The blue plane."),
        ],
        Settings:
        [
            new OperationSetting("strength", "Strength in 0 to 1. Left out: the denoiser's own (NoiseXTerminator's is set from each plate's noise)."),
        ])
    { Summary = "Lowers the noise of three planes together as one colour image, and hands each plane back.",
        ExtraResults =
        [
            new OperationResultRole(Green, "The green plane, denoised."),
            new OperationResultRole(Blue, "The blue plane, denoised."),
        ],
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<DenoiseSettings> SettingsInfo => OperationSettingsJsonContext.Default.DenoiseSettings;

    /// <inheritdoc/>
    public override string? Refusal(DenoiseSettings settings)
        => settings.Strength is null or (>= 0f and <= 1f) ? null : "strength is 0 to 1, or left out for the denoiser's own";

    /// <summary>The denoiser registered here; empty where there is none.</summary>
    public override string ServedBy(DenoiseSettings settings) => Served(settings).By;

    /// <inheritdoc/>
    public override string ServedTag(DenoiseSettings settings) => Served(settings).Tag;

    /// <summary>The name and the origin word from one resolution of the role (<see cref="IEnhancerAvailability.ServedOf"/>).</summary>
    public override (string By, string Tag) Served(DenoiseSettings settings)
    {
        var (name, tag) = IEnhancerAvailability.ServedOf(denoiser, EnhanceOptions.Default);
        return (name, tag);
    }

    /// <summary>Where the three planes go: red's as the main result, green's and blue's as the extra ones.</summary>
    public static StepOutputs Outputs(string red, string green, string blue)
        => new(red) { Extra = ImmutableDictionary<string, string>.Empty.Add(Green, green).Add(Blue, blue) };

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(DenoiseSettings settings, IReadOnlyList<string> inputs, StepOutputs outputs)
    {
        var line = ImmutableArray.CreateBuilder<string>();
        line.Add(StepCommandLine.Program);
        line.Add("image");
        line.Add("denoise");
        line.AddRange(inputs);
        line.Add("--colour");
        // -o once for each plane in the planes' order, as the verb takes it. A plane with no place is not named.
        foreach (var place in Places(outputs))
        {
            if (place is not null)
            {
                line.Add("-o");
                line.Add(place);
            }
        }

        if (settings.Strength is { } strength)
        {
            line.Add("--strength");
            line.Add(StepCommandLine.Number(strength));
        }

        return line.ToImmutable();
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(DenoiseSettings settings, IReadOnlyList<OperationInput> inputs, StepOutputs outputs,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (inputs.Count != 3)
        {
            return OperationOutcome.BadInput($"'{Id}' takes three mono planes, red, green and blue, and was handed {inputs.Count}");
        }

        if (!OperationInputs.TryReadMasters(inputs, out var masters, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        var options = DenoiseOperation.OptionsOf(settings);
        if (!IEnhancerAvailability.Serves(denoiser, 3, options))
        {
            return OperationOutcome.NotServed(
                "no denoiser serves this input here. Register one with AddRcAstroAi() (NoiseXTerminator, else TianWen's own N2N model on colour data).");
        }

        var denoised = await NarrowbandCombination.DenoiseColourAsync(masters[0], masters[1], masters[2], denoiser, options, cancellationToken);
        var places = Places(outputs);
        for (var i = 0; i < denoised.Length; i++)
        {
            say?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"{inputs[i].Name}: noise {SyntheticLuminance.BlockNoise(masters[i]):G4} -> {SyntheticLuminance.BlockNoise(denoised[i]):G4} over {SyntheticLuminance.NoiseBlockPx} px blocks ({denoiser.Name}, strength {ColourComposition.DescribeStrength(options)})"));
            if (places[i] is { } place)
            {
                OperationOutput.Write(denoised[i], place, wcs);
                say?.Invoke($"wrote {place}");
            }
        }

        return OperationOutcome.Done;
    }

    // The place of each plane's result, in the planes' order; null for one that has none.
    private static string?[] Places(StepOutputs outputs)
        => [outputs.Path, outputs.Extra.TryGetValue(Green, out var green) ? green : null, outputs.Extra.TryGetValue(Blue, out var blue) ? blue : null];
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
    { Summary = "Takes the stars out of the image and keeps them as an image of their own.",
        Joint = true,
        ExtraResults = [new OperationResultRole(Stars, "The stars it took out: the input less its starless plate.")],
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<RemoveStarsSettings> SettingsInfo => OperationSettingsJsonContext.Default.RemoveStarsSettings;

    /// <summary>The remover registered here and, with core protection on, the catalogue that would answer.</summary>
    public override string ServedBy(RemoveStarsSettings settings) => Served(settings).By;

    /// <summary>The name (with the core catalogue) and the origin word from one resolution of the remover (<see cref="IEnhancerAvailability.ServedOf"/>).</summary>
    public override (string By, string Tag) Served(RemoveStarsSettings settings)
    {
        var (name, tag) = IEnhancerAvailability.ServedOf(starRemover, EnhanceOptions.Default);
        return (name + (settings.ProtectCores ? "; cores by " + CoreCatalogue.Source(settings.Catalogue) : string.Empty), tag);
    }

    /// <inheritdoc/>
    public override string ServedTag(RemoveStarsSettings settings) => Served(settings).Tag;

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
