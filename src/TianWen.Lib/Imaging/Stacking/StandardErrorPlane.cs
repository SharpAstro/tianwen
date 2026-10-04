using System;
using System.Buffers;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// What a master's standard-error plane IS, in one place: per channel and per pixel, the standard error of the
/// combined value, measured from the scatter of the samples the combine kept (docs/plans/denoiser-training.md,
/// "E16c step 2"). It is the noise the half pairs measure, as a measurement where every single-frame statistic tried
/// is an estimate that reads shared structure as noise (E16c step 1: within 10 percent of the pairs on 30 of 82
/// sessions).
/// </summary>
/// <remarks>
/// <para><b>One frame's noise read robustly from EVERY finite sample, over the KEPT count.</b> The survivors' own scatter
/// was the first rule and failed its test: an iterated clip can tighten on a column until the survivors cluster, and
/// their scatter then says the mean is far better known than it is (against frames of known noise with 3 percent hot
/// outliers, the master's error over that plane spread 1.164 with tails to 7.6, its robust core 1.055; unclipped 1.016).
/// So the per-frame spread is 1.4826 times the median absolute deviation of all the finite samples about their median,
/// which an outlier cannot inflate and a clip cannot shrink, and the mean the combine took is over the kept samples, so
/// that spread over the square root of their count is its error.</para>
/// <para><b>And unbiased in the MEAN for the few samples a column has</b> (<see cref="MadBiasCorrection"/>). A median
/// absolute deviation of n samples reads low: by a factor 0.735 at n = 4, 0.887 at 8, 0.949 at 16 (simulated for this
/// median convention, which averages the two middle values). The plane is linear in this value and blurred before a
/// model sees it, so its local MEAN is what has to be right, and a half of 10 to 30 subs would otherwise read 4 to 9
/// percent low, the size of the bias E16c exists to remove.</para>
/// <para><b>The keep mask is a weight</b>, as <see cref="MeanCombiner"/> reads it: the kept count is the effective one,
/// <c>(sum k)^2 / sum k^2</c>, which is the count for a 0/1 mask. A NaN sample counts for nothing, as the combiner reads
/// it. Fewer than two finite samples, or under two effective kept, answer NaN: one sample is not zero noise, it is no
/// measurement.</para>
/// </remarks>
internal static class StandardErrorPlane
{
    /// <summary>The standard error of <see cref="MeanCombiner.Combine"/>'s answer for this column and mask.</summary>
    internal static float Of(ReadOnlySpan<float> column, ReadOnlySpan<float> keepMask)
    {
        double sumK = 0, sumKK = 0;
        var finite = 0;
        for (var i = 0; i < column.Length; i++)
        {
            if (float.IsNaN(column[i]))
            {
                continue;
            }
            finite++;
            var k = keepMask[i];
            if (k > 0f)
            {
                sumK += k;
                sumKK += (double)k * k;
            }
        }
        if (finite < 2 || sumKK <= 0)
        {
            return float.NaN;
        }
        var effective = sumK * sumK / sumKK;
        if (effective < 2.0 - 1e-9)
        {
            return float.NaN;
        }

        var buffer = ArrayPool<float>.Shared.Rent(finite);
        try
        {
            var n = 0;
            foreach (var v in column)
            {
                if (!float.IsNaN(v))
                {
                    buffer[n++] = v;
                }
            }
            var spread = 1.4826 * MadBiasCorrection(n) * StatisticsHelper.MedianAndMad(buffer.AsSpan(0, n)).Mad;
            return (float)(spread / Math.Sqrt(effective));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The factor that makes <c>1.4826 * MAD</c> of <paramref name="n"/> Gaussian samples unbiased in the mean, for
    /// <see cref="StatisticsHelper.MedianAndMad(Span{float})"/>'s median (the two middle values averaged). Up to 20 from a
    /// simulation of four million columns each (within a percent of Croux and Rousseeuw's table, whose median convention
    /// differs); past it their <c>n / (n - 0.8)</c>, which the simulation matches to 0.2 percent from 16 on.
    /// </summary>
    internal static double MadBiasCorrection(int n) => n switch
    {
        < 2 => double.NaN,
        <= 20 => SmallSampleMadCorrection[n - 2],
        _ => n / (n - 0.8),
    };

    private static readonly double[] SmallSampleMadCorrection =
    [
        1.1952, 1.4869, 1.3608, 1.2172, 1.1894, 1.1379, 1.1272, 1.1013, 1.0956, 1.0802,
        1.0765, 1.0660, 1.0637, 1.0561, 1.0545, 1.0489, 1.0480, 1.0433, 1.0425,
    ];

    /// <summary>
    /// The per-channel planes a sink accumulated, as an image labelled with the OBSERVED peak, as a drizzle weight is:
    /// a standard error has no bound the strategy knows up front, and the label is what a writer states as DATAMAX.
    /// </summary>
    internal static Image Finalise(IIntegrationSink sink, in ImageMeta meta)
    {
        // The sink's image carries a placeholder label; its planes are relabelled once the peak is known.
        var image = sink.FinaliseAsImage(BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, meta: meta);
        var planes = new float[image.ChannelCount][,];
        for (var c = 0; c < planes.Length; c++)
        {
            planes[c] = image.GetChannelArray(c);
        }
        return FromPlanes(planes, meta);
    }

    /// <summary>
    /// The plane in NEW planes, every value times <paramref name="factor"/>: what a master divided into unit scale
    /// (<c>ScaleFloatValuesToUnitCeiling</c>) needs done to its standard error, which is in the master's own units.
    /// </summary>
    internal static Image Scaled(Image standardError, float factor)
    {
        var planes = new float[standardError.ChannelCount][,];
        for (var c = 0; c < planes.Length; c++)
        {
            var source = standardError.GetChannelArray(c);
            var plane = new float[source.GetLength(0), source.GetLength(1)];
            for (var y = 0; y < plane.GetLength(0); y++)
            {
                for (var x = 0; x < plane.GetLength(1); x++)
                {
                    plane[y, x] = source[y, x] * factor;
                }
            }
            planes[c] = plane;
        }
        return FromPlanes(planes, standardError.ImageMeta);
    }

    /// <summary>The same for planes a strategy assembled itself (a strip-wise strategy).</summary>
    internal static Image FromPlanes(float[][,] planes, in ImageMeta meta)
    {
        var (_, peak) = Image.ObservedRange(planes);
        return new Image(
            data: planes,
            bitDepth: BitDepth.Float32,
            maxValue: peak > 0f ? peak : 1f,
            minValue: 0f,
            pedestal: 0f,
            imageMeta: meta);
    }
}
