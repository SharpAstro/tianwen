using System;
using System.Collections.Generic;
using System.Globalization;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A colour master's balance (<see cref="PlanetaryColourBalance"/>): the gains its stack's disk asked for, the disk they were read on,
/// the saturation, and the colour it saturates about (<see cref="PlanetaryColourBalance.Apply"/>). One balance serves every master of the capture, the stack and its sharpening alike.
/// </summary>
public sealed record ColourBalance(LinearRgb Gains, MetricDisk Disk, double Saturation, LinearRgb About)
{
    /// <summary>The balance in words, for a log or a status line.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"balanced to Jupiter's colour: gains R {Gains.R:0.000}, B {Gains.B:0.000} over green, saturation {Saturation:0.0#}");

    /// <summary>FITS cards recording the balance, so a balanced master says so and by how much.</summary>
    public IReadOnlyDictionary<string, (object Value, string Comment)> HeaderCards() => new Dictionary<string, (object Value, string Comment)>
    {
        ["CBALGNR"] = (Gains.R, "colour balance: red gain over green (#1212)"),
        ["CBALGNB"] = (Gains.B, "colour balance: blue gain over green (#1212)"),
        ["CBALSAT"] = (Saturation, "colour balance: saturation about luminance (#1212)"),
    };

    /// <summary><paramref name="master"/> balanced: its own sky taken off, these gains, this saturation. A new image.</summary>
    public Image Apply(Image master)
    {
        var red = master.GetChannelSpan(0);
        var green = master.GetChannelSpan(1);
        var blue = master.GetChannelSpan(2);
        var sky = PlanetaryColour.Sky(red, green, blue, master.Width, master.Height, Disk);
        return PlanetaryColourBalance.Apply(master, Gains, sky, Saturation, About);
    }
}

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

    /// <summary>
    /// The saturation a balance applies by default: the owner's choice on the five colour captures' previews at 1, 1.4 and 2
    /// (2026-10-03, #1212). Every capture read duller than OPAL's reflectance at its resolution, by 1.1 to 3.1 depending on the blur.
    /// </summary>
    public const double DefaultSaturation = 1.4;

    /// <summary>
    /// The balance <paramref name="stacked"/>, a master of <paramref name="planet"/>, asks for at <paramref name="saturation"/>, or null
    /// with the reason in words: Jupiter's colour is the only one measured, a mono master has none to balance, and the disk comes from the
    /// limb fit at the master's instant (<paramref name="epoch"/>, else its own DATE-OBS and EXPTIME's middle).
    /// </summary>
    public static (ColourBalance? Balance, string How) For(Image stacked, CatalogIndex? planet, DateTimeOffset? epoch, double saturation = DefaultSaturation)
    {
        if (stacked.ChannelCount != 3)
        {
            return (null, "a mono master, so no colour to balance");
        }
        if (planet != CatalogIndex.Jupiter)
        {
            return (null, "colours left as captured: only Jupiter's colour is measured (#1212)");
        }
        if (PlanetaryBestStack.InstantOf(stacked, epoch) is not { } instant)
        {
            return (null, "colours left as captured: the master carries no time to fit its limb at");
        }
        var options = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, instant));
        if (PlanetaryLimbFit.Fit(stacked, options) is not { } fit)
        {
            return (null, "colours left as captured: the limb did not fit");
        }
        var disk = MetricDisk.From(fit, options.AxisRatio);
        var (gains, _) = GainsFor(stacked, disk, JupiterDiskColour);
        if (!double.IsFinite(gains.R) || !double.IsFinite(gains.B) || gains.R <= 0 || gains.B <= 0)
        {
            return (null, "colours left as captured: the disk's mean colour could not be read");
        }
        var balance = new ColourBalance(gains, disk, saturation, JupiterDiskColour);
        return (balance, balance.Describe());
    }

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
    /// <paramref name="saturation"/> about <paramref name="about"/>, a colour: each pixel's departure from that colour at the pixel's own
    /// luminance is scaled, <c>c' = a + s (c - a)</c> with <c>a = (Y_c / Y_about) about</c>, so the luminance is kept and a pixel of that
    /// colour is left alone. About white, the departure from grey; about the disk's target colour, the belts' and zones' departure
    /// from the disk, which leaves the disk's mean where the gains put it. One leaves the colours as the gains made them. A new image.
    /// </summary>
    public static Image Apply(Image master, in LinearRgb gains, in LinearRgb sky, double saturation, in LinearRgb about)
    {
        var (width, height) = (master.Width, master.Height);
        var red = master.GetChannelSpan(0);
        var green = master.GetChannelSpan(1);
        var blue = master.GetChannelSpan(2);
        var y = CameraColorMatrix.SrgbToXyz.Slice(3, 3);
        var (wr, wg, wb) = (y[0], y[1], y[2]);
        var aboutLuminance = (wr * about.R) + (wg * about.G) + (wb * about.B);
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
                var scale = ((wr * r) + (wg * g) + (wb * b)) / aboutLuminance;
                var (ar, ag, ab) = (scale * about.R, scale * about.G, scale * about.B);
                var (outR, outG, outB) = ((float)(ar + (saturation * (r - ar))), (float)(ag + (saturation * (g - ag))), (float)(ab + (saturation * (b - ab))));
                (planes[0][row, x], planes[1][row, x], planes[2][row, x]) = (outR, outG, outB);
                min = MathF.Min(min, MathF.Min(outR, MathF.Min(outG, outB)));
                max = MathF.Max(max, MathF.Max(outR, MathF.Max(outG, outB)));
            }
        }
        return new Image(planes, master.BitDepth, max, min, 0, master.ImageMeta);
    }
}
