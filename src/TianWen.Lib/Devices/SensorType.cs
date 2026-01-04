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
    public static SensorType FromFITSValue(params string[] patterns)
    {
        var firstNonNull = patterns.FirstOrDefault(pattern => !string.IsNullOrWhiteSpace(pattern));
        if (firstNonNull is null)
        {
            return SensorType.Monochrome;
        }

        return firstNonNull.ToUpperInvariant() switch
        {
            "RGGB" or "GRBG" or "GBRG" or "BGGR" => SensorType.RGGB,
            _ => SensorType.Unknown
        };
    }
}