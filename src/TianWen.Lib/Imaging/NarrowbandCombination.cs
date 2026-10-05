using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Mono masters on one grid (<see cref="MasterAlignment"/>) combined into a colour image, with a narrowband line's emission
/// added to the broadband channel it belongs in once its continuum is out (<see cref="ContinuumSubtractor"/>):
/// docs/plans/narrowband-colour.md, phase 2's palette mixer, the HaRGB case first.
/// </summary>
/// <remarks>
/// <para><b>Stars out first, back last.</b> A single continuum scale fits no star exactly (each star's line-to-broadband
/// ratio is its own colour, 9.6 percent spread on LDN 1622), so subtracting on the masters leaves a pit or a ring at every
/// star. On starless planes there is no star to leave, the emission is added to them, and the broadband stars
/// (<see cref="Stars"/>) go back on top. The scale is still measured on the planes WITH their stars: a red filter passes
/// the line too, so with the stars gone the only structure the two share is the emission, and a fit there would subtract
/// the line it is meant to keep (it read 1.01 on LDN 1622's starless pair).</para>
/// <para><b>One scale for every plane through the star remover.</b> A remover takes values in <c>[0, 1]</c>, and dividing each
/// plane by its own maximum puts each on its own scale, so a scale measured between two masters no longer holds between
/// their starless plates (1.8 percent apart on LDN 1622). <see cref="StarlessAsync"/> divides every plane by ONE divisor
/// and multiplies it back.</para>
/// </remarks>
public static class NarrowbandCombination
{
    /// <summary>
    /// Every plane with its stars removed by <paramref name="remover"/>, each back on its own scale: all divided by one
    /// divisor (the largest finite value among them), absent pixels filled with the plane's median for the remover and
    /// absent again after.
    /// </summary>
    public static async Task<Image[]> StarlessAsync(IReadOnlyList<Image> planes, IStarRemover remover, CancellationToken cancellationToken = default)
        => (await ThroughOneScaleAsync(planes, remover, headroom: 1f, EnhanceOptions.Default, cancellationToken)).Planes;

    /// <summary>
    /// Every plane split into its starless plate and its stars (the plane less the plate) on one scale
    /// (<see cref="StarlessAsync"/>): <c>image remove-stars</c>, and the recipe's star split.
    /// </summary>
    public static async Task<(Image[] Starless, Image[] Stars)> SplitStarsAsync(
        IReadOnlyList<Image> planes, IStarRemover remover, CancellationToken cancellationToken = default)
    {
        var starless = await StarlessAsync(planes, remover, cancellationToken);
        var stars = new Image[planes.Count];
        for (var i = 0; i < planes.Count; i++)
        {
            stars[i] = Stars(planes[i], starless[i]);
        }
        return (starless, stars);
    }

    /// <summary>
    /// <paramref name="broadband"/> with <paramref name="line"/>'s emission in it: the line's continuum taken out against the
    /// broadband at <paramref name="continuumScale"/> (<see cref="ContinuumSubtractor.Subtract"/>), then added at
    /// <paramref name="weight"/> times what a line value is worth in the broadband (<see cref="LineToBroadband"/>, which
    /// reads the exposures and the fitted scales off the two). <c>image add-line</c>, and the recipe's H-alpha step; also the
    /// weight it applied.
    /// </summary>
    public static (Image WithLine, double AppliedWeight) AddLineFrom(Image broadband, Image line, double continuumScale, double weight)
    {
        var pure = ContinuumSubtractor.Subtract(line, broadband, continuumScale);
        var applied = weight * LineToBroadband(line.ImageMeta, broadband.ImageMeta);
        var withLine = AddLine(broadband, pure, applied);
        pure.Release();
        return (withLine, applied);
    }

    /// <summary>How far below the ceiling of <c>[0, 1]</c> a plane's peak is put before a deblurrer sees it: BlurX lifted
    /// LDN 1622's brightest star to 3.4 times its input peak, and its output stops at 1, so at the peak's own scale 197 pixels
    /// of green came back clipped at 1 and every bright star lost flux. At 4 none did, and the result agreed with 8's to
    /// 0.7 percent on star pixels, the sky to a thousandth of its noise.</summary>
    public const float DefaultDeblurHeadroom = 4f;

