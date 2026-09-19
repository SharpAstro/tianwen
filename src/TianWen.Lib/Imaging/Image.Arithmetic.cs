using System;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Returns a copy with every non-finite (NaN / +/-Inf) sample replaced by
    /// the per-channel mean of the finite samples (0 when a channel is entirely
    /// non-finite). Returns the SAME instance -- no allocation -- when every
    /// sample is already finite, so the clean-input path is bit-identical.
    /// </summary>
    /// <remarks>
    /// <para>The AI enhancers -- the SETI Astro ONNX models and the RC-Astro CLI alike -- compute
    /// non-NaN-aware global normalisation (median / MAD / min-max), so a single NaN poisons the
    /// entire output to NaN. <see cref="Enhancement.SharpenPipeline"/> calls this at its input
    /// boundary, AFTER <see cref="WithInteriorHolesFilled"/>, so what reaches it is only the
    /// border-connected ring a crop would have removed; there the exact value is not critical and
    /// the per-channel mean is a cheap, smooth, range-safe choice.</para>
    /// <para><b>It is not a hole fill, and it used to be used as one.</b> A drizzle master's
    /// rejection voids sit on the brightest part of the picture, different pixels per channel, and
    /// a frame mean written into one channel beside a real value in another is an impossible
    /// colour (the Great Orion speck, 2026-09-19). Interior holes take their neighbours' value;
    /// this takes the remainder.</para>
    /// </remarks>
    public Image ReplaceNonFiniteWithChannelMean()
    {
        var (channels, width, height) = Shape;
        var means = new float[channels];
        var hasNonFinite = false;
        for (var c = 0; c < channels; c++)
        {
            var span = GetChannelSpan(c);
            var sum = 0.0;
            var finite = 0L;
            for (var i = 0; i < span.Length; i++)
            {
                var v = span[i];
                if (float.IsFinite(v))
                {
                    sum += v;
                    finite++;
                }
                else
                {
                    hasNonFinite = true;
                }
            }
            means[c] = finite > 0 ? (float)(sum / finite) : 0f;
        }

        if (!hasNonFinite)
        {
            return this;
        }

        var filled = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var src = GetChannelSpan(c);
            var dst = new float[height, width];
            var dstSpan = MemoryMarshal.CreateSpan(ref dst[0, 0], dst.Length);
            var fill = means[c];
            for (var i = 0; i < src.Length; i++)
            {
                var v = src[i];
                dstSpan[i] = float.IsFinite(v) ? v : fill;
            }
            filled[c] = dst;
        }

        return new Image(filled, bitDepth, MaxValue, MinValue, pedestal, imageMeta);
    }

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
        Span<float> perChannel = stackalloc float[ChannelCount];
        perChannel.Fill(addedPedestal);
        return Subtract(other, perChannel);
    }

    /// <summary>
    /// As <see cref="Subtract(Image, float)"/>, but each channel gets its OWN added pedestal.
    /// </summary>
    /// <remarks>
    /// <para><b>A LEVEL IS A PER-PLANE FACT, and one scalar over three channels describes none of
    /// them.</b> This is the same rule the CFA code learned the hard way -- a whole-frame statistic
    /// over a mosaic describes none of its four photosite populations -- one level up, and it bites
    /// the same way. Background extraction subtracts a model that tracks each channel's own sky; add
    /// back a SHARED constant and every channel lands on that constant, which is not "the level was
    /// preserved" but background neutralisation, a different operation that belongs to a different
    /// step. Measured on an SV605CC narrowband master, whose channel medians are genuinely
    /// R/G 0.332 and B/G 0.552: a shared add-back returned them as 1.004 and 1.003, so a white
    /// balance solved on the input became a gross over-correction on the output and rendered the
    /// frame flat red.</para>
    /// <para>The image's own scalar <see cref="Pedestal"/> accumulates the MEAN of the per-channel
    /// offsets, which is exactly the old value whenever they are all equal, so a mono frame and every
    /// existing caller are bit-identical.</para>
    /// </remarks>
    /// <param name="other">Right-hand operand. Must match this image's shape.</param>
    /// <param name="addedPedestals">One constant per channel, added per pixel after subtraction.</param>
    /// <exception cref="ArgumentException">Shapes mismatch, or the wrong number of pedestals.</exception>
    public Image Subtract(Image other, ReadOnlySpan<float> addedPedestals)
    {
        ValidateSameShape(other);
        if (addedPedestals.Length != ChannelCount)
        {
            throw new ArgumentException(
                $"Expected {ChannelCount} pedestals, one per channel, but got {addedPedestals.Length}.",
                nameof(addedPedestals));
        }

        var dst = CreateChannelData(ChannelCount, Height, Width);
        var pedestalSum = 0f;
        for (var c = 0; c < ChannelCount; c++)
        {
            var lhs = GetChannelSpan(c);
            var rhs = other.GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            SubtractClampVec(lhs, rhs, addedPedestals[c], output);
            pedestalSum += addedPedestals[c];
        }
        return new Image(dst, BitDepth.Float32, MaxValue, 0f,
            pedestal + pedestalSum / ChannelCount, imageMeta);
    }

    /// <summary>
    /// Returns <c>this / other</c> per pixel where the denominator carries information, and
    /// <see cref="float.NaN"/> where it does not. Used by calibration as the flat-field division step.
    /// </summary>
    /// <remarks>
    /// <para><b>A denominator at or below <paramref name="epsilon"/> yields NaN, it is not clamped.</b>
    /// This used to divide by <c>max(other, epsilon)</c>, which does not prevent the division by zero
    /// so much as rename it: with the old 1e-6 default a flat pixel of exactly 0 multiplied the light
    /// by a million, and the result was a finite number indistinguishable from data. It was measured in
    /// the wild at 2.46e8 against a sky of 213 on a QHY master whose overscan columns are zero in the
    /// flat, and 245,827,712 x 1e-6 = 245.83 is exactly the light pixel that went in.</para>
    /// <para>A pixel with no usable denominator has no calibrated value, and NaN is the vocabulary this
    /// codebase already has for that: <see cref="Image.LargestCoveredRectangle()"/> reads it as absence,
    /// <c>MeanCombiner</c> skips it, <c>FillInteriorHolesInPlace</c> interpolates an interior one.
    /// Every one of those does the right thing with a NaN and none of them can do anything at all with
    /// a number six orders too big.</para>
    /// </remarks>
    /// <param name="other">Denominator. Must match this image's shape. Typically
    /// a normalized master flat with median ~ 1.0.</param>
    /// <param name="epsilon">At or below this the denominator carries no information and the output is
    /// NaN. Default 1e-6, which only catches an exact zero; a caller dividing by a master flat wants
    /// <see cref="Calibrator.FlatEpsilon"/>'s measured default instead.</param>
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
            DivideAbsentVec(num, den, epsilon, output);
        }
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
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
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Returns <c>this * scalar</c> per pixel, all channels. The partial-strength-mask
    /// primitive: <see cref="BlendThroughMask"/> uses its mask verbatim, so
    /// <c>mask.Multiply(0.5f)</c> is how a caller applies a masked operation at half
    /// strength. No clamp; NaN propagates.
    /// </summary>
    public Image Multiply(float scalar)
    {
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var src = GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            TensorPrimitives.Multiply(src, scalar, output);
        }
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
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
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
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
    /// Per-pixel linear interpolation: <c>(1 - amount) * this + amount * other</c>,
    /// where <paramref name="amount"/> is clamped to <c>[0, 1]</c>. Used by
    /// <c>SharpenPipeline</c> to blend an AI enhancer's output back toward
    /// its input ("blend slider" -- typical good range 0.4..0.7 for AI4
    /// sharpening, so the network output doesn't fully replace the source
    /// and create the "snap-to-pixel" pixelation on tight star fields).
    /// </summary>
    /// <param name="other">Right-hand operand. Must match this image's shape.</param>
    /// <param name="amount">Interpolation weight. 0 returns <c>this</c>;
    /// 1 returns <paramref name="other"/>; 0.5 is the midpoint.</param>
    /// <exception cref="ArgumentException">Shapes mismatch.</exception>
    public Image Lerp(Image other, float amount)
    {
        ValidateSameShape(other);
        var a = Math.Clamp(amount, 0f, 1f);
        if (a == 0f) return new Image(CopyChannelData(), BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
        if (a == 1f) return new Image(other.CopyChannelData(), BitDepth.Float32, other.MaxValue, other.MinValue, pedestal, imageMeta);

        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var lhs = GetChannelSpan(c);
            var rhs = other.GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            TensorPrimitives.Lerp(lhs, rhs, a, output);
        }
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Per-pixel Subtractive Chromatic Noise Reduction (SCNR) on the green
    /// channel, used to neutralise the green cast on stars (OSC sensors'
    /// 2:1 green-Bayer dominance). For each pixel <c>m = mode == Average ?
    /// (R + B) * 0.5 : max(R, B)</c>; new <c>G = G - amount * max(0, G - m)</c>.
    /// At <paramref name="amount"/> = 1 the green channel is clamped to
    /// <c>m</c>; at 0 the image is unchanged.
    /// </summary>
    /// <remarks>
    /// <para>SCNR is destructive of legitimate green signal (e.g. OIII /
    /// H-beta nebula emission). Apply on a stars-only plate, NOT on the
    /// full image -- callers in <c>SharpenPipeline</c> run SCNR strictly on
    /// the stellar branch (<c>SharpenedStars</c> or <c>StarsOnly</c>) before
    /// recombining with the untouched starless plate, so nebula chromaticity
    /// is preserved.</para>
    /// <para>NaN inputs in any channel propagate to the output G pixel.
    /// Mono / non-RGB images pass through unchanged (the operation is a
    /// no-op when fewer than 3 channels are present).</para>
    /// </remarks>
    /// <param name="mode">Reference value rule: <see cref="ScnrMode.Average"/>
    /// uses <c>(R + B) / 2</c>; <see cref="ScnrMode.Maximum"/> uses
    /// <c>max(R, B)</c> (more aggressive green removal).</param>
    /// <param name="amount">Strength in [0, 1]. Clamped.</param>
    public Image SubtractiveChromaticNoise(ScnrMode mode, float amount = 1.0f)
    {
        if (mode is ScnrMode.None || ChannelCount < 3)
        {
            return new Image(CopyChannelData(), BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
        }

        var clamped = Math.Clamp(amount, 0f, 1f);
        var dst = CreateChannelData(ChannelCount, Height, Width);

        // R + B copied verbatim. The G channel gets the SCNR formula.
        // Any channels beyond RGB (rare) copy verbatim too.
        var rSpan = GetChannelSpan(0);
        var gSpan = GetChannelSpan(1);
        var bSpan = GetChannelSpan(2);
        var rDst = MemoryMarshal.CreateSpan(ref dst[0][0, 0], dst[0].Length);
        var gDst = MemoryMarshal.CreateSpan(ref dst[1][0, 0], dst[1].Length);
        var bDst = MemoryMarshal.CreateSpan(ref dst[2][0, 0], dst[2].Length);
        rSpan.CopyTo(rDst);
        bSpan.CopyTo(bDst);
        ScnrChannelG(rSpan, gSpan, bSpan, gDst, mode, clamped);
        for (var c = 3; c < ChannelCount; c++)
        {
            GetChannelSpan(c).CopyTo(MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length));
        }

        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Helper for <see cref="Lerp"/> and <see cref="SubtractiveChromaticNoise"/>:
    /// duplicates the backing <c>float[][,]</c> when callers need a
    /// pass-through-with-fresh-buffer result (caller can mutate the returned
    /// image without aliasing the source).
    /// </summary>
    private float[][,] CopyChannelData()
    {
        var copy = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            GetChannelSpan(c).CopyTo(MemoryMarshal.CreateSpan(ref copy[c][0, 0], copy[c].Length));
        }
        return copy;
    }

    /// <summary>
    /// Accumulates <paramref name="other"/> into this image in place
    /// (<c>this[i] += other[i]</c>). The lone in-place exception to the
    /// otherwise immutable arithmetic API: used by the live-stack accumulator
    /// where allocating a fresh result image per arriving frame would dominate
    /// the per-frame cost.
    /// </summary>
    /// <exception cref="ArgumentException">Shapes mismatch.</exception>
    internal void AddInPlace(Image other)
    {
        ValidateSameShape(other);
        for (var c = 0; c < ChannelCount; c++)
        {
            var plane = Planes[c].Data;
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
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
    /// to 0 here: calibration runs on raw lights, which don't contain NaN in
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
    /// SIMD inner loop for <c>dst = den > epsilon ? num / den : NaN</c>. A sensor cell with no
    /// throughput has no calibrated value, so it is marked absent rather than scaled by 1/epsilon.
    /// </summary>
    private static void DivideAbsentVec(ReadOnlySpan<float> num, ReadOnlySpan<float> den, float epsilon, Span<float> dst)
    {
        var width = Vector<float>.Count;
        var epsVec = new Vector<float>(epsilon);
        var nanVec = new Vector<float>(float.NaN);
        var i = 0;
        for (; i <= num.Length - width; i += width)
        {
            var nv = new Vector<float>(num[i..]);
            var dv = new Vector<float>(den[i..]);
            // The quotient is computed for every lane including the absent ones, where it is an
            // infinity that the select then discards. Float division raises nothing in .NET, so
            // this costs one select and never a fault.
            Vector.ConditionalSelect(Vector.GreaterThan(dv, epsVec), nv / dv, nanVec).CopyTo(dst[i..]);
        }
        for (; i < num.Length; i++)
        {
            var d = den[i];
            dst[i] = d > epsilon ? num[i] / d : float.NaN;
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

    /// <summary>
    /// SIMD inner loop for SCNR: <c>m = mode == Average ? (r + b) * 0.5 : max(r, b);
    /// dst = g - amount * max(0, g - m)</c>. NaN propagates: if any of r/g/b
    /// is NaN, dst becomes NaN via the arithmetic.
    /// </summary>
    private static void ScnrChannelG(
        ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        Span<float> dst, ScnrMode mode, float amount)
    {
        var width = Vector<float>.Count;
        var amountVec = new Vector<float>(amount);
        var halfVec = new Vector<float>(0.5f);
        var zero = Vector<float>.Zero;
        var i = 0;
        if (mode is ScnrMode.Average)
        {
            for (; i <= r.Length - width; i += width)
            {
                var rv = new Vector<float>(r[i..]);
                var gv = new Vector<float>(g[i..]);
                var bv = new Vector<float>(b[i..]);
                var refVal = (rv + bv) * halfVec;
                var excess = Vector.Max(gv - refVal, zero);
                (gv - amountVec * excess).CopyTo(dst[i..]);
            }
            for (; i < r.Length; i++)
            {
                var refVal = (r[i] + b[i]) * 0.5f;
                var excess = MathF.Max(g[i] - refVal, 0f);
                dst[i] = g[i] - amount * excess;
            }
        }
        else
        {
            for (; i <= r.Length - width; i += width)
            {
                var rv = new Vector<float>(r[i..]);
                var gv = new Vector<float>(g[i..]);
                var bv = new Vector<float>(b[i..]);
                var refVal = Vector.Max(rv, bv);
                var excess = Vector.Max(gv - refVal, zero);
                (gv - amountVec * excess).CopyTo(dst[i..]);
            }
            for (; i < r.Length; i++)
            {
                var refVal = MathF.Max(r[i], b[i]);
                var excess = MathF.Max(g[i] - refVal, 0f);
                dst[i] = g[i] - amount * excess;
            }
        }
    }
}

/// <summary>
/// Reference-value rule for <see cref="Image.SubtractiveChromaticNoise"/>.
/// Maps to the four PixInsight SCNR variants via the <c>amount</c> parameter:
/// Average + amount=1 = "Average Neutral Protection"; Maximum + amount=1 =
/// "Maximum Neutral Protection"; either mode with amount &lt; 1 = the
/// corresponding "Mask" variant.
/// </summary>
public enum ScnrMode
{
    /// <summary>No SCNR applied -- pass-through.</summary>
    None = 0,

    /// <summary>Reference value is <c>(R + B) / 2</c>. Preserves more green
    /// in highlights than <see cref="Maximum"/>; the recommended default
    /// for star plates because it doesn't over-correct neutral white stars.</summary>
    Average = 1,

    /// <summary>Reference value is <c>max(R, B)</c>. More aggressive green
    /// removal -- useful when the green cast is severe or only the
    /// strongest channel should set the cap.</summary>
    Maximum = 2,
}
