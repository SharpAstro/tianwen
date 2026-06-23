using System;
using System.Buffers;
using System.Drawing;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Coarse planet-disk localisation on the luminance proxy. Phase 2 provides the high-signal bounding box
/// used as the quality estimators' disk-mask (the noise-trap mitigation -- never measure sharpness over
/// the noisy background). The precise centre-of-mass + limb/ellipse fit that alignment and de-rotation
/// need is the Phase 4/5 disk tracker; this is the cheap threshold-bbox those build on.
/// </summary>
public static class PlanetaryDisk
{
    /// <summary>
    /// Bounding box of the bright disk: the extent of luminance-proxy pixels above
    /// <c>mean + <paramref name="sigmaAboveBackground"/> * stddev</c>, padded by <paramref name="pad"/> and
    /// clamped to the frame. Falls back to the whole frame when too few bright pixels are found (a safe
    /// default for a low-contrast or empty capture, so grading still runs).
    /// </summary>
    public static Rectangle BoundingBox(Image frame, double sigmaAboveBackground = 3.0, int pad = 4)
    {
        var full = new Rectangle(0, 0, frame.Width, frame.Height);
        int w = frame.Width, h = frame.Height;
        var n = w * h;
        if (n == 0)
        {
            return full;
        }

        var rented = ArrayPool<float>.Shared.Rent(n);
        try
        {
            var luma = rented.AsSpan(0, n);
            LumaProxy.Fill(frame, full, luma);

            double sum = 0, sum2 = 0;
            for (var i = 0; i < n; i++)
            {
                double v = luma[i];
                sum += v;
                sum2 += v * v;
            }

            var mean = sum / n;
            var variance = (sum2 / n) - (mean * mean);
            var std = Math.Sqrt(Math.Max(variance, 0));
            var threshold = (float)(mean + (sigmaAboveBackground * std));

            int minX = w, minY = h, maxX = -1, maxY = -1;
            long bright = 0;
            for (var y = 0; y < h; y++)
            {
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    if (luma[row + x] > threshold)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        bright++;
                    }
                }
            }

            // Too few bright pixels to be a disk -- score the whole frame rather than a noise speck.
            if (bright < 16 || maxX < minX || maxY < minY)
            {
                return full;
            }

            minX = Math.Max(0, minX - pad);
            minY = Math.Max(0, minY - pad);
            maxX = Math.Min(w - 1, maxX + pad);
            maxY = Math.Min(h - 1, maxY + pad);
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }
}
