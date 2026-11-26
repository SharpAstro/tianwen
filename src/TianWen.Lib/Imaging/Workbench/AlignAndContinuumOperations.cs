using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// Puts a master on another master's grid (<see cref="MasterAlignment.AlignAsync"/>): matched by their stars and
/// resampled once (Lanczos-3, clamped); where the master does not reach is absent (NaN). <c>image align</c> calls this
/// for each master it is given.
/// </summary>
/// <remarks>
/// What any per-pixel combination needs first. A pair meant to be subtracted (a line and its continuum) is best put on
/// a THIRD master's grid, so both are resampled alike. The result carries the reference's plate solution.
/// </remarks>
public sealed class AlignOperation : Operation<NoSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "align";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Align",
        Verb: "image align",
        OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("reference", "The master whose grid the image is put on; it is not resampled.")],
        Settings: []) { Summary = "Puts the image on the pixel grid of a reference master so the two can be combined." };

    /// <inheritdoc/>
    protected override JsonTypeInfo<NoSettings> SettingsInfo => OperationSettingsJsonContext.Default.NoSettings;

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(NoSettings settings, IReadOnlyList<string> inputs, string output)
        => [StepCommandLine.Program, "image", "align", inputs[1], inputs[0], "-o", output];

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(NoSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (inputs.Count != 2)
        {
            return OperationOutcome.BadInput($"align takes a master and its reference, and was handed {inputs.Count} image(s)");
        }

        if (!inputs[1].TryRead(out var reference, out var referenceWcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        var referenceStars = await MasterAlignment.FindStarsAsync(reference, cancellationToken);
        say?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"reference {inputs[1].Name}: {reference.Width}x{reference.Height}, {referenceStars.Count} stars, "
            + $"median FWHM {MasterAlignment.MedianFwhm(referenceStars):F2} px"));
        var outcome = await AlignAsync(reference, referenceWcs, referenceStars, inputs[0], outputPath, say, cancellationToken);
        reference.Release();
        return outcome;
    }

    /// <summary>
    /// One master onto a reference that has already been read and its stars found: what <c>image align</c> calls for
    /// each of its masters, so the reference's stars are read once.
    /// </summary>
    public static async ValueTask<OperationOutcome> AlignAsync(Image reference, WCS? referenceWcs, StarList referenceStars, OperationInput moving, string outputPath,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (!moving.TryRead(out var master, out _, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        var (aligned, reason) = await MasterAlignment.AlignAsync(reference, master, referenceStars, cancellationToken);
        master.Release();
        if (aligned is null)
        {
            return OperationOutcome.BadInput($"{moving.Name}: not aligned, {reason}");
        }

        OperationOutput.Write(aligned.Image, outputPath, referenceWcs);
        say?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"{moving.Name}: {aligned.Stars} stars, matched on {aligned.QuadStars} at tolerance {aligned.QuadTolerance:F2}, "
            + $"rms {aligned.RmsPx:F3} px; scale {aligned.Scale:F5}, rotation {aligned.RotationDeg:F3} deg, "
            + $"shift ({aligned.ToReference.M31:F1}, {aligned.ToReference.M32:F1}) px; median FWHM {aligned.MedianFwhm:F2} px -> {outputPath}"));
        aligned.Image.Release();
        return OperationOutcome.Done;
    }
}

