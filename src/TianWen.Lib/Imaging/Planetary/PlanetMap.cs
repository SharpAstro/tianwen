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
