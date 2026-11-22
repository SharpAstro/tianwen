using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Imaging.Workbench;

// Every settings property is settable and carries its default: the JSON source generator gives an absent init-only
// property its type's default, not the declared one. A setting left out of an edit must be its default.

/// <summary>The settings of <see cref="CropOperation"/>: a rectangle, or the rule that finds one.</summary>
public sealed record CropSettings
{
    /// <summary>Find the rectangle by the coverage scan (<c>image autocrop</c>) in place of the four numbers. The
    /// rule is carried, not its answer: it is resolved against the image each time the step is made.</summary>
    public bool Auto { get; set; }

    /// <summary>Left edge of the rectangle, in pixels from the left.</summary>
    public int X { get; set; }

    /// <summary>Top edge of the rectangle, in pixels from the top.</summary>
    public int Y { get; set; }

    /// <summary>Width of the rectangle in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Height of the rectangle in pixels.</summary>
    public int Height { get; set; }

    /// <summary>With <see cref="Auto"/>: the fraction of the axis an edge the scan refused loses (<c>--trim-declined</c>).</summary>
    public double TrimDeclined { get; set; } = CropOperation.DefaultTrimDeclined;

    /// <summary>With <see cref="Auto"/>: the fraction of each axis taken off every edge after the scan (<c>--margin</c>).</summary>
    public double Margin { get; set; }
}

/// <summary>The settings of <see cref="LinearFitOperation"/>.</summary>
public sealed record LinearFitSettings
{
    /// <summary>Pixels at or above this fraction of either image's peak carry no weight (<c>--reject-high</c>).</summary>
    public double RejectHigh { get; set; } = LinearFit.DefaultRejectHigh;
}

/// <summary>The settings of <see cref="GradientOperation"/>.</summary>
public sealed record GradientSettings
{
    /// <summary>Which extractor runs (<c>--backend</c>).</summary>
    public GradientBackend Backend { get; set; } = GradientBackend.Auto;

    /// <summary>Classical only: the polynomial's degree (<c>--degree</c>).</summary>
    public int Degree { get; set; } = BackgroundExtractionOptions.Default.PolynomialDegree;

    /// <summary>Classical only: the block-mean factor of the working grid (<c>--downsample</c>).</summary>
    public int Downsample { get; set; } = BackgroundExtractionOptions.Default.Downsample;

    /// <summary>Classical only: add the inpainted low-pass surface on the polynomial's residual (<c>--surface</c>).</summary>
    public bool Surface { get; set; }

    /// <summary>Classical only: divide by the background where the default subtracts it (<c>--divide</c>).</summary>
    public bool Divide { get; set; }

    /// <summary>Classical only: regions the fit must not sample (<c>--exclude</c>), each a polygon of at least three
    /// vertices in full-image pixels, written x, y, x, y and so on.</summary>
    public ImmutableArray<ImmutableArray<float>> Exclusions { get; set; } = [];
}

/// <summary>The settings of an operation that has none: its inputs say all there is to say.</summary>
public sealed record NoSettings;

/// <summary>The settings of <see cref="AddLineOperation"/>.</summary>
public sealed record AddLineSettings
{
    /// <summary>The continuum scale between the line and the broadband master (<c>--scale</c>), as <c>image continuum
    /// --dry-run</c> says it on the masters WITH their stars. There is no default: the step is refused without it.</summary>
    public double? Scale { get; set; }

    /// <summary>How much of the pure line goes in, in the broadband's own units (<c>--weight</c>).</summary>
    public double Weight { get; set; } = 1.0;
}

/// <summary>The settings of <see cref="LrgbOperation"/>.</summary>
public sealed record LrgbSettings
{
    /// <summary>The blur in pixels a channel keeps its own colour above (<c>--colour-sigma</c>).</summary>
    public float ColourSigma { get; set; } = LuminanceDetail.DefaultColourSigma;
}

/// <summary>The settings of <see cref="FillDigsOperation"/>.</summary>
public sealed record FillDigsSettings
{
    /// <summary>Leave the filled plate where it stands over the input (<c>--keep-overshoot</c>).</summary>
    public bool KeepOvershoot { get; set; }
}

/// <summary>Which measured scale a continuum subtraction uses.</summary>
public enum ContinuumMethod
{
    /// <summary>The least-deviation residual over the continuum's signal.</summary>
    Flattest,

    /// <summary>The stars' median flux ratio.</summary>
    Photometric,
}

/// <summary>The settings of <see cref="ContinuumOperation"/>.</summary>
public sealed record ContinuumSettings
{
    /// <summary>Subtract with this scale rather than a measured one (<c>--scale</c>).</summary>
    public double? Scale { get; set; }

    /// <summary>Which measured scale to subtract with (<c>--method</c>). Both are always reported.</summary>
    public ContinuumMethod Method { get; set; } = ContinuumMethod.Flattest;

    /// <summary>A continuum pixel carries the flattest fit only this many of its noise above its median (<c>--significance</c>).</summary>
    public double Significance { get; set; } = ContinuumSubtractor.DefaultSignificance;
}

// The canonical form of an operation's settings, which a step's key is made from: one line, every setting written,
// camelCase, enums as words. A member the settings do not have is refused: a misspelt setting would otherwise run at
// its default and say nothing.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CropSettings))]
[JsonSerializable(typeof(LinearFitSettings))]
[JsonSerializable(typeof(GradientSettings))]
[JsonSerializable(typeof(NoSettings))]
[JsonSerializable(typeof(AddLineSettings))]
[JsonSerializable(typeof(LrgbSettings))]
[JsonSerializable(typeof(FillDigsSettings))]
[JsonSerializable(typeof(ContinuumSettings))]
public sealed partial class OperationSettingsJsonContext : JsonSerializerContext
{
}

/// <summary>How an operation writes its command line (the plan's rule 3).</summary>
public static class StepCommandLine
{
    /// <summary>The program every step's command line starts with.</summary>
    public const string Program = "tianwen";

    /// <summary>A number as an option takes it, whatever the computer's language.</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Number(double)"/>
    public static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Number(double)"/>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The command as a person reads and pastes it: an argument holding a space or a semicolon is quoted.</summary>
    public static string Display(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(static argument =>
            argument.Length == 0 || argument.AsSpan().IndexOfAny(' ', ';', '&') >= 0 ? $"\"{argument}\"" : argument));
}
