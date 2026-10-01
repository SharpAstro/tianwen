using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// R8's wavelet gains derived from the measured blur and noise (docs/plans/planetary-restoration.md, R8, #1055): the Wiener filter a
/// stack's own power, its noise and a kernel give, W = (1 - N / P_S)+ / H with no model of the object, and the a trous gains that come
/// nearest it, fitted JOINTLY over the layers (R8 part 1). Each Fourier coefficient is weighted by the stack's power there times the
/// scored bands' own transfers squared: under that model the weighted distance to W is the expected error in those bands less a
/// constant, the quantity <see cref="PlanetaryCeilings.PerBandJointOracle"/> minimises against a truth, so the fit has no knob.
/// </summary>
/// <remarks>
/// Every power is read INSIDE the disk (<see cref="Interior"/>), where the bands are scored. Over the whole window the limb, a step of
/// the disk's full brightness, holds most of the power at every frequency, and a Wiener filter built on it follows the limb's
/// signal-to-noise, not the belts': on a unit test's texture it asked for a gain near 20 at 0.25 cycles a pixel. The layers coarser
/// than the scored bands stay at 1: they reach the limb from the interior (two of them 126 px), which no interior spectrum sees, and
/// weighted by the stack's power alone the lowest frequencies set them, and with them a sixfold error on that texture.
/// </remarks>
public static class PlanetaryWaveletGains
{
    /// <summary>The presets' layer count (the Registax convention, <see cref="WaveletSharpenOptions.PlanetaryDefault"/>).</summary>
    public const int Scales = 6;

    /// <summary>The bands R8 scores, 1 to 4, and the layers it fits.</summary>
    public const int ScoredBands = 4;

    /// <summary>A kernel's transfer below which nothing is restored (R8 part 2's cut).</summary>
    public const double MinTransfer = 0.02;

    /// <summary>
    /// <paramref name="plane"/>'s mean power on a Fourier coefficient, ring by ring, on the padded grid every inverse here works on
    /// (<see cref="PlanetaryInverse.WhiteNoise"/>'s units).
    /// </summary>
    public static ImmutableArray<double> RingPower(ReadOnlySpan<float> plane, int width, int height)
    {
        var n = PlanetaryInverse.GridFor(width, height, 32);
        var field = PlanetaryInverse.Transform(plane, width, height, n);
        var (sum, count) = (new double[n], new int[n]);
        for (var i = 0; i < field.Length; i++)
        {
            var ring = PlanetaryCeilings.Ring(i, n);
            sum[ring] += (field[i].Real * field[i].Real) + (field[i].Imaginary * field[i].Imaginary);
            count[ring]++;
        }
        var power = ImmutableArray.CreateBuilder<double>(n);
        for (var r = 0; r < n; r++)
        {
            power.Add(count[r] > 0 ? sum[r] / count[r] : 0);
        }
        return power.MoveToImmutable();
    }

