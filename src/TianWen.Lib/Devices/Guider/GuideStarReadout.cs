using System;
using System.Collections.Generic;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// What a guide star reads as in one frame, for focusing and setting the exposure by eye (#1915): where it is, how
/// sharp, how strong and whether it clips.
/// </summary>
/// <param name="X">Centroid X, sub-pixel, in frame pixels (0-based).</param>
/// <param name="Y">Centroid Y, sub-pixel.</param>
/// <param name="Hfd">Half-flux diameter in pixels over the star's aperture: the number to minimise while focusing.</param>
/// <param name="Snr">Peak over the background's noise, as the guider's own acquisition reads it.</param>
/// <param name="Flux">Background-subtracted flux in the aperture, in the frame's units.</param>
/// <param name="Peak">The brightest raw pixel in the aperture, in the frame's units (the background is NOT removed).</param>
/// <param name="FullScale">The full scale the saturation is judged against, in the frame's units.</param>
/// <param name="Saturated">Whether <paramref name="Peak"/> reaches the full scale: the star is clipped, and its HFD and centroid read too small.</param>
/// <param name="HProfile">Background-subtracted horizontal cross-section through the centroid, <see cref="GuideStarReadout.Radius"/> either side.</param>
/// <param name="VProfile">The vertical one.</param>
public readonly record struct GuideStarMeasurement(
    double X, double Y, double Hfd, double Snr, double Flux, double Peak, double FullScale, bool Saturated,
    float[] HProfile, float[] VProfile)
{
    /// <summary>The peak as a share of the full scale, 0 to 1 (NaN when the full scale is not known).</summary>
    public double PeakFraction => FullScale > 0 ? Peak / FullScale : double.NaN;
}

/// <summary>
/// Measures ONE guide star in a frame: the one a caller names (a click, or last frame's centroid) or, with none named, the
/// brightest unsaturated isolated one. The centroid, the SNR and the candidate scan are the built-in guider's own
/// (<see cref="GuiderCentroidTracker"/>), never a second implementation, so what is read here is what guiding would lock on.
/// </summary>
public static class GuideStarReadout
{
    /// <summary>Half-width of the aperture and of the profiles, as the guider's search radius.</summary>
    public const int Radius = 16;

    /// <summary>How far from a named position a star is still taken for it: the guider's own star separation.</summary>
    public const int PickRadius = 32;

    /// <summary>A peak at or past this share of the full scale is clipped (the last ADC codes are not linear).</summary>
    public const double SaturationShare = 0.98;

    /// <summary>
    /// A star must have no neighbour of at least a fifth of its flux this close (pixels) to be chosen by auto: a doublet's
    /// centroid sits between its stars and is a poor lock.
    /// </summary>
    private const double IsolationDistance = 2 * Radius;

    private const double IsolationFlux = 0.2;

    /// <summary>
    /// The full scale of <paramref name="image"/>'s samples: the declared sensor full scale, else the depth's, else 1 for a
    /// float frame. Never <see cref="Image.MaxValue"/>, which is only the peak observed.
    /// </summary>
    public static float FullScaleOf(Image image)
    {
        if (image.ImageMeta.SensorFullScaleAdu is { } declared and > 0)
        {
            return declared;
        }
        return image.BitDepth is BitDepth.Float32 ? 1f : (float)((1L << (int)image.BitDepth) - 1);
    }

    /// <summary>
    /// Reads a star of the first plane of <paramref name="image"/> (a guide camera is mono, or its raw mosaic), against its
    /// declared full scale. <paramref name="near"/> names the star, as for <see cref="Measure(float[,], float, ValueTuple{double, double}?)"/>.
    /// </summary>
    public static GuideStarMeasurement? Measure(Image image, (double X, double Y)? near = null)
        => Measure(image.GetChannel(0).Data, FullScaleOf(image), near);

