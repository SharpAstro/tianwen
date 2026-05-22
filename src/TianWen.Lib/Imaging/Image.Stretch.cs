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
    /// Applies the PixInsight MTF formula with a <i>fixed</i> midtones balance
    /// to every pixel (no per-channel auto-targeting). The companion to
    /// <see cref="MtfStretch"/>: same underlying curve, but the curve is the
    /// SAME for every channel and every input median. Use this when the data's
    /// channel median doesn't represent the "interesting" tonal anchor --
    /// notably for stars-only plates after star removal, where the median is
    /// near zero (mostly background pixels with sparse bright peaks) and
    /// auto-targeting that median to e.g. 0.25 would lift the background by
    /// 250x and saturate every star.
    /// </summary>
    /// <param name="midtones">Midtones balance in (0, 1). 0.5 = identity;
    /// values &lt; 0.5 lift shadows (typical stretch); &gt; 0.5 darkens.</param>
    public Image FixedMidtonesStretch(double midtones)
    {
        var (channels, width, height) = Shape;
        var pixelCount = width * height;
        var newData = CreateChannelData(channels, height, width);
        for (var c = 0; c < channels; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref newData[c][0, 0], pixelCount);
            for (var i = 0; i < pixelCount; i++)
            {
                var v = src[i];
                dst[i] = float.IsNaN(v) ? float.NaN : (float)MidtonesTransferFunction(midtones, v);
            }
        }
        return new Image(newData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Frank Sackenheim's StarStretch curve from the SAS Pro / PixInsight
    /// StarStretch script: applies a single fixed-midtones MTF curve where
    /// <c>midtones = 1 / (3^amount + 1)</c>. Pure pass-through wrapper around
    /// <see cref="FixedMidtonesStretch"/> with the SAS Pro slider semantics
    /// baked in.
    /// </summary>
    /// <param name="amount">SAS Pro "amount" slider value. SAS Pro's UI default
    /// is 5.0 (factor = 243, midtones ≈ 0.004) -- aggressive lift designed
    /// for star plates extracted from already-stretched images. On linear
    /// stars-only plates (median near zero), values in the 1.5 - 3.0 range
    /// usually produce well-balanced stretches. Higher = stronger stretch.</param>
    public Image StarStretch(double amount)
    {
        var midtones = 1.0 / (Math.Pow(3.0, amount) + 1.0);
        return FixedMidtonesStretch(midtones);
    }

    /// <summary>
    /// Estimates the background histogram peak (mode) of channel 0 by
    /// bucketing pixel values into a coarse 256-bin histogram and returning
    /// the bin centre of the most-populated bin. For stretched astro frames
    /// most pixels ARE background, so the mode is a good proxy for "where
    /// the sky sits" -- matches what a colour picker in Photoshop / Affinity
    /// reads when sampling background regions.
    /// </summary>
    /// <remarks>
    /// Channel 0 is enough for a calibration step -- the channels are usually
    /// close after background neutralisation. NaN values are skipped.
    /// Returns 0 if the channel is empty / all-NaN.
    /// </remarks>
    public float EstimateBackgroundPeak()
    {
        const int Bins = 256;
        Span<int> hist = stackalloc int[Bins];
        var src = GetChannelSpan(0);
        for (var i = 0; i < src.Length; i++)
        {
            var v = src[i];
            if (float.IsNaN(v)) continue;
            var clamped = Math.Clamp(v, 0f, 1f);
            var idx = Math.Min((int)(clamped * Bins), Bins - 1);
            hist[idx]++;
        }
        var peakIdx = 0;
        var peakCount = 0;
        for (var i = 0; i < Bins; i++)
        {
            if (hist[i] > peakCount) { peakCount = hist[i]; peakIdx = i; }
        }
        return (peakIdx + 0.5f) / Bins;
    }

    /// <summary>
    /// Estimates the rising edge of the histogram -- the pixel value at which
    /// the histogram first lifts off the floor on the way up to the main peak
    /// (background mode). This is the "where the histogram starts lifting up
    /// towards the peak" point Paul (Polymath Astro) and other GHS practitioners
    /// recommend as the GHS symmetry point on linear-input frames: stretching
    /// pivots around the lift-off so the noise floor below isn't dragged up but
    /// everything above (including the background bulk) gets the full curve.
    /// </summary>
    /// <remarks>
    /// Uses a 4096-bin histogram (~0.00024 step) on channel 0 -- enough
    /// resolution to land within ~0.0005 of Paul's hand-picked SP=0.003 for
    /// linear-bg ~0.005 frames. Walks LEFT from the mode bin and stops at the
    /// first bin whose count falls below <paramref name="thresholdFraction"/>
    /// of the peak. NaN values are skipped. Returns 0 if the channel is empty
    /// / all-NaN.
    /// </remarks>
    /// <param name="thresholdFraction">Fraction of the peak count below which
    /// a bin is considered "off the floor". Default 0.05 (5% of peak) -- low
    /// enough to find the true lift-off, high enough to ignore single-bin
    /// noise tails. Range (0, 1).</param>
    public float EstimateRisingEdge(float thresholdFraction = 0.05f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thresholdFraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(thresholdFraction, 1.0f);

        const int Bins = 4096;
        Span<int> hist = stackalloc int[Bins];
        var src = GetChannelSpan(0);
        for (var i = 0; i < src.Length; i++)
        {
            var v = src[i];
            if (float.IsNaN(v)) continue;
            var clamped = Math.Clamp(v, 0f, 1f);
            var idx = Math.Min((int)(clamped * Bins), Bins - 1);
            hist[idx]++;
        }
        var peakIdx = 0;
        var peakCount = 0;
        for (var i = 0; i < Bins; i++)
        {
            if (hist[i] > peakCount) { peakCount = hist[i]; peakIdx = i; }
        }
        if (peakCount == 0) return 0f;

        var threshold = peakCount * thresholdFraction;
        var edgeIdx = peakIdx;
        while (edgeIdx > 0 && hist[edgeIdx] >= threshold) edgeIdx--;
        return (edgeIdx + 0.5f) / Bins;
    }

    /// <summary>
    /// Generalized Hyperbolic Stretch (Mike Cranfield's PixInsight script
    /// family, as ported in SAS Pro <c>ghs_dialog_pro.py</c>). Five-parameter
    /// tone curve with independent shadow / highlight protection points --
    /// addresses MTF's "single hyperbolic" limitation by exposing the
    /// protection regions as separate controls.
    /// </summary>
    /// <remarks>
    /// <para>The curve is constructed in a parametric (us, vp) form with
    /// us as the LUT index (uniform [0, 1]) and vp as the output at that
    /// index. The function passes through (0, 0), (0.5, SP), and (1, 1) by
    /// construction: input pixel value <c>x = us</c> maps to
    /// <c>output = vp(us)</c>. <see cref="BuildGhsLut"/> is exposed so the
    /// LUT can be inspected / tested separately from the per-pixel loop.</para>
    ///
    /// <para>LP / HP protection: linear blend between the raw GHS curve and
    /// identity in the respective region (below / above SP). <c>0</c> = no
    /// protection (raw GHS); <c>1</c> = full identity (no stretch in that
    /// region). The gamma tail (<paramref name="gamma"/>) is rarely needed
    /// and defaults to 1.0 (off).</para>
    /// </remarks>
    /// <param name="intensity">Curve exponent (a in SAS Pro). Smaller values
    /// (0.2 - 0.8) give stronger midtones lift; ~1.0 is near identity at
    /// SP=0.5; larger values darken. Default <c>0.5</c> for a moderate
    /// stretch on linear input.</param>
    /// <param name="asymmetry">Direction parameter (b in SAS Pro). 1.0 =
    /// symmetric curve. Values &gt; 1 stretch shadows more aggressively;
    /// values &lt; 1 emphasise highlights. Default <c>1.0</c>.</param>
    /// <param name="shadowProtection">LP in [0, 1]: how much of the curve
    /// below SP is replaced with identity. 0 = full GHS, 1 = pure identity
    /// below SP. Default <c>0.0</c>.</param>
    /// <param name="highlightProtection">HP in [0, 1]: same as
    /// <paramref name="shadowProtection"/> but for the region above SP.
    /// Default <c>0.0</c>; raise (e.g. 0.3) to gently preserve bright
    /// regions without an extra compression step.</param>
    /// <param name="symmetryPoint">SP in (0, 1): the input pixel value at
    /// which the curve hinges. Output at <c>x = 0.5</c> equals SP. Default
    /// <c>0.25</c> matches the SAS Pro / PixInsight statistical-stretch
    /// convention.</param>
    /// <param name="gamma">Output gamma applied after the GHS curve.
    /// Default <c>1.0</c> (no gamma).</param>
    public Image GeneralizedHyperbolicStretch(
        double intensity = 0.5,
        double asymmetry = 1.0,
        double shadowProtection = 0.0,
        double highlightProtection = 0.0,
        double symmetryPoint = 0.25,
        double gamma = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intensity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(asymmetry);
        ArgumentOutOfRangeException.ThrowIfNegative(shadowProtection);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shadowProtection, 1.0);
        ArgumentOutOfRangeException.ThrowIfNegative(highlightProtection);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(highlightProtection, 1.0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(symmetryPoint);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(symmetryPoint, 1.0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gamma);

        const int LutSize = 65536;
        var lut = new float[LutSize];
        BuildGhsLut(lut, intensity, asymmetry, shadowProtection, highlightProtection, symmetryPoint, gamma);

        var (channels, width, height) = Shape;
        var pixelCount = width * height;
        var newData = CreateChannelData(channels, height, width);
        for (var c = 0; c < channels; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref newData[c][0, 0], pixelCount);
            for (var i = 0; i < pixelCount; i++)
            {
                var v = src[i];
                if (float.IsNaN(v)) { dst[i] = float.NaN; continue; }
                var clamped = Math.Clamp(v, 0f, 1f);
                var idx = clamped * (LutSize - 1);
                var i0 = (int)idx;
                if (i0 >= LutSize - 1) { dst[i] = lut[LutSize - 1]; continue; }
                var frac = idx - i0;
                dst[i] = lut[i0] * (1f - frac) + lut[i0 + 1] * frac;
            }
        }
        return new Image(newData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Populates <paramref name="lut"/> with the GHS tone curve indexed by
    /// uniform <c>us = i / (N - 1)</c>. Standalone for testability and so
    /// callers can cache / inspect the LUT outside the per-pixel hot loop.
    /// </summary>
    internal static void BuildGhsLut(
        Span<float> lut, double a, double b, double lp, double hp, double sp, double g)
    {
        const double eps = 1e-9;
        var n = lut.Length;
        var halfA = Math.Pow(0.5, a);
        // midL/midR are constant across all us (depend only on a, b at us=0.5).
        var midL = halfA / (halfA + b * halfA + eps);
        var midR = halfA / (halfA + (1.0 / b) * halfA + eps);

        // First pass: build the raw curve into the LUT.
        for (var i = 0; i < n; i++)
        {
            var us = (double)i / (n - 1);
            double up, vp;
            if (us <= 0.5)
            {
                // Left half: maps us [0, 0.5] to up [0, sp].
                up = 2.0 * sp * us;
                var num = Math.Pow(us, a);
                var raw = num / (num + b * Math.Pow(1.0 - us, a) + eps);
                vp = raw * (sp / Math.Max(midL, eps));
            }
            else
            {
                // Right half: maps us [0.5, 1] to up [sp, 1].
                up = sp + 2.0 * (1.0 - sp) * (us - 0.5);
                var num = Math.Pow(us, a);
                var raw = num / (num + (1.0 / b) * Math.Pow(1.0 - us, a) + eps);
                vp = sp + (raw - midR) * ((1.0 - sp) / Math.Max(1.0 - midR, eps));
            }

            // LP / HP linear blend with identity in protected region.
            if (lp > 0 && up <= sp) vp = (1.0 - lp) * vp + lp * up;
            if (hp > 0 && up >= sp) vp = (1.0 - hp) * vp + hp * up;

            // Optional output gamma.
            if (Math.Abs(g - 1.0) > 1e-6)
            {
                var clamped = Math.Clamp(vp, 0.0, 1.0);
                vp = Math.Pow(clamped, 1.0 / g);
            }

            lut[i] = (float)Math.Clamp(vp, 0.0, 1.0);
        }

        // Monotonic correction (np.maximum.accumulate equivalent). Guards
        // against tiny non-monotonic dips that the floating-point arithmetic
        // can introduce near LP/HP transitions.
        var running = lut[0];
        for (var i = 1; i < n; i++)
        {
            if (lut[i] < running) lut[i] = running;
            else running = lut[i];
        }
    }

    /// <summary>
    /// Reinhard-style soft highlight compression. Below <paramref name="knee"/>
    /// the curve is identity (no change). Above <paramref name="knee"/> values
    /// are compressed by <c>v_out = knee + range · t / (1 + amount · t)</c>
    /// where <c>t = (v - knee) / (1 - knee)</c>. As <paramref name="amount"/>
    /// rises the asymptote of the compressed region drops further below 1.0,
    /// so saturated pixels get pulled back from clipping. Identity at the
    /// midpoint is preserved by virtue of <paramref name="knee"/> being above
    /// 0.5 in typical use. Pairs with <see cref="ReduceBackground"/>: bg-pull
    /// handles the low end, this handles the high end, the two are independent
    /// (asymmetric overall curve passing through (0.5, 0.5) at identity).
    /// </summary>
    /// <param name="knee">Threshold above which compression starts. Default
    /// <c>0.7</c> -- below the typical Frank-0.25 stretched median so the
    /// nebula-bright regions get gentle roll-off while pure mid-tones stay
    /// untouched. Range (0, 1).</param>
    /// <param name="amount">Compression strength. <c>0</c> = no compression
    /// (identity above knee); higher = stronger roll-off. <c>1.0</c> maps
    /// <c>v=1.0</c> to <c>knee + (1-knee)/2</c> -- a useful default that
    /// recovers ~half the headroom above the knee. Range &gt;= 0.</param>
    public Image CompressHighlights(double knee, double amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(knee);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(knee, 1.0);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        var (channels, width, height) = Shape;
        var pixelCount = width * height;
        var newData = CreateChannelData(channels, height, width);
        var k = (float)knee;
        var a = (float)amount;
        for (var c = 0; c < channels; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref newData[c][0, 0], pixelCount);
            for (var i = 0; i < pixelCount; i++)
            {
                var v = src[i];
                dst[i] = float.IsNaN(v) ? float.NaN : ApplyHdr(v, a, k);
            }
        }
        return new Image(newData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Pulls background luminosity down via a symmetric S-curve through
    /// five control points: <c>(0,0), (bg, bg·c), (0.5, 0.5),
    /// (1-bg, 1-bg·c), (1, 1)</c>. Identity at the midpoint, symmetric
    /// around it, monotonic. Cubic Hermite spline interpolation between
    /// control points (smooth, no kinks). Matches the "reduce background
    /// luminosity" curve in Affinity / Photoshop workflows on stretched
    /// astro frames -- darken the histogram peak (background) while
    /// preserving highlights and bright structure.
    /// </summary>
    /// <param name="backgroundPeak">The X anchor of the low control point.
    /// Typically the histogram peak post-stretch (call
    /// <see cref="EstimateBackgroundPeak"/> to auto-derive). Must be in
    /// <c>(0, 0.5)</c>.</param>
    /// <param name="compression">How aggressively to pull the background
    /// down. The low control point is mapped to <c>backgroundPeak · compression</c>.
    /// Default <c>0.10</c> matches the empirically-measured 3/255 ≈ 0.012
    /// background in finished Affinity work (for a typical 0.112 stretched
    /// background peak). Must be in <c>(0, 1]</c>; 1.0 = no reduction.</param>
    public Image ReduceBackground(double backgroundPeak, double compression)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backgroundPeak);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(backgroundPeak, 0.5);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(compression);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(compression, 1.0);

        const int LutSize = 4096;
        Span<float> lut = stackalloc float[LutSize];
        BuildBackgroundReduceLut(lut, (float)backgroundPeak, (float)compression);

        var (channels, width, height) = Shape;
        var pixelCount = width * height;
        var newData = CreateChannelData(channels, height, width);
        for (var c = 0; c < channels; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref newData[c][0, 0], pixelCount);
            for (var i = 0; i < pixelCount; i++)
            {
                var v = src[i];
                if (float.IsNaN(v)) { dst[i] = float.NaN; continue; }
                var clamped = Math.Clamp(v, 0f, 1f);
                var idx = clamped * (LutSize - 1);
                var i0 = (int)idx;
                if (i0 >= LutSize - 1) { dst[i] = lut[LutSize - 1]; continue; }
                var frac = idx - i0;
                dst[i] = lut[i0] * (1f - frac) + lut[i0 + 1] * frac;
            }
        }
        return new Image(newData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Populates <paramref name="lut"/> with the cubic-Hermite-interpolated
    /// 5-point S-curve used by <see cref="ReduceBackground"/>. Standalone
    /// for testability + so callers can inspect / cache the LUT outside the
    /// per-pixel hot loop. Finite-difference tangents at each control point.
    /// </summary>
    internal static void BuildBackgroundReduceLut(Span<float> lut, float backgroundPeak, float compression)
    {
        var bg = backgroundPeak;
        var bgY = backgroundPeak * compression;
        Span<float> xs = stackalloc float[5] { 0f, bg, 0.5f, 1f - bg, 1f };
        Span<float> ys = stackalloc float[5] { 0f, bgY, 0.5f, 1f - bgY, 1f };

        // Finite-difference tangents at each control point. Edges use
        // one-sided differences; interior points average the left and
        // right slopes.
        Span<float> m = stackalloc float[5];
        m[0] = (ys[1] - ys[0]) / (xs[1] - xs[0]);
        m[4] = (ys[4] - ys[3]) / (xs[4] - xs[3]);
        for (var k = 1; k < 4; k++)
        {
            var dL = (ys[k] - ys[k - 1]) / (xs[k] - xs[k - 1]);
            var dR = (ys[k + 1] - ys[k]) / (xs[k + 1] - xs[k]);
            m[k] = 0.5f * (dL + dR);
        }

        for (var i = 0; i < lut.Length; i++)
        {
            var t = (float)i / (lut.Length - 1);
            // Find segment containing t.
            var seg = 0;
            while (seg < 3 && t > xs[seg + 1]) seg++;
            var h = xs[seg + 1] - xs[seg];
            var u = (t - xs[seg]) / h;
            var u2 = u * u;
            var u3 = u2 * u;
            var h00 = 2f * u3 - 3f * u2 + 1f;
            var h10 = u3 - 2f * u2 + u;
            var h01 = -2f * u3 + 3f * u2;
            var h11 = u3 - u2;
            lut[i] = h00 * ys[seg]
                   + h10 * h * m[seg]
                   + h01 * ys[seg + 1]
                   + h11 * h * m[seg + 1];
        }
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
