using System;
using System.Collections.Generic;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// The continuum taken out of a narrowband master: a line filter passes a slice of every star's broadband light, so an
/// "Ha" master is the line PLUS that continuum, and added to a colour image as it is it doubles the stars and adds no
/// emission. Subtracting a scaled broadband master on the same grid (<see cref="MasterAlignment"/>) leaves the line:
/// <c>line - k (continuum - median continuum)</c>, which keeps the line's own background (docs/plans/narrowband-colour.md,
/// phase 0, technique E; #874).
/// </summary>
/// <remarks>
/// <para><b>The scale is the flattest residual, solved exactly.</b> Siril's <c>ContinuumSubtraction.py</c> sweeps
/// <c>k</c> and fits a smooth V to the mean absolute deviation of the residual, because the right <c>k</c> cancels every
/// star and so leaves the flattest image. That objective is an L1 fit through one scale, <c>sum |c_i - k d_i|</c> over
/// the two planes taken about their backgrounds, whose minimiser is the weighted median of <c>c_i / d_i</c> by
/// <c>|d_i|</c> (<see cref="StatisticsHelper.WeightedMedian"/>). Line emission the continuum lacks is an outlier to an L1
/// fit, which is why the flattest residual is not pulled by the nebula it is meant to keep. It is read only where the
/// continuum stands out of its noise, with an offset fitted rather than assumed (<see cref="FlattestResidualScale"/> says
/// why each), so it is solved as a least-absolute-deviation line rather than read as one weighted median.</para>
/// <para><b>The stars give a second opinion</b> (<see cref="PhotometricScale"/>, PixInsight's method): the median ratio
/// of each star's flux in the line to its flux in the continuum. It needs both masters' stars at one width, since a star's
/// flux is a fixed-aperture sum that falls as its PSF widens; if the two disagree materially, the pairing or the PSFs
/// are the suspect, not the subtraction.</para>
/// </remarks>
public static class ContinuumSubtractor
{
    /// <summary>The flattest residual's scale; the pixels it was read over (where the continuum stands out); the residual's
    /// and the line's own mean absolute deviation about their medians over those pixels.</summary>
    public readonly record struct Scale(double K, long Pixels, double ResidualAad, double LineAad);

    /// <summary>How far above its background, in its own noise, a continuum pixel must stand to carry the fit.</summary>
    public const double DefaultSignificance = 5.0;

