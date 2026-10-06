using System;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// R2d's D2 and D3 (docs/plans/star-remover-training.md): a starless background a star remover is taught on, starless at
/// every scale a star lives at. The plate builder's plates keep the faint stars it could not find (about 29 a 256 px tile),
/// so a target made from a plate asks the net to remove an injected faint star and keep one exactly like it.
/// </summary>
/// <remarks>
/// <para><b>What it keeps of the plate.</b> Its starlet scales from <see cref="KeepFromFwhm"/> PSF widths up and its
/// residual: the nebula's and the sky's large-scale shape, where no star survives.</para>
/// <para><b>What it draws anew under them</b> (D1's read, "the real sky is noise where it is smooth, and turbulent where it is
/// not"). Per replaced scale and channel, the plate's own local signal above its noise (<see cref="Amplitude"/>, read with
/// the plate's sources masked out) sets the texture's strength there: none on a smooth sky, where the scales are the
/// master's noise and nothing else, and as much as the plate had on a nebula. The texture's shape is one log-normal field of
/// power-law index <see cref="TextureIndex"/> a cell, shared by the channels as a nebula's shape is: emission and dust are
/// turbulent, and a log-normal brightness is what turbulence gives them. The noise is not drawn here: the exporter adds the
/// master's own, in its shape, as it does to an injected star.</para>
/// <para><b>The knots it adds (D3).</b> Compact structure that is not the PSF: round ones 1.6 to 4 times its width, elongated
/// ones 2.5 to 5 to one. Every star in a frame shares its PSF, and a knot does not, which is the cue a remover must learn and a
/// label no real plate can give: the knots are in the target, so the net is taught to keep them.</para>
/// </remarks>
public sealed class SyntheticBackground
{
    /// <summary>The plate's starlet scales are kept from this many PSF widths up.</summary>
    public const double KeepFromFwhm = 4.0;

    /// <summary>The starlet scales the plate is read in: 1 to 32 px.</summary>
    public const int ScaleCount = 6;

    /// <summary>The texture's power-law index: D1 read 3.04 on the textured sky and 3.34 on the strongly textured.</summary>
    public const double TextureIndex = 3.1;

    /// <summary>The log-normal's width: a Gaussian field <c>g</c> becomes <c>exp(s g)</c>, emission's long bright tail.</summary>
    public const double LogNormalSigma = 0.8;

    /// <summary>The knots a cell holds, on average (Poisson).</summary>
    public const double KnotsPerCell = 3.0;

    /// <summary>The faintest and brightest knot's peak, in the cell's noise sigma.</summary>
    public const double KnotMinSigma = 5.0, KnotMaxSigma = 200.0;

    /// <summary>The threshold the plate's sources are masked from in the amplitude maps: under the 4 sigma the eval finds the
    /// kept sources at, so a faint star the finder only just misses raises no texture where it was.</summary>
    public const float AmplitudeMaskSigma = 3f;

    /// <summary>The smallest window the local signal is read over, in pixels (a Gaussian's sigma): a faint star's energy
    /// spread thin over it, a nebula's not.</summary>
    public const float AmplitudeWindowPx = 16f;

    /// <summary>The noise's variance is taken out of the local variance times this: a margin, so a sky that is noise and
    /// unresolved faint stars alone reads no texture.</summary>
    public const double NoiseMargin = 1.2;

    private readonly float[][] _coarse;      // [channel][y * width + x]
    private readonly float[][][] _amplitude; // [channel][scale < FirstKept][y * width + x]

    private SyntheticBackground(int width, int height, int firstKept, double fwhm, float[][] coarse, float[][][] amplitude)
    {
        Width = width;
        Height = height;
        FirstKept = firstKept;
        Fwhm = fwhm;
        _coarse = coarse;
        _amplitude = amplitude;
    }

    /// <summary>The frame's width.</summary>
    public int Width { get; }

    /// <summary>The frame's height.</summary>
    public int Height { get; }

    /// <summary>The first starlet scale kept from the plate (2^FirstKept px); the finer ones are drawn anew.</summary>
    public int FirstKept { get; }

    /// <summary>The PSF width the scales were set from, in pixels.</summary>
    public double Fwhm { get; }

