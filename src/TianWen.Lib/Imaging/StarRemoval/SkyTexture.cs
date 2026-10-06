using System;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The star-free sky's texture per starlet scale (docs/plans/star-remover-training.md, R2d's D1): what a synthetic
/// background must reproduce under the scales a star lives at. Read on a plate's luminance, away from every source it kept
/// (<see cref="PlateSources"/>, found as the plate builder finds them), its canvas ring and its frame's edge, per scale of
/// the B3 starlet (<see cref="ATrousWaveletTransform"/>, 1 to 32 px) and per background class
/// (<see cref="StarlessSpeckles.TextureEdges"/>).
/// </summary>
/// <remarks>
/// <para><b>The slope.</b> For a field whose power falls as <c>k^-beta</c>, a starlet scale of <c>2^j</c> px holds an energy
/// that goes as <c>2^(j (beta - 2))</c>, so the slope of <c>log2</c> of the scales' energy against <c>j</c> is
/// <c>beta - 2</c>: white noise reads 0 for beta, a Kolmogorov field 11/3. It is fitted over the scales from
/// <see cref="FirstSlopeScale"/> up, the finest ones being the noise's.</para>
/// <para><b>The noise is in it.</b> A master's noise is correlated (a starlet's second scale over its first reads 0.45 on a
/// demosaiced master, 0.31 to 0.33 on a drizzled one, 0.22 on white noise; E16b), and nothing here takes it out: read a fine
/// scale against those ratios before calling it texture.</para>
/// </remarks>
public static class SkyTexture
{
    /// <summary>The starlet scales read: 1, 2, 4, 8, 16 and 32 px.</summary>
    public const int ScaleCount = 6;

    /// <summary>How far round a source the plate kept, in PSF widths, its sky is not read.</summary>
    public const double SourceMaskFwhm = 2.0;

    /// <summary>Pixels within this of the frame's edge or its canvas ring are not read: the coarsest scale's kernel reaches
    /// 2 x 2^5 px.</summary>
    public const int EdgePx = 64;

    /// <summary>The finest scale the slope is fitted from (4 px).</summary>
    public const int FirstSlopeScale = 2;

    /// <summary>One scale's coefficients over the pixels read.</summary>
    /// <param name="ScalePx">The scale, 2^j px.</param>
    /// <param name="Pixels">The pixels read.</param>
    /// <param name="RobustRms">1.4826 times the coefficients' MAD about their median.</param>
    /// <param name="Rms">Their plain RMS.</param>
    /// <param name="Skewness">Their skewness.</param>
    /// <param name="Kurtosis">Their excess kurtosis: 0 for a Gaussian field. A fourth moment, so a few leftovers the finder
    /// does not call sources (a subtracted star's residue, a source under its threshold) set it on a real plate: it read in
    /// the hundreds to thousands at 2 to 8 px on the first six. <paramref name="TailFraction"/> is the robust reading.</param>
    /// <param name="TailFraction">The share of the coefficients more than <see cref="TailSigma"/> robust RMS from their
    /// median: 5.7e-7 for a Gaussian field, more where the sky holds sparse, sharp structure.</param>
    public sealed record ScaleStats(int ScalePx, long Pixels, double RobustRms, double Rms, double Skewness, double Kurtosis, double TailFraction);

    /// <summary>The robust RMS a coefficient is beyond to count in <see cref="ScaleStats.TailFraction"/>.</summary>
    public const double TailSigma = 5.0;

    /// <summary>One background class's scales and slope.</summary>
    public sealed record ClassStats(float MinTexture, float MaxTexture, long Pixels, ImmutableArray<ScaleStats> Scales, double SpectralIndex);

