using System;
using System.Collections.Generic;
using System.Globalization;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// Synthetics S4b (docs/plans/star-remover-training.md, #1781): the noise a master's channels share, with its spatial
/// structure. <see cref="ChannelNoiseCorrelation"/> (#1739) read the correlation between channels at zero lag and mixed the
/// channels' fields by it, which put the shared part into each channel with that channel's own shape: it falls away at once,
/// where in a real master R-G one pixel away is half its zero-lag value (a demosaic's or a drizzle's interpolation) and an
/// ASI533's R-B sits on the diagonal (+0.050 at offset 1,1 against +0.010 at 0,0). A noise-only classifier then saw a
/// zero-lag correlation with no neighbourhood, a shape no real master has.
/// <para>
/// <see cref="Measure"/> reads each pair's cross-correlation at every offset (dy, dx) from -<see cref="Reach"/> to
/// <see cref="Reach"/> off the starless plate, the same instrument as #1739's (the residual from a 5 x 5 median, the same
/// absence rule, <see cref="ChannelNoiseCorrelation.RobustCorrelation"/>), and each channel's own lag-1 for the log.
/// </para>
/// <para>
/// <see cref="Mix"/> draws the structure. Channel c is <c>sqrt(1 - v_c) * own_c + w_c * G</c>: its own field as the draw made
/// it, and a kernel <c>w_c</c> (<c>2 * Reach + 1</c> pixels square, <c>v_c</c> its energy) over ONE shared white field
/// <c>G</c>. The cross-covariance of channels a and b at offset d is then the cross-correlation of their two kernels, and the
/// kernels are FITTED (Levenberg-Marquardt) so that it is the measure's at every offset up to the reach and nothing past it,
/// AS THE INSTRUMENT READS IT: the measure is of a 5 x 5 median's residual, which takes about a third of a structure smooth
/// over a few pixels (a draw of R-G 0.144 at zero lag reads 0.098, 0.088 at one pixel reads 0.046), so the fit maps the
/// kernels' correlation through that residual (a 5 x 5 mean's, which predicts the median's read to 0.005) before it is
/// compared, and what is drawn reads, by <see cref="Measure"/>, as what was measured.
/// A measure the model cannot reach (kernels of more than half the variance, or a residual over
/// <see cref="FitTolerance"/>) is shrunk toward independent until it can, and <see cref="Shrink"/> says by how much. The shared
/// part's own lag-1 is not the draw's, so each channel's own field is high-passed (isotropically, by its four neighbours) by the
/// amount that makes the sum's POOLED horizontal-plus-vertical raw lag-1 the draw's again, with unit variance. Only the pooled
/// lag-1 is held: the per-axis lag-1 moves freely (and the fit constrains only the cross-correlations, so a kernel's own shape
/// can be elongated or a checkerboard), and a target beyond the beta range is not reached, silently.
/// </para>
/// <para>
/// The shared field comes from a random stream the caller hands in, and nothing else is drawn, so every other stream of an
/// export is what it was.
/// </para>
/// </summary>
public sealed class ChannelNoiseStructure
{
    /// <summary>The largest offset, per axis, a cross-correlation is read and drawn at; a kernel is <c>2 * Reach + 1</c> square.</summary>
    public const int Reach = 2;

    /// <summary>Residual pixels per channel <see cref="Measure"/> reads; a larger plate is read on sparser blocks.</summary>
    public const int MaxSamples = 1 << 18;

    /// <summary>The fit's tolerance: the root mean square, over every pair and offset up to the reach, of the drawn cross-correlation less the measure's.</summary>
    public const double FitTolerance = 0.01;

    /// <summary>The most of a channel's variance the shared part may take; a measure that asks for more is shrunk.</summary>
    public const double MaxSharedVariance = 0.5;

    private const int Side = (2 * Reach) + 1;
    private const int Taps = Side * Side;
    private const int Block = 16;
    private const int MinLagSamples = 1000;
    private const double MinGain = 1e-4;

    private readonly double[][,] _cross;
    private readonly double[] _ownLag1;
    private readonly double[][] _kernels;
    private readonly double[] _shared;