    /// <summary>
    /// Every plane deblurred by <paramref name="deblurrer"/> (RC-Astro BlurXTerminator), each back on its own scale: all
    /// divided by one divisor, <paramref name="headroom"/> times the largest finite value among them, so a sharpened
    /// star's peak stays below the deblurrer's ceiling; absent pixels filled for it and absent again after. Also how many
    /// output pixels reached that ceiling anyway, which should be none.
    /// </summary>
    /// <remarks>
    /// Run each mono channel through it before stars are removed, as BlurX is meant to be used: it brings the channels'
    /// stars to nearly one width (LDN 1622: 1.97 to 2.24 px before, 1.41 to 1.46 after) where blurring them to the widest
    /// (<see cref="PsfMatch"/>) gives up the detail the luminance is for.
    /// </remarks>
    public static Task<(Image[] Planes, long AtCeiling)> DeblurAsync(
        IReadOnlyList<Image> planes, IImageDeblurrer deblurrer, float headroom = DefaultDeblurHeadroom, CancellationToken cancellationToken = default)
        => ThroughOneScaleAsync(planes, deblurrer, headroom, EnhanceOptions.Default, cancellationToken);

    /// <summary>
    /// Every plane denoised by <paramref name="denoiser"/> (RC-Astro NoiseXTerminator where licensed) under
    /// <paramref name="options"/> (its strength), each back on its own scale, absent pixels absent. Hand it STARLESS
    /// planes: a star is never denoised, as the enhance pipeline's split program never does (its denoise runs on the
    /// starless plate).
    /// </summary>
    public static async Task<Image[]> DenoiseAsync(
        IReadOnlyList<Image> planes, IDenoiseEnhancer denoiser, EnhanceOptions options, CancellationToken cancellationToken = default)
        => (await ThroughOneScaleAsync(planes, denoiser, headroom: 1f, options, cancellationToken)).Planes;

    /// <summary>
    /// Three mono planes denoised together as one colour image, so the denoiser sees their colour, and handed back as
    /// three, each with its own metadata.
    /// </summary>
    public static async Task<Image[]> DenoiseColourAsync(
        Image red, Image green, Image blue, IDenoiseEnhancer denoiser, EnhanceOptions options, CancellationToken cancellationToken = default)
    {
        var colour = Rgb(red, green, blue);
        var denoised = (await DenoiseAsync([colour], denoiser, options, cancellationToken))[0];
        colour.Release();
        Image[] sources = [red, green, blue];
        var result = new Image[3];
        for (var c = 0; c < 3; c++)
        {
            var src = denoised.GetChannelSpan(c);
            var plane = new float[red.Height, red.Width];
            for (var y = 0; y < red.Height; y++)
            {
                for (var x = 0; x < red.Width; x++)
                {
                    plane[y, x] = src[(y * red.Width) + x];
                }
            }
            result[c] = new Image([plane], BitDepth.Float32, sources[c].MaxValue, sources[c].MinValue, sources[c].Pedestal, sources[c].ImageMeta);
        }
        denoised.Release();
        return result;
    }

