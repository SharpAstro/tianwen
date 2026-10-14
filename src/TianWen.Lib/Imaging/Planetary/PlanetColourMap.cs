using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A planet's global map in colour (docs/plans/atlas-planet-detail.md, A4, #1037): three <see cref="PlanetMap"/> planes, red, green
/// and blue, each in that map's convention (planetographic latitude, WEST longitude, row 0 the north edge, column 0 at 0 degrees with
/// longitude falling to the right). What the sky atlas renders a planet's disc from; the planetary stacker keeps its mono maps.
/// </summary>
public sealed class PlanetColourMap
{
    /// <summary>A map of three planes of one size.</summary>
    public PlanetColourMap(PlanetMap red, PlanetMap green, PlanetMap blue)
    {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        if (green.Width != red.Width || blue.Width != red.Width || green.Height != red.Height || blue.Height != red.Height)
        {
            throw new ArgumentException("The three planes differ in size");
        }
        Red = red;
        Green = green;
        Blue = blue;
    }

    /// <summary>The red plane.</summary>
    public PlanetMap Red { get; }

    /// <summary>The green plane.</summary>
    public PlanetMap Green { get; }

    /// <summary>The blue plane.</summary>
    public PlanetMap Blue { get; }

    /// <summary>Samples around the full circle of longitude.</summary>
    public int Width => Red.Width;

    /// <summary>Samples from pole to pole.</summary>
    public int Height => Red.Height;

    /// <summary>
    /// The map from 8-bit RGB (or RGBA, by <paramref name="bytesPerPixel"/>) samples, row-major from the north edge, scaled to [0, 1].
    /// <paramref name="sourceWestAtColumn0"/> is the WEST longitude the source's first column begins at, and
    /// <paramref name="sourceEastward"/> whether its longitude grows EAST to the right (west falling, as this map's own convention) or
    /// west: the source is turned into this map's convention, so every caller states where its own data begins rather than shifting
    /// it by hand. With <paramref name="fillHoles"/>, the holes are filled as <see cref="HoleThreshold"/> and the fill below describe.
    /// </summary>
    public static PlanetColourMap FromRgb8(ReadOnlySpan<byte> pixels, int width, int height, int bytesPerPixel = 3,
        double sourceWestAtColumn0 = 0, bool sourceEastward = true, bool fillHoles = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerPixel, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels.Length, width * height * bytesPerPixel);
        var r = new float[width * height];
        var g = new float[width * height];
        var b = new float[width * height];
        var hole = new bool[width * height];
        for (var row = 0; row < height; row++)
        {
            for (var c = 0; c < width; c++)
            {
                // This map's column c begins at west longitude 360 - c * 360 / width; find the source column there.
                var west = 360.0 - (c * 360.0 / width);
                var offset = sourceEastward ? sourceWestAtColumn0 - west : west - sourceWestAtColumn0;
                var sc = (int)Math.Round(offset * width / 360.0);
                sc = ((sc % width) + width) % width;
                var si = ((row * width) + sc) * bytesPerPixel;
                var di = (row * width) + c;
                r[di] = pixels[si] / 255f;
                g[di] = pixels[si + 1] / 255f;
                b[di] = pixels[si + 2] / 255f;
                hole[di] = pixels[si] + pixels[si + 1] + pixels[si + 2] < HoleThreshold;
            }
        }
        if (fillHoles)
        {
            FillHoles([r, g, b], hole, width, height);
        }
        return new PlanetColourMap(new PlanetMap(r, width, height), new PlanetMap(g, width, height), new PlanetMap(b, width, height));
    }

    /// <summary>
    /// A sample whose three 8-bit channels sum below this is a hole: OPAL's composite is black where Hubble never saw the planet (a pole
    /// turned away, Uranus's whole southern hemisphere in 2025, Saturn's north pole at a ring-plane crossing), and a few rows of near
    /// black (a sum of 8) fringe it. The darkest real sample of the four 2025 composites sums above 150.
    /// </summary>
    public const int HoleThreshold = 30;

    /// <summary>
    /// How far round a hole the samples are dropped with it, in degrees: those at the edge of what Hubble saw are seen at grazing
    /// incidence and fall toward black (the mono maps' 2 degrees, <see cref="PlanetMap.FilledZonally"/>, left a fringe on the colour
    /// composites' poles).
    /// </summary>
    public const double HoleEdgeDegrees = 6;

    // A display fill, apart from PlanetMap.FilledZonally (the stacker's, which keeps a row's longitude pattern where it holds a row, so
    // a whole missing hemisphere came out as the last row's streaks): a row keeping half its samples fills its holes with its kept
    // mean; any other row takes the kept mean colour of the nearest kept rows, interpolated in latitude between them and held beyond.
    private static void FillHoles(float[][] planes, bool[] hole, int width, int height)
    {
        var dx = (int)Math.Ceiling(HoleEdgeDegrees * width / 360);
        var dy = (int)Math.Ceiling(HoleEdgeDegrees * height / 180);
        var dropped = new bool[hole.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!hole[(y * width) + x])
                {
                    continue;
                }
                for (var j = Math.Max(0, y - dy); j <= Math.Min(height - 1, y + dy); j++)
                {
                    for (var i = -dx; i <= dx; i++)
                    {
                        dropped[(j * width) + (((x + i) % width) + width) % width] = true;
                    }
                }
            }
        }

        var rowMean = new double[height, planes.Length];
        var rowKept = new bool[height];
        for (var y = 0; y < height; y++)
        {
            var count = 0;
            for (var x = 0; x < width; x++)
            {
                if (!dropped[(y * width) + x])
                {
                    count++;
                    for (var p = 0; p < planes.Length; p++)
                    {
                        rowMean[y, p] += planes[p][(y * width) + x];
                    }
                }
            }
            if (count * 2 >= width)
            {
                rowKept[y] = true;
                for (var p = 0; p < planes.Length; p++)
                {
                    rowMean[y, p] /= count;
                }
            }
        }
        if (Array.IndexOf(rowKept, true) < 0)
        {
            return;
        }

        Span<double> fill = stackalloc double[planes.Length];
        for (var y = 0; y < height; y++)
        {
            if (rowKept[y])
            {
                for (var p = 0; p < planes.Length; p++)
                {
                    fill[p] = rowMean[y, p];
                }
            }
            else
            {
                var above = y - 1;
                while (above >= 0 && !rowKept[above])
                {
                    above--;
                }
                var below = y + 1;
                while (below < height && !rowKept[below])
                {
                    below++;
                }
                for (var p = 0; p < planes.Length; p++)
                {
                    fill[p] = above < 0 ? rowMean[below, p]
                        : below >= height ? rowMean[above, p]
                        : rowMean[above, p] + ((rowMean[below, p] - rowMean[above, p]) * (y - above) / (below - above));
                }
            }
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (dropped[i] || !rowKept[y])
                {
                    for (var p = 0; p < planes.Length; p++)
                    {
                        planes[p][i] = (float)fill[p];
                    }
                }
            }
        }
    }
}
