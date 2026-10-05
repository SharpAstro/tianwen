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
    {
        var divisor = 0f;
        foreach (var plane in planes)
        {
            for (var c = 0; c < plane.ChannelCount; c++)
            {
                foreach (var v in plane.GetChannelSpan(c))
                {
                    if (float.IsFinite(v) && v > divisor)
                    {
                        divisor = v;
                    }
                }
            }
        }
        if (divisor <= 0f)
        {
            divisor = 1f;
        }

        var result = new Image[planes.Count];
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
            var input = new Image(scaled, BitDepth.Float32, 1f, 0f, 0f, plane.ImageMeta);
            var starless = await remover.EnhanceAsync(input, cancellationToken);
            input.Release();

            var back = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var src = plane.GetChannelSpan(c);
                var removed = starless.GetChannelSpan(c);
                var dst = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var at = (y * width) + x;
                        dst[y, x] = float.IsFinite(src[at]) ? removed[at] * divisor : float.NaN;
                    }
                }
                back[c] = dst;
            }
            starless.Release();
            result[i] = new Image(back, BitDepth.Float32, plane.MaxValue, plane.MinValue, plane.Pedestal, plane.ImageMeta);
        }
        return result;
    }

    /// <summary>The stars a remover took out: the plane less its starless plate, absent where either is.</summary>
    public static Image Stars(Image plane, Image starless) => Combine(plane, starless, static (a, b) => a - b);

    /// <summary>A starless plane with its stars (<see cref="Stars"/>) put back.</summary>
    public static Image WithStars(Image starless, Image stars) => Combine(starless, stars, static (a, b) => a + b);

    /// <summary>
    /// What a line master's counts are worth in a broadband channel's units: the ratio of their exposures. Both filters pass
    /// the line at much the same transmission (a red filter and an H-alpha one both near their peak at 656 nm); a filter
    /// curve would refine it. One when either exposure is unknown.
    /// </summary>
    public static double LineToBroadband(in ImageMeta line, in ImageMeta broadband)
        => line.ExposureDuration > TimeSpan.Zero && broadband.ExposureDuration > TimeSpan.Zero
            ? broadband.ExposureDuration.TotalSeconds / line.ExposureDuration.TotalSeconds
            : 1.0;

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
