using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A planet's global map (docs/plans/planetary-restoration.md, T1): equirectangular in PLANETOGRAPHIC latitude and WEST
/// longitude, as the Hubble OPAL maps are. Row 0 is the north pole's edge (+90 degrees), latitude falling down the rows, and
/// column 0 begins at 360 (0) degrees west, longitude falling to the right, so the map reads as the disk does with north up
/// and east on the left. OPAL's maps have their limb darkening removed (a Minnaert correction per filter), which a render puts
/// back (<see cref="PlanetaryRender"/>).
/// </summary>
public sealed class PlanetMap
{
    private readonly float[] _values;

    /// <summary>A map of <paramref name="width"/> by <paramref name="height"/> samples, row-major from the north edge.</summary>
    public PlanetMap(float[] values, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, width * height);
        _values = values;
        Width = width;
        Height = height;
    }

    /// <summary>Samples around the full circle of longitude.</summary>
    public int Width { get; }

    /// <summary>Samples from pole to pole.</summary>
    public int Height { get; }

    /// <summary>
    /// The map at a planetographic latitude and west longitude (degrees), interpolated bilinearly between sample centres,
    /// wrapping in longitude and held at the poles' rows.
    /// </summary>
    public double Sample(double latitudeDeg, double westLongitudeDeg)
    {
        var west = westLongitudeDeg % 360;
        if (west < 0)
        {
            west += 360;
        }
        // Sample centres: column c at 360 - (c + 0.5) * 360 / Width west, row r at 90 - (r + 0.5) * 180 / Height.
        var fx = ((360 - west) * Width / 360.0) - 0.5;
        var fy = Math.Clamp(((90 - latitudeDeg) * Height / 180.0) - 0.5, 0, Height - 1);

        var x0 = (int)Math.Floor(fx);
        var tx = fx - x0;
        x0 = ((x0 % Width) + Width) % Width;
        var x1 = (x0 + 1) % Width;
        var y0 = Math.Min((int)Math.Floor(fy), Height - 2);
        var ty = fy - y0;

        var top = (_values[(y0 * Width) + x0] * (1 - tx)) + (_values[(y0 * Width) + x1] * tx);
        var bottom = (_values[((y0 + 1) * Width) + x0] * (1 - tx)) + (_values[((y0 + 1) * Width) + x1] * tx);
        return (top * (1 - ty)) + (bottom * ty);
    }

    /// <summary>
    /// The map's mean over the sphere, each row weighted by the cosine of its latitude, holes left out (a sample at zero, which no
    /// reflectance is): what a ring's brightness is stated against (<see cref="SaturnRing.Level"/>).
    /// </summary>
    public double MeanAlbedo
    {
        get
        {
            double sum = 0, weight = 0;
            for (var r = 0; r < Height; r++)
            {
                var w = Math.Cos((90 - ((r + 0.5) * 180.0 / Height)) * Math.PI / 180);
                for (var c = 0; c < Width; c++)
                {
                    if (_values[(r * Width) + c] is var value && value > 0)
                    {
                        sum += w * value;
                        weight += w;
                    }
                }
            }
            return weight > 0 ? sum / weight : 0;
        }
    }

    /// <summary>
    /// The map with its holes filled along latitude (S1 of docs/plans/planetary-restoration.md, #1231). OPAL leaves a sample at zero
    /// where Hubble never saw the planet: on Saturn, the band the rings hid south of the equator (no row half valid from 5 to 26 degrees
    /// south in the 2022 maps, 7 to 37 in 2021's) and the pole turned away (south of 66 to 70 degrees), a quarter of each map. The
    /// samples beside a hole are no better: seen at the edge of what Hubble saw, they fall to a tenth of their row (the 10th percentile
    /// at 4.9 degrees south is 24 in the 2022 F631N map, 220 at 4.1), and the fall reaches about 1.5 degrees in. So a sample within
    /// <paramref name="edgeDegrees"/> of any hole, in latitude or longitude, is dropped with the hole. A row that keeps at least
    /// <paramref name="minValidFraction"/> of its samples fills what it lost with the mean of what it kept, which suits a planet whose
    /// markings are zonal; a row that keeps fewer is replaced whole by the kept rows either side, linearly in latitude, and held from the
    /// nearest kept row beyond the last. The same map when it has no holes.
    /// </summary>
    public PlanetMap FilledZonally(double edgeDegrees = 2, double minValidFraction = 0.5)
    {
        var kept = new bool[_values.Length];
        var anyHole = false;
        for (var i = 0; i < _values.Length; i++)
        {
            kept[i] = _values[i] > 0;
            anyHole |= !kept[i];
        }
        if (!anyHole)
        {
            return this;
        }
        kept = Eroded(kept, (int)Math.Round(edgeDegrees * Width / 360), (int)Math.Round(edgeDegrees * Height / 180));

        var filled = new float[_values.Length];
        var keptRow = new bool[Height];
        for (var r = 0; r < Height; r++)
        {
            double sum = 0;
            var count = 0;
            for (var c = 0; c < Width; c++)
            {
                if (kept[(r * Width) + c])
                {
                    sum += _values[(r * Width) + c];
                    count++;
                }
            }
            if (count == 0 || count < minValidFraction * Width)
            {
                continue;
            }
            keptRow[r] = true;
            var mean = (float)(sum / count);
            for (var c = 0; c < Width; c++)
            {
                filled[(r * Width) + c] = kept[(r * Width) + c] ? _values[(r * Width) + c] : mean;
            }
        }
        if (Array.IndexOf(keptRow, true) < 0)
        {
            throw new InvalidOperationException("The map has no row valid enough to fill from");
        }

        for (var r = 0; r < Height; r++)
        {
            if (keptRow[r])
            {
                continue;
            }
            var above = r - 1;
            while (above >= 0 && !keptRow[above])
            {
                above--;
            }
            var below = r + 1;
            while (below < Height && !keptRow[below])
            {
                below++;
            }
            for (var c = 0; c < Width; c++)
            {
                filled[(r * Width) + c] = (above, below) switch
                {
                    ( < 0, _) => filled[(below * Width) + c],
                    (_, var b) when b >= Height => filled[(above * Width) + c],
                    _ => (float)(filled[(above * Width) + c] + ((r - above) / (double)(below - above) * (filled[(below * Width) + c] - filled[(above * Width) + c]))),
                };
            }
        }
        return new PlanetMap(filled, Width, Height);
    }

    // The mask with every sample within `dx` columns (wrapping in longitude) or `dy` rows of a false one made false: a box erosion, by
    // its two separable passes.
    private bool[] Eroded(bool[] mask, int dx, int dy)
    {
        var across = new bool[mask.Length];
        for (var r = 0; r < Height; r++)
        {
            // The count of false samples in the window centred on each column, slid along the row.
            var holes = 0;
            for (var k = -dx; k <= dx; k++)
            {
                holes += mask[(r * Width) + (((k % Width) + Width) % Width)] ? 0 : 1;
            }
            for (var c = 0; c < Width; c++)
            {
                across[(r * Width) + c] = holes == 0;
                holes -= mask[(r * Width) + ((((c - dx) % Width) + Width) % Width)] ? 0 : 1;
                holes += mask[(r * Width) + ((c + dx + 1) % Width)] ? 0 : 1;
            }
        }
        var result = new bool[mask.Length];
        for (var c = 0; c < Width; c++)
        {
            for (var r = 0; r < Height; r++)
            {
                var keep = true;
                for (var k = Math.Max(0, r - dy); keep && k <= Math.Min(Height - 1, r + dy); k++)
                {
                    keep = across[(k * Width) + c];
                }
                result[(r * Width) + c] = keep;
            }
        }
        return result;
    }

    /// <summary>
    /// Reads a map from a FITS file whose first row is the north edge, as OPAL's are (checked on the 2022 map: its Great Red
    /// Spot reads at 23 degrees south). Null when the file holds no image.
    /// </summary>
    public static PlanetMap? ReadFits(string path)
    {
        if (!Image.TryReadFitsFile(path, out var image))
        {
            return null;
        }
        try
        {
            return new PlanetMap(image.GetChannelSpan(0).ToArray(), image.Width, image.Height);
        }
        finally
        {
            image.Release();
        }
    }
}
