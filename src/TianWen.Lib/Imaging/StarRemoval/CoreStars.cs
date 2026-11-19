using System;
using System.Collections.Generic;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>A catalogued star placed on a frame: where it lies, in the frame's 0-based pixel coordinates with a pixel's
/// centre at its index (as <see cref="Astrometry.WCS.SkyToPixel"/> gives them), and how bright the catalogue has it.</summary>
public readonly record struct FrameStar(float X, float Y, float Magnitude);

/// <summary>
/// The stars inside a protected core (#2242). <see cref="ExtendedCoreMask"/> puts the INPUT back in the brightest part of
/// a galaxy or nebula, so a nucleus or knot a remover took as a star comes back, and with it every star that sits there:
/// 30 Doradus's cluster, the stars beside M31's nucleus, the Lagoon's two bright ones. What a remover took cannot tell the
/// two apart (two of four models read took M31's nucleus peak whole, exactly as they took the stars around it), and neither
/// can a peak's width. A star catalogue can: of the 17 compact peaks in M31's core 16 are catalogued stars and the one that
/// is not is the nucleus. This finds the catalogued stars the frame shows inside the cores and gives their footprint, which
/// <see cref="ExtendedCoreMask.LeavingOut"/> takes out of what the mask puts back: there the remover's plate stands.
/// </summary>
/// <remarks>
/// <para><b>A peak is a star when the catalogue has one on it that can be it.</b> The compact light is the luminance
/// smoothed by 1 px less its <see cref="Options.MedianPx"/> median. A local peak of it inside a core, over
/// <see cref="Options.PeakSigma"/> sky sigmas, is taken as a star when a catalogued star lies within
/// <see cref="Options.MatchPx"/> of it AND carries at least a tenth of the peak's light: the peak may read no more than
/// <see cref="Options.MagnitudeSlack"/> magnitudes brighter than that star on the frame's own zero point. The second half
/// keeps a nucleus or knot that merely has a faint star on it (the Lagoon's Hourglass reads 6.8 magnitudes over the one
/// star catalogued on it; R136's unresolved centre reads 1.9 over its own and goes with the stars).</para>
/// <para><b>The zero point and the reach are the frame's own.</b> Both are read off the catalogued stars the frame shows
/// away from the cores: the zero point is the median of magnitude plus 2.5 log of the compact peak, and the reach (how far
/// a star of a given magnitude stands over its sky, ring medians down to <see cref="Options.RingSigma"/> sigmas) is a
/// straight line by magnitude through the half-magnitude medians. A star's footprint is a disc of that reach, feathered.
/// The remover's own take is no measure of a footprint inside a core: it takes broad light there too.</para>
/// <para>With too few stars to read a zero point, nothing is taken as a star and the mask is left as it was.</para>
/// </remarks>
public static class CoreStars
{
    /// <summary>How a peak inside a core is told for a catalogued star, and how large its footprint is.</summary>
    /// <param name="MedianPx">The side of the median the compact light is read against.</param>
    /// <param name="PeakSigma">The compact light a peak inside a core needs, in sky sigmas, to be looked at.</param>
    /// <param name="MatchPx">How near a catalogued star must lie to a peak to be it.</param>
    /// <param name="MagnitudeSlack">How many magnitudes brighter than its catalogued star a peak may read and still be that
    /// star (2.5: the star carries at least a tenth of the peak's light).</param>
    /// <param name="RingSigma">A star reaches as far as its ring medians stand this many sky sigmas over its own sky.</param>
    /// <param name="MaxReachPx">The largest reach read or given.</param>
    /// <param name="FeatherPx">The Gaussian the footprints are feathered with.</param>
    /// <param name="CalibrationSigma">The compact peak a catalogued star needs, in sky sigmas, to count toward the zero point.</param>
    /// <param name="MinCalibrationStars">The fewest such stars a zero point is read from.</param>
    public sealed record Options(
        int MedianPx = 15,
        double PeakSigma = 10.0,
        double MatchPx = 1.5,
        double MagnitudeSlack = 2.5,
        double RingSigma = 1.0,
        int MaxReachPx = 48,
        double FeatherPx = 1.5,
        double CalibrationSigma = 20.0,
        int MinCalibrationStars = 20)
    {
        /// <summary>The defaults, read on M31, 30 Doradus and the Lagoon (2026-11-19).</summary>
        public static readonly Options Default = new();
    }

