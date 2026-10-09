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
    /// <param name="Coherence">The structure tensor's coherence, <c>(l1 - l2) / (l1 + l2)</c> over a window of
    /// <see cref="TensorWindowScales"/> times the scale, weighted by the tensor's energy: 1 for structure running one way, and
    /// not 0 for an isotropic field over a finite window (read it against <see cref="Measurement.NoiseCoherence"/>).</param>
    /// <param name="Alignment">The mean of <c>cos 2(theta - theta_coarse)</c> between the scale's dominant gradient and the
    /// coarse structure's (the scales the synthetic background keeps, <see cref="SyntheticBackground.FirstKeptScale"/> up),
    /// weighted by both coherences: +1 where the fine structure runs along the coarse contours, -1 across them, 0 where the
    /// two are unrelated.</param>
    public sealed record ScaleStats(int ScalePx, long Pixels, double RobustRms, double Rms, double Skewness, double Kurtosis, double TailFraction,
        double Coherence, double Alignment);

    /// <summary>The structure tensor's window, a Gaussian's sigma, in units of the scale it reads (R2e's S1).</summary>
    public const double TensorWindowScales = 2.0;

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
    /// <param name="NoiseCoherence">White noise's <see cref="ScaleStats.Coherence"/> per scale, read the same way: what an
    /// isotropic field reads over the window, the null a plate's coherence is set against.</param>
    /// <param name="NoiseAlignment">White noise's <see cref="ScaleStats.Alignment"/> per scale against the coarse structure
    /// two octaves up (<see cref="CoarseStart"/> at its closest): what band overlap alone reads, the null for alignment.</param>
    public sealed record Measurement(double Fwhm, int Sources, double ReadFraction, ImmutableArray<ScaleStats> Scales, double SpectralIndex,
        ImmutableArray<ClassStats> Classes, ImmutableArray<double> NoiseCoherence, ImmutableArray<double> NoiseAlignment);

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
        float[] plane = [.. luminance];
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

        // The structure per class (index ClassCount is every class together), computed once per scale.
        var classCount = StarlessSpeckles.TextureEdges.Length;
        var (coherence, alignment) = Structure(decomposition, width, height, fwhm, classOf, classCount);

        var all = Scales(decomposition, classOf, static c => c > -2, coherence[classCount], alignment[classCount]);
        var classes = ImmutableArray.CreateBuilder<ClassStats>(classCount);
        for (var c = 0; c < classCount; c++)
        {
            var cls = c;
            var scales = Scales(decomposition, classOf, k => k == cls, coherence[c], alignment[c]);
            classes.Add(new ClassStats(StarlessSpeckles.TextureEdges[c],
                c + 1 < classCount ? StarlessSpeckles.TextureEdges[c + 1] : float.PositiveInfinity,
                scales[0].Pixels, scales, SpectralIndexOf(scales)));
        }
        var (noiseCoherence, noiseAlignment) = NoiseNull.Value;
        return new Measurement(fwhm, sources.Length, (double)readPixels / plane.Length, all, SpectralIndexOf(all), classes.MoveToImmutable(),
            noiseCoherence, noiseAlignment);
    }

    /// <summary>
    /// The structure tensor of <paramref name="plane"/> over a Gaussian window of <paramref name="sigma"/> px, per pixel: its
    /// coherence, the dominant gradient's doubled angle as a cosine and a sine, and its energy (the trace).
    /// </summary>
    internal static (float[] Coherence, float[] Cos2, float[] Sin2, float[] Energy) StructureTensor(ReadOnlySpan<float> plane, int width, int height, float sigma)
    {
        var n = width * height;
        var jxx = new float[n];
        var jyy = new float[n];
        var jxy = new float[n];
        for (var y = 0; y < height; y++)
        {
            var up = Math.Max(0, y - 1);
            var down = Math.Min(height - 1, y + 1);
            for (var x = 0; x < width; x++)
            {
                var left = Math.Max(0, x - 1);
                var right = Math.Min(width - 1, x + 1);
                var gx = (plane[(y * width) + right] - plane[(y * width) + left]) / (right - left);
                var gy = (plane[(down * width) + x] - plane[(up * width) + x]) / (down - up);
                var i = (y * width) + x;
                jxx[i] = gx * gx;
                jyy[i] = gy * gy;
                jxy[i] = gx * gy;
            }
        }
        jxx = Image.SeparableGaussianBlur(jxx, width, height, sigma);
        jyy = Image.SeparableGaussianBlur(jyy, width, height, sigma);
        jxy = Image.SeparableGaussianBlur(jxy, width, height, sigma);
        var coherence = new float[n];
        var cos2 = new float[n];
        var sin2 = new float[n];
        var energy = new float[n];
        for (var i = 0; i < n; i++)
        {
            var trace = jxx[i] + jyy[i];
            var dxy = jxx[i] - jyy[i];
            var spread = MathF.Sqrt((dxy * dxy) + (4f * jxy[i] * jxy[i]));
            energy[i] = trace;
            coherence[i] = trace > 0f ? spread / trace : 0f;
            cos2[i] = spread > 0f ? dxy / spread : 0f;
            sin2[i] = spread > 0f ? 2f * jxy[i] / spread : 0f;
        }
        return (coherence, cos2, sin2, energy);
    }

    /// <summary>
    /// The first scale of the coarse structure a scale <paramref name="j"/> is aligned against: the scales the synthetic
    /// background keeps (<paramref name="firstKept"/>) or two octaves above <paramref name="j"/>, whichever is coarser, since
    /// adjacent starlet scales share frequencies and read aligned on any field (8 px against 16 px up read 0.23 on white
    /// noise). <see cref="ScaleCount"/> means the residual alone; past it the scale has no alignment.
    /// </summary>
    internal static int CoarseStart(int j, int firstKept) => Math.Max(firstKept, j + 2);

    // The coarse plane from scale `start` up: the residual and every detail from there.
    private static float[] CoarseFrom(WaveletDecomposition decomposition, int start)
    {
        var coarse = decomposition.Residual.ToArray();
        for (var j = start; j < ScaleCount; j++)
        {
            var detail = decomposition.Detail(j);
            for (var i = 0; i < coarse.Length; i++)
            {
                coarse[i] += detail[i];
            }
        }
        return coarse;
    }

    // Per class and scale ([class][scale], the last class every class together): the energy-weighted coherence, and the
    // alignment with the coarse structure from CoarseStart up, weighted by both coherences.
    private static (double[][] Coherence, double[][] Alignment) Structure(WaveletDecomposition decomposition, int width, int height,
        double fwhm, int[] classOf, int classCount)
    {
        var firstKept = SyntheticBackground.FirstKeptScale(fwhm);
        var coarseTensors = new (float[] Coherence, float[] Cos2, float[] Sin2, float[] Energy)?[ScaleCount + 1];

        var coherence = new double[classCount + 1][];
        var alignment = new double[classCount + 1][];
        for (var c = 0; c <= classCount; c++)
        {
            coherence[c] = new double[ScaleCount];
            alignment[c] = new double[ScaleCount];
        }
        for (var j = 0; j < ScaleCount; j++)
        {
            var t = StructureTensor(decomposition.Detail(j), width, height, (float)(TensorWindowScales * (1 << j)));
            var start = CoarseStart(j, firstKept);
            var hasCoarse = start <= ScaleCount;
            var coarseTensor = hasCoarse
                ? coarseTensors[start] ??= StructureTensor(CoarseFrom(decomposition, start), width, height, (float)(TensorWindowScales * (1 << start)))
                : default;
            var sumE = new double[classCount + 1];
            var sumEc = new double[classCount + 1];
            var sumW = new double[classCount + 1];
            var sumWa = new double[classCount + 1];
            for (var i = 0; i < classOf.Length; i++)
            {
                var c = classOf[i];
                if (c < 0)
                {
                    continue;
                }
                var e = (double)t.Energy[i];
                var ec = e * t.Coherence[i];
                sumE[c] += e;
                sumEc[c] += ec;
                sumE[classCount] += e;
                sumEc[classCount] += ec;
                if (hasCoarse)
                {
                    var w = (double)t.Coherence[i] * coarseTensor.Coherence[i];
                    var wa = w * (((double)t.Cos2[i] * coarseTensor.Cos2[i]) + ((double)t.Sin2[i] * coarseTensor.Sin2[i]));
                    sumW[c] += w;
                    sumWa[c] += wa;
                    sumW[classCount] += w;
                    sumWa[classCount] += wa;
                }
            }
            for (var c = 0; c <= classCount; c++)
            {
                coherence[c][j] = sumE[c] > 0 ? sumEc[c] / sumE[c] : double.NaN;
                alignment[c][j] = sumW[c] > 0 ? sumWa[c] / sumW[c] : double.NaN;
            }
        }
        return (coherence, alignment);
    }

    // White noise read as a plate is, per scale: its coherence (energy-weighted) and its alignment against the coarse structure
    // two octaves up, a window's reach in from the edge. The nulls.
    private static readonly Lazy<(ImmutableArray<double> Coherence, ImmutableArray<double> Alignment)> NoiseNull = new(static () =>
    {
        const int size = 1024;
        var random = new Random(1);
        var noise = new float[size * size];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble()));
        }
        var decomposition = ATrousWaveletTransform.Decompose(noise, size, size, ScaleCount);
        var coherence = ImmutableArray.CreateBuilder<double>(ScaleCount);
        var alignment = ImmutableArray.CreateBuilder<double>(ScaleCount);
        for (var j = 0; j < ScaleCount; j++)
        {
            var sigma = TensorWindowScales * (1 << j);
            var t = StructureTensor(decomposition.Detail(j), size, size, (float)sigma);
            var start = j + 2;
            var coarse = start <= ScaleCount
                ? StructureTensor(CoarseFrom(decomposition, start), size, size, (float)(TensorWindowScales * (1 << start)))
                : default;
            var margin = (int)Math.Ceiling(3 * sigma);
            double sumE = 0, sumEc = 0, sumW = 0, sumWa = 0;
            for (var y = margin; y < size - margin; y++)
            {
                for (var x = margin; x < size - margin; x++)
                {
                    var i = (y * size) + x;
                    sumE += t.Energy[i];
                    sumEc += (double)t.Energy[i] * t.Coherence[i];
                    if (start <= ScaleCount)
                    {
                        var w = (double)t.Coherence[i] * coarse.Coherence[i];
                        sumW += w;
                        sumWa += w * (((double)t.Cos2[i] * coarse.Cos2[i]) + ((double)t.Sin2[i] * coarse.Sin2[i]));
                    }
                }
            }
            coherence.Add(sumE > 0 ? sumEc / sumE : double.NaN);
            alignment.Add(sumW > 0 ? sumWa / sumW : double.NaN);
        }
        return (coherence.MoveToImmutable(), alignment.MoveToImmutable());
    });

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

    // Each scale's statistics over the pixels whose class the filter takes, with that class's structure per scale.
    private static ImmutableArray<ScaleStats> Scales(WaveletDecomposition decomposition, int[] classOf, Func<int, bool> take,
        double[] coherence, double[] alignment)
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
                builder.Add(new ScaleStats(1 << j, n, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN));
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
                robustRms > 0 ? (double)tail / n : double.NaN, coherence[j], alignment[j]));
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