    /// <param name="channels">The channel count.</param>
    /// <param name="cross">For each pair (a, b) with a below b, in the order (0,1), (0,2), ..., (1,2), ...: the correlation
    /// of channel a at a pixel with channel b at that pixel plus (dy, dx), at <c>[dy + Reach, dx + Reach]</c>.</param>
    /// <param name="ownLag1">Each channel's own lag-1 correlation, as measured; reported, never drawn from.</param>
    public ChannelNoiseStructure(int channels, IReadOnlyList<double[,]> cross, IReadOnlyList<double> ownLag1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        var pairs = channels * (channels - 1) / 2;
        ArgumentOutOfRangeException.ThrowIfNotEqual(cross.Count, pairs);
        ArgumentOutOfRangeException.ThrowIfNotEqual(ownLag1.Count, channels);
        _cross = new double[pairs][,];
        for (var k = 0; k < pairs; k++)
        {
            var m = cross[k];
            if (m.GetLength(0) != Side || m.GetLength(1) != Side)
            {
                throw new ArgumentException($"a pair's cross-correlation is {Side} x {Side} (offsets -{Reach} to {Reach}), not {m.GetLength(0)} x {m.GetLength(1)}", nameof(cross));
            }
            foreach (var r in m)
            {
                if (!(r > -1.0 && r < 1.0))
                {
                    throw new ArgumentException($"a cross-correlation is finite and inside (-1, 1); pair {k} has {r}", nameof(cross));
                }
            }
            _cross[k] = m.Copy();
        }
        Channels = channels;
        _ownLag1 = [.. ownLag1];
        (_kernels, Shrink, FitRms) = FitKernels(channels, _cross);
        _shared = new double[channels];
        for (var c = 0; c < channels; c++)
        {
            var v = 0.0;
            foreach (var w in _kernels[c])
            {
                v += w * w;
            }
            _shared[c] = v;
        }
    }

    /// <summary>The channel count.</summary>
    public int Channels { get; }

    /// <summary>The factor the measure's cross-correlations were drawn at: 1 when the model reached them, below it when they were shrunk toward independent, 0 when they were not drawn.</summary>
    public double Shrink { get; }

    /// <summary>The root mean square of the fitted cross-correlation less the (shrunk) measure's, over every pair and offset up to the reach.</summary>
    public double FitRms { get; }

    /// <summary>True when nothing is shared (a mono plate, a plate with no noise to read, a measure shrunk to nothing): <see cref="Mix"/> returns its fields as given.</summary>
    public bool IsIdentity => Array.TrueForAll(_shared, static v => v < MinGain * MinGain);

    /// <summary>The share of channel <paramref name="c"/>'s variance the shared field carries.</summary>
    public double SharedVariance(int c) => _shared[c];

    /// <summary>Channel <paramref name="c"/>'s own lag-1 correlation as measured, of the 5 x 5 median's residual (the mean of the horizontal and the vertical, under the field's own: white noise reads -0.08), NaN where nothing was read.</summary>
    public double OwnLag1(int c) => _ownLag1[c];

