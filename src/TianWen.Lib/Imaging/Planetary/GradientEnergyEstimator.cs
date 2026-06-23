using System;
using System.Buffers;
using System.Drawing;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Mean squared Sobel gradient over the disk region -- an edge-energy sharpness measure. Sharp detail
/// has strong first-derivative response; seeing blur attenuates it. One pass on the luminance proxy
/// (<see cref="LumaProxy"/>), allocation-light and stateless (parallel-safe). An alternative spatial
/// estimator to <see cref="LaplacianEnergyEstimator"/>; the Laplacian variance is the default because
/// it is the more standard focus measure, but gradient energy is less sensitive to single-pixel noise.
/// <para>
/// <paramref name="normalizeBrightness"/> (default) divides by the mean luminance squared for a
/// brightness-invariant, relative-contrast score.
/// </para>
/// </summary>
public sealed class GradientEnergyEstimator(bool normalizeBrightness = true) : IFrameQualityEstimator
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

            double sumG2 = 0, sumLuma = 0;
            long n = 0;
            for (var y = 1; y < rh - 1; y++)
            {
                var row = y * rw;
                var up = row - rw;
                var dn = row + rw;
                for (var x = 1; x < rw - 1; x++)
                {
                    // Sobel 3x3.
                    var tl = luma[up + x - 1]; var tc = luma[up + x]; var tr = luma[up + x + 1];
                    var ml = luma[row + x - 1]; var mr = luma[row + x + 1];
                    var bl = luma[dn + x - 1]; var bc = luma[dn + x]; var br = luma[dn + x + 1];

                    var gx = (tr + (2f * mr) + br) - (tl + (2f * ml) + bl);
                    var gy = (bl + (2f * bc) + br) - (tl + (2f * tc) + tr);
                    sumG2 += ((double)gx * gx) + ((double)gy * gy);
                    sumLuma += luma[row + x];
                    n++;
                }
            }

            if (n == 0)
            {
                return 0f;
            }

            var energy = sumG2 / n;
            if (normalizeBrightness)
            {
                var meanLuma = sumLuma / n;
                energy /= (meanLuma * meanLuma) + 1e-6;
            }

            return (float)energy;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }
}
