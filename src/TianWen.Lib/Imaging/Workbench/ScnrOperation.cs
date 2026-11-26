using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// SCNR on a stars plate (<see cref="Image.SubtractiveChromaticNoise"/>): green taken down to where a neutral star has it.
/// The step the enhance runs on its stars before they go back (<see cref="Enhancement.ScnrStarsStep"/>), alone, so a chain
/// of single steps does what the enhance does (the owner's choice, 2026-11-26). <c>image scnr</c> calls this.
/// </summary>
/// <remarks>
/// <para><b>Balanced colour only.</b> A neutral star has red, green and blue equal only once the colour is balanced. On a
/// raw OSC plate green is the strongest channel of almost every star (two green photosites, the QE peak), so SCNR there
/// strips green from all of them and the balance applied afterwards turns them magenta (a whole globular cluster on the
/// dataset gallery's Omega Centauri). So the step refuses a plate whose colour is not balanced in its pixels
/// (<see cref="ColourCalibration.Applied"/>, the <c>WBAPPLD</c> card), as the enhance skips it there.</para>
/// <para>The plate is read as written: a stars image is exactly zero wherever the remover left a pixel alone, which is a
/// value there, not the absent ring a master marks with zero. The result is on the plate's own scale.</para>
/// </remarks>
public sealed class ScnrOperation : Operation<ScnrSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "scnr";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "SCNR",
        Verb: "image scnr",
        OperationOutputKind.Stored,
        ExtraInputs: [],
        Settings:
        [
            new OperationSetting("mode", "What green is taken down to: average, the mean of red and blue (the enhance's), or maximum, the larger "
                + "of them, which takes more."),
            new OperationSetting("amount", "How much of the way green goes, from above 0 to 1."),
        ]);

    /// <inheritdoc/>
    protected override JsonTypeInfo<ScnrSettings> SettingsInfo => OperationSettingsJsonContext.Default.ScnrSettings;

    /// <inheritdoc/>
    public override string? Refusal(ScnrSettings settings)
        => settings.Mode is ScnrMode.None ? "the mode is average or maximum"
            : float.IsFinite(settings.Amount) && settings.Amount is > 0f and <= 1f ? null
            : "the amount is above 0 and at most 1";

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(ScnrSettings settings, IReadOnlyList<string> inputs, string output)
        => [StepCommandLine.Program, "image", "scnr", inputs[0], "-o", output,
            "--mode", settings.Mode.ToString().ToLowerInvariant(), "--amount", StepCommandLine.Number(settings.Amount)];

    /// <inheritdoc/>
    public override ValueTask<OperationOutcome> RunAsync(ScnrSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (!inputs[0].TryRead(out var stars, out var wcs, out var refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        if (stars.ChannelCount < 3)
        {
            return ValueTask.FromResult(OperationOutcome.BadInput($"{inputs[0].Name} is not colour, and SCNR takes green out of a colour image"));
        }

        if (stars.ImageMeta.ColourCalibration is not { Applied: true })
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(
                $"{inputs[0].Name}'s colour is not balanced in its pixels, so green is not where a neutral star has it and SCNR would turn "
                + "its stars magenta: put the white balance in first (the enhance's whiteBalance, or a master the stack balanced)"));
        }

        var result = stars.SubtractiveChromaticNoise(settings.Mode, settings.Amount);
        OperationOutput.Write(result, outputPath, wcs);
        say?.Invoke($"{inputs[0].Name}: SCNR {settings.Mode.ToString().ToLowerInvariant()} at {StepCommandLine.Number(settings.Amount)}");
        say?.Invoke($"wrote {outputPath}");
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}
