using System;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    // Rec.709 luma, the codebase default (see StretchUniforms.LumaWeights). Masks are shape-only,
    // so the exact weights barely matter; kept as a default callers can override.
    private static readonly (float R, float G, float B) DefaultLumaWeights = (0.2126f, 0.7152f, 0.0722f);

    /// <summary>
    /// Builds a feathered <b>luminance range mask</b> as a single-channel <see cref="Image"/> in [0, 1]:
    /// ~1 over mid-tone signal (nebulosity), ~0 in the background, and ~0 in the brightest highlights
    /// (star cores). This is the reusable primitive behind masked saturation, masked contrast boost, and
    /// any other "affect the signal, protect the background and stars" operation -- feed it to
    /// <see cref="BlendThroughMask"/>. The result is an ordinary <see cref="Image"/>, so it renders,
    /// exports, and reports statistics like any other (handy for previewing what a step will touch).
    /// </summary>
    /// <param name="shadowPercentile">Luminance percentile taken as the background anchor; the mask is 0
    /// at or below it. Default 40 (the 40th percentile is a robust background estimate for a stretched
    /// deep-sky frame). In [0, 100].</param>
    /// <param name="shadowGamma">Exponent applied to the shadow ramp; &gt;1 pulls faint signal down so only
    /// clearly-above-background pixels get full weight. Default 1.5.</param>
    /// <param name="highlightStart">Luminance where the highlight roll-off begins; above this the mask
    /// fades back to 0 to protect star cores / clipped highlights. Default 0.9. In (0, 1].</param>
    /// <param name="highlightWidth">Width of the highlight roll-off below <paramref name="highlightStart"/>.
    /// Default 0.3; smaller = sharper highlight protection.</param>
    /// <param name="blurSigma">Gaussian feather (pixels) so the mask has no hard edges. Default 3. 0 = no
    /// blur.</param>
    /// <param name="lumaWeights">Luminance weights; defaults to Rec.709.</param>
    /// <returns>A single-channel <see cref="Image"/> mask in [0, 1], same Width/Height as the source.</returns>
    public Image LuminanceRangeMask(
        float shadowPercentile = 40f,
        float shadowGamma = 1.5f,
        float highlightStart = 0.9f,
        float highlightWidth = 0.3f,
        float blurSigma = 3f,
        (float R, float G, float B)? lumaWeights = null)
    {
        var w = Width;
        var h = Height;
        var count = w * h;

        // Luminance. Mono images use the single channel directly.
        var luma = new float[count];
        if (ChannelCount >= 3)
        {
            var (wr, wg, wb) = lumaWeights ?? DefaultLumaWeights;
            var r = GetChannelSpan(0);
            var g = GetChannelSpan(1);
            var b = GetChannelSpan(2);
            for (var i = 0; i < count; i++)
            {
                luma[i] = wr * r[i] + wg * g[i] + wb * b[i];
            }
        }
        else
        {
            GetChannelSpan(0).CopyTo(luma);
        }

        // Background anchor + peak. PercentileFast reorders its buffer, so percentile off a scratch copy.
        var scratch = new float[count];
        luma.CopyTo(scratch, 0);
        var bg = StatisticsHelper.PercentileFast(scratch, Math.Clamp(shadowPercentile / 100f, 0f, 1f));
        var maxL = float.NegativeInfinity;
        for (var i = 0; i < count; i++)
        {
            if (luma[i] > maxL) maxL = luma[i];
        }

        var invSpan = 1f / MathF.Max(maxL - bg, 1e-5f);
        var invHi = highlightWidth > 0f ? 1f / highlightWidth : float.PositiveInfinity;

        var mask = new float[count];
        for (var i = 0; i < count; i++)
        {
            var l = luma[i];
            // Shadow ramp: rise from background to peak, steepened by gamma.
            var low = Clamp01((l - bg) * invSpan);
            if (shadowGamma != 1f) low = MathF.Pow(low, shadowGamma);
            // Highlight roll-off: fade to 0 above highlightStart to protect star cores.
            var high = highlightWidth > 0f ? Clamp01((highlightStart - l) * invHi) : 1f;
            mask[i] = low * high;
        }

        if (blurSigma > 0f)
        {
            mask = SeparableGaussianBlur(mask, w, h, blurSigma);
        }

        var channel = new float[h, w];
        MemoryMarshal.CreateSpan(ref mask[0], count).CopyTo(MemoryMarshal.CreateSpan(ref channel[0, 0], count));
        return FromChannel(channel, maxValue: 1f, minValue: 0f);
    }

    /// <summary>
    /// Blends <paramref name="processed"/> into this image through a single-channel <paramref name="mask"/>:
    /// <c>result = this * (1 - mask) + processed * mask</c>, per pixel, broadcast across all channels. This
    /// is what makes any whole-image operation "masked" -- e.g.
    /// <c>img.BlendThroughMask(img.Saturate(2.5f), img.LuminanceRangeMask())</c>. The mask is used verbatim
    /// (not re-clamped), so pre-scale it if you want a partial-strength apply.
    /// </summary>
    /// <param name="processed">The processed image to blend in where the mask is high. Same shape as this.</param>
    /// <param name="mask">Single-channel blend factor in [0, 1], same Width/Height as this.</param>
    /// <exception cref="ArgumentException">Shapes mismatch, or the mask is not single-channel.</exception>
    public Image BlendThroughMask(Image processed, Image mask)
    {
        ValidateSameShape(processed);
        if (mask.Width != Width || mask.Height != Height)
        {
            throw new ArgumentException(
                $"Mask shape mismatch: image is {Height}x{Width}, mask is {mask.Height}x{mask.Width}.",
                nameof(mask));
        }
        if (mask.ChannelCount != 1)
        {
            throw new ArgumentException($"Mask must be single-channel, got {mask.ChannelCount} channels.", nameof(mask));
        }

        var m = mask.GetChannelSpan(0);
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var baseSpan = GetChannelSpan(c);
            var procSpan = processed.GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            // output = base + (proc - base) * mask  == base*(1-m) + proc*m
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = baseSpan[i] + (procSpan[i] - baseSpan[i]) * m[i];
            }
        }
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Colour saturation: pushes each channel away from its per-pixel luminance by
    /// <paramref name="boost"/> (<c>out = L + (in - L) * boost</c>), clamped to [0, 1]. Whole-image and
    /// unmasked -- compose with <see cref="LuminanceRangeMask"/> + <see cref="BlendThroughMask"/> for a
    /// masked saturation that leaves the background and star cores alone. A no-op copy for &lt;3-channel
    /// (mono) images.
    /// </summary>
    /// <param name="boost">Saturation multiplier. 1 = unchanged, &gt;1 more saturated, &lt;1 desaturated.</param>
    /// <param name="lumaWeights">Luminance weights; defaults to Rec.709.</param>
    public Image Saturate(float boost, (float R, float G, float B)? lumaWeights = null)
    {
        if (ChannelCount < 3)
        {
            return new Image(CopyChannelData(), BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
        }

        var (wr, wg, wb) = lumaWeights ?? DefaultLumaWeights;
        var dst = CreateChannelData(ChannelCount, Height, Width);
        var r = GetChannelSpan(0);
        var g = GetChannelSpan(1);
        var b = GetChannelSpan(2);
        var rDst = MemoryMarshal.CreateSpan(ref dst[0][0, 0], dst[0].Length);
        var gDst = MemoryMarshal.CreateSpan(ref dst[1][0, 0], dst[1].Length);
        var bDst = MemoryMarshal.CreateSpan(ref dst[2][0, 0], dst[2].Length);
        for (var i = 0; i < rDst.Length; i++)
        {
            var l = wr * r[i] + wg * g[i] + wb * b[i];
            rDst[i] = Clamp01(l + (r[i] - l) * boost);
            gDst[i] = Clamp01(l + (g[i] - l) * boost);
            bDst[i] = Clamp01(l + (b[i] - l) * boost);
        }
        for (var c = 3; c < ChannelCount; c++)
        {
            GetChannelSpan(c).CopyTo(MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length));
        }
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Contrast boost via the shared <see cref="ApplyBoost"/> S-curve (the same curve the stretch pipeline
    /// uses), applied per channel. Whole-image and unmasked -- compose with <see cref="LuminanceRangeMask"/>
    /// + <see cref="BlendThroughMask"/> for a masked contrast boost that leaves the background alone (the
    /// Affinity "masked contrast boost" macro). Expects stretched values in [0, 1].
    /// </summary>
    /// <param name="boost">Boost amount (0 = off, typical 0.25-1.5).</param>
    /// <param name="backgroundLevel">Symmetry point of the curve (post-stretch background). Null derives it
    /// from <see cref="EstimateBackgroundPeak"/>.</param>
    public Image ContrastBoost(float boost, float? backgroundLevel = null)
    {
        var bg = backgroundLevel ?? EstimateBackgroundPeak();
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var src = GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = ApplyBoost(src[i], boost, bg);
            }
        }
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
    }

    /// <summary>
    /// The composed "finishing boost" over the mask primitives: <see cref="Saturate"/> and/or
    /// <see cref="ContrastBoost"/> applied through one shared <see cref="LuminanceRangeMask"/>, so
    /// the background and star cores stay untouched -- the Affinity masked-contrast-boost +
    /// saturation macro, automated. Expects stretched display-referred values in [0, 1]: on a
    /// LINEAR master the luminance mask degenerates to ~0 everywhere (background at ~0, star
    /// cores rolled off, nebulosity a few percent of peak), so callers apply this AFTER the
    /// stretch, never before. Returns the same instance when <paramref name="options"/> is a
    /// no-op (no allocation).
    /// </summary>
    /// <param name="options">Saturation multiplier + contrast boost amount; see
    /// <see cref="MaskedBoostOptions"/>.</param>
    public Image MaskedBoost(MaskedBoostOptions options)
    {
        if (options.IsNoOp)
        {
            return this;
        }

        var mask = LuminanceRangeMask();
        var processed = this;
        if (options.Saturation != 1f)
        {
            processed = Saturate(options.Saturation);
        }
        if (options.ContrastBoost != 0f)
        {
            var boosted = processed.ContrastBoost(options.ContrastBoost);
            if (!ReferenceEquals(processed, this))
            {
                processed.Release();
            }
            processed = boosted;
        }

        var result = BlendThroughMask(processed, mask);
        if (!ReferenceEquals(processed, this))
        {
            processed.Release();
        }
        mask.Release();
        return result;
    }

    /// <summary>
    /// Per-pixel complement <c>1 - v</c>, per channel. The mask-inversion primitive: an inverted
    /// <see cref="LuminanceRangeMask"/> selects the background instead of the signal (e.g. for a
    /// background-only smoothing pass through <see cref="BlendThroughMask"/>). Expects unit-range
    /// [0, 1] input (a mask or stretched data); values outside come out outside symmetrically.
    /// NaN propagates.
    /// </summary>
    public Image Invert()
    {
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var src = GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = 1f - src[i];
            }
        }
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Hard threshold, per channel: <c>v &gt;= threshold ? 1 : 0</c>. Turns a soft mask (or a
    /// stretched image) into a binary selection; follow with <see cref="GaussianBlur"/> to
    /// feather the hard edge back into a usable blend mask (the classic binarize-then-feather
    /// mask recipe). NaN maps to 0 (not selected).
    /// </summary>
    /// <param name="threshold">Selection cut-off in [0, 1].</param>
    public Image Binarize(float threshold)
    {
        var dst = CreateChannelData(ChannelCount, Height, Width);
        for (var c = 0; c < ChannelCount; c++)
        {
            var src = GetChannelSpan(c);
            var output = MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length);
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = src[i] >= threshold ? 1f : 0f;
            }
        }
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Per-channel separable Gaussian blur (edge-clamped). On a mask this IS feathering --
    /// soften a hard selection edge (e.g. after <see cref="Binarize"/>) so the blend through
    /// <see cref="BlendThroughMask"/> has no visible seam; the same kernel
    /// <see cref="LuminanceRangeMask"/> applies internally via its <c>blurSigma</c> parameter.
    /// Returns the same instance (no allocation) when <paramref name="sigma"/> is &lt;= 0.
    /// </summary>
    /// <param name="sigma">Gaussian sigma in pixels. Kernel radius is <c>ceil(3 * sigma)</c>.</param>
    public Image GaussianBlur(float sigma)
    {
        if (sigma <= 0f)
        {
            return this;
        }

        var w = Width;
        var h = Height;
        var dst = CreateChannelData(ChannelCount, h, w);
        var flat = new float[w * h];
        for (var c = 0; c < ChannelCount; c++)
        {
            GetChannelSpan(c).CopyTo(flat);
            var blurred = SeparableGaussianBlur(flat, w, h, sigma);
            MemoryMarshal.CreateSpan(ref blurred[0], blurred.Length)
                .CopyTo(MemoryMarshal.CreateSpan(ref dst[c][0, 0], dst[c].Length));
        }
        return new Image(dst, BitDepth.Float32, MaxValue, MinValue, pedestal, imageMeta);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>
    /// Separable Gaussian blur on a row-major <c>float[]</c> (edge-clamped both axes). A plain flat-raster
    /// blur -- distinct from <c>MilkyWayTextureBaker.GaussianBlur</c>, which is spherical (RA-wrap,
    /// dec-scaled sigma) and not applicable here.
    /// </summary>
    private static float[] SeparableGaussianBlur(float[] src, int w, int h, float sigma)
    {
        var radius = Math.Max(1, (int)MathF.Ceiling(sigma * 3f));
        var kernel = new float[radius * 2 + 1];
        var twoSigmaSq = 2f * sigma * sigma;
        var norm = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var k = MathF.Exp(-i * i / twoSigmaSq);
            kernel[i + radius] = k;
            norm += k;
        }
        var inv = 1f / norm;
        for (var i = 0; i < kernel.Length; i++) kernel[i] *= inv;

        // Horizontal pass -> tmp.
        var tmp = new float[src.Length];
        for (var y = 0; y < h; y++)
        {
            var rowBase = y * w;
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var t = -radius; t <= radius; t++)
                {
                    var sx = x + t;
                    if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
                    sum += src[rowBase + sx] * kernel[t + radius];
                }
                tmp[rowBase + x] = sum;
            }
        }

        // Vertical pass -> dst.
        var dst = new float[src.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var t = -radius; t <= radius; t++)
                {
                    var sy = y + t;
                    if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
                    sum += tmp[sy * w + x] * kernel[t + radius];
                }
                dst[y * w + x] = sum;
            }
        }
        return dst;
    }
}

/// <summary>
/// Knobs for <see cref="Image.MaskedBoost"/> -- the masked (background- and star-core-protected)
/// display finishing boost. Defaults are the identity so an all-default instance is a no-op;
/// consumers should treat a null options reference the same way. Operates on STRETCHED [0, 1]
/// data only (see <see cref="Image.MaskedBoost"/> for why linear input degenerates).
/// </summary>
/// <param name="Saturation">Saturation multiplier fed to <see cref="Image.Saturate"/>.
/// 1 = off; typical 1.3-2.0 for a finished deep-sky render. Ignored on mono images.</param>
/// <param name="ContrastBoost">S-curve boost amount fed to <see cref="Image.ContrastBoost"/>
/// (the stretch pipeline's <c>ApplyBoost</c> curve, pivoting at the estimated background
/// level). 0 = off; typical 0.25-1.5.</param>
public sealed record MaskedBoostOptions(float Saturation = 1f, float ContrastBoost = 0f)
{
    /// <summary>True when every knob is at its identity value -- callers skip the pass entirely.</summary>
    public bool IsNoOp => Saturation == 1f && ContrastBoost == 0f;
}
