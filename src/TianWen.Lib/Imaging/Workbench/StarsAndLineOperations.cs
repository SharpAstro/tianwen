using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// Puts the stars back: the starless plate plus the stars <c>image remove-stars</c> took out
/// (<see cref="NarrowbandCombination.WithStars"/>, PixelMath's starless + stars). <c>image add-stars</c> calls this.
/// </summary>
/// <remarks>
/// The stars are read as written: a stars image is exactly zero wherever the remover left a pixel alone, which is a
/// value there, not the absent ring a master marks with zero. The plate is read as a master.
/// </remarks>
public sealed class AddStarsOperation : Operation<NoSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "add-stars";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Add stars",
        Verb: "image add-stars",
        OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("stars", "The stars image remove-stars wrote beside the starless plate.")],
        Settings: []);

    /// <inheritdoc/>
    protected override JsonTypeInfo<NoSettings> SettingsInfo => OperationSettingsJsonContext.Default.NoSettings;

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(NoSettings settings, IReadOnlyList<string> inputs, string output)
        => [StepCommandLine.Program, "image", "add-stars", "--starless", inputs[0], "--stars", inputs[1], "-o", output];

    /// <inheritdoc/>
    public override ValueTask<OperationOutcome> RunAsync(NoSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (inputs.Count != 2)
        {
            return ValueTask.FromResult(OperationOutcome.BadInput($"add-stars takes a starless plate and its stars, and was handed {inputs.Count} image(s)"));
        }

        if (!inputs[0].TryReadMaster(out var starless, out var wcs, out var refusal)
            || !inputs[1].TryRead(out var stars, out _, out refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        if (!OperationInputs.OnOneGrid(starless, stars))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(OperationInputs.NotOnOneGrid));
        }

        OperationOutput.Write(NarrowbandCombination.WithStars(starless, stars), outputPath, wcs);
        say?.Invoke($"wrote {outputPath}");
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}

/// <summary>
/// A narrowband line's emission into a broadband channel, the HaRGB step
/// (<see cref="NarrowbandCombination.AddLineFrom"/>): the line's continuum taken out against the broadband at the scale
/// given, the rest added. <c>image add-line</c> calls this.
/// </summary>
/// <remarks>
/// Run it on starless plates, so no star is left a pit or a ring. The scale is the one number of a colour composition
/// carried by hand: it is measured on the masters WITH their stars (<c>image continuum --dry-run</c>), since on starless
/// plates the only structure the two share is the line.
/// </remarks>
public sealed class AddLineOperation : Operation<AddLineSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "add-line";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Add line",
        Verb: "image add-line",
        OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("line", "The narrowband master (H-alpha) whose emission goes into the image.")],
        Settings:
        [
            new OperationSetting("scale", "The continuum scale between the line and the image, as image continuum --dry-run says it on the masters "
                + "with their stars. Needed: there is no default."),
            new OperationSetting("weight", "How much of the pure line goes in, in the image's own units: 1 adds it once more at the line master's "
                + "signal to noise."),
        ]);

    /// <inheritdoc/>
    protected override JsonTypeInfo<AddLineSettings> SettingsInfo => OperationSettingsJsonContext.Default.AddLineSettings;

    /// <inheritdoc/>
    public override string? Refusal(AddLineSettings settings)
    {
        if (settings.Scale is not { } scale || !double.IsFinite(scale))
        {
            return "scale is needed: the continuum scale between the two masters, as image continuum --dry-run says it";
        }

        return double.IsFinite(settings.Weight) ? null : "weight is a number";
    }

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(AddLineSettings settings, IReadOnlyList<string> inputs, string output)
        =>
        [
            StepCommandLine.Program, "image", "add-line", "--broadband", inputs[0], "--line", inputs[1], "-o", output,
            "--scale", StepCommandLine.Number(settings.Scale ?? double.NaN),
            "--weight", StepCommandLine.Number(settings.Weight),
        ];

    /// <inheritdoc/>
    public override ValueTask<OperationOutcome> RunAsync(AddLineSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (inputs.Count != 2 || settings.Scale is not { } scale)
        {
            return ValueTask.FromResult(OperationOutcome.BadInput("add-line takes a broadband master, the line master and the continuum scale between them"));
        }

        if (!OperationInputs.TryReadMasters(inputs, out var masters, out var wcs, out var refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        var (withLine, weight) = NarrowbandCombination.AddLineFrom(masters[0], masters[1], scale, settings.Weight);
        say?.Invoke(string.Create(CultureInfo.InvariantCulture, $"continuum scale {scale:G5}; the line added at {weight:G4}"));
        OperationOutput.Write(withLine, outputPath, wcs);
        say?.Invoke($"wrote {outputPath}");
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}

/// <summary>
/// The dig fill alone, the step <c>image remove-stars</c> runs after TianWen's own models
/// (<see cref="NarrowbandCombination.FillDigsAlone"/>, #2204): where a star was taken, a hole dug under it is filled to
/// the sky around it, and the plate is then held at or under the input. <c>image fill-digs</c> calls this.
/// </summary>
/// <remarks>
/// The plate is read as written: remove-stars wrote its absent pixels as NaN, and an exact zero in it is a value the
/// model gave. The master the stars were removed from is read as a master. No model runs.
/// </remarks>
public sealed class FillDigsOperation : Operation<FillDigsSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "fill-digs";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Fill digs",
        Verb: "image fill-digs",
        OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("input", "The master the stars were removed from.")],
        Settings:
        [
            new OperationSetting("keepOvershoot", "Leave the filled plate where it stands over the input. By default it is held at or under "
                + "the input, as remove-stars holds it."),
        ]);

    /// <inheritdoc/>
    protected override JsonTypeInfo<FillDigsSettings> SettingsInfo => OperationSettingsJsonContext.Default.FillDigsSettings;

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(FillDigsSettings settings, IReadOnlyList<string> inputs, string output)
    {
        ImmutableArray<string> line = [StepCommandLine.Program, "image", "fill-digs", "--input", inputs[1], "--starless", inputs[0], "-o", output];
        return settings.KeepOvershoot ? line.Add("--keep-overshoot") : line;
    }

    /// <inheritdoc/>
    public override ValueTask<OperationOutcome> RunAsync(FillDigsSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (inputs.Count != 2)
        {
            return ValueTask.FromResult(OperationOutcome.BadInput($"fill-digs takes a starless plate and the master it came from, and was handed {inputs.Count} image(s)"));
        }

        // The master's plate solution, as the verb has always written it.
        if (!inputs[1].TryReadMaster(out var input, out var wcs, out var refusal)
            || !inputs[0].TryRead(out var starless, out _, out refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        if (!OperationInputs.OnOneGrid(input, starless))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(OperationInputs.NotOnOneGrid));
        }

        var (plate, share) = NarrowbandCombination.FillDigsAlone(input, starless, holdAtOrUnderPlane: !settings.KeepOvershoot);
        say?.Invoke(string.Create(CultureInfo.InvariantCulture, $"{inputs[0].Name}: dig-fill step covered {share:P1} of the pixels"));
        OperationOutput.Write(plate, outputPath, wcs);
        say?.Invoke($"wrote {outputPath}");
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}
