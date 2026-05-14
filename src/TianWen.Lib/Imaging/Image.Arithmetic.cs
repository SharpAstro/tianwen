using System;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Returns <c>this - other + addedPedestal</c> per pixel, clamped to &gt;= 0.
    /// Used by calibration as <c>(light - bias - dark) + addedPedestal</c>: the
    /// optional constant offset prevents negative pixels when the dark mean
    /// exceeds the light's background. The offset is accumulated onto the
    /// returned image's pedestal field so downstream stretch / stats code can
    /// subtract it back out.
    /// </summary>
    /// <param name="other">Right-hand operand. Must match this image's shape.</param>
    /// <param name="addedPedestal">Constant added per pixel after subtraction. Default 0.</param>
    /// <returns>A new <see cref="Image"/> at <see cref="BitDepth.Float32"/>.
    /// Shares <see cref="ImageMeta"/> with the left operand.</returns>
    /// <exception cref="ArgumentException">Shapes mismatch.</exception>
    public Image Subtract(Image other, float addedPedestal = 0f)
    {
        ValidateSameShape(other);
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var lhs = GetChannelSpan(c);
            var rhs = other.GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            SubtractClampVec(lhs, rhs, addedPedestal, output);
        }
        return new Image(dst, BitDepth.Float32, maxValue, 0f, pedestal + addedPedestal, imageMeta);
    }

    /// <summary>
    /// Returns <c>this / max(other, epsilon)</c> per pixel. Used by calibration
    /// as the flat-field division step; <paramref name="epsilon"/> clamps the
    /// denominator to avoid div-by-zero on dead flat pixels.
    /// </summary>
    /// <param name="other">Denominator. Must match this image's shape. Typically
    /// a normalized master flat with median ~ 1.0.</param>
    /// <param name="epsilon">Lower clamp on the denominator. Default 1e-6.</param>
    /// <returns>A new <see cref="Image"/> at <see cref="BitDepth.Float32"/>.
    /// Shares <see cref="ImageMeta"/> with the left operand.</returns>
    /// <exception cref="ArgumentException">Shapes mismatch.</exception>
    public Image Divide(Image other, float epsilon = 1e-6f)
    {
        ValidateSameShape(other);
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var num = GetChannelSpan(c);
            var den = other.GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            DivideClampVec(num, den, epsilon, output);
        }
        return new Image(dst, BitDepth.Float32, maxValue, minValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Returns the element-wise product <c>this * other</c>. Two-image multiply
    /// is rare on the calibration path but supports weighted-mean integration
    /// (multiply by per-frame weight before summing) and Bayer-flat correction
    /// modelling.
    /// </summary>
    /// <exception cref="ArgumentException">Shapes mismatch.</exception>
    public Image Multiply(Image other)
    {
        ValidateSameShape(other);
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var lhs = GetChannelSpan(c);
            var rhs = other.GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            TensorPrimitives.Multiply(lhs, rhs, output);
        }
        return new Image(dst, BitDepth.Float32, maxValue, minValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Accumulates <paramref name="other"/> into this image in place
    /// (<c>this[i] += other[i]</c>). The lone in-place exception to the
    /// otherwise immutable arithmetic API — used by the live-stack accumulator
    /// where allocating a fresh result image per arriving frame would dominate
    /// the per-frame cost.
    /// </summary>
    /// <exception cref="ArgumentException">Shapes mismatch.</exception>
    internal void AddInPlace(Image other)
    {
        ValidateSameShape(other);
        for (var c = 0; c < ChannelCount; c++)
        {
            var dst = MemoryMarshal.CreateSpan(ref data[c][0, 0], data[c].Length);
            var src = other.GetChannelSpan(c);
            TensorPrimitives.Add(dst, src, dst);
        }
    }

    private void ValidateSameShape(Image other)
    {
        if (other.Width != Width || other.Height != Height || other.ChannelCount != ChannelCount)
        {
            throw new ArgumentException(
                $"Shape mismatch: this is {ChannelCount}x{Height}x{Width}, other is {other.ChannelCount}x{other.Height}x{other.Width}.",
                nameof(other));
        }
    }

    /// <summary>
    /// SIMD inner loop for <c>dst = max(lhs - rhs + pedestal, 0)</c>.
    /// Negative-pixel clamp matches SetiAstro's subtract_dark_with_pedestal:
    /// the pedestal absorbs small overshoot and the clamp catches the rest so
    /// downstream stats don't see false-negative values. NaN inputs may collapse
    /// to 0 here — calibration runs on raw lights, which don't contain NaN in
    /// practice; NaN borders arise later in the pipeline (registration warp).
    /// </summary>
    private static void SubtractClampVec(ReadOnlySpan<float> lhs, ReadOnlySpan<float> rhs, float pedestal, Span<float> dst)
    {
        var width = Vector<float>.Count;
        var pedVec = new Vector<float>(pedestal);
        var zero = Vector<float>.Zero;
        var i = 0;
        for (; i <= lhs.Length - width; i += width)
        {
            var av = new Vector<float>(lhs[i..]);
            var bv = new Vector<float>(rhs[i..]);
            var result = Vector.Max(av - bv + pedVec, zero);
            result.CopyTo(dst[i..]);
        }
        for (; i < lhs.Length; i++)
        {
            var v = lhs[i] - rhs[i] + pedestal;
            dst[i] = v > 0f ? v : 0f;
        }
    }

    /// <summary>
    /// SIMD inner loop for <c>dst = num / max(den, epsilon)</c>. The denominator
    /// clamp avoids inf/NaN on dead flat-field pixels (sensor cells with zero
    /// throughput).
    /// </summary>
    private static void DivideClampVec(ReadOnlySpan<float> num, ReadOnlySpan<float> den, float epsilon, Span<float> dst)
    {
        var width = Vector<float>.Count;
        var epsVec = new Vector<float>(epsilon);
        var i = 0;
        for (; i <= num.Length - width; i += width)
        {
            var nv = new Vector<float>(num[i..]);
            var dv = Vector.Max(new Vector<float>(den[i..]), epsVec);
            (nv / dv).CopyTo(dst[i..]);
        }
        for (; i < num.Length; i++)
        {
            var d = den[i];
            dst[i] = num[i] / (d > epsilon ? d : epsilon);
        }
    }
}
