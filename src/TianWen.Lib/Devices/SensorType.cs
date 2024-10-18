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
    /// Camera produces RGGB encoded Bayer array images.
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

        // TODO this is a bit simplified, support stuff like GRGB etc and support inferring via BayerOffsetY
        return string.Equals(firstNonNull, "RGGB", StringComparison.OrdinalIgnoreCase)
            ? SensorType.RGGB
            : SensorType.Unknown;
    }
}