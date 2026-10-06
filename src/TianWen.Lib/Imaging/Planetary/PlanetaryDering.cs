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
    /// there is its ring (#1168). Except about a moon (#1181): each compact source <see cref="PlanetaryMetrics.CompactSources"/> finds in the
    /// stack (a local maximum beyond 1.05 radii standing above its own surroundings) keeps its sharpening (<see cref="KeepMoon"/>). A planet's
    /// halo only falls away from the limb, so it holds no such maximum and stays held; held too, the moon beside Jupiter on 2022-09-03 was
    /// left as stacked (peak 0.024 above the sky against the floored 0.101). The planes are the window's (the sky zero, the disk one).
    /// </summary>
    public static float[] Bounded(ReadOnlySpan<float> sharpened, ReadOnlySpan<float> stacked, int width, int height, MetricDisk disk)
    {
        var moons = PlanetaryMetrics.CompactSources(stacked, width, height, disk, count: MaxMoons);
        var result = new float[sharpened.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var v = Math.Max(sharpened[i], 0f);
                result[i] = disk.ClearRadiiAt(x, y) > 1 ? Math.Min(v, Math.Max(stacked[i], 0f)) : v;
            }
        }
        KeepMoons(result, Floor(sharpened), width, height, disk, moons);
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

        /// <summary>The stack times its glow share: the glow the truth has, none of the sharpening.</summary>
        ModelGlow,

        /// <summary>The planet's model through the pupil alone.</summary>
        Model,

        /// <summary>The stack less the model through its blur, plus the model through the pupil alone.</summary>
        GlowSwapped,

        /// <summary>The model out to 1.5 radii, blended to the stack by the plane's inscribed circle (at most 2.5 radii).</summary>
        ModelFeathered,
    }

    /// <summary>
    /// <see cref="Bounded"/>'s three successors measured against its dark trough at the limb (#1171), held at the sky inside the limb and
    /// free about a moon as it is (<see cref="KeepMoon"/>). Outside the limb: the stack as it is (<see cref="OutsideLimb.Stack"/>); bounded and
    /// at or above <paramref name="stacked"/> times <paramref name="glowShare"/>, the share of the stack's glow the truth keeps there
    /// (<see cref="OutsideLimb.ModelFloor"/>); or bounded at the limb and blended to the stack by 1.1 radii (<see cref="OutsideLimb.Blended"/>).
    /// </summary>
    public static float[] Outside(ReadOnlySpan<float> sharpened, ReadOnlySpan<float> stacked, int width, int height, MetricDisk disk, OutsideLimb outside,
        ReadOnlySpan<float> glowShare = default, ReadOnlySpan<float> model = default, ReadOnlySpan<float> blurredModel = default)
    {
        var moons = PlanetaryMetrics.CompactSources(stacked, width, height, disk, count: MaxMoons);
        // Where ModelFeathered hands the model back to the stack: by the plane's inscribed circle, at most 2.5 radii, from 0.5 radii inside it.
        // Saturn's are counted past its rings (MetricDisk.ClearRadiiAt, S4), whose outer edge is a radius along the equator.
        var featherEnd = Math.Min(2.5, ((Math.Min(width, height) / 2.0) - 4) / (disk.Radius * (disk.Rings?.OuterRadii ?? 1)));
        var featherStart = Math.Max(1.0, Math.Min(1.5, featherEnd - 0.5));
        var result = new float[sharpened.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var v = Math.Max(sharpened[i], 0f);
                // The planet is its globe and, for Saturn, its rings: they keep their sharpening as a moon does (S4, #1184).
                var r = disk.ClearRadiiAt(x, y);
                if (r <= 1)
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
                    OutsideLimb.ModelGlow => stack * (glowShare.IsEmpty ? 0f : glowShare[i]),
                    OutsideLimb.Model => model.IsEmpty ? bounded : Math.Max(model[i], 0f),
                    OutsideLimb.GlowSwapped => model.IsEmpty || blurredModel.IsEmpty ? bounded : stacked[i] - blurredModel[i] + model[i],
                    OutsideLimb.ModelFeathered => model.IsEmpty ? bounded : Feather(Math.Max(model[i], 0f), stacked[i], r, featherStart, featherEnd),
                    _ => Blend(bounded, stack, r),
                };
            }
        }
        KeepMoons(result, Floor(sharpened), width, height, disk, moons);
        return result;
    }

    /// <summary>
    /// A moon kept in <paramref name="drawn"/>, the <see cref="PlanetaryMetrics.SourceBox"/> square about it of what is drawn there, from
    /// <paramref name="sharpened"/>, the same square of its sharpening (both <see cref="PlanetaryMetrics.Patch"/>es, in one unit): within
    /// <see cref="MoonReachPx"/>, the sharpening above the plane its own surroundings make (<see cref="PlanetaryMetrics.SurroundPlane"/>), laid
    /// on the plane what is drawn about it makes, feathered from 2 px inside the reach to nothing at it. So a moon adds its own light and none
    /// of the glow it sits in (#1301): kept whole, the blue glow about a ring tip a moon was found on stood five times the model drawn around
    /// it, a hard-edged disc. A pixel <paramref name="keep"/> refuses (dx, dy from the middle) stays as drawn.
    /// </summary>
    public static void KeepMoon(Span<float> drawn, ReadOnlySpan<float> sharpened, Func<int, int, bool> keep)
    {
        if (PlanetaryMetrics.SurroundPlane(drawn) is not { } around || PlanetaryMetrics.SurroundPlane(sharpened) is not { } own)
        {
            return;
        }
        const int half = PlanetaryMetrics.SourceBox / 2;
        for (var dy = -MoonReachPx; dy <= MoonReachPx; dy++)
        {
            for (var dx = -MoonReachPx; dx <= MoonReachPx; dx++)
            {
                var d = Math.Sqrt((dx * dx) + (dy * dy));
                var i = ((dy + half) * PlanetaryMetrics.SourceBox) + dx + half;
                if (d > MoonReachPx || !float.IsFinite(drawn[i]) || !float.IsFinite(sharpened[i]) || !keep(dx, dy))
                {
                    continue;
                }
                var s = Math.Clamp((MoonReachPx - d) / 2, 0, 1);
                var w = s * s * (3 - (2 * s));
                var moon = sharpened[i] - (own.A + (own.B * dx) + (own.C * dy)) + around.A + (around.B * dx) + (around.C * dy);
                drawn[i] = (float)((w * moon) + ((1 - w) * drawn[i]));
            }
        }
    }

    // Every moon in `moons` kept in `result` from `sharpened` (KeepMoon), both the window's planes, but on the planet itself, whose globe and
    // rings keep their sharpening as they are.
    private static void KeepMoons(float[] result, ReadOnlySpan<float> sharpened, int width, int height, MetricDisk disk, ImmutableArray<(int X, int Y)> moons)
    {
        const int half = PlanetaryMetrics.SourceBox / 2;
        foreach (var (mx, my) in moons)
        {
            var drawn = PlanetaryMetrics.Patch(result, width, height, mx, my);
            KeepMoon(drawn, PlanetaryMetrics.Patch(sharpened, width, height, mx, my), (dx, dy) => disk.ClearRadiiAt(mx + dx, my + dy) > 1);
            for (var dy = -MoonReachPx; dy <= MoonReachPx; dy++)
            {
                for (var dx = -MoonReachPx; dx <= MoonReachPx; dx++)
                {
                    var (x, y) = (mx + dx, my + dy);
                    if (x >= 0 && x < width && y >= 0 && y < height)
                    {
                        result[(y * width) + x] = drawn[((dy + half) * PlanetaryMetrics.SourceBox) + dx + half];
                    }
                }
            }
        }
    }

    // The model to `start` radii, the stack from `end`, a smoothstep between.
    private static float Feather(float model, float stack, double radii, double start, double end)
    {
        var s = end > start ? Math.Clamp((radii - start) / (end - start), 0, 1) : 1;
        var w = (float)(s * s * (3 - (2 * s)));
        return ((1 - w) * model) + (w * stack);
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

    /// <summary>How far about a moon, px, its sharpening stands where the planet's is held or handed back to the stack (#1181, #1211).</summary>
    public const int MoonReachPx = 5;

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
                    var inside = (1 - disk.ClearRadiiAt(x, y)) * disk.Radius;
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
