using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using nom.tam.fits;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Workbench;

// Every settings property is settable and carries its default (see OperationSettings.cs).

/// <summary>The settings of <see cref="SolveOperation"/>.</summary>
public sealed record SolveSettings
{
    /// <summary>The pixel scale in arcseconds a pixel (<c>--scale</c>, or <c>--focus-length</c> with
    /// <c>--pixel-size</c>); left out, the solver reads it off the image's header.</summary>
    public double? Scale { get; set; }

    /// <summary>How far the true scale may be from the stated one, as a fraction of it (<c>--range</c>).</summary>
    public float Range { get; set; } = SolveOperation.DefaultRange;

    /// <summary>Where to look: the right ascension in hours (<c>--search-origin</c>). With <see cref="SearchDec"/>, or
    /// both left out for a blind solve.</summary>
    public double? SearchRa { get; set; }

    /// <summary>Where to look: the declination in degrees (<c>--search-origin</c>).</summary>
    public double? SearchDec { get; set; }

    /// <summary>How far around that place to look, in degrees (<c>--search-radius</c>). Used only with a place to look.</summary>
    public double SearchRadius { get; set; } = SolveOperation.DefaultSearchRadius;
}

/// <summary>
/// A plate solution kept as a file of its own: a FITS header with no data, holding the cards
/// <see cref="WCS.WriteToHeader"/> writes.
/// </summary>
/// <remarks>
/// It is read back through <see cref="WCS.FromHeader"/>, the one reader of those cards. So a solution taken from this
/// file is the solution a FITS image written with it gives whoever reads that image, to the last digit a card holds:
/// a step that takes the solution from here and a verb that reads it off the solved image work on the same numbers.
/// </remarks>
public static class PlateSolutionFile
{
    /// <summary>Writes <paramref name="solution"/> to <paramref name="path"/>.</summary>
    public static void Write(string path, WCS solution)
    {
        // The three cards every FITS header starts with, saying there is no data after it.
        var header = new Header();
        header.AddValue("SIMPLE", true, "a plate solution: a header and no data");
        header.AddValue("BITPIX", 8, "no data");
        header.AddValue("NAXIS", 0, "no data");
        solution.WriteToHeader(header);
        using var writer = FitsWriter.CreateFile(path, header);
        writer.Finish();
    }

    /// <summary>
    /// The solution as a FITS header holds it: its numbers after the cards have been written and read once.
    /// </summary>
    /// <remarks>
    /// A card holds fewer digits than a double, and a long number is written in another form than a short one
    /// (<c>1.398765432110E-05</c> for the solver's double, <c>1.39876543211E-05</c> for the same number read back).
    /// So a solution written straight from the solver and one written after it was read from a file differ in their
    /// cards' text, though in no digit a card keeps. Held once, a solution is written the same way ever after: it is
    /// what <see cref="Read"/> gives of a file <see cref="Write"/> wrote.
    /// </remarks>
    public static WCS AsHeld(WCS solution)
    {
        var header = new Header();
        solution.WriteToHeader(header);
        return WCS.FromHeader(header) ?? solution;
    }

