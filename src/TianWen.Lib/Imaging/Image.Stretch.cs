using System;
using System.Buffers;
using System.Collections.Immutable;
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
    public float EstimateRisingEdge(int channel = 0, float thresholdFraction = 0.05f)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, ChannelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thresholdFraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(thresholdFraction, 1.0f);

        const int Bins = 4096;
        Span<int> hist = stackalloc int[Bins];
        var src = GetChannelSpan(channel);
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
    /// <param name="lnD">Stretch factor, in the user-facing
    /// <c>ln(D + 1)</c> convention from the PixInsight slider (the
    /// screenshot's <c>1.30</c> -- internally converts to
    /// <c>D = exp(lnD) - 1</c>). 0 = identity (no stretch); larger lifts
    /// the shadows more aggressively. Range &gt;= 0. Default <c>1.3</c>
    /// matches Paul (Polymath Astro)'s example case-1 (linear -> display)
    /// stretch.</param>
    /// <param name="b">Local stretch intensity. <b>Signed:</b> negative
    /// values pick the logarithmic / power-with-negative-B branches,
    /// zero picks the exponential branch, positive picks the
    /// hyperbolic / harmonic branch. Larger |b| = more focused stretch
    /// around SP. Range any finite double. Default <c>-1.0</c>
    /// (logarithmic) matches Paul's example settings.</param>
    /// <param name="sp">Symmetry point in [LP, HP]. <b>Input pixel value
    /// where the curve has maximum gradient</b> -- the inflection point.
    /// Typically set to the histogram lift-off (the linear bg peak) for
    /// stretch-from-linear use; the screenshot uses 0.57 for an
    /// already-MTF-stretched input. Default <c>0.5</c>.</param>
    /// <param name="lp">Lowlight (shadow) protection point in [0, SP].
    /// Below LP the curve is linear at the gradient evaluated at LP --
    /// hard floor that preserves shadow texture. Default <c>0.0</c>
    /// (no shadow protection).</param>
    /// <param name="hp">Highlight protection point in [SP, 1]. Above HP
    /// the curve is linear at the gradient evaluated at HP -- prevents
    /// the upper tail from being compressed to identity. Default
    /// <c>1.0</c> (no highlight protection).</param>
    public Image GeneralizedHyperbolicStretch(
        double lnD = 1.3,
        double b = -1.0,
        double sp = 0.5,
        double lp = 0.0,
        double hp = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lnD);
        ArgumentOutOfRangeException.ThrowIfNegative(sp);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sp, 1.0);
        ArgumentOutOfRangeException.ThrowIfNegative(lp);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lp, sp);
        ArgumentOutOfRangeException.ThrowIfLessThan(hp, sp);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hp, 1.0);

        const int LutSize = 65536;
        var lut = new float[LutSize];
        BuildGhsLut(lut, lnD, b, sp, lp, hp);

        var (channels, width, height) = Shape;
        var pixelCount = width * height;
        var newData = CreateChannelData(channels, height, width);
        for (var c = 0; c < channels; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref newData[c][0, 0], pixelCount);
            ApplyLutToChannel(src, dst, lut);
        }
        return new Image(newData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Per-channel GHS overload: applies a SEPARATE curve to each channel
    /// using the corresponding entry of <paramref name="lnD"/>,
    /// <paramref name="b"/>, <paramref name="sp"/>, <paramref name="lp"/>,
    /// <paramref name="hp"/>. PixInsight's "unlinked" stretch mode --
    /// useful when channels carry unequal bg levels (typical for an
    /// OSC drizzle without prior bg neutralisation), where the linked
    /// overload produces a colour cast.
    /// </summary>
    /// <remarks>
    /// All parameter arrays must have the same length as
    /// <see cref="ChannelCount"/>. Caller is responsible for deriving
    /// the per-channel parameters (typically: SP via
    /// <see cref="EstimateRisingEdge"/> per channel, LnD via
    /// <see cref="ConvergeGhsStretchFactor"/> per channel).
    /// </remarks>
    public Image GeneralizedHyperbolicStretchPerChannel(
        ReadOnlySpan<double> lnD, ReadOnlySpan<double> b,
        ReadOnlySpan<double> sp, ReadOnlySpan<double> lp,
        ReadOnlySpan<double> hp)
    {
        var (channels, width, height) = Shape;
        if (lnD.Length != channels || b.Length != channels || sp.Length != channels
            || lp.Length != channels || hp.Length != channels)
        {
            throw new ArgumentException(
                $"Per-channel parameter arrays must have length {channels} (got lnD={lnD.Length}, b={b.Length}, sp={sp.Length}, lp={lp.Length}, hp={hp.Length})");
        }

        const int LutSize = 65536;
        var lut = new float[LutSize];
        var pixelCount = width * height;
        var newData = CreateChannelData(channels, height, width);
        for (var c = 0; c < channels; c++)
        {
            BuildGhsLut(lut, lnD[c], b[c], sp[c], lp[c], hp[c]);
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref newData[c][0, 0], pixelCount);
            ApplyLutToChannel(src, dst, lut);
        }
        return new Image(newData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Maps every pixel in <paramref name="src"/> through
    /// <paramref name="lut"/> with linear interpolation between LUT
    /// entries; writes to <paramref name="dst"/>. NaN pixels pass through
    /// unmodified. Extracted to keep the linked + per-channel overloads
    /// from duplicating the per-pixel loop.
    /// </summary>
    private static void ApplyLutToChannel(ReadOnlySpan<float> src, Span<float> dst, ReadOnlySpan<float> lut)
    {
        var lutSize = lut.Length;
        for (var i = 0; i < src.Length; i++)
        {
            var v = src[i];
            if (float.IsNaN(v)) { dst[i] = float.NaN; continue; }
            var clamped = Math.Clamp(v, 0f, 1f);
            var idx = clamped * (lutSize - 1);
            var i0 = (int)idx;
            if (i0 >= lutSize - 1) { dst[i] = lut[lutSize - 1]; continue; }
            var frac = idx - i0;
            dst[i] = lut[i0] * (1f - frac) + lut[i0 + 1] * frac;
        }
    }

    /// <summary>
    /// Pre-computed coefficient set for one branch of the GHS curve. Each
    /// segment of the piecewise function (<c>z &lt; LP</c>,
    /// <c>LP &lt;= z &lt; SP</c>, <c>SP &lt;= z &lt;= HP</c>,
    /// <c>z &gt; HP</c>) reads its coefficients from this struct.
    /// <c>e2</c> / <c>e3</c> are only used by the power-form branches
    /// (<c>B != 0</c> and <c>B != -1</c>); other branches leave them at 0.
    /// Order + meaning match Mike Cranfield's reference PixInsight
    /// implementation (<c>GHSStretch.js</c> lines 902-1040). Internal so
    /// the convergence helper can probe coefficients independently of
    /// the LUT build.
    /// </summary>
    internal readonly record struct GhsCoefficients(
        double A1, double B1,
        double A2, double B2, double C2, double D2, double E2,
        double A3, double B3, double C3, double D3, double E3,
        double A4, double B4);

    /// <summary>
    /// Derives the 4-segment piecewise GHS coefficients for the supplied
    /// curve parameters. Faithful port of <c>GHSStretch.js</c> section 5
    /// (lines 902-1040). The output curve passes through <c>(0, 0)</c>
    /// and <c>(1, 1)</c> and is continuous at LP / SP / HP by construction.
    /// </summary>
    /// <param name="D">Stretch factor in linear units
    /// (<c>D = exp(lnD) - 1</c>); not the user-facing slider value.</param>
    /// <param name="b">Local stretch intensity (signed). The sign +
    /// magnitude select the branch:
    /// <c>b == -1</c> logarithmic, <c>b &lt; 0</c> power (negative b),
    /// <c>b == 0</c> exponential, <c>b &gt; 0</c> hyperbolic.</param>
    internal static GhsCoefficients ComputeGhsCoefficients(double D, double b, double sp, double lp, double hp)
    {
        double qlp, q0, qwp, q1, q;
        double a1, b1, a2, b2_, c2, d2, e2 = 0.0, a3, b3, c3, d3, e3 = 0.0, a4, b4;

        if (b == -1.0)
        {
            // Logarithmic branch: y = a + b * ln(c + d * z).
            qlp = -Math.Log(1.0 + D * (sp - lp));
            q0 = qlp - D * lp / (1.0 + D * (sp - lp));
            qwp = Math.Log(1.0 + D * (hp - sp));
            q1 = qwp + D * (1.0 - hp) / (1.0 + D * (hp - sp));
            q = 1.0 / (q1 - q0);

            a1 = 0.0;
            b1 = D / (1.0 + D * (sp - lp)) * q;

            a2 = -q0 * q;
            b2_ = -q;
            c2 = 1.0 + D * sp;
            d2 = -D;

            a3 = -q0 * q;
            b3 = q;
            c3 = 1.0 - D * sp;
            d3 = D;

            a4 = (qwp - q0 - D * hp / (1.0 + D * (hp - sp))) * q;
            b4 = q * D / (1.0 + D * (hp - sp));
        }
        else if (b < 0.0)
        {
            // Power branch with negative b: y = a + b * (c + d * z)^e.
            // Sign flip on b matches the reference's internal handling.
            var B = -b;
            qlp = (1.0 - Math.Pow(1.0 + D * B * (sp - lp), (B - 1.0) / B)) / (B - 1.0);
            q0 = qlp - D * lp * Math.Pow(1.0 + D * B * (sp - lp), -1.0 / B);
            qwp = (Math.Pow(1.0 + D * B * (hp - sp), (B - 1.0) / B) - 1.0) / (B - 1.0);
            q1 = qwp + D * (1.0 - hp) * Math.Pow(1.0 + D * B * (hp - sp), -1.0 / B);
            q = 1.0 / (q1 - q0);

            a1 = 0.0;
            b1 = D * Math.Pow(1.0 + D * B * (sp - lp), -1.0 / B) * q;

            a2 = (1.0 / (B - 1.0) - q0) * q;
            b2_ = -q / (B - 1.0);
            c2 = 1.0 + D * B * sp;
            d2 = -D * B;
            e2 = (B - 1.0) / B;

            a3 = (-1.0 / (B - 1.0) - q0) * q;
            b3 = q / (B - 1.0);
            c3 = 1.0 - D * B * sp;
            d3 = D * B;
            e3 = (B - 1.0) / B;

            a4 = (qwp - q0 - D * hp * Math.Pow(1.0 + D * B * (hp - sp), -1.0 / B)) * q;
            b4 = D * Math.Pow(1.0 + D * B * (hp - sp), -1.0 / B) * q;
        }
        else if (b == 0.0)
        {
            // Exponential branch: y = a + b * exp(c + d * z).
            qlp = Math.Exp(-D * (sp - lp));
            q0 = qlp - D * lp * Math.Exp(-D * (sp - lp));
            qwp = 2.0 - Math.Exp(-D * (hp - sp));
            q1 = qwp + D * (1.0 - hp) * Math.Exp(-D * (hp - sp));
            q = 1.0 / (q1 - q0);

            a1 = 0.0;
            b1 = D * Math.Exp(-D * (sp - lp)) * q;

            a2 = -q0 * q;
            b2_ = q;
            c2 = -D * sp;
            d2 = D;

            a3 = (2.0 - q0) * q;
            b3 = -q;
            c3 = D * sp;
            d3 = -D;

            a4 = (qwp - q0 - D * hp * Math.Exp(-D * (hp - sp))) * q;
            b4 = D * Math.Exp(-D * (hp - sp)) * q;
        }
        else
        {
            // b > 0: Hyperbolic / harmonic branch:
            // y = a + b * (c + d * z)^e with e = -1/b.
            qlp = Math.Pow(1.0 + D * b * (sp - lp), -1.0 / b);
            q0 = qlp - D * lp * Math.Pow(1.0 + D * b * (sp - lp), -(1.0 + b) / b);
            qwp = 2.0 - Math.Pow(1.0 + D * b * (hp - sp), -1.0 / b);
            q1 = qwp + D * (1.0 - hp) * Math.Pow(1.0 + D * b * (hp - sp), -(1.0 + b) / b);
            q = 1.0 / (q1 - q0);

            a1 = 0.0;
            b1 = D * Math.Pow(1.0 + D * b * (sp - lp), -(1.0 + b) / b) * q;

            a2 = -q0 * q;
            b2_ = q;
            c2 = 1.0 + D * b * sp;
            d2 = -D * b;
            e2 = -1.0 / b;

            a3 = (2.0 - q0) * q;
            b3 = -q;
            c3 = 1.0 - D * b * sp;
            d3 = D * b;
            e3 = -1.0 / b;

            a4 = (qwp - q0 - D * hp * Math.Pow(1.0 + D * b * (hp - sp), -(b + 1.0) / b)) * q;
            b4 = D * Math.Pow(1.0 + D * b * (hp - sp), -(b + 1.0) / b) * q;
        }

        return new GhsCoefficients(a1, b1, a2, b2_, c2, d2, e2, a3, b3, c3, d3, e3, a4, b4);
    }

    /// <summary>
    /// Evaluates the GHS curve at a single input value <paramref name="z"/>
    /// in <c>[0, 1]</c> given the pre-computed
    /// <paramref name="coeff"/> and the branch selector
    /// <paramref name="b"/>. The 4 piecewise regions are dispatched by
    /// comparing <c>z</c> against <c>lp</c> / <c>sp</c> / <c>hp</c>.
    /// </summary>
    internal static double EvaluateGhs(in GhsCoefficients coeff, double z, double b, double lp, double sp, double hp)
    {
        if (z < lp) return coeff.A1 + coeff.B1 * z;
        if (z > hp) return coeff.A4 + coeff.B4 * z;

        if (b == -1.0)
        {
            return z < sp
                ? coeff.A2 + coeff.B2 * Math.Log(coeff.C2 + coeff.D2 * z)
                : coeff.A3 + coeff.B3 * Math.Log(coeff.C3 + coeff.D3 * z);
        }
        if (b == 0.0)
        {
            return z < sp
                ? coeff.A2 + coeff.B2 * Math.Exp(coeff.C2 + coeff.D2 * z)
                : coeff.A3 + coeff.B3 * Math.Exp(coeff.C3 + coeff.D3 * z);
        }
        // Power form -- the (B-1)/B sign flip in the negative-b branch is
        // already baked into the coefficients (e2 / e3).
        return z < sp
            ? coeff.A2 + coeff.B2 * Math.Pow(coeff.C2 + coeff.D2 * z, coeff.E2)
            : coeff.A3 + coeff.B3 * Math.Pow(coeff.C3 + coeff.D3 * z, coeff.E3);
    }

    /// <summary>
    /// Populates <paramref name="lut"/> with the GHS tone curve sampled
    /// at uniform input values <c>i / (N - 1)</c>. Faithful port of
    /// Mike Cranfield's reference PixInsight script
    /// (<c>mikec1485/GHS/src/scripts/.../lib/GHSStretch.js</c>),
    /// 4 piecewise regions with branch selection on <paramref name="b"/>.
    /// </summary>
    /// <param name="lnD">User-facing slider value
    /// (<c>D_actual = exp(lnD) - 1</c>). 0 = identity.</param>
    /// <param name="b">Signed local stretch intensity.</param>
    /// <param name="sp">Symmetry point (input value where curve hinges).</param>
    /// <param name="lp">Shadow protection point.</param>
    /// <param name="hp">Highlight protection point.</param>
    internal static void BuildGhsLut(
        Span<float> lut, double lnD, double b, double sp, double lp, double hp)
    {
        var n = lut.Length;
        var D = Math.Exp(lnD) - 1.0;

        if (D <= 0.0)
        {
            // Identity (lnD == 0 or invalid). Caller validates lnD >= 0;
            // explicit identity here is robust against floating-point
            // rounding of exp(0) - 1 to a tiny negative.
            for (var i = 0; i < n; i++) lut[i] = (float)i / (n - 1);
            return;
        }

        var coeff = ComputeGhsCoefficients(D, b, sp, lp, hp);
        for (var i = 0; i < n; i++)
        {
            var z = (double)i / (n - 1);
            var y = EvaluateGhs(coeff, z, b, lp, sp, hp);
            lut[i] = (float)Math.Clamp(y, 0.0, 1.0);
        }

        // Monotonic correction (np.maximum.accumulate equivalent). Guards
        // against tiny non-monotonic dips that the floating-point arithmetic
        // can introduce near LP / SP / HP boundary transitions.
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

    /// <summary>
    /// Result of <see cref="ConvergeGhsStretchFactor"/>. <see cref="LnD"/>
    /// is the converged stretch factor (user-facing
    /// <c>ln(D + 1)</c> convention); <see cref="PostStretchMedian"/> /
    /// <see cref="LogSlopeRSquared"/> report where the converged curve
    /// landed. <see cref="ConvergedMedian"/> is true iff the bisection
    /// satisfied the median tolerance.
    /// </summary>
    public readonly record struct GhsConvergence(
        double LnD,
        double PostStretchMedian,
        double LogSlopeRSquared,
        bool ConvergedMedian);

    /// <summary>
    /// Bisects <c>LnD</c> until the post-stretch median lands within
    /// <paramref name="medianTolerance"/> of <paramref name="targetMedian"/>.
    /// <c>B</c>, <c>SP</c>, <c>LP</c>, <c>HP</c> are fixed across the
    /// bisection -- LnD is the natural single variable that controls
    /// curve strength, mirroring <see cref="ConvergeStretchFactor"/> for
    /// MTF. Cheap: each iteration builds a 65536-entry LUT once and
    /// walks the histogram bins through it; no per-pixel pass through
    /// the image. Reports the post-stretch log-slope R^2 as an advisory
    /// quality marker; it does NOT drive convergence (narrowband / steep
    /// nebula inputs naturally violate the exponential-decay assumption
    /// the R^2 measures against, and the metric is left for the operator
    /// to interpret).
    /// </summary>
    /// <remarks>
    /// <para>Monotonicity: higher <c>LnD</c> = larger <c>D</c> = stronger
    /// curve = higher post-stretch median (for stretch-from-linear use
    /// where <c>SP &lt; target</c>). Bisection direction:
    /// <c>median &lt; target</c> -> raise lo; <c>median &gt; target</c>
    /// -> lower hi.</para>
    ///
    /// <para>The log-slope R^2 is computed at the converged LnD only.
    /// Score &gt;= 0.9 generally indicates a well-stretched broadband
    /// astro frame (exponential decay above the bg peak); &lt; 0.7 is
    /// either narrowband (legitimate violation of the decay model) or
    /// a poor stretch. Use as a hint, not a hard gate.</para>
    /// </remarks>
    /// <param name="histogram">Pre-computed channel-0 histogram. Must
    /// have <see cref="ImageHistogram.Total"/> &gt; 0.</param>
    /// <param name="b">GHS signed local stretch intensity. Default
    /// <c>8.0</c> (Paul's case-1 hyperbolic branch).</param>
    /// <param name="sp">Symmetry point. Caller-supplied (typically
    /// <see cref="EstimateRisingEdge"/> on the linear input).</param>
    /// <param name="lp">Shadow protection point.</param>
    /// <param name="hp">Highlight protection point.</param>
    /// <param name="targetMedian">Desired post-stretch median. Default
    /// <c>0.25</c>.</param>
    /// <param name="medianTolerance">Convergence tolerance. Default
    /// <c>0.01</c>.</param>
    /// <param name="minLnD">Lower bisection bound. Default <c>0.1</c>.</param>
    /// <param name="maxLnD">Upper bisection bound. Default <c>8.0</c>
    /// (corresponds to D ~= 2980 -- extreme stretch). The reference
    /// PixInsight script uses a slider range to roughly 8.</param>
    /// <param name="maxIterations">Bisection iteration cap. Default <c>20</c>.</param>
    public static GhsConvergence ConvergeGhsStretchFactor(
        ImageHistogram histogram,
        double b = 8.0,
        double sp = 0.05,
        double lp = 0.0,
        double hp = 0.8,
        double targetMedian = 0.25,
        double medianTolerance = 0.01,
        double minLnD = 0.1,
        double maxLnD = 8.0,
        int maxIterations = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLnD - minLnD);

        if (histogram.Total <= 0f)
        {
            return new GhsConvergence(1.30, double.NaN, double.NaN, ConvergedMedian: false);
        }

        const int LutSize = 65536;
        Span<float> lut = new float[LutSize];
        var bins = histogram.Histogram;
        var binCount = bins.Length;
        var totalF = histogram.Total;
        var halfTotal = totalF * 0.5f;
        var binMax = histogram.RescaledMaxValue ?? 65535f;
        var invBinMax = 1f / binMax;

        var lo = minLnD;
        var hi = maxLnD;
        var lnD = Math.Clamp(1.30, lo, hi);
        var lastMedian = double.NaN;
        var converged = false;

        for (var iter = 0; iter < maxIterations; iter++)
        {
            BuildGhsLut(lut, lnD, b, sp, lp, hp);
            lastMedian = ComputePostStretchMedian(lut, bins, halfTotal, invBinMax);

            if (double.IsNaN(lastMedian)) break;
            if (Math.Abs(lastMedian - targetMedian) <= medianTolerance)
            {
                converged = true;
                break;
            }

            // median < target -> curve too weak -> raise lnD.
            if (lastMedian < targetMedian) lo = lnD;
            else hi = lnD;
            lnD = (lo + hi) * 0.5;
        }

        // Final pass on the converged LnD to compute the R^2 advisory.
        BuildGhsLut(lut, lnD, b, sp, lp, hp);
        var rSquared = ComputeLogSlopeRSquared(lut, bins, totalF, invBinMax);

        return new GhsConvergence(lnD, lastMedian, rSquared, converged);
    }

    /// <summary>
    /// Walks <paramref name="bins"/> through <paramref name="lut"/> to
    /// find the post-stretch median (the post-stretch value at which
    /// cumulative count first reaches <paramref name="halfTotal"/>).
    /// Same idea as <see cref="ConvergeStretchFactor"/>'s inner loop but
    /// generalised: each bin's center value maps through the LUT to its
    /// post-stretch value; cumulative bin counts find the 50th percentile.
    /// </summary>
    private static double ComputePostStretchMedian(
        ReadOnlySpan<float> lut, ImmutableArray<uint> bins, float halfTotal, float invBinMax)
    {
        var binCount = bins.Length;
        var cumulative = 0f;
        for (var i = 0; i < binCount; i++)
        {
            cumulative += bins[i];
            if (cumulative >= halfTotal)
            {
                var v = (i + 0.5f) * invBinMax;
                if (v > 1f) v = 1f;
                var idx = v * (lut.Length - 1);
                var i0 = (int)idx;
                if (i0 >= lut.Length - 1) return lut[lut.Length - 1];
                var frac = idx - i0;
                return lut[i0] * (1f - frac) + lut[i0 + 1] * frac;
            }
        }
        return double.NaN;
    }

    /// <summary>
    /// Computes the R^2 of a least-squares linear fit on
    /// <c>(value, log(count))</c> sampled from the post-stretch
    /// histogram above the bg peak. A well-stretched broadband astro
    /// frame produces approximate exponential decay above the bg peak
    /// -- linear when plotted log-y, R^2 close to 1. Narrowband or
    /// steep nebula inputs naturally fail this; the metric is advisory.
    /// </summary>
    /// <returns>R^2 in <c>[0, 1]</c>, or <see cref="double.NaN"/> when
    /// the post-stretch histogram lacks enough non-empty bins above
    /// the peak to fit a line (e.g. all pixels collapse to a single
    /// bin under an extreme stretch).</returns>
    private static double ComputeLogSlopeRSquared(
        ReadOnlySpan<float> lut, ImmutableArray<uint> bins, float total, float invBinMax)
    {
        var binCount = bins.Length;
        // Project input histogram into a post-stretch histogram by
        // mapping each input bin's center value through the LUT.
        // Same bin count as input -- precision penalty is minor for
        // 65536 bins and lets us reuse the same indexing arithmetic.
        var postBins = new uint[binCount];
        for (var i = 0; i < binCount; i++)
        {
            if (bins[i] == 0) continue;
            var v = (i + 0.5f) * invBinMax;
            if (v > 1f) v = 1f;
            var idx = v * (lut.Length - 1);
            var i0 = (int)idx;
            var stretched = i0 >= lut.Length - 1
                ? lut[lut.Length - 1]
                : lut[i0] + (lut[i0 + 1] - lut[i0]) * (idx - i0);
            var outIdx = (int)(stretched * (binCount - 1));
            if (outIdx < 0) outIdx = 0;
            else if (outIdx >= binCount) outIdx = binCount - 1;
            postBins[outIdx] += bins[i];
        }

        // Find the post-stretch peak bin (mode).
        var peakIdx = 0;
        var peakCount = 0u;
        for (var i = 0; i < binCount; i++)
        {
            if (postBins[i] > peakCount) { peakCount = postBins[i]; peakIdx = i; }
        }
        if (peakCount == 0) return double.NaN;

        // Sample from just above the peak through 0.95 of the top bin.
        // Skip a small margin past the peak (we want the decay region,
        // not the peak itself); cap at 0.95 to avoid the edge artifacts
        // that often live in the final few bins.
        var startIdx = peakIdx + (int)(binCount * 0.02);  // 2% past peak
        var endIdx = (int)(binCount * 0.95);
        if (endIdx - startIdx < 16) return double.NaN; // too few sample bins

        // Build the (x, y) pairs: x = bin value, y = log(count + 1)
        // -- the +1 epsilon prevents log(0) on empty bins without
        // distorting the regression on populated ones.
        // Compute regression statistics in a single pass.
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        var n = 0;
        for (var i = startIdx; i <= endIdx; i++)
        {
            var x = (double)i / (binCount - 1);
            var y = Math.Log(postBins[i] + 1.0);
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
            n++;
        }
        if (n < 2) return double.NaN;

        var meanX = sumX / n;
        var meanY = sumY / n;
        var varX = sumXX / n - meanX * meanX;
        if (varX <= 0) return double.NaN;
        var covXY = sumXY / n - meanX * meanY;
        var slope = covXY / varX;
        var intercept = meanY - slope * meanX;

        double ssTot = 0, ssRes = 0;
        for (var i = startIdx; i <= endIdx; i++)
        {
            var x = (double)i / (binCount - 1);
            var y = Math.Log(postBins[i] + 1.0);
            var yHat = intercept + slope * x;
            ssTot += (y - meanY) * (y - meanY);
            ssRes += (y - yHat) * (y - yHat);
        }
        if (ssTot <= 0) return double.NaN;
        return Math.Clamp(1.0 - ssRes / ssTot, 0.0, 1.0);
    }
}
