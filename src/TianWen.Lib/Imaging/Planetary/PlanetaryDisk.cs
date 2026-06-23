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

    /// <summary>
    /// Intensity-weighted centre of mass of the bright disk over <paramref name="region"/>, on the
    /// luminance proxy with the region mean subtracted (only above-mean pixels contribute, so a uniform
    /// background does not pull the centroid). This is the cheap, every-frame coarse find that removes
    /// bulk drift before sub-pixel phase correlation. It is a <i>relative</i> anchor (consistent
    /// frame-to-frame, which is all registration needs) -- NOT the true geometric centre on a partial
    /// phase (crescent / gibbous): de-rotation's absolute centre must come from the Phase 5/10 limb fit,
    /// never from this. Returns the region centre when there is no signal.
    /// </summary>
    public static (double X, double Y) CenterOfMass(Image frame, Rectangle region)
    {
        if (region.IsEmpty)
        {
            region = LumaProxy.FullFrame(frame);
        }

        var rw = region.Width;
        var rh = region.Height;
        var count = rw * rh;
        var fallback = (region.Left + (rw / 2.0), region.Top + (rh / 2.0));
        if (count == 0)
        {
            return fallback;
        }

        var rented = ArrayPool<float>.Shared.Rent(count);
        try
        {
            var luma = rented.AsSpan(0, count);
            LumaProxy.Fill(frame, region, luma);

            double sum = 0;
            for (var i = 0; i < count; i++)
            {
                sum += luma[i];
            }

            var mean = sum / count;

            double sw = 0, sx = 0, sy = 0;
            for (var yy = 0; yy < rh; yy++)
            {
                var rowBase = yy * rw;
                for (var xx = 0; xx < rw; xx++)
                {
                    var v = luma[rowBase + xx] - mean;
                    if (v > 0)
                    {
                        sw += v;
                        sx += v * (region.Left + xx);
                        sy += v * (region.Top + yy);
                    }
                }
            }

            return sw > 0 ? (sx / sw, sy / sw) : fallback;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Per-pixel "signal confidence" in [0,1] from the luminance proxy: ~1 on the bright disk body, ramping
    /// smoothly to ~0 in the faint surround and sky. Computed once from a reference frame and used to gate
    /// the per-AP "best-of" weighting -- where confidence is high the lucky-imaging local-sharpness weighting
    /// applies; where it is low the integrator falls back to an unbiased mean. This stops the local-sharpness
    /// weight from inflating faint structure: in a low-signal region the weight is highest in exactly the
    /// frames where that region happened to be brightest, so a naive weighted mean drifts toward the bright
    /// realisations and amplifies a real-but-subtle planetary halo into a bright ring. <paramref name="lowFraction"/>
    /// and <paramref name="highFraction"/> are the smoothstep edges as fractions of the background-to-peak
    /// luminance span. The map is in the frame's coordinates (same dimensions), which is the integrator's
    /// output space (frames are warped to this reference).
    /// </summary>
    public static float[,] SignalConfidence(Image reference, float lowFraction = 0.12f, float highFraction = 0.45f)
    {
        int w = reference.Width, h = reference.Height;
        var map = new float[h, w];
        var n = w * h;
        if (n == 0)
        {
            return map;
        }

        var rented = ArrayPool<float>.Shared.Rent(n);
        var sortBuf = ArrayPool<float>.Shared.Rent(n);
        try
        {
            var luma = rented.AsSpan(0, n);
            LumaProxy.Fill(reference, new Rectangle(0, 0, w, h), luma);

            // Most of a planetary frame is sky, so a low percentile is the background and a high percentile
            // is the disk peak -- robust to hot pixels / cosmic hits at the extremes.
            luma.CopyTo(sortBuf.AsSpan(0, n));
            Array.Sort(sortBuf, 0, n);
            var bg = sortBuf[(int)Math.Clamp(0.20 * (n - 1), 0, n - 1)];
            var peak = sortBuf[(int)Math.Clamp(0.995 * (n - 1), 0, n - 1)];
            var span = peak - bg;

            if (span <= 0f)
            {
                // No contrast: trust every pixel equally (the gate becomes a no-op -> uniform mean).
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        map[y, x] = 1f;
                    }
                }

                return map;
            }

            var edge0 = bg + (lowFraction * span);
            var edge1 = bg + (highFraction * span);
            var inv = 1f / MathF.Max(edge1 - edge0, 1e-6f);
            for (var y = 0; y < h; y++)
            {
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    var t = Math.Clamp((luma[row + x] - edge0) * inv, 0f, 1f);
                    map[y, x] = t * t * (3f - (2f * t)); // smoothstep
                }
            }

            return map;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
            ArrayPool<float>.Shared.Return(sortBuf);
        }
    }
}
