using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A sensor's native ADC resolution in bits (e.g. 14 for the ASI533MC Pro / IMX533) -- how many bits
/// the analog-to-digital converter actually produces per sample. Deliberately its own type, NOT a
/// <see cref="BitDepth"/> member and NOT a bare <see langword="int"/>: <see cref="BitDepth"/> only
/// models the FITS-legal container/storage width a frame is written into (BITPIX 8/16/32/64/-32/-64),
/// while a camera's true saturation point is set by its ADC, which almost never matches one of those
/// widths (10/12/14/16/18-bit are the common real values). A 14-bit sensor's pixels still land in a
/// 16-bit (Int16) container, but normalising by the container's full scale (65535) instead of the
/// ADC's (16383) silently overstates the sensor's dynamic range. Keeping this as a distinct type --
/// rather than an int that also happens to be called "bit depth" -- stops the two concepts being
/// passed to each other by accident.
/// </summary>
public readonly record struct AdcResolution(int Bits)
{
    /// <summary>Full-scale ADU at this resolution: 2^<see cref="Bits"/> - 1 (16383 for 14-bit).</summary>
    public long FullScaleAdu => Bits is > 0 and <= 32
        ? (1L << Bits) - 1
        : throw new ArgumentOutOfRangeException(nameof(Bits), Bits, "ADC resolution must be in (0, 32] bits.");
}
