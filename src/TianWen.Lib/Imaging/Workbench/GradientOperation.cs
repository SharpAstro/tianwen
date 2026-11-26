using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>What <see cref="GradientOperation.ExtractAsync"/> made. The caller owns both images and releases each.</summary>
/// <param name="Flattened">The image with its gradient taken out, unit-referred and linear.</param>
/// <param name="Background">The background that was estimated, when it was asked for and the extractor has one.</param>
public sealed record GradientResult(Image Flattened, Image? Background);

/// <summary>
/// Gradient correction: the smooth background (light pollution, vignette, sky glow) estimated and taken out, by the
/// classical fit (<see cref="IBackgroundExtractor"/>) or the registered corrector (<see cref="IGradientCorrector"/>:
/// GraXpert's model where its weights are installed). <c>image flatten</c> calls <see cref="ExtractAsync"/>.
/// </summary>
/// <remarks>
/// A whole-frame fit: its answer for a part of an image is another answer, so it has no preview on a region. What
/// serves it is part of its step's key (<see cref="ServedBy"/>), so a result of GraXpert's model is never taken for
/// the classical fit's on a computer where "auto" means the other one.
/// </remarks>
public sealed class GradientOperation(
    IBackgroundExtractor backgroundExtractor,
    IGradientCorrector gradientCorrector,
    ILogger<GradientOperation>? logger = null) : Operation<GradientSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "gradient";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Gradient",
        Verb: "image flatten",
        OperationOutputKind.Stored,
        ExtraInputs: [],
        Settings:
        [
            new OperationSetting("backend", "auto: the registered corrector (GraXpert's model when its weights are installed, the classical fit "
                + "otherwise). classical: always the classical fit. graxpert: GraXpert's model, refused where it is not installed."),
            new OperationSetting("degree", "Classical only. The polynomial's degree, 0 to 6.",
                "The three reference implementations agree on 2: 1 cannot follow a dome, and above 3 the fit follows the nebula "
                + "(docs/plans/background-extraction.md)."),
            new OperationSetting("downsample", "Classical only. The block-mean factor of the working grid, 1 to 32."),
            new OperationSetting("surface", "Classical only. Add the inpainted low-pass surface on the polynomial's residual.",
                "Off: over 118 real masters it drops the kept fraction from 0.795 to 0.581, because a flexible surface hollows a "
                + "frame-filling nebula (docs/plans/background-extraction.md)."),
            new OperationSetting("divide", "Classical only. Divide by the background where the default subtracts it, for a multiplicative residue."),
            new OperationSetting("exclusions", "Classical only. Regions the fit must not sample: each a polygon in full-image pixels, written x, y, x, y and so on."),
        ]) { Summary = "Removes the smooth background gradient from the image." };

    /// <inheritdoc/>
    protected override JsonTypeInfo<GradientSettings> SettingsInfo => OperationSettingsJsonContext.Default.GradientSettings;

    /// <inheritdoc/>
    public override string? Refusal(GradientSettings settings)
    {
        if (settings.Exclusions.IsDefault)
        {
            return "exclusions is a list: leave it out for none";
        }

        foreach (var polygon in settings.Exclusions)
        {
            if (polygon.IsDefault || polygon.Length < 6 || polygon.Length % 2 != 0)
            {
                return "an exclusion is a polygon of at least three vertices, written x, y, x, y and so on";
            }
        }

        var defaults = BackgroundExtractionOptions.Default;
        var tuned = settings.Degree != defaults.PolynomialDegree || settings.Downsample != defaults.Downsample
            || settings.Surface || settings.Divide || !settings.Exclusions.IsEmpty;
        if (settings.Backend is not GradientBackend.Classical)
        {
            return tuned ? "degree, downsample, surface, divide and exclusions tune the classical fit: set backend to classical with them" : null;
        }

        try
        {
            ClassicalOptions(settings).Validate();
            return null;
        }
        catch (ArgumentException ex)
        {
            // The options say what is out of range in their own words; this is the one place they are asked.
            return ex.Message;
        }
    }

    /// <summary>The classical fit's own name, or the registered corrector's: what makes the pixels for these settings.</summary>
    public override string ServedBy(GradientSettings settings)
        => settings.Backend is GradientBackend.Classical ? "classical" : gradientCorrector.Name;

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(GradientSettings settings, IReadOnlyList<string> inputs, string output)
    {
        var line = ImmutableArray.CreateBuilder<string>();
        line.AddRange(StepCommandLine.Program, "image", "flatten", inputs[0], "-o", output, "--backend", settings.Backend.ToString().ToLowerInvariant());
        if (settings.Backend is GradientBackend.Classical)
        {
            line.AddRange("--degree", StepCommandLine.Number(settings.Degree), "--downsample", StepCommandLine.Number(settings.Downsample));
            if (settings.Surface)
            {
                line.Add("--surface");
            }

            if (settings.Divide)
            {
                line.Add("--divide");
            }

            foreach (var polygon in settings.Exclusions)
            {
                line.Add("--exclude");
                line.Add(string.Join(';', Vertices(polygon).Select(static vertex => $"{StepCommandLine.Number(vertex.X)},{StepCommandLine.Number(vertex.Y)}")));
            }
        }

        return line.ToImmutable();
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(GradientSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (!inputs[0].TryRead(out var source, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        GradientResult? result;
        OperationOutcome outcome;
        try
        {
            (result, outcome) = await ExtractAsync(source, settings, keepBackground: false, say, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An extractor is a model or a fit that can fail on a frame for reasons of its own; the step says so and
            // a run goes on to the next image. A cancellation is not a failure and is not caught.
            logger?.LogError(ex, "Gradient correction failed for {Input}", inputs[0].Name);
            return OperationOutcome.Failed(ex.Message);
        }

        if (result is null)
        {
            return outcome;
        }

        OperationOutput.Write(result.Flattened, outputPath, wcs);
        result.Flattened.Release();
        say?.Invoke($"wrote {outputPath}");
        return OperationOutcome.Done;
    }

    /// <summary>
    /// Estimates and removes the gradient of <paramref name="source"/>: the whole of the work, without the files.
    /// An extractor's own failure is thrown, for the caller to say in its own words.
    /// </summary>
    /// <param name="source">The image as read. It is put on the unit scale here, as every enhancer wants it.</param>
    /// <param name="settings">The settings, already accepted (<see cref="Refusal"/>).</param>
    /// <param name="keepBackground">Also hand back the estimated background, where the extractor has one.</param>
    /// <param name="say">Takes each line the fit reports, or null.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    /// <returns>The result, or null with the outcome saying why there is none.</returns>
    public async Task<(GradientResult? Result, OperationOutcome Outcome)> ExtractAsync(
        Image source, GradientSettings settings, bool keepBackground, Action<string>? say, CancellationToken cancellationToken)
    {
        if (settings.Backend is GradientBackend.Graxpert && !gradientCorrector.Name.Contains("GraXpert", StringComparison.OrdinalIgnoreCase))
        {
            // Naming a backend has to mean it. 'auto' is the mode that substitutes; asking for GraXpert on a computer
            // without its weights and getting the classical fit would make a result depend on where it was made.
            return (null, OperationOutcome.NotServed(
                $"the GraXpert BGE weights are not installed, so the active corrector is '{gradientCorrector.Name}'. Install GraXpert and "
                + "run it once (its model cache is read in place), or use the classical or the auto backend."));
        }

        var normalised = source.ScaleFloatValuesToUnit();
        if (settings.Backend is GradientBackend.Classical)
        {
            // The classical extractor hands back the model whether or not it was asked for.
            var extraction = await backgroundExtractor.ExtractAsync(normalised, ClassicalOptions(settings), cancellationToken);
            if (!keepBackground)
            {
                extraction.Background.Release();
            }

            // The kept fraction is the one number that says whether the fit sampled the sky or the object: the share
            // of working pixels the last iteration was made on, which a frame-filling nebula drives down. G1 measured
            // 0.795 with the surface off against 0.581 with it on over 118 real masters, so a run well under that is
            // the signal to look at the exclusions or to turn the surface back off.
            foreach (var plane in extraction.Planes)
            {
                say?.Invoke(
                    $"  plane {plane.Plane}: kept {plane.KeptFraction:P1}"
                    // P2, and only above a hundredth of a percent: a TianWen master's canvas ring makes this nonzero
                    // on almost every real frame, and "excluded 0.0%" reads as a bug rather than as the edge blocks
                    // it is.
                    + (plane.ExcludedFraction >= 1e-4f ? $" excluded {plane.ExcludedFraction:P2}" : "")
                    + $" residual {plane.ResidualSigma:0.###e+0} sigma, level {plane.Level:0.####}"
                    + $", {plane.Iterations} iterations{(plane.Converged ? "" : " (NOT converged)")}");
            }

            return (new GradientResult(extraction.Cleaned, keepBackground ? extraction.Background : null), OperationOutcome.Done);
        }

        // A model takes [0, 1]: an input that says it is on a unit scale with highlights above 1.0 (a deblurred star, #2504)
        // goes in divided by its peak and its result comes back multiplied by it, so the step writes on its input's scale.
        // The classical fit above works on any scale and needs neither.
        var divisor = normalised.ModelRangeDivisor;
        var into = divisor > 1f ? normalised.MultipliedBy(1f / divisor, normalised.ImageMeta.Rescale(1f / divisor)) : normalised;
        var (corrected, estimated) = keepBackground
            ? await gradientCorrector.EnhanceAndEstimateBackgroundAsync(into, cancellationToken)
            : (await gradientCorrector.EnhanceAsync(into, cancellationToken), null);
        if (divisor > 1f)
        {
            var back = corrected.MultipliedBy(divisor, corrected.ImageMeta.Rescale(divisor) with { UnitReferred = true });
            var backBackground = estimated?.MultipliedBy(divisor, estimated.ImageMeta.Rescale(divisor) with { UnitReferred = true });
            // A corrector that passed its input through handed back `into`, which is ours either way.
            if (!ReferenceEquals(corrected, into))
            {
                corrected.Release();
            }
            estimated?.Release();
            into.Release();
            (corrected, estimated) = (back, backBackground);
        }

        return (new GradientResult(corrected, keepBackground ? estimated : null), OperationOutcome.Done);
    }

    // The classical fit's options for these settings: the reference defaults with the settings over them.
    private static BackgroundExtractionOptions ClassicalOptions(GradientSettings settings) => BackgroundExtractionOptions.Default with
    {
        PolynomialDegree = settings.Degree,
        Downsample = settings.Downsample,
        SurfaceRefinement = settings.Surface,
        Correction = settings.Divide ? BackgroundCorrection.Divide : BackgroundCorrection.Subtract,
        Exclusions = [.. settings.Exclusions.Select(static polygon => new ExclusionPolygon(Vertices(polygon)))],
    };

    private static ImmutableArray<Vector2> Vertices(ImmutableArray<float> polygon)
    {
        var vertices = ImmutableArray.CreateBuilder<Vector2>(polygon.Length / 2);
        for (var i = 0; i + 1 < polygon.Length; i += 2)
        {
            vertices.Add(new Vector2(polygon[i], polygon[i + 1]));
        }

        return vertices.MoveToImmutable();
    }

    /// <summary>A polygon as the settings hold one: its vertices' x and y in turn.</summary>
    public static ImmutableArray<float> Flatten(ExclusionPolygon polygon)
        => [.. polygon.Vertices.SelectMany(static vertex => new[] { vertex.X, vertex.Y })];
}