    /// <summary>The solution in <paramref name="path"/>, or null when the file is not there or holds none.</summary>
    public static WCS? Read(string path)
    {
        // The same boundary Image.TryReadFitsFile keeps around FITS.Lib's reader, which throws what it likes: a file
        // that will not read holds no solution.
        try
        {
            using var fits = Image.OpenFitsHeader(path);
            return WCS.FromFits(fits);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Plate solve (<c>tianwen solve</c>): where on the sky the image is, as a plate solution. It writes no pixels
/// (docs/plans/stacking-workbench.md, "An operation": the output kept as metadata).
/// </summary>
/// <remarks>
/// <para>It is the verb's routine: <c>tianwen solve</c> builds the settings from its options and calls
/// <see cref="SolveAsync"/>, and writes the solved image with <see cref="WriteSolved"/> (<c>-o</c>,
/// <c>--update-fits</c>). The verb's star list, its exports and its annotated picture are its own.</para>
/// <para><b>What a project keeps is the solution</b> (<see cref="PlateSolutionFile"/>), in the step store. The step
/// after it takes the step's input with that solution in place of the one the input had. Written as a file (an
/// export, or the verb's <c>-o</c>) it is the image re-encoded with the solution's cards.</para>
/// <para><b>Which solver answered is not part of the step's key.</b> The solvers are tried in order of priority until
/// one answers, and every answer is a solution of the same sky, so installing another solver does not run again
/// every step after a solve.</para>
/// </remarks>
/// <param name="plateSolver">The solver, or null on a host that has none: the step then says so when it runs.</param>
public sealed class SolveOperation(IPlateSolver? plateSolver) : FindingOperation<SolveSettings>
{
    private const string NoSolver = "no plate solver is registered here, so nothing can solve the image";

    /// <summary>The operation's id.</summary>
    public const string Id = "solve";

    /// <summary>The scale tolerance when none is stated: the solvers' own default.</summary>
    public const float DefaultRange = IPlateSolver.DefaultRange;

    /// <summary>The radius searched around a stated place when none is stated, in degrees: the verb's default.</summary>
    public const double DefaultSearchRadius = 5.0;

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Plate solve",
        Verb: "solve",
        OperationOutputKind.Finding,
        ExtraInputs: [],
        Settings:
        [
            new OperationSetting("scale", "The pixel scale in arcseconds a pixel; left out, the solver reads it off the image's header."),
            new OperationSetting("range", "How far the true scale may be from the stated one, as a fraction of it."),
            new OperationSetting("searchRa", "Where to look: the right ascension in hours. With searchDec, or both left out for a blind solve."),
            new OperationSetting("searchDec", "Where to look: the declination in degrees."),
            new OperationSetting("searchRadius", "How far around that place to look, in degrees. Used only with a place to look."),
        ]);

    /// <inheritdoc/>
    protected override JsonTypeInfo<SolveSettings> SettingsInfo => OperationSettingsJsonContext.Default.SolveSettings;

    /// <inheritdoc/>
    public override string? Refusal(SolveSettings settings)
    {
        if (settings.Scale is { } scale && !(scale > 0 && double.IsFinite(scale)))
        {
            return "scale is arcseconds a pixel, more than 0; leave it out to have it read off the image";
        }

        if (!(settings.Range > 0 && float.IsFinite(settings.Range)))
        {
            return "range is a fraction of the scale, more than 0";
        }

        if ((settings.SearchRa is null) != (settings.SearchDec is null))
        {
            return "a place to look is a right ascension and a declination: state searchRa and searchDec, or neither";
        }

        if (settings.SearchRa is { } ra && ra is not (>= 0 and <= 24))
        {
            return "searchRa is hours, 0 to 24";
        }

        if (settings.SearchDec is { } dec && dec is not (>= -90 and <= 90))
        {
            return "searchDec is degrees, -90 to 90";
        }

        // The radius is read only with a place to look, so it is judged only then.
        return settings.SearchRa is null || (settings.SearchRadius > 0 && double.IsFinite(settings.SearchRadius)) ? null : "searchRadius is degrees, more than 0";
    }

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(SolveSettings settings, IReadOnlyList<string> inputs, string output)
    {
        List<string> line = [StepCommandLine.Program, "solve", inputs[0], "-o", output];
        if (settings.Scale is { } scale)
        {
            line.AddRange(["--scale", StepCommandLine.Number(scale)]);
        }

        line.AddRange(["--range", StepCommandLine.Number(settings.Range)]);
        if (settings is { SearchRa: { } ra, SearchDec: { } dec })
        {
            line.AddRange(
            [
                "--search-origin", $"{StepCommandLine.Number(ra)},{StepCommandLine.Number(dec)}",
                "--search-radius", StepCommandLine.Number(settings.SearchRadius),
            ]);
        }

        return [.. line];
    }

    /// <summary>
    /// The solve: by path where the image is a FITS file, which the external solvers read themselves, and from the
    /// pixels otherwise. It says what the verb prints about a solve.
    /// </summary>
    /// <param name="settings">The scale, the tolerance and where to look.</param>
    /// <param name="image">The image, read: its size goes with a stated scale, and a solve with no file works on it.</param>
    /// <param name="fitsPath">The image's FITS file, or null when it has none (another format, or a derived image).</param>
    /// <param name="say">Takes each line the solve reports, or null.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    /// <returns>What the solver answered, with no solution when it found none; and what it said where it failed.</returns>
    public async ValueTask<(PlateSolveResult Result, string? Failure)> SolveAsync(SolveSettings settings, Image image, string? fitsPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (plateSolver is null)
        {
            return (new PlateSolveResult(null, TimeSpan.Zero), $"[solve] failed: {NoSolver}");
        }

        // Build the ImageDim hint. Left out, the solver falls back to FITS
        // header reads; an explicit scale overrides.
        ImageDim? imageDim = settings.Scale is { } s
            ? new ImageDim(s, image.Width, image.Height)
            : null;
        WCS? searchOrigin = settings is { SearchRa: { } ra, SearchDec: { } dec } ? new WCS(ra, dec) : null;
        say?.Invoke(
            $"[solve] {(imageDim is { } d ? $"scale={d.PixelScale:F3} arcsec/px" : "scale=auto (from FITS)")} {(searchOrigin is null ? "(blind)" : $"hint @ {settings.SearchRadius:F1}°")}");

        PlateSolveResult result;
        string? failure = null;
        try
        {
            result = fitsPath is not null
                ? await plateSolver.SolveFileAsync(fitsPath, imageDim, settings.Range, searchOrigin, searchOrigin is null ? null : settings.SearchRadius, cancellationToken)
                : await plateSolver.SolveImageAsync(image, imageDim, settings.Range, searchOrigin, searchOrigin is null ? null : settings.SearchRadius, cancellationToken);
        }
        catch (PlateSolverException ex)
        {
            failure = $"[solve] failed: {ex.Message}";
            result = new PlateSolveResult(null, TimeSpan.Zero);
        }

        if (result.Solution is { } solved)
        {
            say?.Invoke(
                $"[solve] RA={solved.CenterRA:F4}h Dec={solved.CenterDec:F4}° scale={solved.PixelScaleArcsec:F3}\"/px matched={result.MatchedStars}/{result.DetectedStars} catalog={result.CatalogStars}");
        }

        return (result, failure);
    }

    /// <summary>
    /// The image with its plate solution, as <c>tianwen solve</c> writes it (<c>-o</c>, <c>--update-fits</c>): the
    /// image re-encoded from what was read, with the solution's cards in its header.
    /// </summary>
    /// <remarks>
    /// Written from the solution as a header holds it (<see cref="PlateSolutionFile.AsHeld"/>), so the file the verb
    /// writes from the solver's answer and the file a project writes from the solution it kept are one file, to the
    /// byte. No digit a card keeps is changed by it.
    /// <para>With <paramref name="inputPath"/>, the file the image was read from, the map sidecars beside it (coverage,
    /// rejection, saturation, standard error) are carried to <paramref name="path"/> as a crop of the whole frame
    /// carries them (<see cref="MasterCrop.CarrySidecars"/>), so a crop after the solve, by its step or by its verb on
    /// this file, finds the coverage plane as one on the input would. No pixel changes, so no map does.</para>
    /// </remarks>
    public static void WriteSolved(Image image, WCS solution, string path, string? inputPath = null)
    {
        // The cards a crop of the whole frame carries (stack cards, alignment provenance) are read BEFORE the write: the
        // output may be the input's own path.
        var cards = inputPath is not null ? MasterCrop.WholeFrameCards(inputPath) : null;
        image.WriteToFitsFile(path, PlateSolutionFile.AsHeld(solution), cards);
        if (inputPath is not null)
        {
            MasterCrop.CarrySidecars(inputPath, image.Width, image.Height, path);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> FindAsync(SolveSettings settings, IReadOnlyList<OperationInput> inputs, string findingPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (plateSolver is null)
        {
            return OperationOutcome.NotServed(NoSolver);
        }

        var input = inputs[0];
        if (!input.TryRead(out var image, out _, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        try
        {
            var (result, failure) = await SolveAsync(settings, image, input.Path, say, cancellationToken);
            if (result.Solution is not { } solution)
            {
                return OperationOutcome.Failed(failure ?? NoSolution(input));
            }

            PlateSolutionFile.Write(findingPath, solution);
            return OperationOutcome.Done;
        }
        finally
        {
            image.Release();
        }
    }

    /// <inheritdoc/>
    public override OperationInput Apply(SolveSettings settings, IReadOnlyList<OperationInput> inputs, string findingPath)
    {
        var input = inputs[0];
        // The solve changes no pixel, so the input's file stays its path and what sits beside it (the coverage sidecar an
        // auto crop reads) still describes the pixels: [solve, crop auto] crops as [crop auto] does.
        return OperationInput.FromDerivation($"{input.Name}, solved", input.Path, (out Image? image, out WCS? wcs, out string? refusal) =>
        {
            wcs = null;
            if (!input.TryRead(out image, out _, out refusal))
            {
                return false;
            }

            if (PlateSolutionFile.Read(findingPath) is not { } solution)
            {
                image.Release();
                image = null;
                refusal = Unreadable(input, findingPath);
                return false;
            }

            wcs = solution;
            return true;
        });
    }

    /// <inheritdoc/>
    public override OperationOutcome Write(SolveSettings settings, IReadOnlyList<OperationInput> inputs, string findingPath, string outputPath)
    {
        var input = inputs[0];
        if (PlateSolutionFile.Read(findingPath) is not { } solution)
        {
            return OperationOutcome.Failed(Unreadable(input, findingPath));
        }

        if (!input.TryRead(out var image, out _, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        try
        {
            WriteSolved(image, solution, outputPath, input.Path);
            return OperationOutcome.Done;
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>The verb's routine whole: the solve, and the solved image written to <paramref name="outputPath"/>.</summary>
    public override async ValueTask<OperationOutcome> RunAsync(SolveSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (plateSolver is null)
        {
            return OperationOutcome.NotServed(NoSolver);
        }

        var input = inputs[0];
        if (!input.TryRead(out var image, out _, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        try
        {
            var (result, failure) = await SolveAsync(settings, image, input.Path, say, cancellationToken);
            if (result.Solution is not { } solution)
            {
                return OperationOutcome.Failed(failure ?? NoSolution(input));
            }

            WriteSolved(image, solution, outputPath, input.Path);
            return OperationOutcome.Done;
        }
        finally
        {
            image.Release();
        }
    }

    private static string NoSolution(OperationInput input) => $"no plate solution was found for {input.Name}";

    private static string Unreadable(OperationInput input, string findingPath) => $"the plate solution of {input.Name} cannot be read: {findingPath}";
}