    /// <summary>
    /// Reads a star of <paramref name="frame"/> ([height, width]). With <paramref name="near"/>, the star there: its centroid
    /// is walked to the star's centre (a click a few pixels off lands on it), and a star the walk cannot hold is looked for
    /// among the frame's candidates within <see cref="PickRadius"/>. Without it, the brightest unsaturated isolated star in
    /// the frame, else (every star clipped or crowded) the brightest. Null when no star is there.
    /// </summary>
    public static GuideStarMeasurement? Measure(float[,] frame, float fullScale, (double X, double Y)? near = null)
    {
        var height = frame.GetLength(0);
        var width = frame.GetLength(1);
        var tracker = new GuiderCentroidTracker(Radius);

        if (near is { } at)
        {
            if (TryHold(tracker, frame, width, height, at, out var cx, out var cy))
            {
                return Describe(frame, width, height, cx, cy, fullScale);
            }

            var candidates = tracker.FindStars(frame);
            var best = -1;
            var bestD2 = (double)PickRadius * PickRadius;
            for (var i = 0; i < candidates.Count; i++)
            {
                var dx = candidates[i].X - at.X;
                var dy = candidates[i].Y - at.Y;
                if (dx * dx + dy * dy <= bestD2)
                {
                    bestD2 = dx * dx + dy * dy;
                    best = i;
                }
            }
            return best < 0 ? null : Describe(frame, width, height, candidates[best].X, candidates[best].Y, fullScale);
        }

        var stars = tracker.FindStars(frame);
        if (stars.Count == 0)
        {
            return null;
        }

        // Brightest by flux among those that are not clipped and have no comparable neighbour; failing that, the brightest.
        GuideStarMeasurement? chosen = null;
        var chosenFlux = double.NegativeInfinity;
        for (var i = 0; i < stars.Count; i++)
        {
            var star = stars[i];
            if (star.Flux <= chosenFlux || !IsIsolated(stars, i))
            {
                continue;
            }
            if (Describe(frame, width, height, star.X, star.Y, fullScale) is { Saturated: false } measured
                && !HasCompanion(frame, measured))
            {
                chosen = measured;
                chosenFlux = star.Flux;
            }
        }

        return chosen ?? Describe(frame, width, height, stars[0].X, stars[0].Y, fullScale);
    }