    /// <summary>The measured correlation, of the 5 x 5 median's residual, of channel <paramref name="a"/> at a pixel with channel <paramref name="b"/> at that pixel plus (<paramref name="dy"/>, <paramref name="dx"/>), as given; <paramref name="a"/> is below <paramref name="b"/>.</summary>
    public double Cross(int a, int b, int dy, int dx)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(a, b);
        return _cross[PairIndex(Channels, a, b)][dy + Reach, dx + Reach];
    }

    /// <summary>
    /// The structure of <paramref name="plate"/>: each pair's cross-correlation at every offset up to the reach and each
    /// channel's own lag-1, read from the residual of a 5 x 5 median on blocks of the plate (at most
    /// <see cref="MaxSamples"/> residuals per channel), every pixel whose window touches <paramref name="absent"/>, the
    /// frame's edge or a non-finite value left out, each by <see cref="ChannelNoiseCorrelation.RobustCorrelation"/>. A plate
    /// with nothing to read answers no structure; a mono plate its one-channel identity.
    /// </summary>
    public static ChannelNoiseStructure Measure(Image plate, BitMatrix? absent)
    {
        var channels = plate.ChannelCount;
        var pairs = channels * (channels - 1) / 2;
        var cross = new double[pairs][,];
        for (var k = 0; k < pairs; k++)
        {
            cross[k] = new double[Side, Side];
        }
        var own = new double[channels];
        Array.Fill(own, double.NaN);
        if (channels < 2)
        {
            return new ChannelNoiseStructure(channels, cross, own);
        }
        var (residuals, blocks) = BlockResiduals(plate, absent);
        if (blocks == 0)
        {
            return new ChannelNoiseStructure(channels, cross, own);
        }
        var capacity = blocks * Block * Block;
        var u = new float[2 * capacity];
        var v = new float[2 * capacity];
        for (var c = 0; c < channels; c++)
        {
            var n = 0;
            n = Pairs(residuals[c], residuals[c], blocks, 0, 1, u, v, n);
            n = Pairs(residuals[c], residuals[c], blocks, 1, 0, u, v, n);
            own[c] = Read(u, v, n);
        }
        var index = 0;
        for (var a = 0; a < channels; a++)
        {
            for (var b = a + 1; b < channels; b++, index++)
            {
                for (var dy = -Reach; dy <= Reach; dy++)
                {
                    for (var dx = -Reach; dx <= Reach; dx++)
                    {
                        var n = Pairs(residuals[a], residuals[b], blocks, dy, dx, u, v, 0);
                        cross[index][dy + Reach, dx + Reach] = Read(u, v, n);
                    }
                }
            }
        }
        return new ChannelNoiseStructure(channels, cross, own);
    }

    /// <summary>
    /// The channels' fields with the structure: channel c becomes <c>sqrt(1 - v_c) * own_c' + w_c * G</c>, G one white field
    /// of unit variance drawn from <paramref name="sharedRng"/> (the only draw), <c>own_c'</c> the channel's field high-passed
    /// to the pooled (horizontal plus vertical) lag-1 that keeps the sum's what the field had, and unit variance; a target
    /// beyond the beta range is not reached. A channel with no shared part is its field,
    /// the same array; with nothing shared at all, so is every channel and <paramref name="sharedRng"/> is not touched.
    /// </summary>
    /// <param name="fields">One square field per channel, <paramref name="size"/> pixels a side, of unit variance.</param>
    public float[][] Mix(float[][] fields, int size, Random sharedRng)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(fields.Length, Channels);
        if (IsIdentity)
        {
            return fields;
        }
        var pixels = size * size;
        foreach (var field in fields)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(field.Length, pixels);
        }
        var padded = size + (2 * Reach);
        var shared = NoiseField.White(padded, padded, sharedRng);
        var mixed = new float[Channels][];
        for (var c = 0; c < Channels; c++)
        {
            var v = _shared[c];
            if (v < MinGain * MinGain)
            {
                mixed[c] = fields[c];
                continue;
            }
            var kernel = _kernels[c];
            var plane = new float[pixels];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var sum = 0.0;
                    for (var ky = 0; ky < Side; ky++)
                    {
                        var row = (y + ky) * padded;
                        for (var kx = 0; kx < Side; kx++)
                        {
                            sum += kernel[(ky * Side) + kx] * shared[row + x + kx];
                        }
                    }
                    plane[(y * size) + x] = (float)sum;
                }
            }
            var own = HoldLagOne(fields[c], size, v, SharedLagOne(kernel, v));
            var weight = (float)Math.Sqrt(1.0 - v);
            for (var i = 0; i < pixels; i++)
            {
                plane[i] += weight * own[i];
            }
            mixed[c] = plane;
        }
        return mixed;
    }

    /// <summary>The structure in words, for an export's log: each pair's correlation at the offsets (0,0), (0,1) and (1,1), each channel's own lag-1 and shared share, and what the fit took.</summary>
    public string InWords()
    {
        if (Channels < 2)
        {
            return "one channel";
        }
        var names = Channels == 3 ? new[] { "R", "G", "B" } : null;
        string Name(int c) => names?[c] ?? c.ToString(CultureInfo.InvariantCulture);
        var parts = new List<string>();
        for (var a = 0; a < Channels; a++)
        {
            for (var b = a + 1; b < Channels; b++)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{Name(a)}-{Name(b)} {Cross(a, b, 0, 0):F3} / {Cross(a, b, 0, 1):F3} / {Cross(a, b, 1, 1):F3}"));
            }
        }
        var lag1 = new List<string>();
        var share = new List<string>();
        for (var c = 0; c < Channels; c++)
        {
            lag1.Add(_ownLag1[c].ToString("F3", CultureInfo.InvariantCulture));
            share.Add(_shared[c].ToString("F3", CultureInfo.InvariantCulture));
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{string.Join(", ", parts)} at offsets (dy,dx) (0,0) / (0,1) / (1,1); own lag-1 {string.Join(" / ", lag1)}; shared share {string.Join(" / ", share)}; fitted at {Shrink:F2} of the measure, rms {FitRms:F4}");
    }

    private static int PairIndex(int channels, int a, int b) => (a * ((2 * channels) - a - 1) / 2) + (b - a - 1);

    private static double Read(float[] u, float[] v, int n)
    {
        if (n < MinLagSamples)
        {
            return 0.0;
        }
        var measured = ChannelNoiseCorrelation.RobustCorrelation(u.AsSpan(0, n), v.AsSpan(0, n));
        return double.IsFinite(measured) ? Math.Clamp(measured, -0.99, 0.99) : 0.0;
    }

    /// <summary>
    /// Appends to <paramref name="u"/> and <paramref name="v"/>, from <paramref name="n"/>, every pair of residuals
    /// (<paramref name="a"/> at a pixel, <paramref name="b"/> at that pixel plus (dy, dx)) that lies in one block and is
    /// finite at both ends; returns the new count.
    /// </summary>
    private static int Pairs(float[] a, float[] b, int blocks, int dy, int dx, float[] u, float[] v, int n)
    {
        var y0 = Math.Max(0, -dy);
        var y1 = Math.Min(Block, Block - dy);
        var x0 = Math.Max(0, -dx);
        var x1 = Math.Min(Block, Block - dx);
        for (var k = 0; k < blocks; k++)
        {
            var origin = k * Block * Block;
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var p = a[origin + (y * Block) + x];
                    var q = b[origin + ((y + dy) * Block) + x + dx];
                    if (float.IsFinite(p) && float.IsFinite(q))
                    {
                        u[n] = p;
                        v[n] = q;
                        n++;
                    }
                }
            }
        }
        return n;
    }

    /// <summary>
    /// Each channel's residual from its 5 x 5 median over square blocks of the plate, <see cref="Block"/> pixels a side,
    /// laid on a grid sparse enough to hold at most <see cref="MaxSamples"/> residuals; NaN where the window is not usable.
    /// A block is whole, so a pair at any offset up to the reach is read inside it.
    /// </summary>
    private static (float[][] Residuals, int Blocks) BlockResiduals(Image plate, BitMatrix? absent)
    {
        var (channels, width, height) = plate.Shape;
        const int reach = 2;
        var inner = (long)Math.Max(0, width - (2 * reach)) * Math.Max(0, height - (2 * reach));
        var step = Math.Max(Block, (int)Math.Ceiling(Block * Math.Sqrt(inner / (double)MaxSamples)));
        var origins = new List<(int X, int Y)>();
        for (var y = reach; y + Block <= height - reach; y += step)
        {
            for (var x = reach; x + Block <= width - reach; x += step)
            {
                origins.Add((x, y));
            }
        }
        var residuals = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            residuals[c] = new float[origins.Count * Block * Block];
            Array.Fill(residuals[c], float.NaN);
        }
        var planes = plate.ResidentPlanes();
        Span<float> window = stackalloc float[(2 * reach + 1) * (2 * reach + 1)];
        for (var k = 0; k < origins.Count; k++)
        {
            var (ox, oy) = origins[k];
            for (var by = 0; by < Block; by++)
            {
                for (var bx = 0; bx < Block; bx++)
                {
                    var (x, y) = (ox + bx, oy + by);
                    if (!ChannelNoiseCorrelation.Usable(planes, absent, x, y, reach))
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
                        residuals[c][(k * Block * Block) + (by * Block) + bx] = data[y, x] - StatisticsHelper.MedianFast(window);
                    }
                }
            }
        }
        return (residuals, origins.Count);
    }

    /// <summary>The kernel's own lag-1 correlation, the mean over its four neighbours of its autocorrelation over its energy.</summary>
    private static double SharedLagOne(double[] kernel, double energy)
    {
        var sum = 0.0;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (Math.Abs(dy) + Math.Abs(dx) != 1)
                {
                    continue;
                }
                sum += Correlate(kernel, kernel, dy, dx);
            }
        }
        return sum / 4.0 / energy;
    }

    /// <summary>The sum over e of <paramref name="a"/>(e + (dy, dx)) times <paramref name="b"/>(e), both kernels read where inside their square.</summary>
    private static double Correlate(double[] a, double[] b, int dy, int dx)
    {
        var sum = 0.0;
        for (var ey = -Reach; ey <= Reach; ey++)
        {
            var ay = ey + dy;
            if (ay < -Reach || ay > Reach)
            {
                continue;
            }
            for (var ex = -Reach; ex <= Reach; ex++)
            {
                var ax = ex + dx;
                if (ax < -Reach || ax > Reach)
                {
                    continue;
                }
                sum += a[((ay + Reach) * Side) + ax + Reach] * b[((ey + Reach) * Side) + ex + Reach];
            }
        }
        return sum;
    }

    /// <summary>
    /// The field high-passed (<c>field - beta * mean of its four neighbours</c>, an edge repeated) by the beta that makes
    /// the lag-1 of <c>sqrt(1 - shared) * result + a field of lag-1 <paramref name="sharedLag1"/> and energy
    /// <paramref name="shared"/></c> the field's own, then scaled to unit variance. The lag-1 is the POOLED horizontal plus
    /// vertical one, so the per-axis lag-1 is not held. A beta the target lies beyond is held at the end of its range, the
    /// target is then not reached, and nothing says so.
    /// </summary>
    private static float[] HoldLagOne(float[] field, int size, double shared, double sharedLag1)
    {
        var pixels = size * size;
        var mean = new float[pixels];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var left = field[(y * size) + Math.Max(0, x - 1)];
                var right = field[(y * size) + Math.Min(size - 1, x + 1)];
                var up = field[(Math.Max(0, y - 1) * size) + x];
                var down = field[(Math.Min(size - 1, y + 1) * size) + x];
                mean[(y * size) + x] = 0.25f * (left + right + up + down);
            }
        }
        var (loo, lon, lno, lnn) = (Lag(field, field, size), Lag(field, mean, size), Lag(mean, field, size), Lag(mean, mean, size));
        var (soo, son, snn) = (Dot(field, field), Dot(field, mean), Dot(mean, mean));
        var pairs = 2.0 * size * (size - 1);
        double Lag1At(double beta)
        {
            var cov = (loo - (beta * (lon + lno)) + (beta * beta * lnn)) / pairs;
            var variance = (soo - (2 * beta * son) + (beta * beta * snn)) / pixels;
            return cov / variance;
        }
        var own = loo / pairs / (soo / pixels);
        var target = (own - (shared * sharedLag1)) / (1.0 - shared);
        double lo = -1.95, hi = 1.95, beta;
        if (target >= Lag1At(lo))
        {
            beta = lo;
        }
        else if (target <= Lag1At(hi))
        {
            beta = hi;
        }
        else
        {
            for (var i = 0; i < 40; i++)
            {
                var mid = 0.5 * (lo + hi);
                if (Lag1At(mid) > target)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            beta = 0.5 * (lo + hi);
        }
        var sd = Math.Sqrt((soo - (2 * beta * son) + (beta * beta * snn)) / pixels);
        var scale = sd > 0 ? 1.0 / sd : 1.0;
        var result = new float[pixels];
        for (var i = 0; i < pixels; i++)
        {
            result[i] = (float)((field[i] - (beta * mean[i])) * scale);
        }
        return result;
    }

    /// <summary>The sum over every horizontal and vertical neighbour pair of <paramref name="p"/> at a pixel times <paramref name="q"/> at the next.</summary>
    private static double Lag(float[] p, float[] q, int size)
    {
        var sum = 0.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size) + x;
                if (x + 1 < size)
                {
                    sum += (double)p[i] * q[i + 1];
                }
                if (y + 1 < size)
                {
                    sum += (double)p[i] * q[i + size];
                }
            }
        }
        return sum;
    }

    private static double Dot(float[] p, float[] q)
    {
        var sum = 0.0;
        for (var i = 0; i < p.Length; i++)
        {
            sum += (double)p[i] * q[i];
        }
        return sum;
    }

    // ---- the fit ----

    private const int FitSide = (4 * Reach) + 1;
    private const double VariancePenalty = 20.0;
    private const double OutsideWeight = 0.5;

    /// <summary>
    /// The instrument's reading of a cross-covariance, as a linear map: the residual from a 5 x 5 median is, for the
    /// near-Gaussian noise of a master, close to the residual from a 5 x 5 mean, <c>x - box(x)</c>, and the covariance of two
    /// such residuals at offset d is the sum over r of K(r) times the covariance of the fields at d + r, K the
    /// autocorrelation of <c>h = delta - box / 25</c>. It takes about a third of the correlation of a field smooth over a few
    /// pixels (a draw of R-G 0.144 reads 0.098; the mean's own map says 0.0986), and is what makes the fit reach what the
    /// measure SAW rather than what the field holds. Indexed (ry, rx) from -2 * Reach as <c>[(ry + 2 * Reach) * FitSide + rx + 2 * Reach]</c>.
    /// </summary>
    private static readonly double[] InstrumentCorrelation = BuildInstrument();

    /// <summary>The instrument's variance of white noise of unit variance, <c>K(0)</c>: what the measure's correlation is normalised by, the shape of the noise drawn beside the shared part taken as white.</summary>
    private static double InstrumentVariance => InstrumentCorrelation[((2 * Reach) * FitSide) + (2 * Reach)];

    private static double[] BuildInstrument()
    {
        var h = new double[Side, Side];
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                h[y, x] = -1.0 / Taps;
            }
        }
        h[Reach, Reach] += 1.0;
        var k = new double[FitSide * FitSide];
        for (var ry = -2 * Reach; ry <= 2 * Reach; ry++)
        {
            for (var rx = -2 * Reach; rx <= 2 * Reach; rx++)
            {
                var sum = 0.0;
                for (var uy = 0; uy < Side; uy++)
                {
                    for (var ux = 0; ux < Side; ux++)
                    {
                        var (vy, vx) = (uy + ry, ux + rx);
                        if (vy >= 0 && vy < Side && vx >= 0 && vx < Side)
                        {
                            sum += h[uy, ux] * h[vy, vx];
                        }
                    }
                }
                k[((ry + (2 * Reach)) * FitSide) + rx + (2 * Reach)] = sum;
            }
        }
        return k;
    }

    /// <summary>
    /// Kernels whose pairwise cross-correlations are the measure's, shrunk by a factor of 0.85 at a time until the fit is
    /// within <see cref="FitTolerance"/> and no channel's share passes <see cref="MaxSharedVariance"/>; all zero when no
    /// shrink reaches it, or when the measure has nothing in it.
    /// </summary>
    private static (double[][] Kernels, double Shrink, double Rms) FitKernels(int channels, double[][,] cross)
    {
        var kernels = new double[channels][];
        for (var c = 0; c < channels; c++)
        {
            kernels[c] = new double[Taps];
        }
        var peak = 0.0;
        foreach (var m in cross)
        {
            foreach (var r in m)
            {
                peak = Math.Max(peak, Math.Abs(r));
            }
        }
        if (channels < 2 || peak < 1e-3)
        {
            return (kernels, channels < 2 ? 1.0 : 0.0, 0.0);
        }
        for (var shrink = 1.0; shrink >= 0.05; shrink *= 0.85)
        {
            var (w, rms) = Solve(channels, cross, shrink);
            var feasible = rms <= FitTolerance;
            for (var c = 0; c < channels && feasible; c++)
            {
                var v = 0.0;
                for (var i = 0; i < Taps; i++)
                {
                    v += w[(c * Taps) + i] * w[(c * Taps) + i];
                }
                feasible = v <= MaxSharedVariance * 1.001;
            }
            if (feasible)
            {
                for (var c = 0; c < channels; c++)
                {
                    Array.Copy(w, c * Taps, kernels[c], 0, Taps);
                }
                return (kernels, shrink, rms);
            }
        }
        return (kernels, 0.0, 0.0);
    }

    /// <summary>The best of a few starts' least-squares fit of the kernels to <paramref name="scale"/> times the measure, and its root mean square over the offsets up to the reach.</summary>
    private static (double[] Kernels, double Rms) Solve(int channels, double[][,] cross, double scale)
    {
        var pairs = cross.Length;
        var rows = (pairs * FitSide * FitSide) + channels;
        var parameters = channels * Taps;
        var target = new double[pairs * FitSide * FitSide];
        var weight = new double[target.Length];
        for (var k = 0; k < pairs; k++)
        {
            for (var dy = -2 * Reach; dy <= 2 * Reach; dy++)
            {
                for (var dx = -2 * Reach; dx <= 2 * Reach; dx++)
                {
                    var i = (((k * FitSide) + dy + (2 * Reach)) * FitSide) + dx + (2 * Reach);
                    var inside = Math.Abs(dy) <= Reach && Math.Abs(dx) <= Reach;
                    target[i] = inside ? scale * InstrumentVariance * cross[k][dy + Reach, dx + Reach] : 0.0;
                    weight[i] = inside ? 1.0 : OutsideWeight;
                }
            }
        }

        double[]? best = null;
        var bestCost = double.PositiveInfinity;
        foreach (var width in new[] { 1.0, 0.7, 1.5 })
        {
            var w = Start(channels, cross, width);
            var cost = LevenbergMarquardt(channels, w, target, weight, rows, parameters);
            if (cost < bestCost)
            {
                (best, bestCost) = (w, cost);
            }
        }
        var solved = best ?? new double[parameters];
        var residual = new double[rows];
        Residuals(channels, solved, target, weight, residual, null);
        var squares = 0.0;
        var count = 0;
        for (var k = 0; k < pairs; k++)
        {
            for (var dy = -Reach; dy <= Reach; dy++)
            {
                for (var dx = -Reach; dx <= Reach; dx++)
                {
                    var r = residual[(((k * FitSide) + dy + (2 * Reach)) * FitSide) + dx + (2 * Reach)];
                    squares += r * r;
                    count++;
                }
            }
        }
        return (solved, Math.Sqrt(squares / count));
    }

    /// <summary>A start: each channel a bump of the given width whose energy is the root of its mean zero-offset correlation, signed as its sum with channel 0.</summary>
    private static double[] Start(int channels, double[][,] cross, double width)
    {
        var w = new double[channels * Taps];
        for (var c = 0; c < channels; c++)
        {
            var amplitude = 0.0;
            var count = 0;
            var sign = 1.0;
            for (var a = 0; a < channels; a++)
            {
                for (var b = a + 1; b < channels; b++)
                {
                    if (a != c && b != c)
                    {
                        continue;
                    }
                    amplitude += Math.Abs(cross[PairIndex(channels, a, b)][Reach, Reach]);
                    count++;
                    if (a == 0 && b == c)
                    {
                        var total = 0.0;
                        foreach (var r in cross[PairIndex(channels, a, b)])
                        {
                            total += r;
                        }
                        sign = total < 0 ? -1.0 : 1.0;
                    }
                }
            }
            amplitude = Math.Sqrt(Math.Max(0.02, count > 0 ? amplitude / count : 0.0));
            var norm = 0.0;
            for (var y = 0; y < Side; y++)
            {
                for (var x = 0; x < Side; x++)
                {
                    var (dy, dx) = (y - Reach, x - Reach);
                    var g = Math.Exp(-((dy * dy) + (dx * dx)) / (2.0 * width * width));
                    w[(c * Taps) + (y * Side) + x] = g;
                    norm += g * g;
                }
            }
            for (var i = 0; i < Taps; i++)
            {
                w[(c * Taps) + i] *= sign * amplitude / Math.Sqrt(norm);
            }
        }
        return w;
    }

    /// <summary>
    /// The residuals of the kernels' pairwise cross-correlations (<c>sum over e of a(e + d) * b(e)</c>) from the target at
    /// every offset d up to twice the reach, weighted, then one row a channel for the energy past the cap; with
    /// <paramref name="jacobian"/> (rows by parameters, dense, row-major), its derivatives too.
    /// </summary>
    private static void Residuals(int channels, double[] w, double[] target, double[] weight, double[] residual, double[]? jacobian)
    {
        var parameters = channels * Taps;
        if (jacobian is not null)
        {
            Array.Clear(jacobian);
        }
        var index = 0;
        // The kernels' own cross-correlation (raw) at every offset up to twice the reach, and its derivatives on the two
        // channels' taps (the first Taps columns channel a's, the next b's); the instrument then reads it.
        var raw = new double[FitSide * FitSide];
        var rawJacobian = jacobian is null ? null : new double[FitSide * FitSide * 2 * Taps];
        for (var a = 0; a < channels; a++)
        {
            for (var b = a + 1; b < channels; b++)
            {
                Array.Clear(raw);
                rawJacobian?.AsSpan().Clear();
                for (var dy = -2 * Reach; dy <= 2 * Reach; dy++)
                {
                    for (var dx = -2 * Reach; dx <= 2 * Reach; dx++)
                    {
                        var d = ((dy + (2 * Reach)) * FitSide) + dx + (2 * Reach);
                        var model = 0.0;
                        for (var ey = -Reach; ey <= Reach; ey++)
                        {
                            var ay = ey + dy;
                            if (ay < -Reach || ay > Reach)
                            {
                                continue;
                            }
                            for (var ex = -Reach; ex <= Reach; ex++)
                            {
                                var ax = ex + dx;
                                if (ax < -Reach || ax > Reach)
                                {
                                    continue;
                                }
                                var (la, lb) = (((ay + Reach) * Side) + ax + Reach, ((ey + Reach) * Side) + ex + Reach);
                                var (wa, wb) = (w[(a * Taps) + la], w[(b * Taps) + lb]);
                                model += wa * wb;
                                if (rawJacobian is not null)
                                {
                                    rawJacobian[(d * 2 * Taps) + la] += wb;
                                    rawJacobian[(d * 2 * Taps) + Taps + lb] += wa;
                                }
                            }
                        }
                        raw[d] = model;
                    }
                }
                for (var dy = -2 * Reach; dy <= 2 * Reach; dy++)
                {
                    for (var dx = -2 * Reach; dx <= 2 * Reach; dx++, index++)
                    {
                        var read = 0.0;
                        var row = index * parameters;
                        for (var ry = -2 * Reach; ry <= 2 * Reach; ry++)
                        {
                            var py = dy + ry;
                            if (py < -2 * Reach || py > 2 * Reach)
                            {
                                continue;
                            }
                            for (var rx = -2 * Reach; rx <= 2 * Reach; rx++)
                            {
                                var px = dx + rx;
                                if (px < -2 * Reach || px > 2 * Reach)
                                {
                                    continue;
                                }
                                var k = InstrumentCorrelation[((ry + (2 * Reach)) * FitSide) + rx + (2 * Reach)];
                                var d = ((py + (2 * Reach)) * FitSide) + px + (2 * Reach);
                                read += k * raw[d];
                                if (jacobian is not null && rawJacobian is not null)
                                {
                                    for (var t = 0; t < Taps; t++)
                                    {
                                        jacobian[row + (a * Taps) + t] += weight[index] * k * rawJacobian[(d * 2 * Taps) + t];
                                        jacobian[row + (b * Taps) + t] += weight[index] * k * rawJacobian[(d * 2 * Taps) + Taps + t];
                                    }
                                }
                            }
                        }
                        residual[index] = weight[index] * (read - target[index]);
                    }
                }
            }
        }
        for (var c = 0; c < channels; c++, index++)
        {
            var v = 0.0;
            for (var i = 0; i < Taps; i++)
            {
                v += w[(c * Taps) + i] * w[(c * Taps) + i];
            }
            var over = v - MaxSharedVariance;
            residual[index] = over > 0 ? VariancePenalty * over : 0.0;
            if (jacobian is not null && over > 0)
            {
                for (var i = 0; i < Taps; i++)
                {
                    jacobian[(index * parameters) + (c * Taps) + i] = VariancePenalty * 2.0 * w[(c * Taps) + i];
                }
            }
        }
    }

    /// <summary>Levenberg-Marquardt on <paramref name="w"/>, in place; returns the half sum of squares it ends at.</summary>
    private static double LevenbergMarquardt(int channels, double[] w, double[] target, double[] weight, int rows, int parameters)
    {
        var residual = new double[rows];
        var jacobian = new double[rows * parameters];
        var trial = new double[parameters];
        var trialResidual = new double[rows];
        var normal = new double[parameters * parameters];
        var gradient = new double[parameters];
        var step = new double[parameters];
        Residuals(channels, w, target, weight, residual, jacobian);
        var cost = 0.5 * Dot(residual);
        var lambda = 1e-3;
        for (var iteration = 0; iteration < 300 && lambda < 1e12; iteration++)
        {
            Array.Clear(normal);
            Array.Clear(gradient);
            for (var r = 0; r < rows; r++)
            {
                var row = r * parameters;
                var f = residual[r];
                for (var i = 0; i < parameters; i++)
                {
                    var ji = jacobian[row + i];
                    if (ji == 0.0)
                    {
                        continue;
                    }
                    gradient[i] += ji * f;
                    var line = i * parameters;
                    for (var j = 0; j <= i; j++)
                    {
                        normal[line + j] += ji * jacobian[row + j];
                    }
                }
            }
            var accepted = false;
            while (lambda < 1e12)
            {
                if (SolveDamped(normal, gradient, parameters, lambda, step))
                {
                    for (var i = 0; i < parameters; i++)
                    {
                        trial[i] = w[i] - step[i];
                    }
                    Residuals(channels, trial, target, weight, trialResidual, null);
                    var trialCost = 0.5 * Dot(trialResidual);
                    if (trialCost < cost)
                    {
                        var gain = cost - trialCost;
                        Array.Copy(trial, w, parameters);
                        cost = trialCost;
                        lambda = Math.Max(lambda / 3.0, 1e-12);
                        accepted = true;
                        Residuals(channels, w, target, weight, residual, jacobian);
                        if (gain < 1e-14 * (1.0 + cost))
                        {
                            return cost;
                        }
                        break;
                    }
                }
                lambda *= 4.0;
            }
            if (!accepted)
            {
                break;
            }
        }
        return cost;
    }

    /// <summary>Solves <c>(N + lambda diag(N)) x = g</c> for the lower triangle of <paramref name="normal"/> by Cholesky; false when it is not positive definite.</summary>
    private static bool SolveDamped(double[] normal, double[] gradient, int n, double lambda, double[] x)
    {
        var l = new double[n * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var sum = normal[(i * n) + j];
                if (i == j)
                {
                    sum += lambda * Math.Max(normal[(i * n) + i], 1e-9);
                }
                for (var k = 0; k < j; k++)
                {
                    sum -= l[(i * n) + k] * l[(j * n) + k];
                }
                if (i == j)
                {
                    if (!(sum > 1e-300))
                    {
                        return false;
                    }
                    l[(i * n) + i] = Math.Sqrt(sum);
                }
                else
                {
                    l[(i * n) + j] = sum / l[(j * n) + j];
                }
            }
        }
        for (var i = 0; i < n; i++)
        {
            var sum = gradient[i];
            for (var k = 0; k < i; k++)
            {
                sum -= l[(i * n) + k] * x[k];
            }
            x[i] = sum / l[(i * n) + i];
        }
        for (var i = n - 1; i >= 0; i--)
        {
            var sum = x[i];
            for (var k = i + 1; k < n; k++)
            {
                sum -= l[(k * n) + i] * x[k];
            }
            x[i] = sum / l[(i * n) + i];
        }
        return true;
    }

    private static double Dot(double[] v)
    {
        var sum = 0.0;
        foreach (var x in v)
        {
            sum += x * x;
        }
        return sum;
    }
}