    /// <summary>The channels.</summary>
    public int Channels => _coarse.Length;

    /// <summary>The first starlet scale index kept for a PSF of <paramref name="fwhm"/> pixels: the smallest whose
    /// 2^j px reaches <see cref="KeepFromFwhm"/> widths.</summary>
    public static int FirstKeptScale(double fwhm) => Math.Clamp((int)Math.Ceiling(Math.Log2(KeepFromFwhm * Math.Max(0.5, fwhm))), 1, ScaleCount);

    /// <summary>The plate's local signal above its noise at replaced scale <paramref name="scale"/>, channel
    /// <paramref name="channel"/>, at a pixel.</summary>
    public float Amplitude(int channel, int scale, int x, int y) => _amplitude[channel][scale][(y * Width) + x];

    /// <summary>
    /// Reads <paramref name="unitPlate"/> (a plate on its master's unit scale) once per session: its coarse part, and per
    /// replaced scale and channel the local signal above the noise, with the plate's own sources (found as the builder finds
    /// them, <see cref="PlateSources.DefaultThresholdSigma"/>) and the canvas ring <paramref name="absent"/> left out.
    /// </summary>
    public static SyntheticBackground Build(Image unitPlate, BitMatrix? absent, double fwhm)
    {
        var (channels, width, height) = unitPlate.Shape;
        var n = width * height;
        var firstKept = FirstKeptScale(fwhm);

        var luminance = new float[n];
        for (var c = 0; c < channels; c++)
        {
            var plane = unitPlate.GetChannelSpan(c);
            for (var i = 0; i < n; i++)
            {
                luminance[i] += plane[i] / channels;
            }
        }
        var (sources, _) = PointSourceFinder.Find(luminance, width, height, absent, fwhm, AmplitudeMaskSigma);
        var clean = new float[n];
        for (var i = 0; i < n; i++)
        {
            clean[i] = absent is { } a && a[i / width, i % width] ? 0f : 1f;
        }
        var reach = 2.0 * fwhm;
        var r = (int)Math.Ceiling(reach);
        foreach (var s in sources)
        {
            var cx = (int)Math.Round(s.X);
            var cy = (int)Math.Round(s.Y);
            for (var y = Math.Max(0, cy - r); y <= Math.Min(height - 1, cy + r); y++)
            {
                for (var x = Math.Max(0, cx - r); x <= Math.Min(width - 1, cx + r); x++)
                {
                    if (((x - s.X) * (x - s.X)) + ((y - s.Y) * (y - s.Y)) <= reach * reach)
                    {
                        clean[(y * width) + x] = 0f;
                    }
                }
            }
        }

        var coarse = new float[channels][];
        var amplitude = new float[channels][][];
        for (var c = 0; c < channels; c++)
        {
            var plane = unitPlate.GetChannelSpan(c).ToArray();
            var finite = plane.Where(float.IsFinite).ToArray();
            var fill = finite.Length > 0 ? StatisticsHelper.NthSmallest(finite.AsSpan(), finite.Length / 2) : 0f;
            for (var i = 0; i < n; i++)
            {
                if (!float.IsFinite(plane[i]))
                {
                    plane[i] = fill;
                }
            }
            var decomposition = ATrousWaveletTransform.Decompose(plane, width, height, ScaleCount);
            var keep = decomposition.Residual.ToArray();
            for (var j = firstKept; j < ScaleCount; j++)
            {
                var detail = decomposition.Detail(j);
                for (var i = 0; i < n; i++)
                {
                    keep[i] += detail[i];
                }
            }
            coarse[c] = keep;

            amplitude[c] = new float[firstKept][];
            for (var j = 0; j < firstKept; j++)
            {
                amplitude[c][j] = SignalAmplitude(decomposition.Detail(j), clean, width, height, j);
            }
        }
        return new SyntheticBackground(width, height, firstKept, fwhm, coarse, amplitude);
    }

