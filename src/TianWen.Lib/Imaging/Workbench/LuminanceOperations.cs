using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// A synthetic luminance from red, green and blue masters WITH their stars (<see cref="SyntheticLuminance"/>): each
/// on green's scale by its stars' flux ratios, weighted by its noise over 4 px blocks there.
/// <c>image luminance</c> calls this.
/// </summary>
/// <remarks>
/// The edit is on the GREEN master, whose scale the luminance is on, and takes red and blue. Where the masters differ
/// in star width it also takes red, green and blue copies to read the star scales on (the <c>image match-psf</c>
/// results): all three or none. The noise is still read on the masters, and they are what is combined.
/// </remarks>
public sealed class LuminanceOperation : Operation<NoSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "luminance";

    private static readonly string[] Names = ["red", "green", "blue"];

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Luminance",
        Verb: "image luminance",
        OperationOutputKind.Stored,
        ExtraInputs:
        [
            new OperationInputRole("red", "The red master."),
            new OperationInputRole("blue", "The blue master."),
            new OperationInputRole("measure red", "A red copy to read the star scales on (image match-psf).", Optional: true),
            new OperationInputRole("measure green", "A green copy to read the star scales on.", Optional: true),
            new OperationInputRole("measure blue", "A blue copy to read the star scales on.", Optional: true),
        ],
        Settings: []) { Summary = "Builds a luminance image from the colour masters, matched to a common star size." };

    /// <inheritdoc/>
    protected override JsonTypeInfo<NoSettings> SettingsInfo => OperationSettingsJsonContext.Default.NoSettings;

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(NoSettings settings, IReadOnlyList<string> inputs, string output)
    {
        ImmutableArray<string> line = [StepCommandLine.Program, "image", "luminance", "--red", inputs[1], "--green", inputs[0], "--blue", inputs[2], "-o", output];
        return inputs.Count == 6 ? line.AddRange(new[] { "--measure-on", inputs[3], inputs[4], inputs[5] }) : line;
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(NoSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (inputs.Count is not (3 or 6))
        {
            return OperationOutcome.BadInput("a luminance takes green, red and blue, and with them red, green and blue copies to measure on: all three or none");
        }

        // Red, green, blue: the order the routine takes them in, with green the reference.
        if (!OperationInputs.TryReadMasters([inputs[1], inputs[0], inputs[2]], out var masters, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        Image[]? measureOn = null;
        if (inputs.Count == 6)
        {
            if (!OperationInputs.TryReadMasters([inputs[3], inputs[4], inputs[5]], out measureOn, out _, out refusal))
            {
                return OperationOutcome.BadInput(refusal);
            }

            if (!OperationInputs.OnOneGrid(masters[0], measureOn[0]))
            {
                return OperationOutcome.BadInput("the copies to measure on are red, green and blue on the masters' grid");
            }
        }

        var (luminance, parts, noise) = await SyntheticLuminance.BuildAsync(masters, measureOn, reference: 1, cancellationToken);
        for (var i = 0; i < parts.Length; i++)
        {
            say?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"{Names[i]}: scale {parts[i].Scale:G4} onto green ({parts[i].Stars} stars), noise {parts[i].ScaledNoise:G4} on green's scale, weight {parts[i].Weight:P1}"));
        }

        var best = Math.Min(parts[0].ScaledNoise, Math.Min(parts[1].ScaledNoise, parts[2].ScaledNoise));
        say?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"noise {noise:G4} against the best single channel's {best:G4}: {best / noise:F2}x its signal to noise"));
        OperationOutput.Write(luminance, outputPath, wcs);
        say?.Invoke($"wrote {outputPath}");
        return OperationOutcome.Done;
    }
}

/// <summary>
/// Gives a channel the luminance's fine detail (<see cref="LuminanceDetail"/>, LRGB in linear form): blur(channel) +
/// (L - blur(L)) / scale, the scale measured between the two by a linear fit. <c>image lrgb</c> calls this for each
/// channel it is given.
/// </summary>
/// <remarks>Run it on starless planes; the stars go back after (<see cref="AddStarsOperation"/>), in their own colour.</remarks>
public sealed class LrgbOperation : Operation<LrgbSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "lrgb";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "LRGB",
        Verb: "image lrgb",
        OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("luminance", "The (starless, denoised) luminance whose detail the image is given.")],
        Settings: [new OperationSetting("colourSigma", "The blur in pixels the image keeps its own colour above; below it, the luminance's detail.")]) { Summary = "Gives a colour image the fine detail of a luminance image while it keeps its own colour." };

    /// <inheritdoc/>
    protected override JsonTypeInfo<LrgbSettings> SettingsInfo => OperationSettingsJsonContext.Default.LrgbSettings;

    /// <inheritdoc/>
    public override string? Refusal(LrgbSettings settings)
        => float.IsFinite(settings.ColourSigma) && settings.ColourSigma > 0 ? null : "colourSigma is a blur in pixels, above 0";

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(LrgbSettings settings, IReadOnlyList<string> inputs, string output)
        =>
        [
            StepCommandLine.Program, "image", "lrgb", "--luminance", inputs[1], inputs[0], "-o", output,
            "--colour-sigma", StepCommandLine.Number(settings.ColourSigma),
        ];

    /// <inheritdoc/>
    public override ValueTask<OperationOutcome> RunAsync(LrgbSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (inputs.Count != 2)
        {
            return ValueTask.FromResult(OperationOutcome.BadInput($"lrgb takes a channel and the luminance, and was handed {inputs.Count} image(s)"));
        }

        // The luminance first, as the verb has always read them: its plate solution is the one written.
        if (!OperationInputs.TryReadMasters([inputs[1], inputs[0]], out var planes, out var wcs, out var refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        var (luminance, channel) = (planes[0], planes[1]);
        var scale = LuminanceDetail.ScaleFor(channel, luminance);
        if (!double.IsFinite(scale))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput($"{inputs[0].Name}: no scale could be measured against the luminance."));
        }

        // LuminanceDetail.Transfer's two halves, the scale checked first so a refusal is an answer, not a throw.
        var detailed = LuminanceDetail.Apply(channel, luminance, scale, settings.ColourSigma);
        say?.Invoke(string.Create(CultureInfo.InvariantCulture, $"{inputs[0].Name}: the luminance's detail at 1/{scale:G4}"));
        OperationOutput.Write(detailed, outputPath, wcs);
        say?.Invoke($"wrote {outputPath}");
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}