    // Every plane through one enhancer on ONE scale: a scale measured between two planes then still holds between their
    // results, which a divisor per plane breaks (1.8 percent on LDN 1622's starless pair).
    private static async Task<(Image[] Planes, long AtCeiling)> ThroughOneScaleAsync(
        IReadOnlyList<Image> planes, IImageEnhancer enhancer, float headroom, EnhanceOptions options, CancellationToken cancellationToken)
    {
        var peak = 0f;
        foreach (var plane in planes)
        {
            for (var c = 0; c < plane.ChannelCount; c++)
            {
                foreach (var v in plane.GetChannelSpan(c))
                {
                    if (float.IsFinite(v) && v > peak)
                    {
                        peak = v;
                    }
                }
            }
        }
        var divisor = peak > 0f ? peak * headroom : 1f;

        var result = new Image[planes.Count];
        long atCeiling = 0;
        for (var i = 0; i < planes.Count; i++)
        {
            var plane = planes[i];
            var (channels, width, height) = plane.Shape;
            var scaled = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var src = plane.GetChannelSpan(c);
                var fill = FiniteMedian(src);
                var dst = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var v = src[(y * width) + x];
                        dst[y, x] = (float.IsFinite(v) ? v : fill) / divisor;
                    }
                }
                scaled[c] = dst;
            }
            var input = new Image(scaled, BitDepth.Float32, 1f / headroom, 0f, 0f, plane.ImageMeta);
            var output = await enhancer.EnhanceAsync(input, options, null, cancellationToken);
            input.Release();

            // Labelled with the peak and floor it HOLDS, not the input's: a deblurred star stands 3.4 times above the
            // input's peak, and a label below the data is a scale every later step reads wrong.
            var back = new float[channels][,];
            var max = float.NegativeInfinity;
            var min = float.PositiveInfinity;
            for (var c = 0; c < channels; c++)
            {
                var src = plane.GetChannelSpan(c);
                var enhanced = output.GetChannelSpan(c);
                var dst = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var at = (y * width) + x;
                        if (enhanced[at] >= CeilingTolerance)
                        {
                            atCeiling++;
                        }
                        var v = float.IsFinite(src[at]) ? enhanced[at] * divisor : float.NaN;
                        dst[y, x] = v;
                        if (float.IsFinite(v))
                        {
                            max = Math.Max(max, v);
                            min = Math.Min(min, v);
                        }
                    }
                }
                back[c] = dst;
            }
            output.Release();
            result[i] = float.IsFinite(max)
                ? new Image(back, BitDepth.Float32, max, min, plane.Pedestal, plane.ImageMeta)
                : new Image(back, BitDepth.Float32, plane.MaxValue, plane.MinValue, plane.Pedestal, plane.ImageMeta);
        }
        return (result, atCeiling);
    }

    // An enhancer's [0, 1] ceiling, less a float's rounding.
    private const float CeilingTolerance = 0.9999f;

    /// <summary>The stars a remover took out: the plane less its starless plate, absent where either is.</summary>
    public static Image Stars(Image plane, Image starless) => Combine(plane, starless, static (a, b) => a - b);

    /// <summary>A starless plane with its stars (<see cref="Stars"/>) put back.</summary>
    public static Image WithStars(Image starless, Image stars) => Combine(starless, stars, static (a, b) => a + b);

    /// <summary>
    /// What a line master's values are worth in a broadband channel's: the ratio of their exposures, times the ratio of
    /// the scales each was put on since it was taken (<see cref="ImageMeta.FluxScale"/>, a linear fit's slope). Both filters
    /// pass the line at much the same transmission (a red filter and an H-alpha one both near their peak at 656 nm); a
    /// filter curve would refine it. The exposure ratio is one when either exposure is unknown.
    /// </summary>
    public static double LineToBroadband(in ImageMeta line, in ImageMeta broadband)
    {
        var exposure = line.ExposureDuration > TimeSpan.Zero && broadband.ExposureDuration > TimeSpan.Zero
            ? broadband.ExposureDuration.TotalSeconds / line.ExposureDuration.TotalSeconds
            : 1.0;
        return exposure * (broadband.FluxScale ?? 1.0) / (line.FluxScale ?? 1.0);
    }

    /// <summary>
    /// <paramref name="broadband"/> with <paramref name="pureLine"/>'s emission added, <c>broadband + weight (line - median
    /// line)</c>, so the line's background adds nothing; absent where either is.
    /// </summary>
    public static Image AddLine(Image broadband, Image pureLine, double weight)
    {
        var median = FiniteMedian(pureLine.GetChannelSpan(0));
        return Combine(broadband, pureLine, (a, b) => (float)(a + (weight * (b - median))));
    }

    /// <summary>One colour image from three mono masters on one grid, the red master's metadata, the largest of their
    /// full scales.</summary>
    public static Image Rgb(Image red, Image green, Image blue)
    {
        if (red.Width != green.Width || red.Width != blue.Width || red.Height != green.Height || red.Height != blue.Height)
        {
            throw new ArgumentException("the three masters must be on one grid (MasterAlignment, tianwen image align)");
        }
        var planes = new float[3][,];
        var source = new[] { red, green, blue };
        for (var c = 0; c < 3; c++)
        {
            var src = source[c].GetChannelSpan(0);
            var plane = new float[red.Height, red.Width];
            for (var y = 0; y < red.Height; y++)
            {
                for (var x = 0; x < red.Width; x++)
                {
                    plane[y, x] = src[(y * red.Width) + x];
                }
            }
            planes[c] = plane;
        }
        var meta = red.ImageMeta with { SensorType = SensorType.Color, Filter = Filter.None };
        return new Image(planes, BitDepth.Float32, Math.Max(red.MaxValue, Math.Max(green.MaxValue, blue.MaxValue)), 0f, red.Pedestal, meta);
    }

    private static Image Combine(Image a, Image b, Func<float, float, float> op)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"{a.Width}x{a.Height} against {b.Width}x{b.Height}: put them on one grid first");
        }
        var (channels, width, height) = a.Shape;
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var x = a.GetChannelSpan(c);
            var y = b.GetChannelSpan(Math.Min(c, b.ChannelCount - 1));
            var plane = new float[height, width];
            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    var at = (py * width) + px;
                    plane[py, px] = float.IsFinite(x[at]) && float.IsFinite(y[at]) ? op(x[at], y[at]) : float.NaN;
                }
            }
            planes[c] = plane;
        }
        return new Image(planes, BitDepth.Float32, a.MaxValue, a.MinValue, a.Pedestal, a.ImageMeta);
    }

    private static float FiniteMedian(ReadOnlySpan<float> values)
    {
        var finite = new float[values.Length];
        var n = 0;
        foreach (var v in values)
        {
            if (float.IsFinite(v))
            {
                finite[n++] = v;
            }
        }
        return n > 0 ? StatisticsHelper.MedianFast(finite.AsSpan(0, n)) : 0f;
    }
}
