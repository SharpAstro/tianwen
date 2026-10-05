using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A colour in OKLab (Ottosson, 2020): a lightness <paramref name="L"/> and two opponent axes, <paramref name="A"/> green to red and
/// <paramref name="B"/> blue to yellow, from LINEAR sRGB. Its point is that distances in it are close to the differences the eye sees, and
/// that a colour's chroma scaled about grey keeps the hue the eye sees, which scaling in RGB does not. White is (1, 0, 0); the cube roots
/// make a, b grow as the cube root of a colour's level, so compare colours at one luminance.
/// </summary>
public readonly record struct OkLab(double L, double A, double B)
{
    /// <summary>The distance from grey, the eye's colourfulness at this lightness.</summary>
    public double Chroma => Math.Sqrt((A * A) + (B * B));

    /// <summary>The hue angle, degrees from +a (red) toward +b (yellow), in (-180, 180].</summary>
    public double HueDeg => Math.Atan2(B, A) * 180 / Math.PI;

    /// <summary>The OKLab of linear sRGB (<paramref name="r"/>, <paramref name="g"/>, <paramref name="b"/>); a negative channel is allowed.</summary>
    public static OkLab FromLinearSrgb(double r, double g, double b)
    {
        var l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        var m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        var s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));
        return new OkLab(
            (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    /// <summary>This colour back in linear sRGB, the inverse of <see cref="FromLinearSrgb"/>.</summary>
    public (double R, double G, double B) ToLinearSrgb()
    {
        var l = L + (0.3963377774 * A) + (0.2158037573 * B);
        var m = L - (0.1055613458 * A) - (0.0638541728 * B);
        var s = L - (0.0894841775 * A) - (1.2914855480 * B);
        (l, m, s) = (l * l * l, m * m * m, s * s * s);
        return (
            (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s),
            (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s),
            (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s));
    }

    /// <summary>How far hue <paramref name="toDeg"/> lies from hue <paramref name="fromDeg"/>, degrees, the shorter way round, 0 to 180.</summary>
    public static double HueDistanceDeg(double fromDeg, double toDeg)
    {
        var d = Math.Abs(toDeg - fromDeg) % 360;
        return d > 180 ? 360 - d : d;
    }
}
