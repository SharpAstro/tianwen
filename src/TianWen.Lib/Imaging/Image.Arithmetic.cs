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
    /// Returns <c>this + other</c> per pixel. NaN-preserving via
    /// <see cref="TensorPrimitives.Add(ReadOnlySpan{float}, ReadOnlySpan{float}, Span{float})"/>.
    /// Used by the <c>SharpenPipeline</c> recombine step
    /// (<c>Final = DeconvolvedStarless + SharpenedStars</c>) and anywhere
    /// else two same-shaped images need to be combined additively without
    /// a clamp. For the running-sum accumulator pattern that needs to
    /// mutate one operand in place, use <see cref="AddInPlace"/>.
    /// </summary>
    /// <param name="other">Right-hand operand. Must match this image's shape.</param>
    /// <returns>A new <see cref="Image"/> at <see cref="BitDepth.Float32"/>.
    /// Shares <see cref="ImageMeta"/> with the left operand.</returns>
    /// <exception cref="ArgumentException">Shapes mismatch.</exception>
    public Image Add(Image other)
    {
        ValidateSameShape(other);
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var lhs = GetChannelSpan(c);
            var rhs = other.GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            TensorPrimitives.Add(lhs, rhs, output);
        }
        return new Image(dst, BitDepth.Float32, maxValue, minValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Per-pixel <c>screen</c> blend: <c>1 - (1 - this) * (1 - other)</c>,
    /// clamped to <c>[0, 1]</c>. Used by the <c>SharpenPipeline</c> when
    /// <c>RecombineMode.Screen</c> is selected -- this is the
    /// stretched-space "reverse multiply" identity that NAFNet-family star
    /// removers were trained against (<c>stretched_source =
    /// screen(stretched_starless, stretched_stars)</c>). Linear-space
    /// callers should prefer <see cref="Add"/> for physical correctness.
    /// </summary>
    /// <param name="other">Right-hand operand. Must match this image's shape.</param>
    /// <returns>A new <see cref="Image"/> at <see cref="BitDepth.Float32"/>.
    /// Shares <see cref="ImageMeta"/> with the left operand. NaN inputs
    /// propagate to the corresponding output pixel.</returns>
    /// <exception cref="ArgumentException">Shapes mismatch.</exception>
    public Image Screen(Image other)
    {
        ValidateSameShape(other);
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var lhs = GetChannelSpan(c);
            var rhs = other.GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            ScreenVec(lhs, rhs, output);
        }
        return new Image(dst, BitDepth.Float32, 1.0f, 0f, pedestal, imageMeta);
    }

    /// <summary>
    /// Per-pixel <c>unscreen</c> extraction: <c>1 - (1 - this) / (1 - other)</c>,
    /// clamped to <c>[0, 1]</c>. Inverts <see cref="Screen"/> -- given a
    /// composite <c>this</c> and one of the original layers
    /// <paramref name="other"/>, recovers the second layer. Used by the
    /// <c>SharpenPipeline</c> when <c>RecombineMode.Screen</c> is selected:
    /// <c>StarsOnly = Source.Unscreen(Starless)</c>.
    /// </summary>
    /// <remarks>
    /// <para>The denominator is clamped at <c>1 - max(0, min(1, other))</c>
    /// with a floor of <c>epsilon</c> to avoid division by zero on
    /// saturated pixels in <paramref name="other"/>. Result values outside
    /// <c>[0, 1]</c> are clamped: negative results (when
    /// <c>this &lt; other</c>) become 0 -- the layer would contribute
    /// negative light, which is unphysical and indicates a pixel where the
    /// "other" estimate overshot the true composite.</para>
    /// <para>NaN inputs propagate.</para>
    /// </remarks>
    public Image Unscreen(Image other, float epsilon = 1e-6f)
    {
        ValidateSameShape(other);
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var lhs = GetChannelSpan(c);
            var rhs = other.GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            UnscreenVec(lhs, rhs, epsilon, output);
        }
        return new Image(dst, BitDepth.Float32, 1.0f, 0f, pedestal, imageMeta);
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

    /// <summary>
    /// SIMD inner loop for <c>dst = 1 - (1 - lhs) * (1 - rhs)</c>, clamped
    /// to <c>[0, 1]</c>. NaN on either input propagates to the output via
    /// the natural propagation rules of float arithmetic.
    /// </summary>
    private static void ScreenVec(ReadOnlySpan<float> lhs, ReadOnlySpan<float> rhs, Span<float> dst)
    {
        var width = Vector<float>.Count;
        var one = Vector<float>.One;
        var zero = Vector<float>.Zero;
        var i = 0;
        for (; i <= lhs.Length - width; i += width)
        {
            var av = new Vector<float>(lhs[i..]);
            var bv = new Vector<float>(rhs[i..]);
            var result = one - (one - av) * (one - bv);
            result = Vector.Min(Vector.Max(result, zero), one);
            result.CopyTo(dst[i..]);
        }
        for (; i < lhs.Length; i++)
        {
            var v = 1f - (1f - lhs[i]) * (1f - rhs[i]);
            dst[i] = v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }

    /// <summary>
    /// SIMD inner loop for <c>dst = 1 - (1 - lhs) / max(1 - rhs, epsilon)</c>,
    /// clamped to <c>[0, 1]</c>. <c>lhs</c> is the composite ("source"),
    /// <c>rhs</c> is the known layer ("starless"); the result is the
    /// inferred second layer ("stars"). Denominator clamp at
    /// <paramref name="epsilon"/> guards against saturated <c>rhs</c>
    /// pixels (where <c>1 - rhs</c> would be 0 and produce inf).
    /// </summary>
    private static void UnscreenVec(ReadOnlySpan<float> lhs, ReadOnlySpan<float> rhs, float epsilon, Span<float> dst)
    {
        var width = Vector<float>.Count;
        var one = Vector<float>.One;
        var zero = Vector<float>.Zero;
        var epsVec = new Vector<float>(epsilon);
        var i = 0;
        for (; i <= lhs.Length - width; i += width)
        {
            var av = new Vector<float>(lhs[i..]);
            var bv = new Vector<float>(rhs[i..]);
            var denom = Vector.Max(one - bv, epsVec);
            var result = one - (one - av) / denom;
            result = Vector.Min(Vector.Max(result, zero), one);
            result.CopyTo(dst[i..]);
        }
        for (; i < lhs.Length; i++)
        {
            var denom = 1f - rhs[i];
            if (denom < epsilon) denom = epsilon;
            var v = 1f - (1f - lhs[i]) / denom;
            dst[i] = v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