    /// <summary>A plate's sky texture.</summary>
    /// <param name="Fwhm">The PSF width the sources were found with, in pixels.</param>
    /// <param name="Sources">The sources the plate kept, found and masked.</param>
    /// <param name="ReadFraction">The share of the frame read.</param>
    /// <param name="Scales">Every scale over every pixel read.</param>
    /// <param name="SpectralIndex">The power law's index, beta, from the scales' robust energy.</param>
    /// <param name="Classes">The same per background class.</param>
    public sealed record Measurement(double Fwhm, int Sources, double ReadFraction, ImmutableArray<ScaleStats> Scales, double SpectralIndex,
        ImmutableArray<ClassStats> Classes);

    /// <summary>
    /// Measures <paramref name="luminance"/> (row-major, <paramref name="width"/> x <paramref name="height"/>), a plate whose
    /// stars were found and subtracted at a PSF of <paramref name="fwhm"/> pixels. <paramref name="absent"/> is its canvas
    /// ring.
    /// </summary>
    public static Measurement Measure(float[] luminance, int width, int height, BitMatrix? absent, double fwhm,
        float thresholdSigma = PlateSources.DefaultThresholdSigma)
    {
        var (sources, sky) = PointSourceFinder.Find(luminance, width, height, absent, fwhm, thresholdSigma);
        var texture = TextureField.FromSkyMap(sky, luminance, width, height, absent, fwhm);
        var read = ReadMask(width, height, absent, sources, fwhm);

        // The transform reads every pixel, so a non-finite one takes the finite pixels' median rather than spreading.
        var plane = (float[])luminance.Clone();
        var finite = plane.Where(float.IsFinite).ToArray();
        var fill = finite.Length > 0 ? StatisticsHelper.NthSmallest(finite.AsSpan(), finite.Length / 2) : 0f;
        for (var i = 0; i < plane.Length; i++)
        {
            if (!float.IsFinite(plane[i]))
            {
                plane[i] = fill;
            }
        }
        var decomposition = ATrousWaveletTransform.Decompose(plane, width, height, ScaleCount);

        var classOf = new int[plane.Length];
        long readPixels = 0;
        for (var i = 0; i < plane.Length; i++)
        {
            var y = i / width;
            var x = i % width;
            if (read[y, x])
            {
                readPixels++;
                classOf[i] = StarlessSpeckles.ClassOf(texture, x, y);
            }
            else
            {
                classOf[i] = -2;
            }
        }

        var all = Scales(decomposition, classOf, static c => c > -2);
        var classes = ImmutableArray.CreateBuilder<ClassStats>(StarlessSpeckles.TextureEdges.Length);
        for (var c = 0; c < StarlessSpeckles.TextureEdges.Length; c++)
        {
            var cls = c;
            var scales = Scales(decomposition, classOf, k => k == cls);
            classes.Add(new ClassStats(StarlessSpeckles.TextureEdges[c],
                c + 1 < StarlessSpeckles.TextureEdges.Length ? StarlessSpeckles.TextureEdges[c + 1] : float.PositiveInfinity,
                scales[0].Pixels, scales, SpectralIndexOf(scales)));
        }
        return new Measurement(fwhm, sources.Length, (double)readPixels / plane.Length, all, SpectralIndexOf(all), classes.MoveToImmutable());
    }

