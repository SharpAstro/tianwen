using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.StarRemoval;

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

/// <summary>The settings of <see cref="DeblurOperation"/>.</summary>
public sealed record DeblurSettings
{
    /// <summary>The brightest input pixel goes to the deblurrer at 1/headroom of its ceiling (<c>--headroom</c>).</summary>
    public float Headroom { get; set; } = NarrowbandCombination.DefaultDeblurHeadroom;
}

/// <summary>The settings of <see cref="DenoiseOperation"/>.</summary>
public sealed record DenoiseSettings
{
    /// <summary>Strength in 0 to 1 (<c>--strength</c>); left out, the denoiser's own.</summary>
    public float? Strength { get; set; }
}

/// <summary>The settings of <see cref="RemoveStarsOperation"/>.</summary>
public sealed record RemoveStarsSettings
{
    /// <summary>Keep the brightest part of each galaxy or nebula out of the stars (off: <c>--no-protect-cores</c>).</summary>
    public bool ProtectCores { get; set; } = true;

    /// <summary>The star catalogue asked for the stars inside a protected core (<c>--core-catalogue</c>).</summary>
    public CoreCatalogueChoice Catalogue { get; set; } = CoreCatalogueChoice.Auto;

    /// <summary>Fill the holes TianWen's own models dig under the stars they take (off: <c>--no-fill-digs</c>).</summary>
    public bool FillDigs { get; set; } = true;

    /// <summary>Leave the plate as the remover wrote it (<c>--keep-overshoot</c>).</summary>
    public bool KeepOvershoot { get; set; }
}

/// <summary>The channel a colour composition's linear fit puts the others on.</summary>
public enum CombineLinearFit
{
    /// <summary>Green, as a rule.</summary>
    Green,

    /// <summary>Red.</summary>
    Red,

    /// <summary>Blue.</summary>
    Blue,

    /// <summary>The masters are left as they are.</summary>
    None,
}

/// <summary>The settings of <see cref="CombineOperation"/>: which steps of the recipe run, and their numbers.</summary>
public sealed record CombineSettings
{
    /// <summary>The channel red, green and blue are put on the scale of first (<c>--linear-fit</c>).</summary>
    public CombineLinearFit LinearFit { get; set; } = CombineLinearFit.Green;

    /// <summary>Deblur every master before its stars are removed (<c>--deblur</c>).</summary>
    public bool Deblur { get; set; }

    /// <summary>Blur every sharper master to the widest one's star width, for the colour only (<c>--match-psf</c>).</summary>
    public bool MatchPsf { get; set; }

    /// <summary>Stars out before the line goes in, and back last (<c>--starless</c>).</summary>
    public bool Starless { get; set; }

    /// <summary>With <see cref="Starless"/>: the star catalogue asked for the stars inside a protected core (<c>--core-catalogue</c>).</summary>
    public CoreCatalogueChoice Catalogue { get; set; } = CoreCatalogueChoice.Auto;

    /// <summary>The continuum scale against red as given (<c>--ha-scale</c>); left out, it is measured.</summary>
    public double? LineScale { get; set; }

    /// <summary>How much of the pure line goes into red, in red's own units (<c>--ha-weight</c>).</summary>
    public double LineWeight { get; set; } = 1.0;

    /// <summary>Give every channel the synthetic luminance's detail (<c>--lrgb</c>).</summary>
    public bool Lrgb { get; set; }

    /// <summary>With <see cref="Lrgb"/>: the blur in pixels the channels keep their own colour above (<c>--colour-sigma</c>).</summary>
    public float ColourSigma { get; set; } = LuminanceDetail.DefaultColourSigma;

    /// <summary>Denoise the starless planes (<c>--denoise</c>); needs <see cref="Starless"/>.</summary>
    public bool Denoise { get; set; }

    /// <summary>The strength for the planes that carry the detail (<c>--denoise-strength</c>); left out, the denoiser's own.</summary>
    public float? DenoiseStrength { get; set; }

    /// <summary>The strength for the colour when a luminance carries the detail (<c>--colour-denoise-strength</c>).</summary>
    public float ColourDenoiseStrength { get; set; } = 0.5f;
}

/// <summary>The settings of <see cref="ProtectCoresOperation"/>.</summary>
public sealed record ProtectCoresSettings
{
    /// <summary>The star catalogue asked for the stars inside a protected core (<c>--core-catalogue</c>).</summary>
    public CoreCatalogueChoice Catalogue { get; set; } = CoreCatalogueChoice.Auto;
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
[JsonSerializable(typeof(ProtectCoresSettings))]
[JsonSerializable(typeof(DeblurSettings))]
[JsonSerializable(typeof(DenoiseSettings))]
[JsonSerializable(typeof(RemoveStarsSettings))]
[JsonSerializable(typeof(CombineSettings))]
[JsonSerializable(typeof(SolveSettings))]
[JsonSerializable(typeof(PlanetaryStackSettings))]
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