    // The local RMS of a detail plane over the clean pixels, smoothed at twice the scale or AmplitudeWindowPx, less its noise
    // (the clean pixels' robust variance, which a smooth sky dominates, with NoiseMargin), floored at zero: the signal the
    // plate has at that scale there.
    private static float[] SignalAmplitude(ReadOnlySpan<float> detail, float[] clean, int width, int height, int scale)
    {
        var n = detail.Length;
        var energy = new float[n];
        var sample = new float[n];
        var count = 0;
        for (var i = 0; i < n; i++)
        {
            energy[i] = clean[i] * detail[i] * detail[i];
            if (clean[i] > 0f)
            {
                sample[count++] = detail[i];
            }
        }
        var noiseVariance = 0.0;
        if (count > 0)
        {
            var span = sample.AsSpan(0, count);
            var median = StatisticsHelper.NthSmallest(span, count / 2);
            for (var i = 0; i < count; i++)
            {
                span[i] = Math.Abs(span[i] - median);
            }
            var robust = 1.4826 * StatisticsHelper.NthSmallest(span, count / 2);
            noiseVariance = robust * robust;
        }
        var sigma = Math.Max(AmplitudeWindowPx, 2f * (1 << scale));
        var local = Image.SeparableGaussianBlur(energy, width, height, sigma);
        var weight = Image.SeparableGaussianBlur(clean, width, height, sigma);
        var amplitude = new float[n];
        for (var i = 0; i < n; i++)
        {
            var variance = weight[i] > 0.05f ? local[i] / weight[i] : 0f;
            amplitude[i] = (float)Math.Sqrt(Math.Max(0.0, variance - (NoiseMargin * noiseVariance)));
        }
        return amplitude;
    }

    /// <summary>One knot a cell holds, in the cell's coordinates, its peak per channel in the plate's units.</summary>
    public readonly record struct Knot(double X, double Y, double SigmaMajorPx, double SigmaMinorPx, double AngleRad, double[] Peak);

