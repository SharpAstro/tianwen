using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// Crop, to a rectangle a person gave or to the one the coverage scan finds
/// (docs/plans/stacking-workbench.md, "Crop and auto-crop"). One operation: auto-crop is a setting, not a second one.
/// </summary>
/// <remarks>
/// <para>It is the two verbs' routine: <c>image crop</c> and <c>image autocrop</c> build its settings from their
/// options and call <see cref="TryResolve"/> (the rectangle: the four numbers, or <see cref="MasterAutoCrop.Scan"/>)
/// and <see cref="Write"/> (<see cref="MasterCrop"/>), and the workbench runs the same two. The verb reads the scan
/// result off <see cref="CropResolution"/> for its declined and margin lines, and its <c>--dry-run</c> stops between
/// the two.</para>
/// <para><b>Derived.</b> The edit holds the rectangle or the rule; the pixels are cut out of the input when the step
/// after it asks, and no file is written. Written as a file (an export, or the verb), a crop of a FILE carries what
/// the file holds beyond its pixels: the alignment and stack cards and the map sidecars
/// (<see cref="MasterCrop.CropLoaded"/>). A crop handed on in memory is the pixels and the moved plate solution
/// only, which is all the stored operations read.</para>
/// </remarks>
public sealed class CropOperation(ILogger? logger = null) : DerivedOperation<CropSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "crop";

    /// <summary>What a refused edge loses by default with <see cref="CropSettings.Auto"/>: the edge walk's own cap,
    /// and <c>image autocrop</c>'s default.</summary>
    public const double DefaultTrimDeclined = 0.05;

    // MasterAutoCropRule.ForModel holds the declined fraction to this.
    private const double MaxTrimDeclined = 0.25;

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id,
        Version: 1,
        Title: "Crop",
        Verb: "image crop, image autocrop",
        OperationOutputKind.Derived,
        ExtraInputs: [],
        Settings:
        [
            new OperationSetting("auto", "Find the rectangle by the coverage scan in place of x, y, width and height."),
            new OperationSetting("x", "Left edge of the rectangle, in pixels from the left."),
            new OperationSetting("y", "Top edge of the rectangle, in pixels from the top."),
            new OperationSetting("width", "Width of the rectangle in pixels."),
            new OperationSetting("height", "Height of the rectangle in pixels."),
            new OperationSetting("trimDeclined", "With auto: the fraction of the axis an edge the scan refused loses; 0 keeps a refused edge.",
                "The edge walk's own cap. A refused edge kept its partial-coverage ramp, which a background model then fits "
                + "(the QHY294C SMC master, docs/plans/stacking-workbench.md)."),
            new OperationSetting("margin", "With auto: the fraction of each axis taken off every edge after the scan."),
        ]) { Summary = "Cuts the image to a rectangle, found from the coverage of the frames or stated by hand." };

    /// <inheritdoc/>
    protected override JsonTypeInfo<CropSettings> SettingsInfo => OperationSettingsJsonContext.Default.CropSettings;

    /// <inheritdoc/>
    public override string? Refusal(CropSettings settings)
    {
        if (settings.Auto)
        {
            if (settings.X != 0 || settings.Y != 0 || settings.Width != 0 || settings.Height != 0)
            {
                return "auto finds the rectangle itself: leave x, y, width and height out, or leave auto out";
            }

            if (settings.TrimDeclined is not (>= 0 and <= MaxTrimDeclined))
            {
                return FormattableString.Invariant($"trimDeclined is a fraction of the axis, 0 to {MaxTrimDeclined}");
            }

            return settings.Margin is not (>= 0 and <= MasterAutoCropRule.MaxMargin)
                ? FormattableString.Invariant($"margin is a fraction of the axis, 0 to {MasterAutoCropRule.MaxMargin}")
                : null;
        }

        if (settings.Width <= 0 || settings.Height <= 0)
        {
            return "a rectangle needs a width and a height of at least 1, or set auto";
        }

        if (settings.X < 0 || settings.Y < 0)
        {
            return "x and y are 0 or more";
        }

        return settings.TrimDeclined != DefaultTrimDeclined || settings.Margin != 0
            ? "trimDeclined and margin belong to auto"
            : null;
    }

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(CropSettings settings, IReadOnlyList<string> inputs, string output)
        => settings.Auto
            ?
            [
                StepCommandLine.Program, "image", "autocrop", inputs[0], "-o", output,
                "--trim-declined", StepCommandLine.Number(settings.TrimDeclined),
                "--margin", StepCommandLine.Number(settings.Margin),
            ]
            :
            [
                StepCommandLine.Program, "image", "crop", inputs[0], "-o", output,
                "--x", StepCommandLine.Number(settings.X),
                "--y", StepCommandLine.Number(settings.Y),
                "--width", StepCommandLine.Number(settings.Width),
                "--height", StepCommandLine.Number(settings.Height),
            ];

    /// <inheritdoc/>
    public override OperationInput Derive(CropSettings settings, IReadOnlyList<OperationInput> inputs)
    {
        var input = inputs[0];
        return OperationInput.FromDerivation($"crop of {input.Name}", (out Image? image, out WCS? wcs, out string? refusal) =>
        {
            image = null;
            wcs = null;
            if (!input.TryRead(out var source, out var sourceWcs, out refusal)
                || !TryResolve(settings, source, input.Path, say: null, out var resolution, out refusal))
            {
                return false;
            }

            var rectangle = resolution.Rectangle;
            image = source.Crop(rectangle);
            wcs = sourceWcs?.CroppedTo(rectangle.X, rectangle.Y);
            return true;
        });
    }

    /// <inheritdoc/>
    public override ValueTask<OperationOutcome> RunAsync(CropSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        var input = inputs[0];
        if (!input.TryRead(out var image, out var wcs, out var refusal)
            || !TryResolve(settings, image, input.Path, say, out var resolution, out refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        var rectangle = resolution.Rectangle;
        var result = Write(input.Path, image, wcs, rectangle, outputPath);
        if (!result.Succeeded)
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(result.Message));
        }

        say?.Invoke($"{result.SourceWidth}x{result.SourceHeight} -> {result.Width}x{result.Height} at ({rectangle.X},{rectangle.Y}) -> {outputPath}");
        if (result.Sidecars.Count > 0)
        {
            say?.Invoke($"its maps were cropped with it: {string.Join(", ", result.Sidecars)}");
        }

        foreach (var left in result.SidecarsLeftOut)
        {
            say?.Invoke($"not carried: {left}");
        }

        return ValueTask.FromResult(OperationOutcome.Done);
    }

    /// <summary>
    /// The rectangle the settings mean for this image: the four numbers, or what the scan finds. False with the reason
    /// when it is not a crop of the image; <paramref name="resolution"/> still holds the scan's answer then, so a verb
    /// can say what the scan did before it refuses.
    /// </summary>
    /// <param name="settings">The edit's settings.</param>
    /// <param name="image">The image the rectangle is for.</param>
    /// <param name="path">The file it was read from, which the scan reads the coverage sidecar of; null for a derived image.</param>
    /// <param name="say">Told what the scan kept; null for a caller that words it itself.</param>
    /// <param name="resolution">The rectangle, and the scan when <see cref="CropSettings.Auto"/>.</param>
    /// <param name="refusal">Why not, in words.</param>
    public bool TryResolve(CropSettings settings, Image image, string? path, Action<string>? say, out CropResolution resolution,
        [NotNullWhen(false)] out string? refusal)
    {
        PixelRect rectangle;
        if (settings.Auto)
        {
            var rule = MasterAutoCropRule.ForModel(settings.TrimDeclined, settings.Margin);
            var scan = MasterAutoCrop.Scan(image, path, rule, logger);
            rectangle = scan.Rect;
            resolution = new CropResolution(rectangle, scan, rule);
            say?.Invoke($"the scan keeps {rectangle.Width}x{rectangle.Height} at ({rectangle.X},{rectangle.Y}) of {image.Width}x{image.Height}, "
                + $"by the {(scan.FromCoverage ? "coverage plane" : "edge walk")}");
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                refusal = "the scan left nothing to keep, so no empty crop is written";
                return false;
            }
        }
        else
        {
            rectangle = new PixelRect(settings.X, settings.Y, settings.Width, settings.Height);
            resolution = new CropResolution(rectangle, null, null);
        }

        refusal = MasterCrop.Validate(rectangle, image.Width, image.Height);
        return refusal is null;
    }

    /// <summary>
    /// Writes <paramref name="rectangle"/> of an image as a FITS file, as both verbs do. The crop of a FILE carries what
    /// the file holds beyond its pixels, which is all the two verbs do; a derived image (no <paramref name="inputPath"/>)
    /// has no file behind it and so nothing more to carry. A refusal writes nothing.
    /// </summary>
    public static MasterCropResult Write(string? inputPath, Image image, WCS? wcs, PixelRect rectangle, string outputPath)
        => inputPath is { } path
            ? MasterCrop.CropLoaded(path, image, wcs, rectangle, outputPath)
            : MasterCrop.Crop(image, wcs, provenance: null, rectangle, outputPath);
}

/// <summary>What <see cref="CropOperation.TryResolve"/> found.</summary>
/// <param name="Rectangle">The rectangle to cut.</param>
/// <param name="Scan">The scan that found it; null when the settings gave the four numbers.</param>
/// <param name="Rule">The rule the scan ran under, so a verb prints its applied margin as it stands; null with <paramref name="Scan"/>.</param>
public readonly record struct CropResolution(PixelRect Rectangle, MasterAutoCropResult? Scan, MasterAutoCropRule? Rule);
