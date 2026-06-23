using System;
using System.Buffers;
using System.Drawing;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Variance of the Laplacian over the disk region -- the classic focus / sharpness measure. A sharp
/// frame has strong second-derivative response (high variance); a seeing-blurred frame is smooth (low
/// variance). One pass on the luminance proxy (<see cref="LumaProxy"/>), allocation-light (a single
/// pooled scratch buffer) and stateless, so frames can be graded in parallel.
/// <para>
/// <paramref name="normalizeBrightness"/> (default) divides the energy by the mean luminance squared, so
/// a frame is not preferred merely for being brighter or for the disk drifting into / out of the region;
/// it makes the score a relative-contrast sharpness. This is the <see cref="IFrameQualityEstimator"/>
/// default estimator.
/// </para>
/// </summary>
public sealed class LaplacianEnergyEstimator(bool normalizeBrightness = true) : IFrameQualityEstimator
{
    /// <inheritdoc/>
    public float Score(Image frame, Rectangle region)
    {
        if (region.IsEmpty)
        {
            region = LumaProxy.FullFrame(frame);
        }

        var rw = region.Width;
        var rh = region.Height;
        if (rw < 3 || rh < 3)
        {
            return 0f;
        }

        var rented = ArrayPool<float>.Shared.Rent(rw * rh);
        try
        {
            var luma = rented.AsSpan(0, rw * rh);
            LumaProxy.Fill(frame, region, luma);

            // Welford-free two-moment accumulation of the discrete Laplacian over interior pixels.
            double sumL = 0, sumL2 = 0, sumLuma = 0;
            long n = 0;
            for (var y = 1; y < rh - 1; y++)
            {
                var row = y * rw;
                for (var x = 1; x < rw - 1; x++)
                {
                    var i = row + x;
                    var c = luma[i];
                    var lap = (4f * c) - luma[i - 1] - luma[i + 1] - luma[i - rw] - luma[i + rw];
                    sumL += lap;
                    sumL2 += (double)lap * lap;
                    sumLuma += c;
                    n++;
                }
            }

            if (n == 0)
            {
                return 0f;
            }

            var meanL = sumL / n;
            var variance = (sumL2 / n) - (meanL * meanL);
            if (variance < 0)
            {
                variance = 0; // guard against tiny negative from float rounding
            }

            if (normalizeBrightness)
            {
                var meanLuma = sumLuma / n;
                variance /= (meanLuma * meanLuma) + 1e-6;
            }

            return (float)variance;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }
}
