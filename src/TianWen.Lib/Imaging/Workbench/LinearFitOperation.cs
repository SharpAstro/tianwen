using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// Puts a mono master on another master's scale (PixInsight's LinearFit): <see cref="LinearFit.Measure"/> and
/// <see cref="LinearFit.Apply"/>, and nothing between them. <c>image linear-fit</c> calls this for each master it is
/// given.
/// </summary>
/// <remarks>
/// It takes the master first and its reference second, both read as masters (their absent pixels absent), and writes
/// the fitted master on its own scale with the reference's plate solution. The slope travels in the file as
/// <c>FLUXSCAL</c> (<see cref="ImageMeta.FluxScale"/>), which <c>image add-line</c> reads.
/// </remarks>
public sealed class LinearFitOperation : Operation<LinearFitSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "linear-fit";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Linear fit",
        Verb: "image linear-fit",
        OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("reference", "The master whose scale the image is put on (green, as a rule).")],
        Settings:
        [
            new OperationSetting("rejectHigh", "Pixels at or above this fraction of either image's peak carry no weight.",
                "PixInsight's LinearFit default."),
        ]);

    /// <inheritdoc/>
    protected override JsonTypeInfo<LinearFitSettings> SettingsInfo => OperationSettingsJsonContext.Default.LinearFitSettings;

    /// <inheritdoc/>
    public override string? Refusal(LinearFitSettings settings)
        => double.IsFinite(settings.RejectHigh) && settings.RejectHigh > 0 ? null : "rejectHigh is a fraction of the peak, above 0";

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(LinearFitSettings settings, IReadOnlyList<string> inputs, string output)
        =>
        [
            StepCommandLine.Program, "image", "linear-fit", inputs[1], inputs[0], "-o", output,
            "--reject-high", StepCommandLine.Number(settings.RejectHigh),
        ];

    /// <inheritdoc/>
    public override ValueTask<OperationOutcome> RunAsync(LinearFitSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (inputs.Count != 2)
        {
            return ValueTask.FromResult(OperationOutcome.BadInput($"a linear fit takes a master and its reference, and was handed {inputs.Count} image(s)"));
        }

        var (target, reference) = (inputs[0], inputs[1]);
        if (!reference.TryReadMaster(out var referenceMaster, out var referenceWcs, out var refusal)
            || !target.TryReadMaster(out var master, out var wcs, out refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        if (master.Width != referenceMaster.Width || master.Height != referenceMaster.Height)
        {
            return ValueTask.FromResult(OperationOutcome.BadInput("the masters are not on one grid: put them there first (tianwen image align)."));
        }

        var fit = LinearFit.Measure(master, referenceMaster, LinearFit.DefaultRejectLow, settings.RejectHigh);
        if (!double.IsFinite(fit.Slope) || fit.Slope <= 0)
        {
            return ValueTask.FromResult(OperationOutcome.BadInput($"{target.Name}: no line fits ({fit.Pixels} pixels)."));
        }

        say?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"{target.Name}: {fit.Offset:G5} + {fit.Slope:G5} x ({fit.Pixels} pixels, mean absolute deviation {fit.MeanAbsoluteDeviation:G4})"));
        // The reference's plate solution first, as the verb has always written it: the two are on one grid.
        OperationOutput.Write(LinearFit.Apply(master, fit), outputPath, referenceWcs ?? wcs);
        say?.Invoke($"wrote {outputPath}");
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}
