using System;
using System.Linq;

namespace TianWen.Lib.Devices;

public enum SensorType
{
    /// <summary>
    /// Camera produces monochrome array with no Bayer encoding.
    /// </summary>
    Monochrome,

    /// <summary>
    /// Camera produces color image directly, requiring no Bayer decoding.
    /// </summary>
    Color,

    /// <summary>
    /// Camera produces RGGB encoded Bayer array images, needs to be used together with BayerX,Y offset to determine the actual pattern.
    /// </summary>
    RGGB,

    /// <summary>
    /// Indicates unknown sensor type, e.g. if camera was not initalised or <see cref="ICameraDriver.CanFastReadout"/> is <code>false</code>.
    /// </summary>
    Unknown = int.MaxValue
}

public static class SensorTypeEx
{
    public static (SensorType SensorType, int X, int Y) FromFITSValue(int fileOffsetX, int fileOffsetY, params string[] patterns)
    {
        var firstNonNull = patterns.FirstOrDefault(pattern => !string.IsNullOrWhiteSpace(pattern));
        if (firstNonNull is null)
        {
            return (SensorType.Monochrome, 0, 0);
        }

        var (sensorType, sensorOffsetX, sensorOffsetY) = firstNonNull.ToUpperInvariant() switch
        {
            "RGGB" => (SensorType.RGGB, 0, 0),
            "GRBG" => (SensorType.RGGB, 1, 0),
            "GBRG" => (SensorType.RGGB, 0, 1),
            "BGGR" => (SensorType.RGGB, 1, 1),
            _ => (SensorType.Unknown, 0, 0)
        };

        var offsetX = (fileOffsetX + sensorOffsetX) % 2;
        var offsetY = (fileOffsetY + sensorOffsetY) % 2;

        // TODO: not sure if this is true?
        return (sensorType, offsetX, offsetY);            
    }

    public static int[,] GetBayerPatternMatrix(this SensorType sensorType, int offsetX, int offsetY)
    {
        const int R = 0, G = 1, B = 2;

        // RGGB is the base pattern, offsets determine the actual pattern:
        // offsetX=0, offsetY=0: RGGB (R G / G B)
        // offsetX=1, offsetY=0: GRBG (G R / B G)
        // offsetX=0, offsetY=1: GBRG (G B / R G)
        // offsetX=1, offsetY=1: BGGR (B G / G R)
        int[,] basePattern = sensorType switch
        {
            SensorType.RGGB => new int[,] { { R, G }, { G, B } },
            _ => new int[,] { { R, G }, { G, B } } // Default to RGGB for unknown
        };

        // Apply offset by rotating the pattern
        var pattern = new int[2, 2];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                pattern[y, x] = basePattern[(y + offsetY) % 2, (x + offsetX) % 2];
            }
        }

        return pattern;
    }
}