    /// <summary>
    /// <paramref name="plane"/> inside <paramref name="disk"/>: its mean there taken out, then tapered by a raised cosine from 0.8 to 0.9
    /// radii (<see cref="PlanetaryMetrics"/>' scored region) to zero, so no edge of the disk or of the cut reaches its spectrum.
    /// </summary>
    public static float[] Interior(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var taper = new double[width * height];
        double sum = 0, weight = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = disk.RadiiAt(x, y);
                var t = r <= 0.8 ? 1 : r >= PlanetaryMetrics.InnerRadii ? 0 : 0.5 * (1 + Math.Cos(Math.PI * (r - 0.8) / (PlanetaryMetrics.InnerRadii - 0.8)));
                var i = (y * width) + x;
                taper[i] = t;
                sum += t * plane[i];
                weight += t;
            }
        }
        var mean = weight > 0 ? sum / weight : 0;
        var result = new float[width * height];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (float)((plane[i] - mean) * taper[i]);
        }
        return result;
    }

    /// <summary>The stack's power inside <paramref name="disk"/>, ring by ring (<see cref="Interior"/>, <see cref="RingPower"/>).</summary>
    public static ImmutableArray<double> StackPower(ReadOnlySpan<float> stack, int width, int height, MetricDisk disk) =>
        RingPower(Interior(stack, width, height, disk), width, height);

    /// <summary>
    /// The noise of a stack of two halves inside <paramref name="disk"/>, ring by ring: the power of half their difference, which holds
    /// the stack's own noise and none of its signal, coloured as the stacking coloured it.
    /// </summary>
    public static ImmutableArray<double> HalvesNoise(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int width, int height, MetricDisk disk)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("The halves differ in size.", nameof(b));
        }
        var difference = new float[a.Length];
        for (var i = 0; i < difference.Length; i++)
        {
            difference[i] = (a[i] - b[i]) / 2;
        }
        return RingPower(Interior(difference, width, height, disk), width, height);
    }

    /// <summary>
    /// The Wiener filter, ring by ring, that <paramref name="stackPower"/>, <paramref name="noise"/> and <paramref name="transfer"/> give
    /// (each ring r at r / n cycles a pixel, n the padded grid): (1 - N / P_S)+ / H, zero where H is under <see cref="MinTransfer"/>.
    /// </summary>
    public static ImmutableArray<double> Wiener(ImmutableArray<double> stackPower, ImmutableArray<double> noise, Func<double, double> transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var n = stackPower.Length;
        var w = ImmutableArray.CreateBuilder<double>(n);
        for (var r = 0; r < n; r++)
        {
            var h = transfer(r / (double)n);
            w.Add(h < MinTransfer || stackPower[r] <= 0 ? 0 : Math.Max(0, 1 - (noise[r] / stackPower[r])) / h);
        }
        return w.MoveToImmutable();
    }

    /// <summary>
    /// The <paramref name="scales"/> gains, finest first, that leave the least expected error in the scored bands inside 0.9 radii of
    /// <paramref name="disk"/>, the planet taken as its disk plus a texture: the <paramref name="fitted"/> finest fitted jointly, the
    /// coarser layers and the residual left at 1.
    /// <list type="bullet">
    /// <item>The disk is deterministic: <paramref name="sharpDisk"/> (the limb fit's model, <see cref="PlanetaryLimbFit.SharpModel"/>)
    /// against <paramref name="blurredDisk"/>, the same through the kernel, as <see cref="PlanetaryCeilings.PerBandJointOracle"/> fits
    /// a stack against a truth. This is where a gain's cost at the limb is counted: a step of the disk's full brightness just outside
    /// the scored region, which no spectrum of the interior sees.</item>
    /// <item>The texture is stationary: its Wiener filter <paramref name="wiener"/> over every Fourier coefficient of the padded grid,
    /// each weighted by the stack's power inside the disk (<paramref name="stackPower"/>) times the sum of the fitted layers'
    /// transfers squared, the expected error in the bands they make.</item>
    /// </list>
    /// The <paramref name="held"/> finest layers can be held at 1 too, for a kernel that is not measured there: their bands' error
    /// still counts, and the other gains are fitted around them.
    /// </summary>
    public static ImmutableArray<double> Fit(ImmutableArray<double> stackPower, ImmutableArray<double> wiener, ReadOnlySpan<float> sharpDisk, ReadOnlySpan<float> blurredDisk,
        int width, int height, MetricDisk disk, int scales = Scales, int fitted = ScoredBands, int held = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fitted, scales);
        ArgumentOutOfRangeException.ThrowIfNegative(held);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(held, fitted);
        var (a, b) = NormalEquations(stackPower, wiener, sharpDisk, blurredDisk, width, height, disk, fitted);
        // The held gains are 1: what they put into the others' equations moves to the right-hand side.
        var free = fitted - held;
        var (af, bf) = (new double[free, free], new double[free]);
        for (var j = 0; j < free; j++)
        {
            bf[j] = b[held + j];
            for (var h = 0; h < held; h++)
            {
                bf[j] -= a[held + j, h];
            }
            for (var k = 0; k < free; k++)
            {
                af[j, k] = a[held + j, held + k];
            }
        }
        var solved = PlanetaryCeilings.Solve(af, bf);
        var gains = ImmutableArray.CreateBuilder<double>(scales);
        for (var j = 0; j < scales; j++)
        {
            gains.Add(j >= held && j < fitted ? solved[j - held] : 1);
        }
        return gains.MoveToImmutable();
    }

    /// <summary>
    /// <see cref="Fit(ImmutableArray{double}, ImmutableArray{double}, ReadOnlySpan{float}, ReadOnlySpan{float}, int, int, MetricDisk, int, int, int)"/>'s
    /// gains under the constraint that their filter times <paramref name="kernel"/>, taken to the image, is at or above zero within
    /// <paramref name="reach"/> px of its centre (Magain, Courbin and Sohy 1998: restore toward a non-negative target, never past it):
    /// a composite that cannot dig below the sky (R8 follow-up 1). A small quadratic program over the fitted gains, solved by a quadratic
    /// penalty on the violated points, raised until none is violated by more than a millionth of the composite's peak.
    /// </summary>
    public static ImmutableArray<double> FitNonNegative(ImmutableArray<double> stackPower, ImmutableArray<double> wiener, ReadOnlySpan<float> sharpDisk, ReadOnlySpan<float> blurredDisk,
        int width, int height, MetricDisk disk, Func<double, double> kernel, int reach = 15, int scales = Scales, int fitted = ScoredBands)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fitted, scales);
        var (a, b) = NormalEquations(stackPower, wiener, sharpDisk, blurredDisk, width, height, disk, fitted);
        var (rows, offsets) = PlanetaryDering.CompositeRows(kernel, fitted, reach);
        return Gains(SolveNonNegative(a, b, rows, offsets), scales);
    }

    /// <summary>
    /// min x' a x - 2 b' x subject to rows x + offsets at or above zero, by a quadratic penalty on the violated rows, raised a decade at a
    /// time until no row is violated by more than a millionth of the largest offset.
    /// </summary>
    internal static double[] SolveNonNegative(double[,] a, double[] b, double[][] rows, double[] offsets)
    {
        var n = b.Length;
        var x = PlanetaryCeilings.Solve(a, b);
        double peak = 0, diagonal = 0, rowScale = 0;
        foreach (var o in offsets)
        {
            peak = Math.Max(peak, Math.Abs(o));
        }
        for (var j = 0; j < n; j++)
        {
            diagonal = Math.Max(diagonal, a[j, j]);
        }
        foreach (var row in rows)
        {
            foreach (var v in row)
            {
                rowScale = Math.Max(rowScale, v * v);
            }
        }
        var tolerance = 1e-6 * Math.Max(peak, 1e-12);
        var rho = diagonal / Math.Max(rowScale, 1e-300);
        for (var outer = 0; outer < 16; outer++)
        {
            for (var inner = 0; inner < 50; inner++)
            {
                var (m, r) = ((double[,])a.Clone(), (double[])b.Clone());
                for (var i = 0; i < rows.Length; i++)
                {
                    var value = offsets[i];
                    for (var j = 0; j < n; j++)
                    {
                        value += rows[i][j] * x[j];
                    }
                    if (value >= 0)
                    {
                        continue;
                    }
                    for (var j = 0; j < n; j++)
                    {
                        r[j] -= rho * rows[i][j] * offsets[i];
                        for (var k = 0; k < n; k++)
                        {
                            m[j, k] += rho * rows[i][j] * rows[i][k];
                        }
                    }
                }
                var next = PlanetaryCeilings.Solve(m, r);
                double change = 0;
                for (var j = 0; j < n; j++)
                {
                    change = Math.Max(change, Math.Abs(next[j] - x[j]));
                }
                x = next;
                if (change < 1e-12)
                {
                    break;
                }
            }
            double worst = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                var value = offsets[i];
                for (var j = 0; j < n; j++)
                {
                    value += rows[i][j] * x[j];
                }
                worst = Math.Max(worst, -value);
            }
            if (worst <= tolerance)
            {
                break;
            }
            rho *= 10;
        }
        return x;
    }

    // Both halves of the fit's least squares, the texture's and the disk's, summed.
    private static (double[,] A, double[] B) NormalEquations(ImmutableArray<double> stackPower, ImmutableArray<double> wiener, ReadOnlySpan<float> sharpDisk, ReadOnlySpan<float> blurredDisk,
        int width, int height, MetricDisk disk, int fitted)
    {
        var (a, b) = TextureNormalEquations(stackPower, wiener, fitted);
        var (da, db) = PlanetaryCeilings.JointNormalEquations(blurredDisk, sharpDisk, width, height, disk, fitted);
        for (var j = 0; j < fitted; j++)
        {
            b[j] += db[j];
            for (var k = 0; k < fitted; k++)
            {
                a[j, k] += da[j, k];
            }
        }
        return (a, b);
    }

    /// <summary>
    /// The gains for the stationary texture alone, the disk left out: what that costs at the limb is why <see cref="Fit"/> does not
    /// leave it out.
    /// </summary>
    public static ImmutableArray<double> FitTexture(ImmutableArray<double> stackPower, ImmutableArray<double> wiener, int scales = Scales, int fitted = ScoredBands)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fitted, scales);
        var (a, b) = TextureNormalEquations(stackPower, wiener, fitted);
        return Gains(PlanetaryCeilings.Solve(a, b), scales);
    }

    // The fitted gains, then 1 for every coarser layer.
    private static ImmutableArray<double> Gains(double[] solved, int scales)
    {
        var gains = ImmutableArray.CreateBuilder<double>(scales);
        for (var j = 0; j < scales; j++)
        {
            gains.Add(j < solved.Length ? solved[j] : 1);
        }
        return gains.MoveToImmutable();
    }

    // The texture's expected error in the fitted bands as x' a x - 2 b' x plus a constant, in the units of a sum over pixels (Parseval's
    // 1 / n^2 on the unnormalised transform).
    private static (double[,] A, double[] B) TextureNormalEquations(ImmutableArray<double> stackPower, ImmutableArray<double> wiener, int fitted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fitted);
        var n = stackPower.Length;
        var parseval = 1.0 / ((double)n * n);
        var a = new double[fitted, fitted];
        var b = new double[fitted];
        var phi = new double[fitted + 1];
        var psi = new double[fitted];
        for (var i = 0; i < n * n; i++)
        {
            var ring = PlanetaryCeilings.Ring(i, n);
            if (stackPower[ring] <= 0)
            {
                continue;
            }
            var (ky, kx) = Math.DivRem(i, n);
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
            for (var j = 0; j <= fitted; j++)
            {
                phi[j] = Scaling(j, fx, fy);
            }
            double scored = 0;
            for (var j = 0; j < fitted; j++)
            {
                psi[j] = phi[j] - phi[j + 1];
                scored += psi[j] * psi[j];
            }
            var weight = stackPower[ring] * scored * parseval;
            // The coarser layers and the residual pass at 1, together the approximation after the fitted layers, so the gains fit what
            // the fitted layers must add to it.
            var target = wiener[ring] - phi[fitted];
            for (var j = 0; j < fitted; j++)
            {
                b[j] += weight * psi[j] * target;
                for (var k = j; k < fitted; k++)
                {
                    a[j, k] += weight * psi[j] * psi[k];
                }
            }
        }
        for (var j = 0; j < fitted; j++)
        {
            for (var k = 0; k < j; k++)
            {
                a[j, k] = a[k, j];
            }
        }
        return (a, b);
    }

    /// <summary><paramref name="plane"/> with each a trous layer times its gain (finest first), the residual kept: linear, no threshold.</summary>
    public static float[] Apply(ReadOnlySpan<float> plane, int width, int height, ReadOnlySpan<double> gains)
    {
        var gainsF = new float[gains.Length];
        for (var j = 0; j < gains.Length; j++)
        {
            gainsF[j] = (float)gains[j];
        }
        return ATrousWaveletTransform.Decompose(plane, width, height, gains.Length).Reconstruct(gainsF);
    }

    /// <summary>
    /// The a trous approximation after <paramref name="level"/> smoothings, as a transfer: the B3 spline's cos^4(pi 2^k f) along each
    /// axis for every k below the level (one at level 0). Layer j passes the difference of levels j and j + 1.
    /// </summary>
    internal static double Scaling(int level, double fx, double fy)
    {
        var value = 1.0;
        for (var k = 0; k < level; k++)
        {
            var step = 1 << k;
            var (cx, cy) = (Math.Cos(Math.PI * step * fx), Math.Cos(Math.PI * step * fy));
            var (x2, y2) = (cx * cx, cy * cy);
            value *= x2 * x2 * y2 * y2;
        }
        return value;
    }
}
