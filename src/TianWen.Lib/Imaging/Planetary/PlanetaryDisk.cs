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
    /// <see cref="BoundingBox"/> at its defaults, whether the frame's planet is cut, by the frame's edge or by a straight line inside
    /// it, or missing from it (<see cref="FrameGrader.IsCutOrEmpty"/>), and how elongated its light lies (<see cref="Elongation"/>,
    /// NaN with no planet), from the frame's luminance: the grader asks all three of every frame.
    /// </summary>
    /// <remarks>
    /// Every frame of every stack is graded through this, and a live stack must grade each frame a fast capture sends (#1174), so it
    /// reads a mono frame's channel as its luminance (which <see cref="LumaProxy.Fill"/> would only copy) and finds the box in the cut
    /// test's own scan: the same box as <see cref="BoundingBox"/> and the same answer, in two scans of a mono frame where the separate
    /// passes took a copy and four.
    /// </remarks>
    internal static (PixelRect Box, bool CutOrEmpty, float Elongation) BoundingBoxAndCut(Image frame)
    {
        int w = frame.Width, h = frame.Height;
        var n = w * h;
        if (n == 0)
        {
            return (new PixelRect(0, 0, w, h), true, float.NaN);
        }
        if (frame.ChannelCount == 1)
        {
            var channel = frame.GetChannelSpan(0);
            return BoxAndCut(channel, w, h);
        }
        using var rented = ArrayPoolHelper.Rent<float>(n);
        var luma = rented.AsSpan(0, n);
        LumaProxy.Fill(frame, new PixelRect(0, 0, w, h), luma);
        return BoxAndCut(luma, w, h);
    }

    // BoxOf at BoundingBox's defaults (pad 4, every bright pixel counted, 16 to make a disk) and the cut test, from one scan of the
    // luminance: the extent is taken of every pixel the scan passes above the level, and each one not yet in a blob starts a walk.
    // The planet is the largest blob above the level, its pixels joined by their edges; the frame's edge cuts it when the blob
    // reaches the edge, and a blob under PlanetPixels is no planet. A whole one is still cut when a straight line inside the frame cuts
    // it (CutInside). The planet's elongation is read last, from the same buffers (Elongation).
    private static (PixelRect Box, bool CutOrEmpty, float Elongation) BoxAndCut(ReadOnlySpan<float> luma, int width, int height)
    {
        var (mean, deviation) = MeanAndDeviation(luma);
        var level = (float)(mean + (3.0 * deviation));
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
        var cut = largest < PlanetPixels || largestTouches || CutInside(luma, width, height, (float)mean, (float)(mean + deviation), seen, queue);
        var elongation = ElongationOf(luma, width, height, mean, seen, queue);
        if (bright < minPixels || maxX < minX || maxY < minY)
        {
            return (new PixelRect(0, 0, width, height), cut, elongation);
        }
        return (PixelRect.FromLTRB(Math.Max(0, minX - pad), Math.Max(0, minY - pad), Math.Min(width - 1, maxX + pad) + 1, Math.Min(height - 1, maxY + pad) + 1), cut, elongation);
    }

    /// <summary>
    /// How much longer than wide <paramref name="frame"/>'s planet lies: the square root of the ratio of the largest to the smallest
    /// variance of its pixels' positions, over its largest blob above <see cref="SmearLevel"/> of the way from the frame's mean to its
    /// <see cref="SmearPeakQuantile"/> brightness (pixels joined by their edges); NaN when the blob holds fewer than
    /// <see cref="PlanetPixels"/>. A planet's shape is fixed over a run, so a frame far longer than the run's typical one was taken
    /// while the telescope MOVED (#1300, <see cref="FrameGrader.SmearRatio"/>).
    /// </summary>
    /// <remarks>
    /// Read at a level set from the planet's own brightness, never at the mean plus deviations the cut test floods at: a large disk
    /// raises the frame's spread so far that three deviations above its mean lie above the disk itself (the 678MC and 12-inch SCT
    /// Jupiters held no pixel there). Measured over five captures (2026-10-06): the four on tracked mounts keep every frame within
    /// 1.06 of their median elongation (1.13 for Jupiter's disk, 2.27 to 2.63 for Saturn's rings); the owner's untracked Dobsonian of
    /// 2021-08-01 holds 25 frames past 1.5 times, up to 2.63, the frames taken as the scope moved.
    /// </remarks>
    public static float Elongation(Image frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return BoundingBoxAndCut(frame).Elongation;
    }

    /// <summary>The share of the way from a frame's mean to its <see cref="SmearPeakQuantile"/> brightness its planet's
    /// <see cref="Elongation"/> is read above.</summary>
    public const double SmearLevel = 0.3;

    /// <summary>The quantile of a frame's luminance taken as its planet's peak for <see cref="Elongation"/>: past a hot pixel or two.</summary>
    public const double SmearPeakQuantile = 0.999;

    // Elongation from the frame's own buffers: the largest blob above the level, its pixels' position moments gathered as it is walked.
    private static float ElongationOf(ReadOnlySpan<float> luma, int width, int height, double mean, Span<bool> seen, Span<int> queue)
    {
        var peak = Quantile(luma, SmearPeakQuantile);
        if (!(peak > mean))
        {
            return float.NaN;
        }
        var level = (float)(mean + (SmearLevel * (peak - mean)));
        seen.Clear();
        var n = width * height;
        var (largest, sx, sy, sxx, syy, sxy) = (0, 0.0, 0.0, 0.0, 0.0, 0.0);
        for (var start = 0; start < n; start++)
        {
            if (seen[start] || !(luma[start] > level))
            {
                continue;
            }
            var (head, tail) = (0, 0);
            var (bx, by, bxx, byy, bxy) = (0.0, 0.0, 0.0, 0.0, 0.0);
            seen[start] = true;
            queue[tail++] = start;
            while (head < tail)
            {
                var index = queue[head++];
                var (x, y) = (index % width, index / width);
                bx += x;
                by += y;
                bxx += (double)x * x;
                byy += (double)y * y;
                bxy += (double)x * y;
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
                (largest, sx, sy, sxx, syy, sxy) = (tail, bx, by, bxx, byy, bxy);
            }
        }
        if (largest < PlanetPixels)
        {
            return float.NaN;
        }
        var (mx, my) = (sx / largest, sy / largest);
        var (cxx, cyy, cxy) = ((sxx / largest) - (mx * mx), (syy / largest) - (my * my), (sxy / largest) - (mx * my));
        var half = (cxx + cyy) / 2;
        var spread = Math.Sqrt(Math.Max((half * half) - ((cxx * cyy) - (cxy * cxy)), 0));
        var (major, minor) = (half + spread, half - spread);
        return minor > 0 ? (float)Math.Sqrt(major / minor) : float.PositiveInfinity;
    }

    /// <summary>
    /// Where a planetary master holds light: the bounding box of every pixel at least <see cref="FootprintLevel"/> of the way from its
    /// sky (its luminance's tenth percentile) to its peak (<see cref="SmearPeakQuantile"/>) with four such neighbours of eight, the
    /// planet, its rings, its moons and its halo, padded by <see cref="FootprintPad"/> and clamped to the frame; the whole frame when its
    /// light cannot be told from the sky, so a crop then keeps all of it. A crop to where the frames reached never cuts it
    /// (<see cref="PlanetaryMaster.CropToCovered"/>, #1300).
    /// </summary>
    public static PixelRect Footprint(Image master)
    {
        ArgumentNullException.ThrowIfNull(master);
        int w = master.Width, h = master.Height;
        var n = w * h;
        if (n == 0)
        {
            return PixelRect.Empty;
        }
        using var rented = ArrayPoolHelper.Rent<float>(n);
        var luma = rented.AsSpan(0, n);
        LumaProxy.Fill(master, new PixelRect(0, 0, w, h), luma);
        var (sky, peak) = (Quantile(luma, 0.1), Quantile(luma, SmearPeakQuantile));
        return peak > sky
            ? BoxOf(luma, w, h, (float)(sky + (FootprintLevel * (peak - sky))), FootprintPad, minNeighbours: 4, PlanetPixels)
            : new PixelRect(0, 0, w, h);
    }

    /// <summary>The share of the way from a master's sky to its peak that <see cref="Footprint"/> counts as light.</summary>
    public const double FootprintLevel = 0.02;

    /// <summary>The pixels <see cref="Footprint"/> keeps around the light it finds.</summary>
    public const int FootprintPad = 8;

    // The value below which `quantile` of the luminance lies, to a 1,024th of its range: a histogram, two passes, no sort.
    private static double Quantile(ReadOnlySpan<float> luma, double quantile)
    {
        var (min, max, count) = (float.PositiveInfinity, float.NegativeInfinity, 0);
        foreach (var v in luma)
        {
            if (float.IsNaN(v))
            {
                continue;
            }
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            count++;
        }
        if (!(max > min))
        {
            return max;
        }
        const int bins = 1024;
        Span<int> counts = stackalloc int[bins];
        counts.Clear();
        var scale = (bins - 1) / (double)(max - min);
        foreach (var v in luma)
        {
            if (!float.IsNaN(v))
            {
                counts[(int)((v - min) * scale)]++;
            }
        }
        var above = (long)Math.Floor((1 - quantile) * count);
        long seenAbove = 0;
        for (var bin = bins - 1; bin > 0; bin--)
        {
            seenAbove += counts[bin];
            if (seenAbove > above)
            {
                return min + (bin / scale);
            }
        }
        return min;
    }

    // A planet the CAMERA cut, which PIPP's crop then moved away from the frame's edge (#1291): the cut lies inside the frame, a straight
    // row or column of the planet's own light with the dark beyond it, and the blob above never reaches the edge. An untracked
    // Dobsonian's planet drifts off the sensor, and the owner's 2021-08-19 Saturn holds about 250 such frames in 5,572; the gradient took
    // the straight cut for the sharpest edge in the run and made one the reference of every stack, which then carried the cut as a seam.
    // No limb steps from the dark into the planet in one pixel along a line, since the blur spreads every limb over pixels. So the
    // planet is flooded again at a lower level, `lit` (one deviation above the frame's mean, which a cut through a ring's dimmer light
    // still reaches where three do not), and each of its pixels whose neighbour lies at or below `dark` (the frame's mean) is a step out
    // of the dark, counted along its row (the neighbour above, or the one below) and its column (left, or right). The planet is cut when
    // one line holds InteriorCutPixels such steps and InteriorCutFraction of the planet's extent across that line. Measured on ten real
    // captures (2026-10-06): no whole frame of any reached the fraction but one corrupt readout, the cut runs' frames did, and a whole
    // disk of three pixels' radius made at most six steps in a line, which the floor of eight leaves whole.
    private static bool CutInside(ReadOnlySpan<float> luma, int width, int height, float dark, float lit, Span<bool> seen, Span<int> queue)
    {
        // A line holds no more of the planet's steps than of every lit pixel's, so a frame with no line of InteriorCutPixels steps among
        // all of them (a whole planet over its sky) is answered in one pass, without the flood.
        if (!AnyLineSteps(luma, width, height, dark, lit))
        {
            return false;
        }
        seen.Clear();
        var n = width * height;
        // Every blob above `lit`, its pixels kept in the queue one blob after another, so the largest is a slice of it.
        var (tail, largestStart, largestEnd) = (0, 0, 0);
        var (acrossLargest, downLargest) = (0, 0);
        for (var start = 0; start < n; start++)
        {
            if (seen[start] || !(luma[start] > lit))
            {
                continue;
            }
            var (head, blobStart) = (tail, tail);
            int minX = width, minY = height, maxX = -1, maxY = -1;
            seen[start] = true;
            queue[tail++] = start;
            while (head < tail)
            {
                var index = queue[head++];
                var (x, y) = (index % width, index / width);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (x > 0 && !seen[index - 1] && luma[index - 1] > lit)
                {
                    seen[index - 1] = true;
                    queue[tail++] = index - 1;
                }
                if (x < width - 1 && !seen[index + 1] && luma[index + 1] > lit)
                {
                    seen[index + 1] = true;
                    queue[tail++] = index + 1;
                }
                if (y > 0 && !seen[index - width] && luma[index - width] > lit)
                {
                    seen[index - width] = true;
                    queue[tail++] = index - width;
                }
                if (y < height - 1 && !seen[index + width] && luma[index + width] > lit)
                {
                    seen[index + width] = true;
                    queue[tail++] = index + width;
                }
            }
            if (tail - blobStart > largestEnd - largestStart)
            {
                (largestStart, largestEnd) = (blobStart, tail);
                (acrossLargest, downLargest) = (maxX - minX + 1, maxY - minY + 1);
            }
        }
        if (largestEnd - largestStart < PlanetPixels)
        {
            return false;
        }

        // The steps out of the dark along each row, the dark above in the first half and below in the second, and along each column, the
        // dark to the left and to the right.
        using var rentedRows = ArrayPoolHelper.Rent<int>(2 * height);
        var rows = rentedRows.AsSpan(0, 2 * height);
        rows.Clear();
        using var rentedColumns = ArrayPoolHelper.Rent<int>(2 * width);
        var columns = rentedColumns.AsSpan(0, 2 * width);
        columns.Clear();
        foreach (var index in queue[largestStart..largestEnd])
        {
            var (x, y) = (index % width, index / width);
            if (y > 0 && luma[index - width] <= dark)
            {
                rows[y]++;
            }
            if (y < height - 1 && luma[index + width] <= dark)
            {
                rows[height + y]++;
            }
            if (x > 0 && luma[index - 1] <= dark)
            {
                columns[x]++;
            }
            if (x < width - 1 && luma[index + 1] <= dark)
            {
                columns[width + x]++;
            }
        }
        return AnyLineHolds(rows, acrossLargest) || AnyLineHolds(columns, downLargest);

        static bool AnyLineHolds(ReadOnlySpan<int> steps, int extent)
        {
            var needed = Math.Max(InteriorCutPixels, InteriorCutFraction * extent);
            foreach (var count in steps)
            {
                if (count >= needed)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // Whether any row or column holds InteriorCutPixels steps out of the dark among every pixel above `lit`, the planet's or not: CutInside's
    // count without its flood. Rows are answered as they are read; columns add up across the rows.
    private static bool AnyLineSteps(ReadOnlySpan<float> luma, int width, int height, float dark, float lit)
    {
        using var rentedColumns = ArrayPoolHelper.Rent<int>(2 * width);
        var columns = rentedColumns.AsSpan(0, 2 * width);
        columns.Clear();
        for (var y = 0; y < height; y++)
        {
            var row = luma.Slice(y * width, width);
            var above = y > 0 ? luma.Slice((y - 1) * width, width) : default;
            var below = y < height - 1 ? luma.Slice((y + 1) * width, width) : default;
            var (up, down) = (0, 0);
            for (var x = 0; x < width; x++)
            {
                if (!(row[x] > lit))
                {
                    continue;
                }
                if (!above.IsEmpty && above[x] <= dark)
                {
                    up++;
                }
                if (!below.IsEmpty && below[x] <= dark)
                {
                    down++;
                }
                if (x > 0 && row[x - 1] <= dark)
                {
                    columns[x]++;
                }
                if (x < width - 1 && row[x + 1] <= dark)
                {
                    columns[width + x]++;
                }
            }
            if (up >= InteriorCutPixels || down >= InteriorCutPixels)
            {
                return true;
            }
        }
        foreach (var count in columns)
        {
            if (count >= InteriorCutPixels)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The steps out of the dark one line inside the frame must hold before it cuts the planet (#1291).</summary>
    public const int InteriorCutPixels = 8;

    /// <summary>The share of the planet's extent across a line inside the frame that the line's steps out of the dark must reach before it
    /// cuts the planet (#1291).</summary>
    public const double InteriorCutFraction = 0.3;

    // The mean of the luminance plus `sigma` of its standard deviations.
    private static float Threshold(ReadOnlySpan<float> luma, double sigma)
    {
        var (mean, deviation) = MeanAndDeviation(luma);
        return (float)(mean + (sigma * deviation));
    }

    // The mean of the luminance and its standard deviation.
    private static (double Mean, double Deviation) MeanAndDeviation(ReadOnlySpan<float> luma)
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
        return (mean, Math.Sqrt(Math.Max(variance, 0)));
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
