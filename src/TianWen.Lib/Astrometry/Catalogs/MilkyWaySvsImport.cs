using System;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Tuning for <see cref="MilkyWaySvsImport.ToBgra"/>.
/// </summary>
/// <param name="Width">Output width. The source's width must be a whole multiple of it, which is the box each output
/// pixel averages.</param>
/// <param name="Height">Output height, half the width.</param>
/// <param name="Scale">What the sky map multiplies the map's colour by, written as the texture's alpha. 0.33 puts the
/// brightest 0.1 % of the sky where the Tycho-2 bake had it (displayed 0.218 against 0.218) and its median close
/// (0.032 against 0.028), so the band is as bright as before and only better drawn.</param>
/// <param name="BlackPoint">A linear level taken off every channel before it is encoded, about the map's 10th
/// percentile: the sky away from the band goes dark, as the sky map's background always was, and the faint halo NASA
/// draws round the band stays.</param>
public sealed record MilkyWaySvsImportOptions(int Width = 2048, int Height = 1024, float Scale = 0.33f, float BlackPoint = 0.0025f);

/// <summary>
/// Turns NASA's starless all-sky map into the sky map's Milky Way texture: the Goddard Scientific Visualization
/// Studio's "Deep Star Maps 2020" Milky Way background (<c>milkyway_2020_4k.exr</c>, https://svs.gsfc.nasa.gov/4851),
/// which renders the sky from about 1.7 billion Hipparcos-2, Tycho-2 and Gaia DR2 stars and leaves out the Hipparcos
/// and Tycho stars, which the sky map draws itself.
/// </summary>
/// <remarks>
/// <para><b>Credit</b>, as the page asks: NASA/Goddard Space Flight Center Scientific Visualization Studio, with the
/// Gaia DR2 data to ESA/Gaia/DPAC.</para>
/// <para><b>Orientation.</b> The map is plate carree in J2000 with 0h at its centre and right ascension rising to the
/// LEFT, the sky as seen from inside the sphere; the sky map's texture has right ascension rising to the right
/// (<c>u = ra / 2pi + 0.5</c> in <c>skymap_mw.frag</c>). So each row is mirrored: output column <c>x</c> averages the
/// source columns mirrored about the centre. North stays at the top in both.</para>
/// <para><b>Values.</b> The EXR is linear light (NASA's own preview JPEG is its sRGB encoding, to a mean error of
/// 0.009 against 0.18 had the values been taken as display values). Each output pixel averages its box in linear
/// light, takes off the black point, and is sRGB-encoded into the full 8-bit range of the colour channels, while the
/// alpha is the constant <see cref="MilkyWaySvsImportOptions.Scale"/>. The sky map shows colour times alpha
/// (<see cref="MilkyWayTextureFile"/>), so it draws NASA's look dimmed by the scale, with 256 levels across the map's
/// whole range rather than the few a dimmed colour alone would keep.</para>
/// </remarks>
public static class MilkyWaySvsImport
{
    /// <summary>
    /// Converts the map's interleaved linear RGB samples (as <c>ExrImageCodec.DecodeRgbHalf</c> returns them) into the
    /// BGRA pixels <see cref="MilkyWayTextureBaker.WriteRaw"/> writes, row-major with north at the top.
    /// </summary>
    /// <exception cref="ArgumentException">The source or the output is not 2:1, the source is not a whole multiple of
    /// the output, or <paramref name="rgb"/> holds too few samples.</exception>
    public static byte[] ToBgra(ReadOnlySpan<Half> rgb, int sourceWidth, int sourceHeight, MilkyWaySvsImportOptions options)
    {
        var (width, height) = (options.Width, options.Height);
        if (sourceHeight * 2 != sourceWidth || height * 2 != width || width <= 0)
        {
            throw new ArgumentException(
                $"Both maps must be 2:1 equirectangular: source {sourceWidth}x{sourceHeight}, output {width}x{height}.");
        }

        if (sourceWidth % width != 0)
        {
            throw new ArgumentException($"The source width {sourceWidth} is not a whole multiple of {width}.");
        }

        if (rgb.Length < sourceWidth * sourceHeight * 3)
        {
            throw new ArgumentException($"{rgb.Length} samples cannot hold a {sourceWidth}x{sourceHeight} RGB map.", nameof(rgb));
        }

        var factor = sourceWidth / width;
        var perBox = 1f / (factor * factor);
        var black = options.BlackPoint;
        var range = 1f - black;
        var alpha = (byte)MathF.Round(Math.Clamp(options.Scale, 0f, 1f) * 255f);
        var bgra = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float r = 0f, g = 0f, b = 0f;
                for (var j = 0; j < factor; j++)
                {
                    var row = (y * factor + j) * sourceWidth;
                    for (var i = 0; i < factor; i++)
                    {
                        // Mirrored about the centre: right ascension rises to the left in the source.
                        var at = (row + sourceWidth - 1 - (x * factor + i)) * 3;
                        r += (float)rgb[at];
                        g += (float)rgb[at + 1];
                        b += (float)rgb[at + 2];
                    }
                }

                var o = (y * width + x) * 4;
                bgra[o] = Encode(b * perBox, black, range);
                bgra[o + 1] = Encode(g * perBox, black, range);
                bgra[o + 2] = Encode(r * perBox, black, range);
                bgra[o + 3] = alpha;
            }
        }

        return bgra;
    }

    private static byte Encode(float linear, float black, float range)
        => (byte)MathF.Round(Bt2020Pq.SrgbOetf(MathF.Max(linear - black, 0f) / range) * 255f);
}
