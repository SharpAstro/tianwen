using System;
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    public async Task<Image> StretchLinkedAsync(double stretchFactor = 0.2d, double shadowsClipping = -3d, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
            return await debayered.StretchLinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else if (imageMeta.SensorType is SensorType.Monochrome)
        {
            return await StretchUnlinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else
        {
            var (channelCount, width, height) = Shape;

            var (pedestral, median, mad) = GetPedestralMedianAndMADScaledToUnit(0);

            var stretchedData = CreateChannelData(channelCount, height, width);
            for (var c = 0; c < channelCount; c++)
            {
                await StretchChannelAsync(stretchedData, c, stretchFactor, shadowsClipping, pedestral, median, mad, cancellationToken);
            }
            return new Image(stretchedData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
        }
    }

    public async Task<Image> StretchUnlinkedAsync(double stretchFactor = 0.2d, double shadowsClipping = -3d, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
            return await debayered.StretchUnlinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }

        var (channelCount, width, height) = Shape;

        var stretchedData = CreateChannelData(channelCount, height, width);

        for (var c = 0; c < channelCount; c++)
        {
            var (pedestral, median, mad) = GetPedestralMedianAndMADScaledToUnit(c);
            await StretchChannelAsync(stretchedData, c, stretchFactor, shadowsClipping, pedestral, median, mad, cancellationToken);
        }

        return new Image(stretchedData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Luma-only stretch: computes luminance Y from RGB, stretches Y → Y', then scales
    /// all channels by Y'/Y to preserve chrominance ratios.
    /// Falls back to unlinked stretch for mono images.
    /// </summary>
    public async Task<Image> StretchLumaAsync(double stretchFactor = 0.2d, double shadowsClipping = -3d, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
            return await debayered.StretchLumaAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else if (imageMeta.SensorType is SensorType.Monochrome || Shape.ChannelCount < 3)
        {
            return await StretchUnlinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else
        {
            var (channelCount, width, height) = Shape;
            var stretchedData = CreateChannelData(channelCount, height, width);
            await StretchLumaCoreAsync(stretchedData, stretchFactor, shadowsClipping, cancellationToken);
            return new Image(stretchedData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
        }
    }

    /// <summary>
    /// Luma-only stretch into a pre-allocated destination buffer, reusing memory.
    /// </summary>
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

    private async Task StretchLumaCoreAsync(float[][,] destination, double stretchFactor, double shadowsClipping, CancellationToken cancellationToken)
    {
        var (channelCount, width, height) = Shape;

        // Build luminance channel
        var lumaData = new float[height, width];
        var srcR = data[0];
        var srcG = data[1];
        var srcB = data[2];
        var needsNorm = MaxValue > 1.0f + float.Epsilon;
        var normFactor = 1.0f / MaxValue;

        var lumaMin = float.MaxValue;
        await Parallel.ForAsync(0, height, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, async (y, ct) => await Task.Run(() =>
        {
            var rowMin = float.MaxValue;
            for (var x = 0; x < width; x++)
            {
                var r = srcR[y, x];
                var g = srcG[y, x];
                var b = srcB[y, x];
                if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
                {
                    lumaData[y, x] = float.NaN;
                }
                else
                {
                    if (needsNorm) { r *= normFactor; g *= normFactor; b *= normFactor; }
                    var luma = LumaR * r + LumaG * g + LumaB * b;
                    lumaData[y, x] = luma;
                    if (luma < rowMin) rowMin = luma;
                }
            }
            Interlocked.Exchange(ref lumaMin, Math.Min(Volatile.Read(ref lumaMin), rowMin));
            return ValueTask.CompletedTask;
        }, ct));

        if (lumaMin == float.MaxValue) lumaMin = 0f;

        // Compute stats on the luminance channel using a temporary single-channel image
        var lumaChannelData = new float[1][,];
        lumaChannelData[0] = lumaData;
        var lumaImage = new Image(lumaChannelData, BitDepth.Float32, 1.0f, lumaMin, 0f, imageMeta with { SensorType = SensorType.Monochrome });

        var (_, median, mad) = lumaImage.GetPedestralMedianAndMADScaledToUnit(0);
        var (shadows, midtones, highlights, rescale) = ComputeStretchParameters(median, mad, stretchFactor, shadowsClipping);

        // Compute per-channel pedestals — avoids green cast from RGGB sensors
        // where the shared luma pedestal clips R and B to zero
        var channelPedestals = new float[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            var (ped, _, _) = GetPedestralMedianAndMADScaledToUnit(c);
            channelPedestals[c] = ped;
        }

        // Stretch: subtract per-channel pedestal, compute luma from pedestal-subtracted values,
        // stretch luma, scale all channels by Y'/Y
        await Parallel.ForAsync(0, height, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, async (y, ct) => await Task.Run(() =>
        {
            for (var x = 0; x < width; x++)
            {
                if (float.IsNaN(data[0][y, x]))
                {
                    for (var c = 0; c < channelCount; c++)
                    {
                        destination[c][y, x] = float.NaN;
                    }
                    continue;
                }

                // Per-channel pedestal subtraction
                var pr = data[0][y, x];
                var pg = data[1][y, x];
                var pb = data[2][y, x];
                if (needsNorm) { pr *= normFactor; pg *= normFactor; pb *= normFactor; }
                pr -= channelPedestals[0];
                pg -= channelPedestals[1];
                pb -= channelPedestals[2];

                // Luma from pedestal-subtracted channels
                var lumaNorm = LumaR * pr + LumaG * pg + LumaB * pb;

                // Stretch the luminance
                var rescaledLuma = (lumaNorm - shadows) * rescale;
                var stretchedLuma = (float)MidtonesTransferFunction(midtones, rescaledLuma);

                // Scale factor: Y'/Y (avoid division by zero)
                var scale = lumaNorm > 1e-7f ? stretchedLuma / lumaNorm : 0f;

                // Cap scale to prevent channel saturation
                if (scale > 0f)
                {
                    var maxCh = Math.Max(pr, Math.Max(pg, pb));
                    if (maxCh > 1e-7f)
                    {
                        scale = Math.Min(scale, 1.0f / maxCh);
                    }
                }

                destination[0][y, x] = Math.Clamp(pr * scale, 0f, 1f);
                destination[1][y, x] = Math.Clamp(pg * scale, 0f, 1f);
                destination[2][y, x] = Math.Clamp(pb * scale, 0f, 1f);

                // Extra channels (alpha etc.) pass through
                for (var c = 3; c < channelCount; c++)
                {
                    destination[c][y, x] = data[c][y, x];
                }
            }
            return ValueTask.CompletedTask;
        }, ct));
    }

    private async Task StretchChannelAsync(float[][,] stretched, int channel, double stretchFactor, double shadowsClipping, float pedestral, float median, float mad, CancellationToken cancellationToken = default)
    {
        var (channelCount, width, height) = Shape;

        if (channel < 0 || channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        var normFactor = MaxValue > 1.0f + float.Epsilon ? (float)(1.0 / MaxValue) : 1f;
        var (shadows, midtones, highlights, rescale) = ComputeStretchParameters(median, mad, stretchFactor, shadowsClipping);

        var srcChannel = data[channel];
        var dstChannel = stretched[channel];

        await Parallel.ForAsync(0, height, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, async (y, ct) => await Task.Run(() =>
        {
            for (var x = 0; x < width; x++)
            {
                var value = srcChannel[y, x];
                if (!float.IsNaN(value))
                {
                    dstChannel[y, x] = StretchValue(value, normFactor, pedestral, shadows, midtones, rescale);
                }
                else
                {
                    dstChannel[y, x] = float.NaN;
                }
            }
            return ValueTask.CompletedTask;
        }, ct));
    }

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
    /// CPU mirror of the GLSL Luma stretch path (<c>stretchMode == 2</c>). Per-channel pedestal
    /// subtract + WB -> Rec.709 luma -> MTF -> scale all channels by Y'/Y. Matches the GLSL
    /// exactly, including the omission of background neutralization (which is only applied in
    /// the per-channel Linked/Unlinked path).
    /// </summary>
    public static (float R, float G, float B) StretchLumaPixelCpu(float r, float g, float b, in StretchUniforms u)
    {
        var nr = r * u.NormFactor;
        var ng = g * u.NormFactor;
        var nb = b * u.NormFactor;
        var prr = MathF.Max((nr - u.Pedestal.R) * u.WhiteBalance.R, 0f);
        var prg = MathF.Max((ng - u.Pedestal.G) * u.WhiteBalance.G, 0f);
        var prb = MathF.Max((nb - u.Pedestal.B) * u.WhiteBalance.B, 0f);
        var yNorm = LumaR * prr + LumaG * prg + LumaB * prb;
        var rescaled = (yNorm - u.Shadows.R) * u.Rescale.R;
        var yPrime = (float)MidtonesTransferFunction(u.Midtones.R, rescaled);
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
                    (rOut, gOut, bOut) = StretchLumaPixelCpu(rRaw, gRaw, bRaw, u);
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
