using FC.SDK.Raw;
using System;
using System.Numerics;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Where a white-balanced Canon raw frame clips, and the scale its pixels are delivered in.
/// <para>
/// <c>CanonRaw.PreprocessMosaic</c> subtracts the black level, divides by the range up to the 14-bit ceiling and
/// multiplies each photosite by its white-balance factor, with no clamp. A body saturates BELOW the ceiling (an EOS
/// 6D at 15490 of 16383) and green, the smallest factor, clips first: on a blown highlight green stops at the white
/// point while red and blue go on rising to their own factor, so the highlight is magenta. The white point of the
/// balanced frame is therefore the raw white level times the LOWEST factor, and a pixel past it is clipped and neutral.
/// </para>
/// </summary>
internal static class CanonWhitePoint
{
    /// <summary>The pedestal <c>CanonRaw.PreprocessMosaic</c> subtracts (the Canon 14-bit default its docs name).</summary>
    public const int BlackLevel = 2048;

    /// <summary>What <c>CanonRaw.PreprocessMosaic</c> uses when the file carries no as-shot balance.</summary>
    public static readonly CanonWhiteBalance DaylightFallback = new(2.0f, 1.0f, 1.0f, 1.4f);

    /// <summary>The raw range a unit-referred pixel is divided by: the 14-bit ceiling less the black level.</summary>
    public static float HeadroomAdu(int bitDepth, int blackLevel) => Math.Max(1, ((1 << bitDepth) - 1) - blackLevel);

    /// <summary>
    /// One row of the picture: every pixel of <paramref name="source"/> held to <paramref name="white"/>, multiplied by
    /// <paramref name="toOutput"/> (1 for the unit-referred frame, the range for ADU counts) and raised by
    /// <paramref name="offset"/> (0 for the unit-referred frame, the black level for ADU counts), into
    /// <paramref name="destination"/>. Returns the row's peak.
    /// </summary>
    /// <remarks>
    /// The ADU frame keeps the body's black level, as any camera's frame keeps its offset: black-subtracted, half of a dark's
    /// read noise is below zero, and a 16-bit FITS file (BZERO 32768) writes every one of those as 0, so each Canon dark and
    /// bias lost the lower half of its noise and read back with a median of 0.
    /// </remarks>
    public static float ClampRow(ReadOnlySpan<float> source, Span<float> destination, float white, float toOutput, float offset = 0f)
    {
        destination = destination[..source.Length];
        var i = 0;
        var peak = 0f;

        // Vector<float> lanes: min, multiply, add and max are exact IEEE operations (and neither form is contracted into a
        // fused multiply-add), so the row is bit for bit the scalar loop's
        // (ClampRowScalar, which the tail below is and the tests compare against), and the peak is a max, which is
        // order-free, so unlike a running SUM it may be taken per lane.
        if (Vector.IsHardwareAccelerated && source.Length >= Vector<float>.Count)
        {
            var whiteLanes = new Vector<float>(white);
            var scaleLanes = new Vector<float>(toOutput);
            var offsetLanes = new Vector<float>(offset);
            var peakLanes = Vector<float>.Zero;
            for (var last = source.Length - Vector<float>.Count; i <= last; i += Vector<float>.Count)
            {
                var clamped = (Vector.Min(new Vector<float>(source[i..]), whiteLanes) * scaleLanes) + offsetLanes;
                clamped.CopyTo(destination[i..]);
                peakLanes = Vector.Max(peakLanes, clamped);
            }

            for (var lane = 0; lane < Vector<float>.Count; lane++)
            {
                peak = MathF.Max(peak, peakLanes[lane]);
            }
        }

        return MathF.Max(peak, ClampRowScalar(source[i..], destination[i..], white, toOutput, offset));
    }

    /// <summary>The definition <see cref="ClampRow"/> is held to: one pixel at a time.</summary>
    internal static float ClampRowScalar(ReadOnlySpan<float> source, Span<float> destination, float white, float toOutput, float offset = 0f)
    {
        var peak = 0f;
        for (var i = 0; i < source.Length; i++)
        {
            var value = (MathF.Min(source[i], white) * toOutput) + offset;
            destination[i] = value;
            if (value > peak)
            {
                peak = value;
            }
        }

        return peak;
    }

    /// <summary>
    /// The white point in unit-referred values (1.0 is the raw ceiling above the black level): the body's own
    /// saturation level, or the ceiling when it has no override (<paramref name="maxRaw"/> of 0), times the smallest
    /// white-balance factor.
    /// </summary>
    public static float UnitFor(int bitDepth, int blackLevel, int maxRaw, CanonWhiteBalance wb)
    {
        var ceiling = (1 << bitDepth) - 1;
        var saturation = maxRaw > blackLevel && maxRaw <= ceiling ? maxRaw : ceiling;
        var lowestFactor = MathF.Min(MathF.Min(wb.R, wb.B), MathF.Min(wb.G1, wb.G2));
        return (saturation - blackLevel) / HeadroomAdu(bitDepth, blackLevel) * lowestFactor;
    }
}
