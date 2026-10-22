using System;
using System.Globalization;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// Synthetics S4 (docs/plans/star-remover-training.md, #1739): the correlation a master's noise carries between its channels,
/// and the mix that gives a draw's independent unit noise fields that correlation. A stacked colour master's channels share
/// their noise through the demosaic or the drizzle (the review read R-G 0.12, G-B 0.05, R-B 0.04 on the real tiles), where the
/// export drew each channel's field on its own (0.05 / 0.02 / 0.02, all of it the tiles' signal leaking into the statistic).
/// <para>
/// The correlation is measured on the starless PLATE, which is the master less its fitted stars and so carries the master's
/// own noise without most of its stars, on the residual from a 5 x 5 median as the review's instrument reads it, and by a
/// ROBUST estimator: each channel's residual in its own MAD, then the MADs of the pair's sum and difference
/// (<see cref="RobustCorrelation"/>). A faint star is light shared by every channel, a correlated outlier that a Pearson
/// coefficient takes for correlated noise: over the review's tiles Pearson read the real masters at 0.119 / 0.048 / 0.038 and
/// the robust form at 0.062 / 0.027 / 0.016, and synth4's independent noise at 0.048 / 0.017 / 0.019 against 0.011 / 0.007 /
/// 0.003. What is drawn is the noise's correlation; the rest of the real tiles' Pearson number is their faint stars, which
/// are S2's to draw.
/// </para>
/// <para>
/// <see cref="Mix"/> takes the lower Cholesky factor of the correlation matrix to the channels' fields, pixel by pixel. Each
/// field keeps its unit variance, and since every channel's field is drawn in the same shape (white, or warped with the
/// same width and frame count), a linear mix of them has that shape's spatial correlation too: each channel's spatial
/// correlation is what it was. The mix draws no random number, so every stream of a draw is what it was without it.
/// </para>
/// </summary>
public sealed class ChannelNoiseCorrelation
{
    /// <summary>The most residuals per channel <see cref="Measure"/> reads; a larger plate is read on a coarser grid.</summary>
    public const int MaxSamples = 1 << 20;

    /// <summary>A correlation matrix whose Cholesky pivot falls under this is shrunk toward the identity until it does not.</summary>
    private const double MinPivot = 1e-3;

    private readonly double[,] _correlation;
    private readonly double[,] _cholesky;