    // The pixels read: off the frame's edge and the canvas ring by EdgePx (a ring's reach taken per row and per column), and
    // off every source by SourceMaskFwhm PSF widths.
    private static BitMatrix ReadMask(int width, int height, BitMatrix? absent, PointSource[] sources, double fwhm)
    {
        var rowFirst = new int[height];
        var rowLast = new int[height];
        var colFirst = Enumerable.Repeat(int.MaxValue, width).ToArray();
        var colLast = Enumerable.Repeat(-1, width).ToArray();
        for (var y = 0; y < height; y++)
        {
            rowFirst[y] = int.MaxValue;
            rowLast[y] = -1;
            for (var x = 0; x < width; x++)
            {
                if (absent is { } a && a[y, x])
                {
                    continue;
                }
                rowFirst[y] = Math.Min(rowFirst[y], x);
                rowLast[y] = x;
                colFirst[x] = Math.Min(colFirst[x], y);
                colLast[x] = Math.Max(colLast[x], y);
            }
        }
        var read = new BitMatrix(height, width);
        for (var y = EdgePx; y < height - EdgePx; y++)
        {
            for (var x = EdgePx; x < width - EdgePx; x++)
            {
                read[y, x] = x - rowFirst[y] >= EdgePx && rowLast[y] - x >= EdgePx && y - colFirst[x] >= EdgePx && colLast[x] - y >= EdgePx
                    && !(absent is { } a && a[y, x]);
            }
        }
        var r = SourceMaskFwhm * fwhm;
        var reach = (int)Math.Ceiling(r);
        foreach (var s in sources)
        {
            var cx = (int)Math.Round(s.X);
            var cy = (int)Math.Round(s.Y);
            for (var y = Math.Max(0, cy - reach); y <= Math.Min(height - 1, cy + reach); y++)
            {
                for (var x = Math.Max(0, cx - reach); x <= Math.Min(width - 1, cx + reach); x++)
                {
                    if (((x - s.X) * (x - s.X)) + ((y - s.Y) * (y - s.Y)) <= r * r)
                    {
                        read[y, x] = false;
                    }
                }
            }
        }
        return read;
    }

    // Each scale's statistics over the pixels whose class the filter takes.
    private static ImmutableArray<ScaleStats> Scales(WaveletDecomposition decomposition, int[] classOf, Func<int, bool> take)
    {
        var builder = ImmutableArray.CreateBuilder<ScaleStats>(ScaleCount);
        var count = classOf.Count(take);
        var values = new float[count];
        for (var j = 0; j < ScaleCount; j++)
        {
            var detail = decomposition.Detail(j);
            var n = 0;
            double sum = 0, sum2 = 0;
            for (var i = 0; i < classOf.Length; i++)
            {
                if (take(classOf[i]))
                {
                    values[n++] = detail[i];
                    sum += detail[i];
                }
            }
            if (n < 3)
            {
                builder.Add(new ScaleStats(1 << j, n, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN));
                continue;
            }
            var mean = sum / n;
            double m3 = 0, m4 = 0;
            for (var i = 0; i < n; i++)
            {
                var d = values[i] - mean;
                var d2 = d * d;
                sum2 += d2;
                m3 += d2 * d;
                m4 += d2 * d2;
            }
            var variance = sum2 / n;
            var span = values.AsSpan(0, n);
            var median = StatisticsHelper.NthSmallest(span, n / 2);
            for (var i = 0; i < n; i++)
            {
                span[i] = Math.Abs(span[i] - median);
            }
            var mad = StatisticsHelper.NthSmallest(span, n / 2);
            var robustRms = 1.4826 * mad;
            long tail = 0;
            for (var i = 0; i < n; i++)
            {
                if (span[i] > TailSigma * robustRms)
                {
                    tail++;
                }
            }
            builder.Add(new ScaleStats(1 << j, n, robustRms, Math.Sqrt(variance),
                variance > 0 ? m3 / n / Math.Pow(variance, 1.5) : double.NaN,
                variance > 0 ? (m4 / n / (variance * variance)) - 3.0 : double.NaN,
                robustRms > 0 ? (double)tail / n : double.NaN));
        }
        return builder.MoveToImmutable();
    }

    // beta from the least-squares slope of log2 of the robust energy against the scale's index, from FirstSlopeScale up.
    private static double SpectralIndexOf(ImmutableArray<ScaleStats> scales)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        var n = 0;
        for (var j = FirstSlopeScale; j < scales.Length; j++)
        {
            var rms = scales[j].RobustRms;
            if (!(rms > 0))
            {
                continue;
            }
            var y = Math.Log2(rms * rms);
            sx += j;
            sy += y;
            sxx += j * j;
            sxy += j * y;
            n++;
        }
        if (n < 2)
        {
            return double.NaN;
        }
        var slope = ((n * sxy) - (sx * sy)) / ((n * sxx) - (sx * sx));
        return slope + 2.0;
    }
}