    /// <summary>
    /// <see cref="Measure(float[,], float, ValueTuple{double, double}?)"/> over a flat row-major plane (a preview source's
    /// samples), copied once: for a pick, which is a click or a button and not per frame.
    /// </summary>
    public static GuideStarMeasurement? Measure(ReadOnlySpan<float> plane, int width, int height, float fullScale, (double X, double Y)? near = null)
    {
        if (width <= 0 || height <= 0 || plane.Length < width * height)
        {
            return null;
        }
        var frame = new float[height, width];
        plane[..(width * height)].CopyTo(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref frame[0, 0], width * height));
        return Measure(frame, fullScale, near);
    }

    /// <summary>
    /// Follows the star last at <paramref name="last"/> into the next frame, per frame: only the window about it is copied and
    /// read, never the whole frame, and no scan runs when the star is gone (null, so the caller says it is lost and keeps
    /// where it was looking).
    /// </summary>
    public static GuideStarMeasurement? Track(ReadOnlySpan<float> plane, int width, int height, float fullScale, (double X, double Y) last)
    {
        if (width <= 0 || height <= 0 || plane.Length < width * height)
        {
            return null;
        }

        const int half = Radius + 12;
        var x0 = Math.Max(0, (int)Math.Round(last.X) - half);
        var y0 = Math.Max(0, (int)Math.Round(last.Y) - half);
        var x1 = Math.Min(width, (int)Math.Round(last.X) + half + 1);
        var y1 = Math.Min(height, (int)Math.Round(last.Y) + half + 1);
        if (x1 - x0 < 2 * (Radius + 4) || y1 - y0 < 2 * (Radius + 4))
        {
            return null;
        }

        var window = new float[y1 - y0, x1 - x0];
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                window[y - y0, x - x0] = plane[y * width + x];
            }
        }

        var tracker = new GuiderCentroidTracker(Radius);
        if (!TryHold(tracker, window, window.GetLength(1), window.GetLength(0), (last.X - x0, last.Y - y0), out var cx, out var cy)
            || Describe(window, window.GetLength(1), window.GetLength(0), cx, cy, fullScale) is not { } read)
        {
            return null;
        }
        return read with { X = read.X + x0, Y = read.Y + y0 };
    }

    /// <summary>
    /// Whether a second star sits inside the aperture of <paramref name="star"/>: the candidate scan merges peaks closer than the
    /// aperture, so a doublet arrives as ONE candidate whose centroid lies between its stars. A local maximum at least four
    /// pixels from the centroid and over a fifth of the star's own height above the sky is a companion.
    /// </summary>
    private static bool HasCompanion(float[,] frame, GuideStarMeasurement star)
    {
        var height = frame.GetLength(0);
        var width = frame.GetLength(1);
        var px = (int)Math.Round(star.X);
        var py = (int)Math.Round(star.Y);
        var bg = Background(frame, width, height, px, py);
        var floor = bg + ((star.Peak - bg) * IsolationFlux);
        for (var dy = -Radius; dy <= Radius; dy++)
        {
            for (var dx = -Radius; dx <= Radius; dx++)
            {
                var x = px + dx;
                var y = py + dy;
                var rx = x - star.X;
                var ry = y - star.Y;
                if (dx * dx + dy * dy > Radius * Radius || rx * rx + ry * ry < 16 || x < 1 || y < 1 || x >= width - 1 || y >= height - 1)
                {
                    continue;
                }
                var v = frame[y, x];
                if (v >= floor && v > frame[y - 1, x] && v > frame[y + 1, x] && v > frame[y, x - 1] && v > frame[y, x + 1])
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsIsolated(List<(double X, double Y, double Flux, double SNR)> stars, int index)
    {
        var star = stars[index];
        for (var j = 0; j < stars.Count; j++)
        {
            if (j == index || stars[j].Flux < star.Flux * IsolationFlux)
            {
                continue;
            }
            var dx = stars[j].X - star.X;
            var dy = stars[j].Y - star.Y;
            if (dx * dx + dy * dy < IsolationDistance * IsolationDistance)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Walks the centroid from <paramref name="at"/> onto the star's centre; false when no star holds it.</summary>
    private static bool TryHold(GuiderCentroidTracker tracker, float[,] frame, int width, int height,
        (double X, double Y) at, out double cx, out double cy)
    {
        cx = at.X;
        cy = at.Y;
        for (var step = 0; step < 4; step++)
        {
            if (!GuiderCentroidTracker.TryCentroid(frame, width, height, (int)Math.Round(cx), (int)Math.Round(cy), Radius,
                    out var nx, out var ny, out _, out var snr) || snr < tracker.MinSNR)
            {
                return false;
            }
            var moved = Math.Abs(nx - cx) + Math.Abs(ny - cy);
            cx = nx;
            cy = ny;
            if (moved < 0.25)
            {
                return true;
            }
        }
        return true;
    }

    private static GuideStarMeasurement? Describe(float[,] frame, int width, int height, double x, double y, float fullScale)
    {
        var px = (int)Math.Round(x);
        var py = (int)Math.Round(y);
        if (!GuiderCentroidTracker.TryCentroid(frame, width, height, px, py, Radius, out var cx, out var cy, out var flux, out var snr))
        {
            return null;
        }

        var (h, v) = GuiderCentroidTracker.ExtractStarProfile(frame, width, height, (int)Math.Round(cx), (int)Math.Round(cy), Radius);

        var peak = 0.0;
        var bg = Background(frame, width, height, px, py);
        var weighted = 0.0;
        var total = 0.0;
        for (var dy = -Radius; dy <= Radius; dy++)
        {
            for (var dx = -Radius; dx <= Radius; dx++)
            {
                if (dx * dx + dy * dy > Radius * Radius)
                {
                    continue;
                }
                var raw = (double)frame[py + dy, px + dx];
                if (raw > peak)
                {
                    peak = raw;
                }
                var value = raw - bg;
                if (value > 0)
                {
                    var rx = px + dx - cx;
                    var ry = py + dy - cy;
                    weighted += value * Math.Sqrt(rx * rx + ry * ry);
                    total += value;
                }
            }
        }

        var hfd = total > 0 ? 2 * weighted / total : double.NaN;
        return new GuideStarMeasurement(cx, cy, hfd, snr, flux, peak, fullScale, fullScale > 0 && peak >= fullScale * SaturationShare, h, v);
    }

    /// <summary>The annulus mean just outside the aperture, the background <c>TryCentroid</c> takes.</summary>
    private static double Background(float[,] frame, int width, int height, int cx, int cy)
    {
        var sum = 0.0;
        var count = 0;
        var inner2 = Radius * Radius;
        var outer = Radius + 3;
        for (var dy = -outer; dy <= outer; dy++)
        {
            for (var dx = -outer; dx <= outer; dx++)
            {
                var d2 = dx * dx + dy * dy;
                var yy = cy + dy;
                var xx = cx + dx;
                if (d2 > inner2 && d2 <= outer * outer && yy >= 0 && yy < height && xx >= 0 && xx < width)
                {
                    sum += frame[yy, xx];
                    count++;
                }
            }
        }
        return count > 0 ? sum / count : 0;
    }
}
