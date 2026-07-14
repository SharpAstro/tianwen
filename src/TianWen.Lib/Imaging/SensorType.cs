using System;
using System.Linq;

namespace TianWen.Lib.Imaging;

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
    /// Indicates unknown sensor type, e.g. if camera was not initialised or ICameraDriver.CanFastReadout is false.
    /// </summary>
    Unknown = int.MaxValue
}

public static class SensorTypeEx
{
    extension(SensorType)
    {
        public static (SensorType SensorType, int X, int Y) FromFITSValue(bool? hasCFA, int channelCount, int fileOffsetX, int fileOffsetY, params string?[] patterns)
        {
            // A 3-plane image is already debayered -- it cannot be a CFA mosaic, whatever stale
            // CFAIMAGE/BAYERPAT provenance tags it still carries (e.g. Astro Pixel Processor keeps
            // them on its RGB integrations). Classifying it by pattern would route it through a
            // real debayer pass downstream, which reads only channel 0 and discards the other two.
            if (channelCount is 3)
            {
                return (SensorType.Color, 0, 0);
            }

            var firstNonNull = patterns.FirstOrDefault(pattern => !string.IsNullOrWhiteSpace(pattern));
            if (hasCFA is false || firstNonNull is null)
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

        /// <summary>True when <paramref name="value"/> is one of the four 2x2 Bayer pattern strings
        /// (case-insensitive, whitespace-tolerant). The gate for treating a free-form header token
        /// (e.g. Astro Pixel Processor's CFAIMAGE, which holds the pattern rather than a T/F flag)
        /// as a pattern candidate -- so boolean or garbage values never reach the pattern switch.</summary>
        public static bool IsKnownBayerPattern(string? value) =>
            value?.Trim().ToUpperInvariant() is "RGGB" or "GRBG" or "GBRG" or "BGGR";
    }

    extension(SensorType sensorType)
    {
        public int[,] GetBayerPatternMatrix(int offsetX, int offsetY)
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
}