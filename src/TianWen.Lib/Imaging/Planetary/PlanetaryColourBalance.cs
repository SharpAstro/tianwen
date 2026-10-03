using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A colour planetary master balanced to the planet's own colour (#1212 step 2, docs/plans/planetary-restoration.md, "A planetary
/// master's colour"): each channel's sky taken off, one gain a channel taking the disk's mean colour to the target's, then a saturation
/// factor about each pixel's luminance. Both steps are linear (a diagonal gain, and <c>Y + s (c - Y)</c> with linear sRGB's luminance Y,
/// which it keeps), so the master stays a linear master.
/// </summary>
public static class PlanetaryColourBalance
{
    /// <summary>
    /// Jupiter's disk-mean colour in linear sRGB, green one: OPAL's reflectance through the CIE 1931 observer under D65, averaged over
    /// a rotation, R/G 1.034 and B/G 0.857 to 0.865 at the five colour captures' geometries and both apparitions (2022, 2024), which
    /// agree within 0.0009 in chromaticity (#1226, rule 2).
    /// </summary>
    public static LinearRgb JupiterDiskColour { get; } = new LinearRgb(1.034, 1, 0.861);

    /// <summary>The gains, green held at one, that take <paramref name="master"/>'s disk mean to <paramref name="target"/>, and each channel's sky.</summary>
    public static (LinearRgb Gains, LinearRgb Sky) GainsFor(Image master, in MetricDisk disk, in LinearRgb target)
    {
        if (master.ChannelCount != 3)
        {
            throw new ArgumentException("A colour balance needs a three-channel master.", nameof(master));
        }
        var red = master.GetChannelSpan(0);
        var green = master.GetChannelSpan(1);
        var blue = master.GetChannelSpan(2);
        var sky = PlanetaryColour.Sky(red, green, blue, master.Width, master.Height, disk);
        var mean = PlanetaryColour.DiskMean(red, green, blue, master.Width, master.Height, disk, sky);
        return (mean.GainsTo(target), sky);
    }

    /// <summary>
    /// <paramref name="master"/> balanced: each channel's <paramref name="sky"/> taken off and its gain applied, then saturated by
    /// <paramref name="saturation"/> about each pixel's luminance (one leaves the colours as the gains made them). A new image.
    /// </summary>
    public static Image Apply(Image master, in LinearRgb gains, in LinearRgb sky, double saturation)
    {
        var (width, height) = (master.Width, master.Height);
        var red = master.GetChannelSpan(0);
        var green = master.GetChannelSpan(1);
        var blue = master.GetChannelSpan(2);
        var y = CameraColorMatrix.SrgbToXyz.Slice(3, 3);
        var (wr, wg, wb) = (y[0], y[1], y[2]);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[height, width];
        }
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (row * width) + x;
                var r = (red[i] - sky.R) * gains.R;
                var g = (green[i] - sky.G) * gains.G;
                var b = (blue[i] - sky.B) * gains.B;
                var luminance = (wr * r) + (wg * g) + (wb * b);
                var (outR, outG, outB) = ((float)(luminance + (saturation * (r - luminance))), (float)(luminance + (saturation * (g - luminance))),
                    (float)(luminance + (saturation * (b - luminance))));
                (planes[0][row, x], planes[1][row, x], planes[2][row, x]) = (outR, outG, outB);
                min = MathF.Min(min, MathF.Min(outR, MathF.Min(outG, outB)));
                max = MathF.Max(max, MathF.Max(outR, MathF.Max(outG, outB)));
            }
        }
        return new Image(planes, master.BitDepth, max, min, 0, master.ImageMeta);
    }
}