    /// <summary>
    /// The <c>k</c> leaving <c>line - k continuum</c> flattest (least absolute deviation, with an offset of its own), over
    /// the pixels where the continuum stands <paramref name="significance"/> of its noise above its median: its stars and
    /// its continuum nebulosity. The two must be on one grid.
    /// </summary>
    /// <remarks>
    /// <para><b>Only where the continuum is signal, or the background's noise pulls the scale to zero.</b> Over every pixel,
    /// as Siril takes it, most pixels are background, where both planes are noise alone and their ratio is centred on zero:
    /// an errors-in-variables fit, attenuated. On a fixture whose true scale was 0.08 the whole frame read 0.0724 while the
    /// stars read 0.0799.</para>
    /// <para><b>With an offset of its own, never one assumed.</b> Taken about each plane's median instead, the scale read
    /// 0.0669: a nebula that fills much of the frame lifts the median it is measured from. So the fit is
    /// <c>min over k and b of sum |line - k continuum - b|</c>. For a given <c>k</c> the best <c>b</c> is the residual's
    /// median, and what remains is convex in <c>k</c>, so a bracket and a golden-section search find it exactly.</para>
    /// <para>The noise is read from neighbouring pixels' differences, which a smooth nebula does not reach.</para>
    /// </remarks>
    public static Scale FlattestResidualScale(
        Image line, Image continuum, int lineChannel = 0, int continuumChannel = 0, double significance = DefaultSignificance)
    {
        RequireOneGrid(line, continuum);
        var x = line.GetChannelSpan(lineChannel);
        var y = continuum.GetChannelSpan(continuumChannel);
        var continuumMedian = FiniteMedian(y);
        var threshold = significance * NeighbourNoise(y, line.Width);
        if (!double.IsFinite(continuumMedian) || !double.IsFinite(threshold))
        {
            return new Scale(double.NaN, 0, double.NaN, double.NaN);
        }

        var lines = new List<float>();
        var continua = new List<float>();
        for (var i = 0; i < x.Length; i++)
        {
            if (float.IsFinite(x[i]) && float.IsFinite(y[i]) && y[i] - continuumMedian > threshold)
            {
                lines.Add(x[i]);
                continua.Add(y[i]);
            }
        }
        if (lines.Count < 3)
        {
            return new Scale(double.NaN, lines.Count, double.NaN, double.NaN);
        }
        var xs = lines.ToArray();
        var ys = continua.ToArray();
        var scratch = new float[xs.Length];

        // A start from the ratios about the medians, then a bracket grown until the objective rises on both sides.
        var lineMedian = FiniteMedian(x);
        var ratios = new float[xs.Length];
        var weights = new float[xs.Length];
        for (var i = 0; i < xs.Length; i++)
        {
            var d = ys[i] - continuumMedian;
            ratios[i] = (float)((xs[i] - lineMedian) / d);
            weights[i] = (float)d;
        }
        var start = (double)StatisticsHelper.WeightedMedian(ratios, weights);
        if (!double.IsFinite(start))
        {
            start = 0;
        }
        var step = Math.Max(Math.Abs(start), 1e-6) * 0.5;
        var lo = start - step;
        var hi = start + step;
        for (var grow = 0; grow < 60 && Objective(lo) < Objective(start); grow++)
        {
            step *= 2;
            lo = start - step;
        }
        step = Math.Max(Math.Abs(start), 1e-6) * 0.5;
        for (var grow = 0; grow < 60 && Objective(hi) < Objective(start); grow++)
        {
            step *= 2;
            hi = start + step;
        }

        // Golden section over [lo, hi]: the objective is convex, so it narrows onto the one minimum.
        var ratio = (Math.Sqrt(5.0) - 1) / 2;
        var a = hi - (ratio * (hi - lo));
        var b = lo + (ratio * (hi - lo));
        var fa = Objective(a);
        var fb = Objective(b);
        for (var iteration = 0; iteration < 80 && hi - lo > 1e-9 * Math.Max(1.0, Math.Abs(hi)); iteration++)
        {
            if (fa <= fb)
            {
                hi = b;
                b = a;
                fb = fa;
                a = hi - (ratio * (hi - lo));
                fa = Objective(a);
            }
            else
            {
                lo = a;
                a = b;
                fa = fb;
                b = lo + (ratio * (hi - lo));
                fb = Objective(b);
            }
        }
        var k = (lo + hi) / 2;

        for (var i = 0; i < xs.Length; i++)
        {
            scratch[i] = xs[i];
        }
        var xMedian = StatisticsHelper.MedianFast(scratch);
        double lineAad = 0;
        foreach (var v in xs)
        {
            lineAad += Math.Abs(v - xMedian);
        }
        return new Scale(k, xs.Length, Objective(k) / xs.Length, lineAad / xs.Length);

        // sum |x - k y - median(x - k y)|: the least absolute deviation at this k with the offset that suits it.
        double Objective(double kk)
        {
            for (var i = 0; i < xs.Length; i++)
            {
                scratch[i] = (float)(xs[i] - (kk * ys[i]));
            }
            var offset = StatisticsHelper.MedianFast(scratch);
            double sum = 0;
            for (var i = 0; i < xs.Length; i++)
            {
                sum += Math.Abs(xs[i] - (kk * ys[i]) - offset);
            }
            return sum;
        }
    }