    /// <summary>
    /// The noise-free background of the <paramref name="size"/> px cell at (<paramref name="x0"/>, <paramref name="y0"/>):
    /// the plate's coarse part, the texture at the replaced scales, and the knots, each channel's knots peaking at a draw
    /// between <see cref="KnotMinSigma"/> and <see cref="KnotMaxSigma"/> times <paramref name="noiseSigma"/> (the cell's noise
    /// per channel, the plate's units). Pixels outside the frame take the nearest one's.
    /// </summary>
    public float[][] Cell(int x0, int y0, int size, ReadOnlySpan<double> noiseSigma, Random random, out ImmutableArray<Knot> knots)
    {
        var texture = TextureScales(size, random);
        var planes = new float[Channels][];
        for (var c = 0; c < Channels; c++)
        {
            var plane = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                var fy = Math.Clamp(y0 + y, 0, Height - 1);
                for (var x = 0; x < size; x++)
                {
                    var fx = Math.Clamp(x0 + x, 0, Width - 1);
                    var f = (fy * Width) + fx;
                    var v = (double)_coarse[c][f];
                    for (var j = 0; j < FirstKept; j++)
                    {
                        v += _amplitude[c][j][f] * texture[j][(y * size) + x];
                    }
                    plane[(y * size) + x] = (float)v;
                }
            }
            planes[c] = plane;
        }
        knots = AddKnots(planes, size, noiseSigma, random);
        return planes;
    }

    // One log-normal field of index TextureIndex at twice the cell's size (so no scale meets a periodic edge), decomposed into
    // the replaced starlet scales, each cut to the cell and scaled to unit robust RMS.
    private float[][] TextureScales(int size, Random random)
    {
        var n = 1;
        while (n < 2 * size)
        {
            n <<= 1;
        }
        var field = PowerLawField(n, TextureIndex, random);
        var mean = 0.0;
        foreach (var v in field)
        {
            mean += v;
        }
        mean /= field.Length;
        var sd = Math.Sqrt(field.Sum(v => (v - mean) * (v - mean)) / field.Length);
        var logNormal = new float[field.Length];
        for (var i = 0; i < field.Length; i++)
        {
            logNormal[i] = (float)Math.Exp(LogNormalSigma * (field[i] - mean) / (sd > 0 ? sd : 1.0));
        }
        var decomposition = ATrousWaveletTransform.Decompose(logNormal, n, n, ScaleCount);
        var offset = (n - size) / 2;
        var scales = new float[FirstKept][];
        for (var j = 0; j < FirstKept; j++)
        {
            var detail = decomposition.Detail(j);
            var cut = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    cut[(y * size) + x] = detail[((y + offset) * n) + x + offset];
                }
            }
            var abs = cut.Select(static v => Math.Abs(v)).ToArray();
            var mad = StatisticsHelper.NthSmallest(abs.AsSpan(), abs.Length / 2);
            var scale = mad > 0 ? 1.0 / (1.4826 * mad) : 0.0;
            for (var i = 0; i < cut.Length; i++)
            {
                cut[i] = (float)(cut[i] * scale);
            }
            scales[j] = cut;
        }
        return scales;
    }

    /// <summary>A Gaussian random field on an <paramref name="n"/> x <paramref name="n"/> grid (a power of two) whose power falls
    /// as <c>k^-beta</c>, zero mean.</summary>
    internal static double[] PowerLawField(int n, double beta, Random random)
    {
        var c = new Complex[n * n];
        for (var ky = 0; ky < n; ky++)
        {
            var fy = ky < n / 2 ? ky : ky - n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = kx < n / 2 ? kx : kx - n;
                var k = Math.Sqrt((fx * fx) + (fy * fy));
                c[(ky * n) + kx] = k == 0
                    ? Complex.Zero
                    : new Complex(Gaussian(random), Gaussian(random)) * Math.Pow(k, -beta / 2.0);
            }
        }
        Fft2D.Inverse(c, n, n);
        return [.. c.Select(static z => z.Real)];
    }

    // The knots: a Poisson count, each round and wider than the PSF or elongated, inside the cell's 16 px rim, its peak per
    // channel a log-uniform draw in noise sigma.
    private ImmutableArray<Knot> AddKnots(float[][] planes, int size, ReadOnlySpan<double> noiseSigma, Random random)
    {
        var psfSigma = Fwhm / 2.3548;
        var count = Poisson(KnotsPerCell, random);
        var knots = ImmutableArray.CreateBuilder<Knot>(count);
        for (var k = 0; k < count; k++)
        {
            double major, minor;
            if (random.NextDouble() < 0.5)
            {
                major = minor = psfSigma * (1.6 + (2.4 * random.NextDouble()));
            }
            else
            {
                minor = psfSigma * (1.0 + (0.5 * random.NextDouble()));
                major = minor * (2.5 + (2.5 * random.NextDouble()));
            }
            var angle = random.NextDouble() * Math.PI;
            var cx = 16 + (random.NextDouble() * (size - 32));
            var cy = 16 + (random.NextDouble() * (size - 32));
            var level = KnotMinSigma * Math.Pow(KnotMaxSigma / KnotMinSigma, random.NextDouble());
            var peak = new double[planes.Length];
            for (var c = 0; c < planes.Length; c++)
            {
                peak[c] = level * (c < noiseSigma.Length ? noiseSigma[c] : noiseSigma[^1]);
            }
            var (sin, cos) = Math.SinCos(angle);
            var reach = (int)Math.Ceiling(4 * major);
            for (var y = Math.Max(0, (int)cy - reach); y <= Math.Min(size - 1, (int)cy + reach); y++)
            {
                for (var x = Math.Max(0, (int)cx - reach); x <= Math.Min(size - 1, (int)cx + reach); x++)
                {
                    var dx = x - cx;
                    var dy = y - cy;
                    var u = ((dx * cos) + (dy * sin)) / major;
                    var v = ((-dx * sin) + (dy * cos)) / minor;
                    var g = Math.Exp(-0.5 * ((u * u) + (v * v)));
                    for (var c = 0; c < planes.Length; c++)
                    {
                        planes[c][(y * size) + x] += (float)(peak[c] * g);
                    }
                }
            }
            knots.Add(new Knot(cx, cy, major, minor, angle, peak));
        }
        return knots.MoveToImmutable();
    }

    private static int Poisson(double mean, Random random)
    {
        var limit = Math.Exp(-mean);
        var product = random.NextDouble();
        var count = 0;
        while (product > limit)
        {
            count++;
            product *= random.NextDouble();
        }
        return count;
    }

    private static double Gaussian(Random random)
        => Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());
}
