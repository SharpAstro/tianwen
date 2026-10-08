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
    /// <summary>The planet whose colour the balance takes the master to.</summary>
    public CatalogIndex Planet { get; init; } = CatalogIndex.Jupiter;

    /// <summary>The balance in words, for a log or a status line.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"balanced to {(Planet == CatalogIndex.Saturn ? "Saturn" : "Jupiter")}'s colour: gains R {Gains.R:0.000}, B {Gains.B:0.000} over green, saturation {Saturation:0.0#}");

    /// <summary>The FITS card holding the red gain over green.</summary>
    public const string RedGainCard = "CBALGNR";

    /// <summary>The FITS card holding the blue gain over green.</summary>
    public const string BlueGainCard = "CBALGNB";

    /// <summary>The FITS card holding the saturation; its presence is what marks a balanced master (<see cref="ImageMeta.IsColourBalanced"/>, #1229).</summary>
    public const string SaturationCard = "CBALSAT";

    /// <summary>Every card <see cref="HeaderCards"/> writes, for a writer that carries a balanced master's cards over from its file.</summary>
    public static IReadOnlyList<string> Cards { get; } = [RedGainCard, BlueGainCard, SaturationCard];

    /// <summary>FITS cards recording the balance, so a balanced master says so and by how much.</summary>
    public IReadOnlyDictionary<string, (object Value, string Comment)> HeaderCards() => new Dictionary<string, (object Value, string Comment)>
    {
        [RedGainCard] = (Gains.R, "colour balance: red gain over green (#1212)"),
        [BlueGainCard] = (Gains.B, "colour balance: blue gain over green (#1212)"),
        [SaturationCard] = (Saturation, "colour balance: saturation about the disk's colour (#1212)"),
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
/// master's colour"): each channel's sky taken off, one gain a channel taking the disk's mean colour to the target's, then, when asked
/// (<see cref="DefaultSaturation"/> is none), a saturation about the disk's colour at each pixel's luminance. Both steps are linear (a
/// diagonal gain, and <c>a + s (c - a)</c> with <c>a</c> the disk's colour at the pixel's linear sRGB luminance, which it keeps), so the
/// master stays a linear master.
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
    /// Saturn's globe's disk-mean colour in linear sRGB, green one, read where its rings leave it clear: OPAL's reflectance through the CIE
    /// 1931 observer under D65, averaged over a rotation, R/G 1.205 to 1.226 and B/G 0.625 to 0.669 over the apparitions 2021 to 2025 at
    /// the EdgeHD capture's geometry of 2022-10-25, whose chromaticities lie 0.0015 to 0.0075 apart, within #1212's 0.010 (S6, #1235, rule 2).
    /// Their mean. The rings take the globe's gains.
    /// </summary>
    public static LinearRgb SaturnDiskColour { get; } = new LinearRgb(1.218, 1, 0.646);

    /// <summary>The disk-mean colour a master of <paramref name="planet"/> is balanced to; null for a planet whose colour is not measured.</summary>
    public static LinearRgb? DiskColourOf(CatalogIndex? planet) => planet switch
    {
        CatalogIndex.Jupiter => JupiterDiskColour,
        CatalogIndex.Saturn => SaturnDiskColour,
        _ => null,
    };

    /// <summary>
    /// The saturation a balance applies by default: none, the gains alone (the owner, 2026-10-06, #1212). <see cref="EyeSaturation"/> was
    /// the default until then, and it is one switch away (<c>planetary-stack --colour-saturation</c>). Shown on the five colour
    /// captures it moved each pixel 0.002 to 0.003 in OKLab, a tenth of what the eye can tell (0.02), and the disk's mean colour not at
    /// all; what is SEEN is the look (#1273, #1277). Every capture still reads duller than OPAL's reflectance at its resolution, and the
    /// camera's own matrix (<see cref="GainsThrough"/>) lifts the chroma 1.45 to 1.52 times from the sensor's curves (#1279, rule E),
    /// as invisibly; whether it becomes the default is the owner's call.
    /// </summary>
    public const double DefaultSaturation = 1;

    /// <summary>
    /// The saturation the owner picked by eye on the five colour captures' previews at 1, 1.4 and 2 (2026-10-03, #1212), the default until
    /// 2026-10-06 and an option since: it brought the masters' chroma spread to about OPAL's.
    /// </summary>
    public const double EyeSaturation = 1.4;

    /// <summary>
    /// The balance <paramref name="stacked"/>, a master of <paramref name="planet"/>, asks for at <paramref name="saturation"/>, or null
    /// with the reason in words: Jupiter's and Saturn's colours are the ones measured, a mono master has none to balance, and the disk comes
    /// from the limb fit at the master's instant (<paramref name="epoch"/>, else its own DATE-OBS and EXPTIME's middle), Saturn's read where
    /// its rings leave it clear.
    /// </summary>
    public static (ColourBalance? Balance, string How) For(Image stacked, CatalogIndex? planet, DateTimeOffset? epoch, double saturation = DefaultSaturation)
    {
        if (stacked.ChannelCount != 3)
        {
            return (null, "a mono master, so no colour to balance");
        }
        if (planet is not { } body || DiskColourOf(body) is not { } target)
        {
            return (null, "colours left as captured: only Jupiter's and Saturn's colours are measured (#1212, #1235)");
        }
        if (PlanetaryBestStack.InstantOf(stacked, epoch) is not { } instant)
        {
            return (null, "colours left as captured: the master carries no time to fit its limb at");
        }
        return PlanetaryLimbFit.FitAt(stacked, body, instant) is { } limb
            ? For(stacked, body, limb.Disk, saturation)
            : (null, "colours left as captured: the limb did not fit");
    }

    /// <summary>
    /// The balance <paramref name="stacked"/> asks for over a <paramref name="disk"/> its caller has already fitted
    /// (<see cref="PlanetaryColourLook.Prepare"/>'s), or null with the reason in words: a limb fit costs seconds, so one master is never
    /// fitted twice for it.
    /// </summary>
    public static (ColourBalance? Balance, string How) For(Image stacked, CatalogIndex planet, in MetricDisk disk, double saturation = DefaultSaturation)
    {
        if (stacked.ChannelCount != 3)
        {
            return (null, "a mono master, so no colour to balance");
        }
        if (DiskColourOf(planet) is not { } target)
        {
            return (null, "colours left as captured: only Jupiter's and Saturn's colours are measured (#1212, #1235)");
        }
        var (gains, _) = GainsFor(stacked, disk, target);
        if (!double.IsFinite(gains.R) || !double.IsFinite(gains.B) || gains.R <= 0 || gains.B <= 0)
        {
            return (null, "colours left as captured: the disk's mean colour could not be read");
        }
        var balance = new ColourBalance(gains, disk, saturation, target) { Planet = planet };
        return (balance, balance.Describe());
    }

    /// <summary>
    /// The gains, green held at one, that take <paramref name="master"/>'s disk mean to <paramref name="target"/>, through
    /// <paramref name="cameraMatrix"/> when one is given (<see cref="GainsThrough"/>), and each channel's sky.
    /// </summary>
    public static (LinearRgb Gains, LinearRgb Sky) GainsFor(Image master, in MetricDisk disk, in LinearRgb target, float[]? cameraMatrix = null)
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
        return (cameraMatrix is null ? mean.GainsTo(target) : GainsThrough(mean, target, cameraMatrix), sky);
    }

    /// <summary>
    /// The gains, green held at one, that take <paramref name="mean"/>, a disk's mean in the camera's own colours, to
    /// <paramref name="target"/>'s chromaticity THROUGH <paramref name="cameraMatrix"/> (camera RGB to linear sRGB, row-major, as
    /// <see cref="FilterCurveDatabase.TryComputeCameraToSrgbMatrix"/> gives it): <c>M (g mean)</c> has the target's colour. The gains are
    /// the white balance the matrix expects; the matrix then undoes the overlap of the camera's channels, which no gain a channel can
    /// (#1279, rule E). NaN where the matrix cannot be inverted.
    /// </summary>
    public static LinearRgb GainsThrough(in LinearRgb mean, in LinearRgb target, ReadOnlySpan<float> cameraMatrix)
    {
        // The camera colour, white-balanced, that the matrix takes to the target: M^-1 target, by Cramer's rule.
        var (m00, m01, m02, m10, m11, m12, m20, m21, m22) = ((double)cameraMatrix[0], (double)cameraMatrix[1], (double)cameraMatrix[2],
            (double)cameraMatrix[3], (double)cameraMatrix[4], (double)cameraMatrix[5], (double)cameraMatrix[6], (double)cameraMatrix[7], (double)cameraMatrix[8]);
        var det = (m00 * ((m11 * m22) - (m12 * m21))) - (m01 * ((m10 * m22) - (m12 * m20))) + (m02 * ((m10 * m21) - (m11 * m20)));
        if (Math.Abs(det) < 1e-12)
        {
            return new LinearRgb(double.NaN, double.NaN, double.NaN);
        }
        var (t0, t1, t2) = (target.R, target.G, target.B);
        var u0 = ((t0 * ((m11 * m22) - (m12 * m21))) - (m01 * ((t1 * m22) - (m12 * t2))) + (m02 * ((t1 * m21) - (m11 * t2)))) / det;
        var u1 = ((m00 * ((t1 * m22) - (m12 * t2))) - (t0 * ((m10 * m22) - (m12 * m20))) + (m02 * ((m10 * t2) - (t1 * m20)))) / det;
        var u2 = ((m00 * ((m11 * t2) - (t1 * m21))) - (m01 * ((m10 * t2) - (t1 * m20))) + (t0 * ((m10 * m21) - (m11 * m20)))) / det;
        var (gr, gg, gb) = (u0 / mean.R, u1 / mean.G, u2 / mean.B);
        return new LinearRgb(gr / gg, 1, gb / gg);
    }

    /// <summary>
    /// <paramref name="colour"/> saturated by <paramref name="saturation"/> about <paramref name="about"/> (<see cref="Apply"/>'s step):
    /// <c>c' = a + s (c - a)</c> with <c>a = (Y_c / Y_about) about</c>, linear sRGB's luminance Y kept.
    /// </summary>
    public static LinearRgb Saturate(in LinearRgb colour, double saturation, in LinearRgb about)
    {
        var y = CameraColorMatrix.SrgbToXyz.Slice(3, 3);
        var scale = ((y[0] * colour.R) + (y[1] * colour.G) + (y[2] * colour.B)) / ((y[0] * about.R) + (y[1] * about.G) + (y[2] * about.B));
        var (ar, ag, ab) = (scale * about.R, scale * about.G, scale * about.B);
        return new LinearRgb(ar + (saturation * (colour.R - ar)), ag + (saturation * (colour.G - ag)), ab + (saturation * (colour.B - ab)));
    }

    /// <summary><paramref name="colour"/> through <paramref name="cameraMatrix"/> (row-major, camera RGB to linear sRGB).</summary>
    public static LinearRgb Through(in LinearRgb colour, ReadOnlySpan<float> cameraMatrix) => new LinearRgb(
        (cameraMatrix[0] * colour.R) + (cameraMatrix[1] * colour.G) + (cameraMatrix[2] * colour.B),
        (cameraMatrix[3] * colour.R) + (cameraMatrix[4] * colour.G) + (cameraMatrix[5] * colour.B),
        (cameraMatrix[6] * colour.R) + (cameraMatrix[7] * colour.G) + (cameraMatrix[8] * colour.B));

    /// <summary>
    /// <paramref name="master"/> balanced: each channel's <paramref name="sky"/> taken off and its gain applied, then, when one is given,
    /// <paramref name="cameraMatrix"/> (<see cref="GainsThrough"/>, rule E of #1279), then saturated by <paramref name="saturation"/>
    /// about <paramref name="about"/>, a colour: each pixel's departure from that colour at the pixel's own
    /// luminance is scaled, <c>c' = a + s (c - a)</c> with <c>a = (Y_c / Y_about) about</c>, so the luminance is kept and a pixel of that
    /// colour is left alone. About white, the departure from grey; about the disk's target colour, the belts' and zones' departure
    /// from the disk, which leaves the disk's mean where the gains put it. One leaves the colours as the gains made them. A new image.
    /// </summary>
    public static Image Apply(Image master, in LinearRgb gains, in LinearRgb sky, double saturation, in LinearRgb about, float[]? cameraMatrix = null)
    {
        var (width, height) = (master.Width, master.Height);
        var red = master.GetChannelSpan(0);
        var green = master.GetChannelSpan(1);
        var blue = master.GetChannelSpan(2);
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
                var balanced = new LinearRgb(r, g, b);
                if (cameraMatrix is not null)
                {
                    balanced = Through(balanced, cameraMatrix);
                }
                var saturated = Saturate(balanced, saturation, about);
                var (outR, outG, outB) = ((float)saturated.R, (float)saturated.G, (float)saturated.B);
                (planes[0][row, x], planes[1][row, x], planes[2][row, x]) = (outR, outG, outB);
                min = MathF.Min(min, MathF.Min(outR, MathF.Min(outG, outB)));
                max = MathF.Max(max, MathF.Max(outR, MathF.Max(outG, outB)));
            }
        }
        // Its sky is now zero in every channel, which the planetary stretch reads (#1229).
        return new Image(planes, master.BitDepth, max, min, 0, master.ImageMeta with { IsColourBalanced = true });
    }
}
