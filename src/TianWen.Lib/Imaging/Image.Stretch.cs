using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Computes luminance stretch statistics (pedestal, median, MAD) from a color image.
    /// Builds a Rec. 709 luminance channel and computes histogram statistics on it.
    /// Falls back to channel 0 stats for mono images. Optionally debayers Bayer images first.
    /// </summary>
    public async Task<(float Pedestal, float Median, float MAD)> GetLumaStretchStatsAsync(DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
            return await debayered.GetLumaStretchStatsAsync(DebayerAlgorithm.None, cancellationToken);
        }

        if (ChannelCount < 3)
        {
            return GetPedestralMedianAndMADScaledToUnit(0);
        }

        return await Task.Run(() =>
        {
            var (_, width, height) = Shape;
            var needsNorm = MaxValue > 1.0f + float.Epsilon;
            var normFactor = 1.0f / MaxValue;

            var lumaChannel = new float[height, width];
            var lumaMin = float.MaxValue;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var r = data[0][y, x];
                    var g = data[1][y, x];
                    var b = data[2][y, x];
                    if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
                    {
                        lumaChannel[y, x] = float.NaN;
                    }
                    else
                    {
                        if (needsNorm) { r *= normFactor; g *= normFactor; b *= normFactor; }
                        var luma = LumaR * r + LumaG * g + LumaB * b;
                        lumaChannel[y, x] = luma;
                        if (luma < lumaMin) lumaMin = luma;
                    }
                }
            }

            if (lumaMin == float.MaxValue) lumaMin = 0f;

            var lumaImage = new Image([lumaChannel], BitDepth.Float32, 1.0f, lumaMin, 0f,
                imageMeta with { SensorType = SensorType.Monochrome });
            return lumaImage.GetPedestralMedianAndMADScaledToUnit(0);
        }, cancellationToken);
    }

    // Rec. 709 luminance weights
    private const float LumaR = 0.2126f;
    private const float LumaG = 0.7152f;
    private const float LumaB = 0.0722f;

    /// <summary>
    /// Computes the stretch parameters (shadows, midtones, highlights, rescale) from channel statistics.
    /// These can be passed as GPU shader uniforms to perform the stretch on the GPU.
    /// </summary>
    public static (double Shadows, double Midtones, double Highlights, double Rescale) ComputeStretchParameters(
        float median, float mad, double stretchFactor, double shadowsClipping)
    {
        double shadows, midtones, highlights, rescale;

        if (median > 0.5)
        {
            shadows = 0f;
            highlights = median - shadowsClipping * mad * MAD_TO_SD;
            rescale = 1.0 / (highlights - 0);
            midtones = MidtonesTransferFunction(stretchFactor, 1f - (highlights - median) * rescale);
        }
        else
        {
            shadows = median + shadowsClipping * mad * MAD_TO_SD;
            rescale = 1.0 / (1.0 - shadows);
            midtones = MidtonesTransferFunction(stretchFactor, (median - shadows) * rescale);
            highlights = 1;
        }

        return (shadows, midtones, highlights, rescale);
    }

    /// <summary>
    /// Midtones Transfer Function (PixInsight STF formula).
    /// Maps [0,1] → [0,1] with midtone balance controlling the curve shape.
    /// This is the single source of truth — the GLSL shader reimplements the same formula.
    /// </summary>
    public static double MidtonesTransferFunction(double midToneBalance, double value)
    {
        var clamped = Math.Clamp(value, 0, 1d);
        if (value == clamped)
        {
            return (midToneBalance - 1) * value / Math.FusedMultiplyAdd(Math.FusedMultiplyAdd(2, midToneBalance, -1), value, - midToneBalance);
        }
        else
        {
            return clamped;
        }
    }

    /// <summary>
    /// Derives the PixInsight midtones balance β such that
    /// <see cref="MidtonesTransferFunction"/>(β, <paramref name="origMedian"/>) ==
    /// <paramref name="targetMedian"/>.
    /// </summary>
    /// <remarks>
    /// The bare PixInsight STF formula <c>MTF(m, x) = (m - 1)·x / ((2m - 1)·x - m)</c>
    /// always lands <c>MTF(m, m) = 0.5</c>. When you want a stretch that lands an
    /// empirical median at a chosen output median (e.g. 0.25 to match
    /// SetiAstroSuite Pro's AI4 NAFNet pre-stretch), translate the parameters via
    /// this helper and then call <see cref="MidtonesTransferFunction"/> directly:
    /// <c>var β = MidtonesBalanceFor(origMed, 0.25); MTF(β, origMed) == 0.25</c>.
    /// Used by <see cref="MtfStretch"/> / <see cref="MtfUnstretch"/>; exposed
    /// publicly so callers can do scalar previews without instantiating an
    /// <see cref="Image"/>.
    /// </remarks>
    public static double MidtonesBalanceFor(double origMedian, double targetMedian)
        => origMedian * (targetMedian - 1.0)
           / (targetMedian * (2.0 * origMedian - 1.0) - origMedian);

    /// <summary>
    /// Adaptive per-channel MTF stretch: subtract each channel's minimum
    /// (auto-pedestal) then apply <see cref="MidtonesTransferFunction"/> with the
    /// midtones balance that maps the channel's shifted median to
    /// <paramref name="targetMedian"/>. Returns a new image with the stretched
    /// data in [0, 1]; the source is not mutated.
    /// </summary>
    /// <remarks>
    /// This is the *input-normalisation* stretch used by ML pipelines that
    /// expect a specific histogram shape (e.g. SetiAstroSuite Pro's AI4 NAFNet
    /// models expect <paramref name="targetMedian"/> = 0.25). It is intentionally
    /// the bare-minimum stretch -- min subtract + MTF, nothing else. Compare
    /// with <see cref="StretchChannelCpu"/>, which is the full *display* pipeline
    /// (pedestal + background-neutralisation + WB + shadows + MTF + rescale).
    /// <para>The forward stretch is round-trippable via <see cref="MtfUnstretch"/>
    /// using the <paramref name="origMin"/> + <paramref name="balances"/> tuple
    /// returned here. NaN inputs are preserved verbatim.</para>
    /// </remarks>
    /// <param name="targetMedian">Output median each channel's median is mapped to.</param>
    /// <param name="origMin">Out: per-channel minimum that was subtracted before MTF.</param>
    /// <param name="balances">Out: per-channel midtones balance β used in the MTF.</param>
    public Image MtfStretch(double targetMedian, out float[] origMin, out double[] balances)
    {
        var (channels, width, height) = Shape;
        var pixelCount = width * height;
        origMin = new float[channels];
        balances = new double[channels];
        var newData = CreateChannelData(channels, height, width);

        var scratch = ArrayPool<float>.Shared.Rent(pixelCount);
        try
        {
            for (var c = 0; c < channels; c++)
            {
                var src = GetChannelSpan(c);
                var dst = MemoryMarshal.CreateSpan(ref newData[c][0, 0], pixelCount);

                // Pass 1: find min (skip NaN).
                var min = float.PositiveInfinity;
                for (var i = 0; i < pixelCount; i++)
                {
                    var v = src[i];
                    if (!float.IsNaN(v) && v < min) min = v;
                }
                if (float.IsPositiveInfinity(min)) min = 0f;
                origMin[c] = min;

                // Pass 2: collect non-NaN (src - min) into scratch, then median.
                var count = 0;
                for (var i = 0; i < pixelCount; i++)
                {
                    var v = src[i];
                    if (!float.IsNaN(v)) scratch[count++] = v - min;
                }
                var med = count > 0 ? MedianFast(scratch.AsSpan(0, count)) : 0f;

                // Derive β. When the channel is all NaN or the shifted median is 0
                // (flat plane), β is undefined; fall back to 0.5 which makes MTF
                // the identity in [0, 1], so the stretch becomes a no-op on this
                // channel (and Unstretch with 1 - β = 0.5 stays identity too).
                var beta = med > 0f
                    ? MidtonesBalanceFor(med, targetMedian)
                    : 0.5;
                balances[c] = beta;

                // Pass 3: subtract min + MTF(β, x), NaN-preserving.
                for (var i = 0; i < pixelCount; i++)
                {
                    var v = src[i];
                    if (float.IsNaN(v))
                    {
                        dst[i] = float.NaN;
                    }
                    else
                    {
                        var shifted = v - min;
                        if (shifted < 0f) shifted = 0f;  // float wobble guard
                        dst[i] = (float)MidtonesTransferFunction(beta, shifted);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }

        return new Image(newData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Inverse of <see cref="MtfStretch"/>. Applies
    /// <see cref="MidtonesTransferFunction"/> with <c>1 - β</c> (which is the
    /// algebraic inverse of MTF with balance β) then adds the per-channel
    /// minimum back. Returns a new image; the source is not mutated.
    /// </summary>
    /// <remarks>
    /// Identity: <c>MTF⁻¹(β, y) == MTF(1 - β, y)</c>. This is why we don't ship a
    /// separate InverseMtf -- the existing
    /// <see cref="MidtonesTransferFunction"/> primitive handles both directions
    /// when you pass it <c>1 - β</c>. NaN inputs are preserved verbatim. The
    /// returned image's <see cref="MaxValue"/> is the empirically observed peak
    /// (re-computed during the pass), since MTF⁻¹ can produce values outside
    /// <c>[0, 1]</c> if the network output excursions exceed the training range.
    /// </remarks>
    public Image MtfUnstretch(ReadOnlySpan<float> origMin, ReadOnlySpan<double> balances)
    {
        var (channels, width, height) = Shape;
        if (origMin.Length != channels)
            throw new ArgumentException($"origMin length ({origMin.Length}) must match channel count ({channels})", nameof(origMin));
        if (balances.Length != channels)
            throw new ArgumentException($"balances length ({balances.Length}) must match channel count ({channels})", nameof(balances));

        var pixelCount = width * height;
        var newData = CreateChannelData(channels, height, width);
        var actualMax = float.NegativeInfinity;

        for (var c = 0; c < channels; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref newData[c][0, 0], pixelCount);
            var inverseBeta = 1.0 - balances[c];
            var min = origMin[c];

            for (var i = 0; i < pixelCount; i++)
            {
                var v = src[i];
                if (float.IsNaN(v))
                {
                    dst[i] = float.NaN;
                }
                else
                {
                    // Inverse MTF identity: MTF⁻¹(β, y) == MTF(1 - β, y).
                    var unstretched = (float)MidtonesTransferFunction(inverseBeta, v) + min;
                    dst[i] = unstretched;
                    if (unstretched > actualMax) actualMax = unstretched;
                }
            }
        }

        if (float.IsNegativeInfinity(actualMax)) actualMax = 1.0f;
        return new Image(newData, BitDepth.Float32, actualMax, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Applies the stretch pipeline to a single value: normalize, subtract pedestal, rescale, MTF.
    /// Matches both the CPU stretch loop and the GLSL <c>stretchChannel()</c> function exactly.
    /// </summary>
    public static float StretchValue(float rawValue, float normFactor, float pedestal, double shadows, double midtones, double rescale)
    {
        var norm = rawValue * normFactor - pedestal;
        var rescaled = (norm - shadows) * rescale;
        return (float)MidtonesTransferFunction(midtones, rescaled);
    }

    /// <summary>
    /// Applies a curves boost to a stretched pixel value. Lifts faint detail above the
    /// background while preserving blacks and highlights.
    /// Matches the GLSL <c>applyCurve</c> in the Vulkan fragment shader exactly.
    /// </summary>
    /// <param name="v">Stretched pixel value in [0, 1].</param>
    /// <param name="boost">Boost amount (0 = off, typical range 0.25–1.5).</param>
    /// <param name="backgroundLevel">Post-stretch background level (symmetry point of the curve).</param>
    public static float ApplyBoost(float v, float boost, float backgroundLevel)
    {
        const float hp = 0.85f;
        if (v <= 0f || v >= 1f || backgroundLevel <= 0f || backgroundLevel >= hp)
        {
            return v;
        }

        var sp = MathF.Min(backgroundLevel * (1f + 0.1f * boost), hp - 0.01f);
        if (v <= sp)
        {
            var t = v / sp;
            var darkPower = 1f + boost * 3f;
            return sp * MathF.Pow(t, darkPower);
        }
        else if (v < hp)
        {
            var t = (v - sp) / (hp - sp);
            return sp + (hp - sp) * MathF.Pow(t, 1f / (1f + boost));
        }

        return v;
    }

    /// <summary>
    /// Applies a LUT-based curve to a stretched pixel value via linear interpolation.
    /// Use <see cref="FritschCarlsonSpline"/> to build the LUT.
    /// </summary>
    public static float ApplyCurveLut(float v, ReadOnlySpan<float> lut)
    {
        var idx = v * (lut.Length - 1);
        var i = (int)idx;
        var frac = idx - i;
        if (i >= lut.Length - 1) return lut[^1];
        if (i < 0) return lut[0];
        return lut[i] * (1f - frac) + lut[i + 1] * frac;
    }

    /// <summary>
    /// CPU mirror of the GLSL <c>stretchChannel()</c> function — single source of truth for the
    /// per-channel stretch pipeline (pedestal subtract -> bg neutralization -> WB -> MTF). Both
    /// the Vulkan fragment shader and CPU consumers (<c>ConsoleImageRenderer</c>, headless
    /// renderers, tests) must produce the same result for the same <see cref="StretchUniforms"/>.
    /// </summary>
    public static float StretchChannelCpu(float raw, int channel, in StretchUniforms u)
    {
        var ped = PickChannel(u.Pedestal, channel);
        var bn = PickChannel(u.BackgroundNeutralization, channel);
        var wb = PickChannel(u.WhiteBalance, channel);
        var sh = PickChannel(u.Shadows, channel);
        var mt = PickChannel(u.Midtones, channel);
        var re = PickChannel(u.Rescale, channel);

        var norm = raw * u.NormFactor - ped;
        // Background neutralization: out = norm * g + (1 - g)
        norm = norm * bn + (1f - bn);
        norm = MathF.Max(norm * wb, 0f);
        return StretchValue(norm, 1f, 0f, sh, mt, re);
    }

    /// <summary>
    /// CPU mirror of the GLSL Luma stretch path (<c>stretchMode == 3</c>). Per-channel pedestal
    /// subtract + WB -> weighted luma -> MTF -> scale all channels by Y'/Y. Matches the GLSL
    /// exactly, including the omission of background neutralization (which is only applied in
    /// the per-channel Linked/Unlinked path). Weights come from <see cref="StretchUniforms.LumaWeights"/>
    /// so Rec.709 / Rec.601 / Rec.2020 selections flow through without code changes.
    /// </summary>
    public static (float R, float G, float B) StretchLumaPixelCpu(float r, float g, float b, in StretchUniforms u)
    {
        var nr = r * u.NormFactor;
        var ng = g * u.NormFactor;
        var nb = b * u.NormFactor;
        var prr = MathF.Max((nr - u.Pedestal.R) * u.WhiteBalance.R, 0f);
        var prg = MathF.Max((ng - u.Pedestal.G) * u.WhiteBalance.G, 0f);
        var prb = MathF.Max((nb - u.Pedestal.B) * u.WhiteBalance.B, 0f);
        var yNorm = u.LumaWeights.R * prr + u.LumaWeights.G * prg + u.LumaWeights.B * prb;
        var rescaled = (yNorm - u.LumaStretch.Shadow) * u.LumaStretch.Rescale;
        var yPrime = (float)MidtonesTransferFunction(u.LumaStretch.Midtones, rescaled);
        var scale = yNorm > 1e-7f ? yPrime / yNorm : 0f;
        var maxCh = MathF.Max(prr, MathF.Max(prg, prb));
        if (scale > 0f && maxCh > 1e-7f) scale = MathF.Min(scale, 1f / maxCh);
        return (
            Math.Clamp(prr * scale, 0f, 1f),
            Math.Clamp(prg * scale, 0f, 1f),
            Math.Clamp(prb * scale, 0f, 1f));
    }

    /// <summary>
    /// CPU mirror of the GLSL <c>applyHdr()</c> soft-knee compression. Above <paramref name="knee"/>,
    /// values are compressed via <c>knee + range * t / (1 + amount * t)</c>.
    /// </summary>
    public static float ApplyHdr(float v, float amount, float knee)
    {
        if (v <= knee) return v;
        var range = 1f - knee;
        var t = (v - knee) / range;
        return knee + range * t / (1f + amount * t);
    }

    private static float PickChannel((float R, float G, float B) v, int ch) => ch switch
    {
        0 => v.R,
        1 => v.G,
        _ => v.B,
    };

    /// <summary>
    /// Predicts the post-stretch output scale (1 / max) so a caller can stamp
    /// <see cref="StretchUniforms.NormalizeScale"/> before rendering. Walks each channel's
    /// histogram from the top to find the brightest raw value, then pushes it through the
    /// full chain (stretch + curves + HDR) — identical to the per-pixel path in
    /// <see cref="RenderStretchedRgba"/>. The stretch + curves + HDR stages are all monotonic,
    /// so the brightest raw value yields the brightest post-stretch value. Returns 1.0 (no-op)
    /// when the predicted max is &lt;= ~1, since further upscaling can only saturate.
    /// </summary>
    /// <remarks>
    /// Approximation: histogram bins are quantised, so the predicted max is the top bin's
    /// centre value rather than the true rendered max. For practical astro inputs this lands
    /// within ~1% of <c>np.max(out)</c> and avoids the GPU reduction-pass needed for exact
    /// parity with SetiAstro's <c>out / out.max()</c>. The CPU and GPU consume the same
    /// uniform so they stay in lockstep regardless.
    /// </remarks>
    public static float PredictPostStretchMaxScale(
        in StretchUniforms u,
        ReadOnlySpan<ImageHistogram> histograms,
        int curvesMode = 0,
        ReadOnlySpan<float> curveLut = default,
        float curvesBoost = 0f,
        float curvesMidpoint = 0.25f,
        float hdrAmount = 0f,
        float hdrKnee = 0.8f)
    {
        if (u.Mode is StretchMode.None || histograms.IsEmpty)
        {
            return 1f;
        }

        var hasLut = curvesMode == 1 && !curveLut.IsEmpty;
        var maxOverall = 0f;

        if (u.Mode is StretchMode.Luma && histograms.Length >= 3)
        {
            // Luma path: take the brightest sample of *each* channel independently and run
            // the full luma kernel on the (r,g,b) triple. This is conservative — the same
            // pixel won't usually peak in all three channels — but matches the monotonicity
            // assumption used elsewhere and avoids needing the joint per-pixel histogram.
            var rRaw = TopBinCenter(histograms[0]);
            var gRaw = TopBinCenter(histograms[1]);
            var bRaw = TopBinCenter(histograms[2]);
            var (rL, gL, bL) = StretchLumaPixelCpu(rRaw, gRaw, bRaw, u);

            // Apply LumaBlend mix with the per-channel linked output (mirrors RenderStretchedRgba)
            if (u.LumaBlend < 1f)
            {
                var rLnk = StretchChannelCpu(rRaw, 0, u);
                var gLnk = StretchChannelCpu(gRaw, 1, u);
                var bLnk = StretchChannelCpu(bRaw, 2, u);
                var b = Math.Clamp(u.LumaBlend, 0f, 1f);
                var omb = 1f - b;
                rL = omb * rLnk + b * rL;
                gL = omb * gLnk + b * gL;
                bL = omb * bLnk + b * bL;
            }

            maxOverall = MathF.Max(rL, MathF.Max(gL, bL));
        }
        else
        {
            for (var c = 0; c < 3 && c < histograms.Length; c++)
            {
                var rawTop = TopBinCenter(histograms[c]);
                var stretched = StretchChannelCpu(rawTop, c, u);
                if (stretched > maxOverall) maxOverall = stretched;
            }
        }

        // Apply curves + HDR to the predicted peak. Both stages are monotonic in [0,1].
        if (hasLut) maxOverall = ApplyCurveLut(maxOverall, curveLut);
        else if (curvesBoost > 0f) maxOverall = ApplyBoost(maxOverall, curvesBoost, curvesMidpoint);
        if (hdrAmount > 0f) maxOverall = ApplyHdr(maxOverall, hdrAmount, hdrKnee);

        // Only return a > 1 scale when the predicted max actually leaves headroom.
        return maxOverall > 1e-6f && maxOverall < 1f ? 1f / maxOverall : 1f;

        static float TopBinCenter(ImageHistogram hist)
        {
            var bins = hist.Histogram;
            var binMax = (float)(hist.RescaledMaxValue ?? 65535d);
            var invBinMax = 1f / binMax;
            for (var i = bins.Length - 1; i >= 0; i--)
            {
                if (bins[i] > 0) return (i + 0.5f) * invBinMax;
            }
            return 0f;
        }
    }

    /// <summary>
    /// CPU mirror of the full GLSL fragment shader (image path) — renders this image into an RGBA
    /// buffer at native resolution. Used by tests and headless renderers that cannot use the GPU.
    /// The pipeline order matches GLSL exactly: stretch (per-channel or luma) -> curves
    /// (LUT or boost) -> HDR -> clamp to [0, 1] -> byte. For Bayer images, debayer first via
    /// <see cref="DebayerAsync"/>.
    /// </summary>
    /// <param name="u">Stretch parameters from <c>AstroImageDocument.ComputeStretchUniforms</c>.</param>
    /// <param name="rgba32">Output buffer, length = Width * Height * 4 (RGBA bytes).</param>
    /// <param name="curvesBoost">Power-law boost amount; ignored when <paramref name="curvesMode"/> is 1.</param>
    /// <param name="curvesMode">0 = power-law boost, 1 = Fritsch-Carlson LUT.</param>
    /// <param name="curveLut">33-entry LUT used when <paramref name="curvesMode"/> is 1.</param>
    /// <param name="curvesMidpoint">Background midpoint for power-law boost.</param>
    /// <param name="hdrAmount">HDR knee-compression amount; 0 = off.</param>
    /// <param name="hdrKnee">HDR knee point.</param>
    public void RenderStretchedRgba(
        in StretchUniforms u,
        Span<byte> rgba32,
        float curvesBoost = 0f,
        int curvesMode = 0,
        ReadOnlySpan<float> curveLut = default,
        float curvesMidpoint = 0.25f,
        float hdrAmount = 0f,
        float hdrKnee = 0.8f)
    {
        var (channelCount, width, height) = Shape;
        var pixelCount = width * height;
        if (rgba32.Length < pixelCount * 4)
            throw new ArgumentException($"rgba32 length ({rgba32.Length}) too small for {width}x{height} ({pixelCount * 4} bytes needed)", nameof(rgba32));

        var isColor = channelCount >= 3;
        var ch0 = GetChannelSpan(0);
        var ch1 = isColor ? GetChannelSpan(1) : default;
        var ch2 = isColor ? GetChannelSpan(2) : default;
        var hasLut = curvesMode == 1 && !curveLut.IsEmpty;

        for (var i = 0; i < pixelCount; i++)
        {
            float rOut, gOut, bOut;

            if (isColor)
            {
                var rRaw = ch0[i];
                var gRaw = ch1[i];
                var bRaw = ch2[i];

                if (u.Mode is StretchMode.Luma)
                {
                    var (rL, gL, bL) = StretchLumaPixelCpu(rRaw, gRaw, bRaw, u);
                    // LumaBlend == 1: pure luma (status quo). < 1: blend with the per-channel
                    // linked path computed here on the fly, matching the GLSL Luma branch.
                    if (u.LumaBlend < 1f)
                    {
                        var rLnk = StretchChannelCpu(rRaw, 0, u);
                        var gLnk = StretchChannelCpu(gRaw, 1, u);
                        var bLnk = StretchChannelCpu(bRaw, 2, u);
                        var b = Math.Clamp(u.LumaBlend, 0f, 1f);
                        var omb = 1f - b;
                        rOut = omb * rLnk + b * rL;
                        gOut = omb * gLnk + b * gL;
                        bOut = omb * bLnk + b * bL;
                    }
                    else
                    {
                        rOut = rL; gOut = gL; bOut = bL;
                    }
                }
                else if (u.Mode is StretchMode.None)
                {
                    // GLSL Mode==None passes texture sample through unmodified
                    rOut = rRaw; gOut = gRaw; bOut = bRaw;
                }
                else
                {
                    rOut = StretchChannelCpu(rRaw, 0, u);
                    gOut = StretchChannelCpu(gRaw, 1, u);
                    bOut = StretchChannelCpu(bRaw, 2, u);
                }
            }
            else
            {
                var raw = ch0[i];
                rOut = u.Mode is StretchMode.None ? raw : StretchChannelCpu(raw, 0, u);
                gOut = bOut = rOut;
            }

            if (hasLut)
            {
                rOut = ApplyCurveLut(rOut, curveLut);
                gOut = ApplyCurveLut(gOut, curveLut);
                bOut = ApplyCurveLut(bOut, curveLut);
            }
            else if (curvesBoost > 0f)
            {
                rOut = ApplyBoost(rOut, curvesBoost, curvesMidpoint);
                gOut = ApplyBoost(gOut, curvesBoost, curvesMidpoint);
                bOut = ApplyBoost(bOut, curvesBoost, curvesMidpoint);
            }

            if (hdrAmount > 0f)
            {
                rOut = ApplyHdr(rOut, hdrAmount, hdrKnee);
                gOut = ApplyHdr(gOut, hdrAmount, hdrKnee);
                bOut = ApplyHdr(bOut, hdrAmount, hdrKnee);
            }

            // Optional post-stretch normalize: rescale so the predicted brightest pixel
            // (computed by PredictPostStretchMaxScale) lands at 1.0. Default scale=1 = no-op.
            if (u.NormalizeScale != 1f)
            {
                rOut *= u.NormalizeScale;
                gOut *= u.NormalizeScale;
                bOut *= u.NormalizeScale;
            }

            var o = i * 4;
            rgba32[o] = (byte)(Math.Clamp(rOut, 0f, 1f) * 255f);
            rgba32[o + 1] = (byte)(Math.Clamp(gOut, 0f, 1f) * 255f);
            rgba32[o + 2] = (byte)(Math.Clamp(bOut, 0f, 1f) * 255f);
            rgba32[o + 3] = 255;
        }
    }

    /// <summary>
    /// Iteratively adjusts <c>stretchFactor</c> until the post-stretch median of the histogram
    /// converges to <paramref name="targetMedian"/>. Uses bisection over the histogram bins
    /// — each bin's midpoint value is fed through <see cref="StretchValue"/> with the current
    /// trial parameters, and the cumulative count walk finds the post-stretch median.
    /// </summary>
    /// <param name="histogram">Pre-computed histogram for the channel or luma.</param>
    /// <param name="pedestal">Pedestal value as returned by <c>GetPedestralMedianAndMADScaledToUnit</c>.</param>
    /// <param name="median">Median value as returned by <c>GetPedestralMedianAndMADScaledToUnit</c>.</param>
    /// <param name="mad">MAD value as returned by <c>GetPedestralMedianAndMADScaledToUnit</c>.</param>
    /// <param name="initialFactor">User-requested stretch factor (clamped to [0.001, 0.5]).</param>
    /// <param name="targetMedian">Desired post-stretch median (default 0.25, PixInsight STF convention).</param>
    /// <param name="maxIterations">Maximum bisection iterations (default 20).</param>
    /// <param name="tolerance">Convergence tolerance on the post-stretch median (default 0.005).</param>
    /// <param name="whiteBalance">If non-1, the convergence operates in WB-scaled space:
    /// median/mad/binNorm are all multiplied by this factor before deriving shadows and
    /// computing the post-stretch median. Use the per-channel WB or, for luma convergence,
    /// the Rec.709-weighted WB scalar. Without this, convergence finds a factor X assuming
    /// pre-WB shadow positions, but the per-channel rendering then applies WB-scaled stats —
    /// each channel's actual post-stretch median ends up offset from the target.</param>
    /// <returns>The converged stretch factor and corresponding midtones value.</returns>
    public static (double ConvergedFactor, double ConvergedMidtones) ConvergeStretchFactor(
        ImageHistogram histogram,
        float pedestal, float median, float mad,
        double initialFactor,
        double shadowsClipping = -3d,
        double targetMedian = 0.25,
        int maxIterations = 20,
        double tolerance = 0.005,
        float whiteBalance = 1f)
    {
        var bins = histogram.Histogram;
        var binMax = (float)(histogram.RescaledMaxValue ?? 65535f);
        var invBinMax = 1f / binMax;
        float totalF = histogram.Total;
        var halfTotal = totalF * 0.5f;

        // Apply WB to convergence stats so shadow/midtones/rescale match the post-WB rendering.
        var wbMedian = median * whiteBalance;
        var wbMad = mad * whiteBalance;

        // Bisection bounds for stretchFactor
        var lo = 0.001;
        var hi = 0.5;
        var factor = Math.Clamp(initialFactor, lo, hi);
        double midtones = 0;

        // Pre-compute normalized bin-centre values [0,1] in post-WB space.
        var binValues = new float[bins.Length];
        for (var i = 0; i < bins.Length; i++)
        {
            binValues[i] = (i + 0.5f) * invBinMax * whiteBalance;
        }

        for (var iter = 0; iter < maxIterations; iter++)
        {
            var (shadows, m, highlights, rescale) = ComputeStretchParameters(wbMedian, wbMad, factor, shadowsClipping);
            midtones = m;

            // Walk histogram bins, accumulate stretched counts to find post-stretch median
            var cumulative = 0f;
            var medianIdx = -1;
            for (var i = 0; i < bins.Length; i++)
            {
                cumulative += bins[i];
                if (cumulative >= halfTotal)
                {
                    medianIdx = i;
                    break;
                }
            }

            if (medianIdx < 0) break;

            // Compute the stretched value at the median bin
            var binNorm = binValues[medianIdx];
            var stretchedMedian = StretchValue(binNorm, /*normFactor*/1f, pedestal, shadows, m, rescale);

            if (Math.Abs(stretchedMedian - targetMedian) <= tolerance)
            {
                return (factor, midtones);
            }

            // Bisect. The post-stretch median is monotonically *increasing* in stretchFactor for
            // typical (median<0.5) astro images: at factor=0.001 the MTF midtones value collapses
            // to ~1 and output goes to 0; at factor=0.5 midtones=0.5 (identity) and output is the
            // rescaled value. So:
            //   too bright (stretchedMedian > target)  ->  factor too HIGH  ->  bisect lower (hi = factor)
            //   too dim   (stretchedMedian < target)   ->  factor too LOW   ->  bisect upper (lo = factor)
            if (stretchedMedian > targetMedian)
            {
                hi = factor;
            }
            else
            {
                lo = factor;
            }
            factor = (lo + hi) * 0.5;
        }

        return (factor, midtones);
    }
}