    /// <summary>What was found.</summary>
    /// <param name="Peaks">The compact peaks inside the cores that were looked at.</param>
    /// <param name="Stars">How many of them were taken as catalogued stars and left removed.</param>
    /// <param name="CalibrationStars">The catalogued stars the zero point was read from (0: none could be read, and nothing was left out).</param>
    /// <param name="ZeroPoint">The magnitude of a compact peak of 1 on this frame.</param>
    /// <param name="ZeroPointScatter">Its scatter over those stars, magnitudes.</param>
    /// <param name="ReachPerMagnitude">How the reach changes per magnitude, px (negative: brighter reaches further).</param>
    /// <param name="ReachAtMagnitude10">The reach of a star of magnitude 10, px.</param>
    public readonly record struct Report(
        int Peaks, int Stars, int CalibrationStars, double ZeroPoint, double ZeroPointScatter, double ReachPerMagnitude, double ReachAtMagnitude10);

    private const int IsolationPx = 8;
    private const int ReachIsolationPx = 12;
    private const float CompanionMagnitudes = 2.5f;
    private const int MaxCalibrationStars = 3000;
    private const int MinStarsPerBin = 8;
    private const int LocalPeakPx = 5;

    /// <summary>
    /// The footprint of the catalogued stars <paramref name="input"/> shows inside <paramref name="mask"/>'s cores: per
    /// pixel, row-major, 1 on a star, 0 away from every star, feathered between. Null when the mask has no core or no
    /// zero point could be read (see <see cref="Report.CalibrationStars"/>).
    /// </summary>
    public static (float[]? Footprint, Report Report) Find(Image input, ExtendedCoreMask mask, IReadOnlyList<FrameStar> stars, Options? options = null)
    {
        options ??= Options.Default;
        var (channels, width, height) = input.Shape;
        if (width != mask.Width || height != mask.Height)
        {
            throw new ArgumentException($"the mask is {mask.Width}x{mask.Height} and the input {width}x{height}");
        }
        var n = width * height;
        var sigma = mask.SkySigma;
        if (mask.Cores.Length == 0 || stars.Count == 0 || !(sigma > 0))
        {
            return (null, default);
        }

        var luminance = new float[n];
        var present = new bool[n];
        Array.Fill(present, true);
        var peak = float.NegativeInfinity;
        for (var c = 0; c < channels; c++)
        {
            var span = input.GetChannelSpan(c);
            for (var i = 0; i < n; i++)
            {
                if (float.IsFinite(span[i]))
                {
                    luminance[i] += span[i] / channels;
                }
                else
                {
                    present[i] = false;
                }
            }
        }
        for (var i = 0; i < n; i++)
        {
            if (present[i])
            {
                peak = MathF.Max(peak, luminance[i]);
            }
            else
            {
                luminance[i] = 0f;
            }
        }
        var frame = new Frame(luminance, present, width, height, options.MedianPx / 2);
        var grid = new StarGrid(stars, width, height);
        var weight = mask.Weight;
        var reach = options.MaxReachPx;

        // The frame's zero point: the compact peaks of catalogued stars with no companion that could carry a tenth of their
        // light within IsolationPx, away from the cores and from the frame's edge.
        var step = Math.Max(1, stars.Count / (4 * MaxCalibrationStars));
        var calibration = new List<(int Index, float Height)>();
        for (var s = 0; s < stars.Count && calibration.Count < MaxCalibrationStars; s += step)
        {
            var star = stars[s];
            var (sx, sy) = ((int)MathF.Round(star.X), (int)MathF.Round(star.Y));
            if (sx < reach + 2 || sy < reach + 2 || sx >= width - reach - 2 || sy >= height - reach - 2
                || !present[(sy * width) + sx] || weight[(sy * width) + sx] > 0f
                || grid.AnyOther(s, IsolationPx, star.Magnitude + CompanionMagnitudes))
            {
                continue;
            }
            var compact = frame.Compact(sx, sy);
            if (compact > options.CalibrationSigma * sigma && compact < 0.5f * peak)
            {
                calibration.Add((s, compact));
            }
        }
        if (calibration.Count < options.MinCalibrationStars)
        {
            return (null, default);
        }
        var zeroPoints = new float[calibration.Count];
        for (var k = 0; k < zeroPoints.Length; k++)
        {
            zeroPoints[k] = stars[calibration[k].Index].Magnitude + (2.5f * MathF.Log10(calibration[k].Height));
        }
        var zeroPoint = StatisticsHelper.MedianFast(zeroPoints.AsSpan().ToArray());
        var departures = new float[zeroPoints.Length];
        for (var k = 0; k < departures.Length; k++)
        {
            departures[k] = MathF.Abs(zeroPoints[k] - zeroPoint);
        }
        var scatter = 1.4826 * StatisticsHelper.MedianFast(departures);

        // The reach by magnitude: of those stars, the ones with no visible neighbour within ReachIsolationPx.
        var magnitudes = new float[calibration.Count];
        for (var k = 0; k < magnitudes.Length; k++)
        {
            magnitudes[k] = stars[calibration[k].Index].Magnitude;
        }
        // A magnitude past the faintest tenth of the stars the frame shows: a neighbour brighter than this is one it shows.
        var visible = StatisticsHelper.NthSmallest(magnitudes.AsSpan().ToArray(), Math.Min(magnitudes.Length - 1, (int)(0.9 * magnitudes.Length))) + 1f;
        var rings = Rings(reach);
        var reaches = new List<(float Magnitude, int Radius)>();
        Span<float> ring = stackalloc float[8 * (reach + 1)];
        var profile = new float[reach + 1];
        foreach (var (index, _) in calibration)
        {
            if (grid.AnyOther(index, ReachIsolationPx, visible))
            {
                continue;
            }
            var star = stars[index];
            var (sx, sy) = ((int)MathF.Round(star.X), (int)MathF.Round(star.Y));
            for (var r = 0; r <= reach; r++)
            {
                var count = 0;
                foreach (var (dx, dy) in rings[r])
                {
                    var i = ((sy + dy) * width) + sx + dx;
                    if (present[i])
                    {
                        ring[count++] = luminance[i];
                    }
                }
                profile[r] = count > 0 ? StatisticsHelper.MedianFast(ring[..count]) : float.NaN;
            }
            // The star's own sky: the middle one of its three outermost rings.
            var (a, b, c) = (profile[reach - 2], profile[reach - 1], profile[reach]);
            var sky = MathF.Max(MathF.Min(a, b), MathF.Min(MathF.Max(a, b), c));
            var radius = reach;
            for (var r = 0; r <= reach; r++)
            {
                if (profile[r] - sky < options.RingSigma * sigma)
                {
                    radius = r;
                    break;
                }
            }
            reaches.Add((star.Magnitude, radius));
        }
        var (slope, intercept) = ReachLine(reaches);
        double Reach(double magnitude) => Math.Clamp(intercept + (slope * magnitude), 2.0, reach);

        // The compact peaks inside the cores, and which of them the catalogue has a star for.
        var compactMap = new Dictionary<int, float>();
        float CompactAt(int x, int y)
        {
            var i = (y * width) + x;
            if (!compactMap.TryGetValue(i, out var value))
            {
                value = present[i] ? frame.Compact(x, y) : float.NegativeInfinity;
                compactMap[i] = value;
            }
            return value;
        }
        const int half = LocalPeakPx / 2;
        var found = new List<(int X, int Y, float Height, bool Star, int Radius)>();
        for (var y = half; y < height - half; y++)
        {
            for (var x = half; x < width - half; x++)
            {
                if (!(weight[(y * width) + x] > 0f))
                {
                    continue;
                }
                var here = CompactAt(x, y);
                if (!(here > options.PeakSigma * sigma))
                {
                    continue;
                }
                var highest = true;
                for (var dy = -half; dy <= half && highest; dy++)
                {
                    for (var dx = -half; dx <= half; dx++)
                    {
                        // A tie goes to the first in reading order, so a flat top is one peak.
                        var other = CompactAt(x + dx, y + dy);
                        if (other > here || (other == here && (dy < 0 || (dy == 0 && dx < 0))))
                        {
                            highest = false;
                            break;
                        }
                    }
                }
                if (!highest)
                {
                    continue;
                }
                var seen = zeroPoint - (2.5 * Math.Log10(here));
                if (grid.BrightestWithin(x, y, (float)options.MatchPx) is { } catalogued && seen >= catalogued - options.MagnitudeSlack)
                {
                    // The brighter of what is seen and what is catalogued: a clipped star reads fainter than it is.
                    found.Add((x, y, here, true, (int)Math.Round(Reach(Math.Min(seen, catalogued))) + 1));
                }
                else
                {
                    found.Add((x, y, here, false, 0));
                }
            }
        }

        // Feathered OUTWARD, as the core mask is: each disc is grown by the feather's reach first, so the ramp lies outside
        // the star's own reach and the remover's plate stands whole across it.
        var grow = (int)Math.Ceiling(options.FeatherPx);
        // The peaks the catalogue has no star for, each with the reach a star of its height would have. Where a star's
        // disc meets one that stands at least as high, a pixel goes to the nearer of the two in units of their reach: the
        // nucleus beside a bright star keeps its own side whole, a faint blip in a bright star's disc stays out with it.
        var kept = new List<(int X, int Y, float Height, int Radius)>();
        foreach (var (x, y, peakHeight, isStar, _) in found)
        {
            if (!isStar)
            {
                kept.Add((x, y, peakHeight, (int)Math.Round(Reach(zeroPoint - (2.5 * Math.Log10(peakHeight)))) + 1 + grow));
            }
        }
        var footprint = new float[n];
        var (minX, minY, maxX, maxY) = (width, height, -1, -1);
        var starCount = 0;
        var rivals = new List<(int X, int Y, int Radius)>();
        foreach (var (x, y, peakHeight, isStar, reached) in found)
        {
            if (!isStar)
            {
                continue;
            }
            var radius = reached + grow;
            starCount++;
            rivals.Clear();
            foreach (var (kx, ky, keptHeight, keptRadius) in kept)
            {
                var reachBoth = radius + keptRadius;
                if (keptHeight >= peakHeight && ((kx - x) * (kx - x)) + ((ky - y) * (ky - y)) < reachBoth * reachBoth)
                {
                    rivals.Add((kx, ky, keptRadius));
                }
            }
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var (px, py) = (x + dx, y + dy);
                    var d2 = (dx * dx) + (dy * dy);
                    if (px < 0 || py < 0 || px >= width || py >= height || d2 > radius * radius)
                    {
                        continue;
                    }
                    var own = true;
                    foreach (var (kx, ky, keptRadius) in rivals)
                    {
                        // Nearer to the kept peak, in units of each one's reach: (d / radius) > (dk / keptRadius), squared.
                        var k2 = ((px - kx) * (px - kx)) + ((py - ky) * (py - ky));
                        if ((long)d2 * keptRadius * keptRadius >= (long)k2 * radius * radius)
                        {
                            own = false;
                            break;
                        }
                    }
                    if (own)
                    {
                        footprint[(py * width) + px] = 1f;
                    }
                }
            }
            (minX, minY, maxX, maxY) = (Math.Min(minX, x - radius), Math.Min(minY, y - radius), Math.Max(maxX, x + radius), Math.Max(maxY, y + radius));
        }
        var report = new Report(found.Count, starCount, calibration.Count, zeroPoint, scatter, slope, Reach(10.0));
        if (starCount == 0)
        {
            return (null, report);
        }
        Feather(footprint, width, height, options.FeatherPx, minX, minY, maxX, maxY);
        return (footprint, report);
    }

    // A straight line through the medians of the reach in half-magnitude bins; one level when fewer than two bins hold
    // enough stars.
    private static (double Slope, double Intercept) ReachLine(List<(float Magnitude, int Radius)> reaches)
    {
        if (reaches.Count == 0)
        {
            return (0, 3);
        }
        var bins = new SortedDictionary<int, List<float>>();
        foreach (var (magnitude, radius) in reaches)
        {
            var bin = (int)Math.Floor(magnitude * 2.0);
            if (!bins.TryGetValue(bin, out var list))
            {
                bins[bin] = list = [];
            }
            list.Add(radius);
        }
        var points = new List<(double X, double Y)>();
        foreach (var (bin, list) in bins)
        {
            if (list.Count >= MinStarsPerBin)
            {
                points.Add(((bin / 2.0) + 0.25, StatisticsHelper.MedianFast(list.ToArray())));
            }
        }
        if (points.Count < 2)
        {
            var all = new float[reaches.Count];
            for (var k = 0; k < all.Length; k++)
            {
                all[k] = reaches[k].Radius;
            }
            return (0, StatisticsHelper.MedianFast(all));
        }
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y) in points)
        {
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
        }
        var count = points.Count;
        var slope = ((count * sxy) - (sx * sy)) / ((count * sxx) - (sx * sx));
        return (slope, (sy - (slope * sx)) / count);
    }

    // The offsets of each ring of radius r (r - 0.5 up to r + 0.5 from the centre), r from 0 to reach.
    private static (int Dx, int Dy)[][] Rings(int reach)
    {
        var rings = new List<(int, int)>[reach + 1];
        for (var r = 0; r <= reach; r++)
        {
            rings[r] = [];
        }
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var r = (int)Math.Floor(Math.Sqrt((dx * dx) + (dy * dy)) + 0.5);
                if (r <= reach)
                {
                    rings[r].Add((dx, dy));
                }
            }
        }
        var result = new (int, int)[reach + 1][];
        for (var r = 0; r <= reach; r++)
        {
            result[r] = [.. rings[r]];
        }
        return result;
    }

    // A Gaussian of the given sigma over the box that holds the footprints (grown by its reach), clamped to [0, 1].
    private static void Feather(float[] footprint, int width, int height, double sigma, int minX, int minY, int maxX, int maxY)
    {
        if (!(sigma > 0))
        {
            return;
        }
        var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new float[(2 * radius) + 1];
        var total = 0f;
        for (var k = -radius; k <= radius; k++)
        {
            total += kernel[k + radius] = (float)Math.Exp(-(k * k) / (2 * sigma * sigma));
        }
        for (var k = 0; k < kernel.Length; k++)
        {
            kernel[k] /= total;
        }
        (minX, minY) = (Math.Max(0, minX - (2 * radius)), Math.Max(0, minY - (2 * radius)));
        (maxX, maxY) = (Math.Min(width - 1, maxX + (2 * radius)), Math.Min(height - 1, maxY + (2 * radius)));
        var (boxWidth, boxHeight) = (maxX - minX + 1, maxY - minY + 1);
        var rows = new float[boxWidth * boxHeight];
        for (var y = 0; y < boxHeight; y++)
        {
            for (var x = 0; x < boxWidth; x++)
            {
                var sum = 0f;
                for (var k = -radius; k <= radius; k++)
                {
                    var px = Math.Clamp(minX + x + k, 0, width - 1);
                    sum += kernel[k + radius] * footprint[((minY + y) * width) + px];
                }
                rows[(y * boxWidth) + x] = sum;
            }
        }
        for (var y = 0; y < boxHeight; y++)
        {
            for (var x = 0; x < boxWidth; x++)
            {
                var sum = 0f;
                for (var k = -radius; k <= radius; k++)
                {
                    var py = Math.Clamp(y + k, 0, boxHeight - 1);
                    sum += kernel[k + radius] * rows[(py * boxWidth) + x];
                }
                footprint[((minY + y) * width) + minX + x] = Math.Clamp(sum, 0f, 1f);
            }
        }
    }

    // The luminance, with the compact light at a pixel: the luminance smoothed by a Gaussian of 1 px less the median of
    // the present pixels in the window about it.
    private sealed class Frame(float[] luminance, bool[] present, int width, int height, int medianHalf)
    {
        private static readonly float[] Smooth = SmoothKernel();

        private static float[] SmoothKernel()
        {
            var kernel = new float[7];
            var total = 0f;
            for (var k = -3; k <= 3; k++)
            {
                total += kernel[k + 3] = MathF.Exp(-(k * k) / 2f);
            }
            for (var k = 0; k < kernel.Length; k++)
            {
                kernel[k] /= total;
            }
            return kernel;
        }

        public float Compact(int x, int y)
        {
            var smooth = 0f;
            var weight = 0f;
            for (var dy = -3; dy <= 3; dy++)
            {
                var py = y + dy;
                if (py < 0 || py >= height)
                {
                    continue;
                }
                for (var dx = -3; dx <= 3; dx++)
                {
                    var px = x + dx;
                    if (px >= 0 && px < width && present[(py * width) + px])
                    {
                        var w = Smooth[dy + 3] * Smooth[dx + 3];
                        smooth += w * luminance[(py * width) + px];
                        weight += w;
                    }
                }
            }
            if (!(weight > 0f))
            {
                return float.NegativeInfinity;
            }
            var side = (2 * medianHalf) + 1;
            Span<float> window = stackalloc float[side * side];
            var count = 0;
            for (var py = Math.Max(0, y - medianHalf); py <= Math.Min(height - 1, y + medianHalf); py++)
            {
                for (var px = Math.Max(0, x - medianHalf); px <= Math.Min(width - 1, x + medianHalf); px++)
                {
                    if (present[(py * width) + px])
                    {
                        window[count++] = luminance[(py * width) + px];
                    }
                }
            }
            return (smooth / weight) - StatisticsHelper.MedianFast(window[..count]);
        }
    }

    // The catalogued stars in cells of 16 px, for the neighbours of a place.
    private sealed class StarGrid
    {
        private const int Cell = 16;
        private readonly IReadOnlyList<FrameStar> _stars;
        private readonly int _columns;
        private readonly int _rows;
        private readonly int[] _start;
        private readonly int[] _order;

        public StarGrid(IReadOnlyList<FrameStar> stars, int width, int height)
        {
            _stars = stars;
            _columns = (width / Cell) + 1;
            _rows = (height / Cell) + 1;
            _start = new int[(_columns * _rows) + 1];
            var cellOf = new int[stars.Count];
            for (var s = 0; s < stars.Count; s++)
            {
                cellOf[s] = CellOf(stars[s].X, stars[s].Y);
                if (cellOf[s] >= 0)
                {
                    _start[cellOf[s] + 1]++;
                }
            }
            for (var c = 0; c < _columns * _rows; c++)
            {
                _start[c + 1] += _start[c];
            }
            _order = new int[_start[^1]];
            var next = _start.AsSpan(0, _columns * _rows).ToArray();
            for (var s = 0; s < stars.Count; s++)
            {
                if (cellOf[s] >= 0)
                {
                    _order[next[cellOf[s]]++] = s;
                }
            }
        }

        private int CellOf(float x, float y)
        {
            var (cx, cy) = ((int)MathF.Floor(x / Cell), (int)MathF.Floor(y / Cell));
            return cx < 0 || cy < 0 || cx >= _columns || cy >= _rows ? -1 : (cy * _columns) + cx;
        }

        /// <summary>The magnitude of the brightest star within <paramref name="radius"/> of a place, or null.</summary>
        public float? BrightestWithin(float x, float y, float radius)
        {
            var brightest = float.PositiveInfinity;
            foreach (var s in Near(x, y, radius))
            {
                var star = _stars[s];
                if (((star.X - x) * (star.X - x)) + ((star.Y - y) * (star.Y - y)) <= radius * radius)
                {
                    brightest = MathF.Min(brightest, star.Magnitude);
                }
            }
            return float.IsFinite(brightest) ? brightest : null;
        }

        /// <summary>Whether another star brighter than <paramref name="magnitude"/> lies within <paramref name="radius"/> of star <paramref name="index"/>.</summary>
        public bool AnyOther(int index, float radius, float magnitude)
        {
            var (x, y) = (_stars[index].X, _stars[index].Y);
            foreach (var s in Near(x, y, radius))
            {
                var star = _stars[s];
                if (s != index && star.Magnitude < magnitude && ((star.X - x) * (star.X - x)) + ((star.Y - y) * (star.Y - y)) <= radius * radius)
                {
                    return true;
                }
            }
            return false;
        }

        private IEnumerable<int> Near(float x, float y, float radius)
        {
            var (x0, x1) = (Math.Max(0, (int)MathF.Floor((x - radius) / Cell)), Math.Min(_columns - 1, (int)MathF.Floor((x + radius) / Cell)));
            var (y0, y1) = (Math.Max(0, (int)MathF.Floor((y - radius) / Cell)), Math.Min(_rows - 1, (int)MathF.Floor((y + radius) / Cell)));
            for (var cy = y0; cy <= y1; cy++)
            {
                for (var cx = x0; cx <= x1; cx++)
                {
                    var cell = (cy * _columns) + cx;
                    for (var k = _start[cell]; k < _start[cell + 1]; k++)
                    {
                        yield return _order[k];
                    }
                }
            }
        }
    }
}
