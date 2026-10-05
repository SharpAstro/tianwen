using System;
using TianWen.Lib.Geometry;

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
    /// <param name="minNeighbours">
    /// When above 0, a bright pixel counts only when at least this many of its 8 neighbours are bright too: a disk is a
    /// blob, and a sensor's noise is not. Needed where a frame can be EMPTY (the planet drifted out of the field): the
    /// threshold of pure noise falls to about one ADU, and a thousand scattered noise pixels then box the whole frame,
    /// which a planetary capture off a Dobsonian did in 25 of 200 sampled frames (the corpus crop, 2026-09-29). 0 keeps the
    /// plain threshold.
    /// </param>
    /// <param name="minPixels">The counted pixels a disk needs; fewer and the frame has none (the whole frame answers).</param>
    public static PixelRect BoundingBox(Image frame, double sigmaAboveBackground = 3.0, int pad = 4, int minNeighbours = 0, int minPixels = 16)
    {
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        int w = frame.Width, h = frame.Height;
        var n = w * h;
        if (n == 0)
        {
            return full;
        }

        using var rented = ArrayPoolHelper.Rent<float>(n);
        var luma = rented.AsSpan(0, n);
        LumaProxy.Fill(frame, full, luma);
        return BoxOf(luma, w, h, Threshold(luma, sigmaAboveBackground), pad, minNeighbours, minPixels);
    }

    /// <summary>
    /// <see cref="BoundingBox"/> at its defaults, and whether the frame's planet is cut by the frame's edge or missing from it
    /// (<see cref="FrameGrader.IsCutOrEmpty"/>), from one pass over the frame's luminance: the grader asks both of every frame.
    /// </summary>
    /// <remarks>
    /// Every frame of every stack is graded through this, and a live stack must grade each frame a fast capture sends (#1174), so it
    /// reads a mono frame's channel as its luminance (which <see cref="LumaProxy.Fill"/> would only copy) and finds the box in the cut
    /// test's own scan: the same box as <see cref="BoundingBox"/> and the same answer, in two scans of a mono frame where the separate
    /// passes took a copy and four.
    /// </remarks>
    internal static (PixelRect Box, bool CutOrEmpty) BoundingBoxAndCut(Image frame)
    {
        int w = frame.Width, h = frame.Height;
        var n = w * h;
        if (n == 0)
        {
            return (new PixelRect(0, 0, w, h), true);
        }
        if (frame.ChannelCount == 1)
        {
            var channel = frame.GetChannelSpan(0);
            return BoxAndCut(channel, w, h, Threshold(channel, 3.0));
        }
        using var rented = ArrayPoolHelper.Rent<float>(n);
        var luma = rented.AsSpan(0, n);
        LumaProxy.Fill(frame, new PixelRect(0, 0, w, h), luma);
        return BoxAndCut(luma, w, h, Threshold(luma, 3.0));
    }

    // BoxOf at BoundingBox's defaults (pad 4, every bright pixel counted, 16 to make a disk) and the cut test, from one scan of the
    // luminance: the extent is taken of every pixel the scan passes above the level, and each one not yet in a blob starts a walk.
    // The planet is the largest blob above the level, its pixels joined by their edges; the frame's edge cuts it when the blob
    // reaches the edge, and a blob under PlanetPixels is no planet.
    private static (PixelRect Box, bool CutOrEmpty) BoxAndCut(ReadOnlySpan<float> luma, int width, int height, float level)
    {
        const int pad = 4;
        const int minPixels = 16;
        var n = width * height;
        using var rentedSeen = ArrayPoolHelper.Rent<bool>(n);
        var seen = rentedSeen.AsSpan(0, n);
        seen.Clear();
        using var rentedQueue = ArrayPoolHelper.Rent<int>(n);
        var queue = rentedQueue.AsSpan(0, n);
        var (largest, largestTouches) = (0, false);
        int minX = width, minY = height, maxX = -1, maxY = -1;
        long bright = 0;
        for (var start = 0; start < n; start++)
        {
            if (!(luma[start] > level))
            {
                continue;
            }
            var (sx, sy) = (start % width, start / width);
            if (sx < minX) minX = sx;
            if (sx > maxX) maxX = sx;
            if (sy < minY) minY = sy;
            if (sy > maxY) maxY = sy;
            bright++;
            if (seen[start])
            {
                continue;
            }
            // One blob, walked breadth first through the queue (each pixel enters it once).
            var (head, tail, touches) = (0, 0, false);
            seen[start] = true;
            queue[tail++] = start;
            while (head < tail)
            {
                var index = queue[head++];
                var (x, y) = (index % width, index / width);
                touches |= x == 0 || y == 0 || x == width - 1 || y == height - 1;
                if (x > 0 && !seen[index - 1] && luma[index - 1] > level)
                {
                    seen[index - 1] = true;
                    queue[tail++] = index - 1;
                }
                if (x < width - 1 && !seen[index + 1] && luma[index + 1] > level)
                {
                    seen[index + 1] = true;
                    queue[tail++] = index + 1;
                }
                if (y > 0 && !seen[index - width] && luma[index - width] > level)
                {
                    seen[index - width] = true;
                    queue[tail++] = index - width;
                }
                if (y < height - 1 && !seen[index + width] && luma[index + width] > level)
                {
                    seen[index + width] = true;
                    queue[tail++] = index + width;
                }
            }
            if (tail > largest)
            {
                (largest, largestTouches) = (tail, touches);
            }
        }
        var cut = largest < PlanetPixels || largestTouches;
        if (bright < minPixels || maxX < minX || maxY < minY)
        {
            return (new PixelRect(0, 0, width, height), cut);
        }
        return (PixelRect.FromLTRB(Math.Max(0, minX - pad), Math.Max(0, minY - pad), Math.Min(width - 1, maxX + pad) + 1, Math.Min(height - 1, maxY + pad) + 1), cut);
    }

    // The mean of the luminance plus `sigma` of its standard deviations.
    private static float Threshold(ReadOnlySpan<float> luma, double sigma)
    {
        double sum = 0, sum2 = 0;
        for (var i = 0; i < luma.Length; i++)
        {
            double v = luma[i];
            sum += v;
            sum2 += v * v;
        }
        var mean = sum / luma.Length;
        var variance = (sum2 / luma.Length) - (mean * mean);
        return (float)(mean + (sigma * Math.Sqrt(Math.Max(variance, 0))));
    }

    /// <summary>The pixels a planet's blob needs (<see cref="FrameGrader.IsCutOrEmpty"/>); fewer and the frame holds none.</summary>
    public const int PlanetPixels = 16;

    // The extent of the luminance above `threshold` (a pixel counting only with `minNeighbours` bright neighbours), padded and
    // clamped to the frame; the whole frame with fewer than `minPixels`.
    private static PixelRect BoxOf(ReadOnlySpan<float> luma, int w, int h, float threshold, int pad, int minNeighbours, int minPixels)
    {
        var full = new PixelRect(0, 0, w, h);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        long bright = 0;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                if (luma[row + x] > threshold && (minNeighbours <= 0 || BrightNeighbours(luma, w, h, x, y, threshold) >= minNeighbours))
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
        if (bright < minPixels || maxX < minX || maxY < minY)
        {
            return full;
        }

        minX = Math.Max(0, minX - pad);
        minY = Math.Max(0, minY - pad);
        maxX = Math.Min(w - 1, maxX + pad);
        maxY = Math.Min(h - 1, maxY + pad);
        return PixelRect.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    // How many of the 8 neighbours of (x, y) are above the threshold; a neighbour off the frame is not.
    private static int BrightNeighbours(ReadOnlySpan<float> luma, int w, int h, int x, int y, float threshold)
    {
        var count = 0;
        for (var dy = -1; dy <= 1; dy++)
        {
            var ny = y + dy;
            if (ny < 0 || ny >= h)
            {
                continue;
            }
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                if ((dx != 0 || dy != 0) && nx >= 0 && nx < w && luma[(ny * w) + nx] > threshold)
                {
                    count++;
                }
            }
        }
        return count;
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
    public static (double X, double Y) CenterOfMass(Image frame, PixelRect region)
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

        using var rented = ArrayPoolHelper.Rent<float>(count);
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

        using var rented = ArrayPoolHelper.Rent<float>(n);
        using var sortBuf = ArrayPoolHelper.Rent<float>(n);
        var luma = rented.AsSpan(0, n);
        LumaProxy.Fill(reference, new PixelRect(0, 0, w, h), luma);

        // Most of a planetary frame is sky, so a low percentile is the background and a high percentile
        // is the disk peak -- robust to hot pixels / cosmic hits at the extremes.
        var sortSpan = sortBuf.AsSpan(0, n);
        luma.CopyTo(sortSpan);
        sortSpan.Sort();
        var bg = sortSpan[(int)Math.Clamp(0.20 * (n - 1), 0, n - 1)];
        var peak = sortSpan[(int)Math.Clamp(0.995 * (n - 1), 0, n - 1)];
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
}