/// <summary>
/// The continuum taken out of a narrowband master against a broadband one on the same grid
/// (<see cref="ContinuumSubtractor"/>, docs/plans/narrowband-colour.md phase 0, #874): line - k (continuum - median
/// continuum), k measured two ways, the flattest residual and the stars' flux ratio. <c>image continuum</c> calls
/// <see cref="SubtractAsync"/>.
/// </summary>
public sealed class ContinuumOperation : Operation<ContinuumSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "continuum";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Continuum",
        Verb: "image continuum",
        OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("continuum", "The broadband master the continuum is read from (red for Ha and SII, green or blue for OIII), on the same grid.")],
        Settings:
        [
            new OperationSetting("scale", "Subtract with this scale rather than a measured one."),
            new OperationSetting("method", "Which measured scale to subtract with: flattest (the least-deviation residual over the continuum's "
                + "signal) or photometric (the stars' median flux ratio). Both are always reported."),
            new OperationSetting("significance", "A continuum pixel carries the flattest fit only this many of its noise above its median."),
        ]) { Summary = "Subtracts the continuum of a broadband master from a narrowband one, leaving the emission line alone." };

    /// <inheritdoc/>
    protected override JsonTypeInfo<ContinuumSettings> SettingsInfo => OperationSettingsJsonContext.Default.ContinuumSettings;

    /// <inheritdoc/>
    public override string? Refusal(ContinuumSettings settings)
    {
        if (settings.Scale is { } scale && !double.IsFinite(scale))
        {
            return "scale is a number, or left out to be measured";
        }

        return double.IsFinite(settings.Significance) ? null : "significance is a number";
    }

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(ContinuumSettings settings, IReadOnlyList<string> inputs, string output)
    {
        ImmutableArray<string> line =
        [
            StepCommandLine.Program, "image", "continuum", "--line", inputs[0], "--continuum", inputs[1], "-o", output,
            "--method", settings.Method.ToString().ToLowerInvariant(),
            "--significance", StepCommandLine.Number(settings.Significance),
        ];
        return settings.Scale is { } scale ? line.AddRange(new[] { "--scale", StepCommandLine.Number(scale) }) : line;
    }

    /// <inheritdoc/>
    public override ValueTask<OperationOutcome> RunAsync(ContinuumSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => inputs.Count == 2
            ? SubtractAsync(settings, inputs[0], inputs[1], outputPath, say, cancellationToken)
            : ValueTask.FromResult(OperationOutcome.BadInput($"continuum takes a line master and its continuum master, and was handed {inputs.Count} image(s)"));

    /// <summary>
    /// Measures the two scales, reports them, and subtracts with the one the settings choose.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="lineInput">The narrowband master.</param>
    /// <param name="continuumInput">The broadband master, on the same grid.</param>
    /// <param name="outputPath">The FITS file to write; null to report the scales and write nothing (<c>--dry-run</c>).</param>
    /// <param name="say">Takes each line reported, or null.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    public async ValueTask<OperationOutcome> SubtractAsync(ContinuumSettings settings, OperationInput lineInput, OperationInput continuumInput, string? outputPath,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (!lineInput.TryRead(out var line, out var lineWcs, out var refusal)
            || !continuumInput.TryRead(out var continuum, out _, out refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        if (line.Width != continuum.Width || line.Height != continuum.Height)
        {
            return OperationOutcome.BadInput(
                $"the line is {line.Width}x{line.Height} and the continuum {continuum.Width}x{continuum.Height}: put them on one grid first (tianwen image align).");
        }

        var maskedLine = MasterAlignment.MaskAbsent(line);
        var maskedContinuum = MasterAlignment.MaskAbsent(continuum);
        var flattest = ContinuumSubtractor.FlattestResidualScale(maskedLine, maskedContinuum,
            MasterAlignment.StarChannel(maskedLine), MasterAlignment.StarChannel(maskedContinuum), settings.Significance);
        var lineStars = await MasterAlignment.FindStarsAsync(maskedLine, cancellationToken);
        var continuumStars = await MasterAlignment.FindStarsAsync(maskedContinuum, cancellationToken);
        var (photometric, matched, spread) = ContinuumSubtractor.PhotometricScale(lineStars, continuumStars);
        var lineFwhm = MasterAlignment.MedianFwhm(lineStars);
        var continuumFwhm = MasterAlignment.MedianFwhm(continuumStars);
        say?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"flattest residual k {flattest.K:R} over {flattest.Pixels} pixels (residual deviation {flattest.ResidualAad:G4} "
            + $"against the line's {flattest.LineAad:G4}); photometric k {photometric:G5} over {matched} stars (spread {spread:P1}); "
            + $"FWHM line {lineFwhm:F2} px, continuum {continuumFwhm:F2} px"));
        if (double.IsFinite(flattest.K) && double.IsFinite(photometric) && Math.Abs(flattest.K - photometric) > 0.1 * Math.Abs(photometric))
        {
            say?.Invoke("the two scales differ by more than 10 percent: check the pairing, the grid and the two PSFs before trusting either.");
        }

        if (double.IsFinite(lineFwhm) && double.IsFinite(continuumFwhm) && Math.Abs(lineFwhm - continuumFwhm) > 0.15 * Math.Max(lineFwhm, continuumFwhm))
        {
            say?.Invoke("the two masters' stars differ in width by more than 15 percent: subtracted stars will leave rings, and a fixed-aperture flux ratio reads the wider side low.");
        }

        var k = settings.Scale ?? (settings.Method is ContinuumMethod.Photometric ? photometric : flattest.K);
        var outcome = OperationOutcome.Done;
        if (!double.IsFinite(k))
        {
            outcome = OperationOutcome.BadInput($"no {settings.Method.ToString().ToLowerInvariant()} scale could be measured; give the scale.");
        }
        else if (outputPath is not null)
        {
            var pure = ContinuumSubtractor.Subtract(maskedLine, maskedContinuum, k);
            OperationOutput.Write(pure, outputPath, lineWcs);
            pure.Release();
            say?.Invoke(string.Create(CultureInfo.InvariantCulture, $"subtracted with k {k:R} -> {outputPath}"));
        }

        line.Release();
        continuum.Release();
        return outcome;
    }
}
