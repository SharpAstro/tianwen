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

            var lumaData = new float[1][,];
            lumaData[0] = new float[height, width];
            var dst = lumaData[0];
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
                        dst[y, x] = float.NaN;
                    }
                    else
                    {
                        if (needsNorm) { r *= normFactor; g *= normFactor; b *= normFactor; }
                        var luma = LumaR * r + LumaG * g + LumaB * b;
                        dst[y, x] = luma;
                        if (luma < lumaMin) lumaMin = luma;
                    }
                }
            }

            if (lumaMin == float.MaxValue) lumaMin = 0f;

            var lumaImage = new Image(lumaData, BitDepth.Float32, 1.0f, lumaMin, 0f,
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
}