    /// <summary>
    /// The median of each star's line flux over its continuum flux, for stars found in both within
    /// <paramref name="matchRadiusPx"/> of each other (the masters on one grid), and the spread of those ratios (their
    /// interquartile range over the median). NaN with fewer than three matched stars.
    /// </summary>
    public static (double K, int Stars, double Spread) PhotometricScale(StarList line, StarList continuum, float matchRadiusPx = 1.5f)
    {
        var reference = new List<ImagedStar>(continuum.Count);
        foreach (var star in continuum)
        {
            if (star.Flux > 0)
            {
                reference.Add(star);
            }
        }
        var ratios = new List<float>();
        var radiusSquared = matchRadiusPx * matchRadiusPx;
        foreach (var star in line)
        {
            if (star.Flux <= 0)
            {
                continue;
            }
            var best = -1;
            var bestDistance = radiusSquared;
            for (var j = 0; j < reference.Count; j++)
            {
                var dx = reference[j].XCentroid - star.XCentroid;
                var dy = reference[j].YCentroid - star.YCentroid;
                var distance = (dx * dx) + (dy * dy);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = j;
                }
            }
            if (best >= 0)
            {
                ratios.Add(star.Flux / reference[best].Flux);
            }
        }
        if (ratios.Count < 3)
        {
            return (double.NaN, ratios.Count, double.NaN);
        }
        var values = ratios.ToArray();
        var median = StatisticsHelper.NthSmallest(values.AsSpan(), values.Length / 2);
        var lower = StatisticsHelper.NthSmallest(values.AsSpan(), values.Length / 4);
        var upper = StatisticsHelper.NthSmallest(values.AsSpan(), (3 * values.Length) / 4);
        return (median, ratios.Count, (upper - lower) / median);
    }

    /// <summary>
    /// <c>line - k (continuum - median continuum)</c> per channel, NaN where either is absent: the line with its own
    /// background, on <paramref name="line"/>'s scale and metadata. One-channel continua serve every line channel.
    /// </summary>
    public static Image Subtract(Image line, Image continuum, double k)
    {
        RequireOneGrid(line, continuum);
        var (channels, width, height) = line.Shape;
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var x = line.GetChannelSpan(c);
            var y = continuum.GetChannelSpan(Math.Min(c, continuum.ChannelCount - 1));
            var finite = new float[y.Length];
            var n = 0;
            foreach (var v in y)
            {
                if (float.IsFinite(v))
                {
                    finite[n++] = v;
                }
            }
            var median = n > 0 ? StatisticsHelper.MedianFast(finite.AsSpan(0, n)) : 0f;
            var plane = new float[height, width];
            for (var py = 0; py < height; py++)
            {
                var row = py * width;
                for (var px = 0; px < width; px++)
                {
                    var a = x[row + px];
                    var b = y[row + px];
                    plane[py, px] = float.IsFinite(a) && float.IsFinite(b) ? (float)(a - (k * (b - median))) : float.NaN;
                }
            }
            planes[c] = plane;
        }
        return new Image(planes, BitDepth.Float32, line.MaxValue, line.MinValue, line.Pedestal, line.ImageMeta);
    }

    private static double FiniteMedian(ReadOnlySpan<float> values)
    {
        var finite = new float[values.Length];
        var n = 0;
        foreach (var v in values)
        {
            if (float.IsFinite(v))
            {
                finite[n++] = v;
            }
        }
        return n > 0 ? StatisticsHelper.MedianFast(finite.AsSpan(0, n)) : double.NaN;
    }

    // A plane's pixel noise from the differences of horizontal neighbours, which cancel anything smooth: 1.4826 MAD over
    // root 2.
    private static double NeighbourNoise(ReadOnlySpan<float> values, int width)
    {
        var differences = new float[values.Length];
        var n = 0;
        for (var i = 0; i + 1 < values.Length; i++)
        {
            if ((i + 1) % width != 0 && float.IsFinite(values[i]) && float.IsFinite(values[i + 1]))
            {
                differences[n++] = values[i + 1] - values[i];
            }
        }
        if (n == 0)
        {
            return double.NaN;
        }
        var (_, mad) = StatisticsHelper.MedianAndMad(differences.AsSpan(0, n));
        return 1.4826 * mad / Math.Sqrt(2.0);
    }

    private static void RequireOneGrid(Image line, Image continuum)
    {
        if (line.Width != continuum.Width || line.Height != continuum.Height)
        {
            throw new ArgumentException(
                $"the line master is {line.Width}x{line.Height} and the continuum {continuum.Width}x{continuum.Height}: "
                + "put them on one grid first (MasterAlignment, tianwen image align)", nameof(continuum));
        }
    }
}
