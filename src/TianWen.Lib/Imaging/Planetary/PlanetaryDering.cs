using System;
using System.Collections.Immutable;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Sharpening a disk without a ring below the sky (docs/plans/planetary-restoration.md, R8 follow-up 2). A ring is a composite kernel's
/// negative lobe (follow-up 1): a band lifted past the truth, or a steep cut. Four ways round it, each put on a wavelet sharpening: a
/// floor at the sky, the limb as its own channel, gains feathered to zero at the limb, and gains refitted so the composite stays
/// non-negative (<see cref="PlanetaryWaveletGains.FitNonNegative"/>). Planes are in the window's units: the sky zero, the disk one.
/// </summary>
public static class PlanetaryDering
{
    /// <summary><paramref name="plane"/> held at or above <paramref name="sky"/>, the floor Richardson-Lucy and L1-L2 kept in R8 part 2.</summary>
    public static float[] Floor(ReadOnlySpan<float> plane, float sky = 0f)
    {
        var result = new float[plane.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Math.Max(plane[i], sky);
        }
        return result;
    }

    /// <summary>
    /// <paramref name="sharpened"/> held at or above the sky and, outside <paramref name="disk"/>'s outline, at or below
    /// <paramref name="stacked"/>, the plane it was sharpened from: a sharpening only moves light inward past the limb, so light it adds
    /// there is its ring (#1168). Except about a moon (#1181): within <paramref name="moonReachPx"/> of each compact source
    /// <see cref="PlanetaryMetrics.CompactSources"/> finds in the stack (a local maximum beyond 1.05 radii standing above its own
    /// neighbourhood), its sharpening stands. A planet's halo only falls away from the limb, so it holds no such maximum and stays held;
    /// held too, the moon beside Jupiter on 2022-09-03 was left as stacked (peak 0.024 above the sky against the floored 0.101). The
    /// planes are the window's (the sky zero, the disk one).
    /// </summary>
    public static float[] Bounded(ReadOnlySpan<float> sharpened, ReadOnlySpan<float> stacked, int width, int height, MetricDisk disk, int moonReachPx = 5)
    {
        var moons = PlanetaryMetrics.CompactSources(stacked, width, height, disk, count: MaxMoons);
        var result = new float[sharpened.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var v = Math.Max(sharpened[i], 0f);
                result[i] = disk.RadiiAt(x, y) > 1 && !Near(moons, x, y, moonReachPx) ? Math.Min(v, Math.Max(stacked[i], 0f)) : v;
            }
        }
        return result;
    }

    /// <summary>What <see cref="Outside"/> puts outside the limb (#1171).</summary>
    public enum OutsideLimb
    {
        /// <summary>The stack as it is.</summary>
        Stack,

        /// <summary>Bounded, and at or above the stack times its glow share.</summary>
        ModelFloor,

        /// <summary>Bounded at the limb, blended to the stack by 1.1 radii.</summary>
        Blended,
    }

    /// <summary>
    /// <see cref="Bounded"/>'s three successors measured against its dark trough at the limb (#1171), held at the sky inside the limb and
    /// free about a moon as it is. Outside the limb: the stack as it is (<see cref="OutsideLimb.Stack"/>); bounded and at or above
    /// <paramref name="stacked"/> times <paramref name="glowShare"/>, the share of the stack's glow the truth keeps there
    /// (<see cref="OutsideLimb.ModelFloor"/>); or bounded at the limb and blended to the stack by 1.1 radii (<see cref="OutsideLimb.Blended"/>).
    /// </summary>
    public static float[] Outside(ReadOnlySpan<float> sharpened, ReadOnlySpan<float> stacked, int width, int height, MetricDisk disk, OutsideLimb outside,
        ReadOnlySpan<float> glowShare = default, int moonReachPx = 5)
    {
        var moons = PlanetaryMetrics.CompactSources(stacked, width, height, disk, count: MaxMoons);
        var result = new float[sharpened.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var v = Math.Max(sharpened[i], 0f);
                var r = disk.RadiiAt(x, y);
                if (r <= 1 || Near(moons, x, y, moonReachPx))
                {
                    result[i] = v;
                    continue;
                }
                var stack = Math.Max(stacked[i], 0f);
                var bounded = Math.Min(v, stack);
                result[i] = outside switch
                {
                    OutsideLimb.Stack => stacked[i],
                    OutsideLimb.ModelFloor => Math.Max(bounded, stack * (glowShare.IsEmpty ? 0f : glowShare[i])),
                    _ => Blend(bounded, stack, r),
                };
            }
        }
        return result;
    }

    // Bounded at the limb, the stack by 1.1 radii, a smoothstep between.
    private static float Blend(float bounded, float stack, double radii)
    {
        var s = Math.Clamp((radii - 1) / 0.1, 0, 1);
        var w = (float)(s * s * (3 - (2 * s)));
        return ((1 - w) * bounded) + (w * stack);
    }

    /// <summary>
    /// The share of a stack's glow outside the limb the truth keeps: the planet's limb model through the pupil's diffraction alone
    /// (<paramref name="target"/>) over the same model through the stack's measured blur (<paramref name="blurred"/>), clamped to [0, 1];
    /// zero where the blurred model holds nothing.
    /// </summary>
    public static float[] GlowShare(ReadOnlySpan<float> target, ReadOnlySpan<float> blurred)
    {
        var share = new float[target.Length];
        for (var i = 0; i < share.Length; i++)
        {
            share[i] = blurred[i] > 1e-4f ? Math.Clamp(target[i] / blurred[i], 0f, 1f) : 0f;
        }
        return share;
    }

    /// <summary>The most compact sources <see cref="Bounded"/> lets keep their sharpening: Jupiter's four moons, Saturn's eight, with room.</summary>
    public const int MaxMoons = 16;

    // Whether (x, y) lies within reach of one of the sources.
    private static bool Near(ImmutableArray<(int X, int Y)> sources, int x, int y, int reach)
    {
        foreach (var (sx, sy) in sources)
        {
            if (((x - sx) * (x - sx)) + ((y - sy) * (y - sy)) <= reach * reach)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// <paramref name="plane"/> sharpened by a trous <paramref name="gains"/> (finest first) and, where given, soft
    /// <paramref name="thresholds"/> in the plane's units: what <see cref="WaveletSharpen"/> does to a master, without its clamp.
    /// </summary>
    public static float[] Sharpen(ReadOnlySpan<float> plane, int width, int height, ReadOnlySpan<double> gains, ReadOnlySpan<double> thresholds = default)
    {
        var (g, t) = (ToFloat(gains), thresholds.IsEmpty ? [] : ToFloat(thresholds));
        return ATrousWaveletTransform.Decompose(plane, width, height, g.Length).Reconstruct(g, t);
    }

    /// <summary>
    /// The limb as its own channel (Lucy 1994; Yuan et al. 2007): the disk's model <paramref name="sharpDisk"/> through the stack's
    /// <paramref name="blur"/> taken from <paramref name="plane"/>, only the residual handed to <paramref name="sharpen"/>, and the model
    /// added back through <paramref name="target"/> alone (the pupil's diffraction, which is non-negative). The limb's step is never
    /// sharpened, so it cannot ring; what is left of it is the model's misfit.
    /// </summary>
    public static float[] LimbChannel(ReadOnlySpan<float> plane, int width, int height, ReadOnlySpan<float> sharpDisk, Func<double, double> blur, Func<double, double> target,
        Func<float[], float[]> sharpen)
    {
        ArgumentNullException.ThrowIfNull(sharpen);
        var model = PlanetaryInverse.Apply(sharpDisk, width, height, blur);
        var residual = new float[plane.Length];
        for (var i = 0; i < residual.Length; i++)
        {
            residual[i] = plane[i] - model[i];
        }
        var sharpened = sharpen(residual);
        var disk = PlanetaryInverse.Apply(sharpDisk, width, height, target);
        for (var i = 0; i < sharpened.Length; i++)
        {
            sharpened[i] += disk[i];
        }
        return sharpened;
    }

    /// <summary>
    /// <paramref name="plane"/> sharpened with each layer's boost feathered to nothing at the limb (PlanetFlow's
    /// <c>sharpen_disk_aware</c>): layer j's weight is one from 2^(j+1) px inside the limb of <paramref name="disk"/> inward and falls
    /// linearly to zero at it, so outside the disk the plane is its own.
    /// </summary>
    public static float[] Feathered(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk, ReadOnlySpan<double> gains, ReadOnlySpan<double> thresholds = default)
    {
        var decomposition = ATrousWaveletTransform.Decompose(plane, width, height, gains.Length);
        var result = decomposition.Residual.ToArray();
        for (var j = 0; j < gains.Length; j++)
        {
            var detail = decomposition.Detail(j);
            var (g, t) = ((float)gains[j], thresholds.IsEmpty ? 0f : (float)thresholds[j]);
            var reach = (double)(2 << j);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    var inside = (1 - disk.RadiiAt(x, y)) * disk.Radius;
                    var w = (float)Math.Clamp(inside / reach, 0, 1);
                    var sharpened = g * (t > 0 ? WaveletDecomposition.SoftThreshold(detail[i], t) : detail[i]);
                    result[i] += (w * sharpened) + ((1 - w) * detail[i]);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// <paramref name="transfer"/> averaged over 2 <paramref name="half"/> + 1 rings, each ring over the neighbours that hold a reading
    /// (a ring past the truth's power floor reads zero and stays zero): a transfer measured against a truth is noisy ring to ring in the
    /// finest bands, and that noise, not the filter, set a composite's negative mass in follow-up 1.
    /// </summary>
    public static RadialTransfer Smoothed(RadialTransfer transfer, int half = 2)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var values = transfer.Values;
        var smoothed = ImmutableArray.CreateBuilder<double>(values.Length);
        for (var r = 0; r < values.Length; r++)
        {
            if (values[r] == 0 && r > 0)
            {
                smoothed.Add(0);
                continue;
            }
            double sum = 0;
            var count = 0;
            for (var k = Math.Max(0, r - half); k <= Math.Min(values.Length - 1, r + half); k++)
            {
                if (values[k] != 0 || k == 0)
                {
                    sum += values[k];
                    count++;
                }
            }
            smoothed.Add(count > 0 ? sum / count : 0);
        }
        return transfer with { Values = smoothed.MoveToImmutable() };
    }

    /// <summary>
    /// The composite kernel of a trous gains and an isotropic <paramref name="blur"/>, as constraints: for each pixel within
    /// <paramref name="reach"/> of its centre on an <paramref name="n"/> grid, the kernel's value is the row times the
    /// <paramref name="fitted"/> gains plus the offset (the coarser layers and the residual at one).
    /// </summary>
    internal static (double[][] Rows, double[] Offsets) CompositeRows(Func<double, double> blur, int fitted, int reach = 15, int n = 256)
    {
        ArgumentNullException.ThrowIfNull(blur);
        var fields = new Complex[fitted + 1][];
        for (var j = 0; j <= fitted; j++)
        {
            fields[j] = new Complex[n * n];
        }
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                var h = blur(Math.Sqrt((fx * fx) + (fy * fy)));
                var i = (ky * n) + kx;
                var previous = 1.0;
                for (var j = 0; j < fitted; j++)
                {
                    var next = PlanetaryWaveletGains.Scaling(j + 1, fx, fy);
                    fields[j][i] = (previous - next) * h;
                    previous = next;
                }
                fields[fitted][i] = previous * h;
            }
        }
        foreach (var field in fields)
        {
            Fft2D.Inverse(field, n, n);
        }
        var rows = new System.Collections.Generic.List<double[]>();
        var offsets = new System.Collections.Generic.List<double>();
        for (var y = -reach; y <= reach; y++)
        {
            for (var x = -reach; x <= reach; x++)
            {
                if ((x * x) + (y * y) > reach * reach)
                {
                    continue;
                }
                var i = (((y + n) % n) * n) + ((x + n) % n);
                var row = new double[fitted];
                for (var j = 0; j < fitted; j++)
                {
                    row[j] = fields[j][i].Real;
                }
                rows.Add(row);
                offsets.Add(fields[fitted][i].Real);
            }
        }
        return ([.. rows], [.. offsets]);
    }

    private static float[] ToFloat(ReadOnlySpan<double> values)
    {
        var result = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = (float)values[i];
        }
        return result;
    }
}