    /// <param name="correlation">A symmetric matrix with a unit diagonal, its off-diagonals in (-1, 1). One that is not
    /// positive definite has its off-diagonals shrunk toward zero until it is (<see cref="Shrink"/> says by how much).</param>
    public ChannelNoiseCorrelation(double[,] correlation)
    {
        var n = correlation.GetLength(0);
        if (n == 0 || correlation.GetLength(1) != n)
        {
            throw new ArgumentException("a channel correlation is a square matrix of at least one channel", nameof(correlation));
        }
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var r = correlation[i, j];
                if (i == j ? r != 1.0 : !(r > -1.0 && r < 1.0) || r != correlation[j, i])
                {
                    throw new ArgumentException($"a channel correlation is symmetric with a unit diagonal; [{i}, {j}] is {r}", nameof(correlation));
                }
            }
        }
        Channels = n;
        _correlation = correlation.Copy();
        var shrink = 1.0;
        while (!TryCholesky(_correlation, shrink, out _cholesky))
        {
            shrink *= 0.9;
        }
        Shrink = shrink;
    }

    /// <summary>The channel count.</summary>
    public int Channels { get; }

    /// <summary>The factor the off-diagonals were taken at to keep the matrix positive definite: 1 for any measured one.</summary>
    public double Shrink { get; }

    /// <summary>The correlation between channels <paramref name="a"/> and <paramref name="b"/>, as given.</summary>
    public double this[int a, int b] => _correlation[a, b];

    /// <summary>
    /// The noise correlation between <paramref name="plate"/>'s channels: each channel's residual from its 5 x 5 median on a
    /// grid of at most <see cref="MaxSamples"/> pixels, every pixel whose window touches <paramref name="absent"/>, the frame's
    /// edge or a non-finite value left out, and each pair read by <see cref="RobustCorrelation"/>. A mono plate answers its
    /// one-channel identity.
    /// </summary>
    public static ChannelNoiseCorrelation Measure(Image plate, BitMatrix? absent)
    {
        var channels = plate.ChannelCount;
        var correlation = new double[channels, channels];
        for (var c = 0; c < channels; c++)
        {
            correlation[c, c] = 1.0;
        }
        if (channels < 2)
        {
            return new ChannelNoiseCorrelation(correlation);
        }
        var residuals = Residuals(plate, absent);
        for (var a = 0; a < channels; a++)
        {
            for (var b = a + 1; b < channels; b++)
            {
                // A plate with no noise to read (a noise-free fixture, a channel with no spread) is drawn independent, as before.
                var measured = RobustCorrelation(residuals[a], residuals[b]);
                var r = double.IsFinite(measured) ? Math.Clamp(measured, -0.99, 0.99) : 0.0;
                correlation[a, b] = r;
                correlation[b, a] = r;
            }
        }
        return new ChannelNoiseCorrelation(correlation);
    }

    /// <summary>
    /// A correlation that a minority of outliers barely moves: each series in its own median and MAD, then
    /// (s+^2 - s-^2) / (s+^2 + s-^2) with s+ and s- the MADs of their sum and difference, which for two Gaussian series of
    /// correlation r is r. NaN when either series has no spread or none is given.
    /// </summary>
    public static double RobustCorrelation(ReadOnlySpan<float> u, ReadOnlySpan<float> v)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(v.Length, u.Length);
        var n = u.Length;
        if (n == 0)
        {
            return double.NaN;
        }
        var scratch = new float[n];
        var (uMedian, uMad) = MedianAndMadOf(u, scratch);
        var (vMedian, vMad) = MedianAndMadOf(v, scratch);
        if (!(uMad > 0f && vMad > 0f))
        {
            return double.NaN;
        }
        var sum = new float[n];
        var difference = scratch;
        for (var i = 0; i < n; i++)
        {
            var a = (u[i] - uMedian) / uMad;
            var b = (v[i] - vMedian) / vMad;
            sum[i] = a + b;
            difference[i] = a - b;
        }
        double plus = StatisticsHelper.MedianAndMad(sum).Mad;
        double minus = StatisticsHelper.MedianAndMad(difference).Mad;
        plus *= plus;
        minus *= minus;
        return plus + minus > 0 ? (plus - minus) / (plus + minus) : double.NaN;
    }

    /// <summary>
    /// The channels' fields given this correlation: channel c becomes the sum over k up to c of L[c, k] times field k, L the
    /// lower Cholesky factor, so channel 0 is its own field (the same array) and every other is a new one.
    /// </summary>
    public float[][] Mix(float[][] fields)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(fields.Length, Channels);
        var length = fields[0].Length;
        var mixed = new float[Channels][];
        mixed[0] = fields[0];
        for (var c = 1; c < Channels; c++)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(fields[c].Length, length);
            var plane = new float[length];
            for (var k = 0; k <= c; k++)
            {
                var weight = (float)_cholesky[c, k];
                var field = fields[k];
                for (var i = 0; i < length; i++)
                {
                    plane[i] += weight * field[i];
                }
            }
            mixed[c] = plane;
        }
        return mixed;
    }

    /// <summary>The off-diagonals in words: R-G, G-B, R-B for three channels, every pair by index otherwise.</summary>
    public string InWords()
    {
        if (Channels == 3)
        {
            return string.Create(CultureInfo.InvariantCulture, $"R-G {this[0, 1]:F3}, G-B {this[1, 2]:F3}, R-B {this[0, 2]:F3}");
        }
        var words = new System.Collections.Generic.List<string>();
        for (var a = 0; a < Channels; a++)
        {
            for (var b = a + 1; b < Channels; b++)
            {
                words.Add(string.Create(CultureInfo.InvariantCulture, $"{a}-{b} {this[a, b]:F3}"));
            }
        }
        return words.Count == 0 ? "one channel" : string.Join(", ", words);
    }

    private static (float Median, float Mad) MedianAndMadOf(ReadOnlySpan<float> values, float[] scratch)
    {
        values.CopyTo(scratch);
        return StatisticsHelper.MedianAndMad(scratch.AsSpan(0, values.Length));
    }

    /// <summary>Each channel's residual from its 5 x 5 median at the same grid of pixels.</summary>
    private static float[][] Residuals(Image plate, BitMatrix? absent)
    {
        var (channels, width, height) = plate.Shape;
        const int reach = 2;
        var inner = (long)Math.Max(0, width - (2 * reach)) * Math.Max(0, height - (2 * reach));
        var stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(inner / (double)MaxSamples)));
        var planes = plate.ResidentPlanes();
        var capacity = (int)(((width / stride) + 1L) * ((height / stride) + 1L));
        var samples = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            samples[c] = new float[capacity];
        }
        var count = 0;
        Span<float> window = stackalloc float[(2 * reach + 1) * (2 * reach + 1)];
        for (var y = reach; y < height - reach; y += stride)
        {
            for (var x = reach; x < width - reach; x += stride)
            {
                if (!Usable(planes, absent, x, y, reach))
                {
                    continue;
                }
                for (var c = 0; c < channels; c++)
                {
                    var data = planes[c];
                    var w = 0;
                    for (var dy = -reach; dy <= reach; dy++)
                    {
                        for (var dx = -reach; dx <= reach; dx++)
                        {
                            window[w++] = data[y + dy, x + dx];
                        }
                    }
                    samples[c][count] = data[y, x] - StatisticsHelper.MedianFast(window);
                }
                count++;
            }
        }
        var result = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            result[c] = samples[c].AsSpan(0, count).ToArray();
        }
        return result;
    }

    private static bool Usable(float[][,] planes, BitMatrix? absent, int x, int y, int reach)
    {
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (absent is { } a && a[y + dy, x + dx])
                {
                    return false;
                }
                foreach (var data in planes)
                {
                    if (!float.IsFinite(data[y + dy, x + dx]))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private static bool TryCholesky(double[,] correlation, double shrink, out double[,] lower)
    {
        var n = correlation.GetLength(0);
        lower = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var value = i == j ? 1.0 : correlation[i, j] * shrink;
                for (var k = 0; k < j; k++)
                {
                    value -= lower[i, k] * lower[j, k];
                }
                if (i == j)
                {
                    if (value < MinPivot)
                    {
                        return false;
                    }
                    lower[i, i] = Math.Sqrt(value);
                }
                else
                {
                    lower[i, j] = value / lower[j, j];
                }
            }
        }
        return true;
    }
}
