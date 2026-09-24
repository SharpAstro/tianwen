using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Where the drizzle deposit loop puts one (sample, cell) contribution. A struct so the JIT
/// specialises <see cref="DrizzleKernel"/>'s loop per sink with no virtual call per deposit.
/// </summary>
internal interface IDropSink
{
    void Add(int channel, int localY, int localX, float value, float area);
}

/// <summary>The plain drizzle deposit: flux += value x area, weight += area.</summary>
internal readonly struct FluxWeightSink(float[][,] flux, float[][,] weight) : IDropSink
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int channel, int localY, int localX, float value, float area)
    {
        flux[channel][localY, localX] += value * area;
        weight[channel][localY, localX] += area;
    }
}

/// <summary>The statistics pass: the plain deposit plus the area-weighted sum of squares.</summary>
internal readonly struct MomentsSink(DrizzleMoments moments) : IDropSink
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int channel, int localY, int localX, float value, float area)
    {
        var weighted = value * area;
        moments.Sum[channel][localY, localX] += weighted;
        moments.Weight[channel][localY, localX] += area;
        moments.Squares[channel][localY, localX] += value * weighted;
    }
}

/// <summary>The clipped deposit: deposits a contribution only if <see cref="DrizzleClip.Keeps"/> it.
/// <paramref name="counts"/> is [rejected, total], a one-element-each array so the struct can count.
/// <c>localY</c> is relative to the deposit's own rows; the moments may carry halo rows above them
/// (<see cref="DrizzleMoments.RowOffset"/>).</summary>
internal readonly struct ClippedSink(DrizzleMoments moments, DrizzleClip clip, float[][,] flux, float[][,] weight, long[] counts) : IDropSink
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int channel, int localY, int localX, float value, float area)
    {
        counts[1]++;
        var my = localY + moments.RowOffset;
        if (!clip.Keeps(
                value, area,
                moments.Sum[channel][my, localX],
                moments.Weight[channel][my, localX],
                moments.Squares[channel][my, localX],
                moments.Slope[channel][my, localX]))
        {
            counts[0]++;
            return;
        }

        flux[channel][localY, localX] += value * area;
        weight[channel][localY, localX] += area;
    }
}

/// <summary>
/// Per-cell, per-channel running sums over EVERY sample a rejecting drizzle deposits: area-weighted
/// value, area, and area-weighted value squared. From them the clipped pass reads a cell's mean and
/// spread, and, by subtracting one sample's own terms, the mean and spread of every OTHER sample.
/// </summary>
/// <remarks>
/// Indexed like the flux and weight planes the statistics pass fills (<c>[channel][y - yStart,
/// x - xStart]</c>), so a strip-local drizzle keeps strip-local moments. A strip's moments also cover
/// one HALO row above and below it wherever the canvas has one (<see cref="RowOffset"/> of them
/// above), so the slope at the strip's edge rows reads the same neighbours it would on the full
/// canvas. Two more planes per channel than a plain drizzle, since <see cref="Sum"/> and
/// <see cref="Weight"/> are the flux and weight the plain deposit would have accumulated anyway.
/// </remarks>
internal sealed class DrizzleMoments
{
    public float[][,] Sum { get; }

    public float[][,] Weight { get; }

    public float[][,] Squares { get; }

    /// <summary>Per cell, the largest step in cell mean to any of its four neighbours, filled by
    /// <see cref="ComputeSlope"/> once the statistics pass is complete.</summary>
    public float[][,] Slope { get; }

    /// <summary>Halo rows above the rows the clipped deposit covers.</summary>
    public int RowOffset { get; }

    public DrizzleMoments(int channels, int height, int width, int rowOffset = 0)
    {
        RowOffset = rowOffset;
        Sum = new float[channels][,];
        Weight = new float[channels][,];
        Squares = new float[channels][,];
        Slope = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            Sum[c] = new float[height, width];
            Weight[c] = new float[height, width];
            Squares[c] = new float[height, width];
            Slope[c] = new float[height, width];
        }
    }

    /// <summary>
    /// Fill <see cref="Slope"/> from the finished sums: for each covered cell, the largest absolute
    /// difference between its mean and a covered neighbour's (left, right, up, down). A cell with no
    /// covered neighbour gets zero, which is no allowance at all.
    /// </summary>
    /// <remarks>
    /// Rows in parallel (each reads its neighbours' sums and writes only its own slope row) and columns
    /// in SIMD lanes. Every lane does what the scalar edge loop does, with the same float divisions,
    /// absolute differences and maxima, and an uncovered cell keeps whatever it held, so the plane is
    /// bit-identical to the scalar one. A maximum is exact, so the order the neighbours are taken in
    /// cannot change it.
    /// </remarks>
    public void ComputeSlope()
    {
        for (var c = 0; c < Sum.Length; c++)
        {
            var sum = Sum[c];
            var weight = Weight[c];
            var slope = Slope[c];
            var h = sum.GetLength(0);
            Parallel.For(0, h, y => SlopeRow(sum, weight, slope, y));
        }
    }

    private static void SlopeRow(float[,] sum, float[,] weight, float[,] slope, int y)
    {
        var h = sum.GetLength(0);
        var w = sum.GetLength(1);
        var x = 0;

        // Interior columns in lanes: every lane there has both a left and a right neighbour.
        if (Vector.IsHardwareAccelerated && w >= Vector<float>.Count + 2)
        {
            var lanes = Vector<float>.Count;
            ref var s0 = ref sum[y, 0];
            ref var w0 = ref weight[y, 0];
            ref var out0 = ref slope[y, 0];
            var hasUp = y > 0;
            var hasDown = y < h - 1;
            ref var sUp = ref hasUp ? ref sum[y - 1, 0] : ref s0;
            ref var wUp = ref hasUp ? ref weight[y - 1, 0] : ref w0;
            ref var sDown = ref hasDown ? ref sum[y + 1, 0] : ref s0;
            ref var wDown = ref hasDown ? ref weight[y + 1, 0] : ref w0;
            var zero = Vector<float>.Zero;
            x = 1;
            for (; x <= w - 1 - lanes; x += lanes)
            {
                var i = (nuint)x;
                var wc = Vector.LoadUnsafe(ref w0, i);
                var m = Vector.LoadUnsafe(ref s0, i) / wc;
                var acc = zero;
                acc = Neighbour(acc, m, Vector.LoadUnsafe(ref s0, i - 1), Vector.LoadUnsafe(ref w0, i - 1));
                acc = Neighbour(acc, m, Vector.LoadUnsafe(ref s0, i + 1), Vector.LoadUnsafe(ref w0, i + 1));
                if (hasUp)
                {
                    acc = Neighbour(acc, m, Vector.LoadUnsafe(ref sUp, i), Vector.LoadUnsafe(ref wUp, i));
                }

                if (hasDown)
                {
                    acc = Neighbour(acc, m, Vector.LoadUnsafe(ref sDown, i), Vector.LoadUnsafe(ref wDown, i));
                }

                var covered = Vector.GreaterThan(wc, zero);
                Vector.ConditionalSelect(covered, acc, Vector.LoadUnsafe(ref out0, i)).StoreUnsafe(ref out0, i);
            }

            // The first column, left of the lanes.
            ScalarSlope(sum, weight, slope, 0, y, w, h);
        }

        for (; x < w; x++)
        {
            ScalarSlope(sum, weight, slope, x, y, w, h);
        }

        static Vector<float> Neighbour(Vector<float> acc, Vector<float> m, Vector<float> neighbourSum, Vector<float> neighbourWeight)
        {
            var step = Vector.Abs((neighbourSum / neighbourWeight) - m);
            var take = Vector.GreaterThan(neighbourWeight, Vector<float>.Zero);
            return Vector.ConditionalSelect(take, Vector.Max(acc, step), acc);
        }
    }

    private static void ScalarSlope(float[,] sum, float[,] weight, float[,] slope, int x, int y, int w, int h)
    {
        var wc = weight[y, x];
        if (wc <= 0f)
        {
            return;
        }

        var m = sum[y, x] / wc;
        var s = 0f;
        if (x > 0 && weight[y, x - 1] > 0f)
        {
            s = MathF.Max(s, MathF.Abs(sum[y, x - 1] / weight[y, x - 1] - m));
        }

        if (x < w - 1 && weight[y, x + 1] > 0f)
        {
            s = MathF.Max(s, MathF.Abs(sum[y, x + 1] / weight[y, x + 1] - m));
        }

        if (y > 0 && weight[y - 1, x] > 0f)
        {
            s = MathF.Max(s, MathF.Abs(sum[y - 1, x] / weight[y - 1, x] - m));
        }

        if (y < h - 1 && weight[y + 1, x] > 0f)
        {
            s = MathF.Max(s, MathF.Abs(sum[y + 1, x] / weight[y + 1, x] - m));
        }

        slope[y, x] = s;
    }
}

/// <summary>
/// The per-sample outlier test a rejecting drizzle applies: a sample is dropped from a cell when it
/// lies more than <see cref="LowSigma"/> below or <see cref="HighSigma"/> above the mean of the
/// OTHER samples in that cell, in units of their spread.
/// </summary>
/// <remarks>
/// <para><b>Why the sample is left out of its own test.</b> A trail sample inflates the very spread
/// it is judged against. With n samples in a cell and one outlier, the classical z-score of that
/// outlier can never exceed (n - 1) / sqrt(n), whatever its brightness: about 4.6 for a red or blue
/// cell of a 91-frame session (a quarter of the photosites each, ~23 samples), which never clears
/// the stack's own high sigma of 5. So the airplane (frame 00027) and the satellite (00049) that
/// reached the Omega Cen master (2024-02-16, ASI533MC) would have survived even a naive per-cell
/// clip, had the drizzle clipped at all. Removing the sample's own terms from the sums is exact,
/// costs three subtractions, and gives the leave-one-out mean and spread of the rest.</para>
/// <para><b>A sample may also deviate by the cell's local SLOPE</b>, scaled by
/// <see cref="SlopeScale"/>, beyond the sigma band. A drizzle deposits a photosite's value as it is,
/// not interpolated, so near a star what a cell receives depends on where inside the photosite the
/// star fell in each frame. That spread is skewed (the few frames whose photosite caught the centre
/// are its tail), and a sigma test calls the tail an outlier: without the allowance the clip took a
/// median 0.75% of every faint star's flux on the Omega Cen master and none of a bright one's, whose
/// own spread covers it. A deviation that comes from WHERE a sample was taken scales with the local
/// gradient, which is the term astrodrizzle's cosmic-ray test adds for the same reason
/// (<c>driz_cr_scale</c>). A trail has no slope in the mean it is judged against (it is one frame in
/// ~90), so it is still clipped, except where it crosses something steep, like a star core.</para>
/// <para><b>The scale is measured, on that master</b> (91 subs, 22,271 stars off the trails, against
/// the same subs drizzled with no clip): median faint-star flux lost 0.75% at 0, 0.19% at 1 and 0.05%
/// at 2, while the light removed along the airplane and the satellite held at 5.93, 5.88 and 5.86 sky
/// sigma, and the clean-sky rejection rate fell from 0.109% (at 1) to 0.057%. Past 2 the stars have
/// nothing left to give back and every step shelters a little more trail over a star.</para>
/// <para><b>The thresholds are the stack's own</b>, from the rejector the pipeline already built for
/// the session (<see cref="From"/>), so a drizzled master and a debayered one reject at the same
/// sigma. A cell whose other samples weigh less than <see cref="MinOtherWeight"/> is not judged at
/// all: two or three samples have no spread worth testing against.</para>
/// </remarks>
internal readonly record struct DrizzleClip(
    float LowSigma,
    float HighSigma,
    float MinOtherWeight = DrizzleClip.DefaultMinOtherWeight,
    float SlopeScale = DrizzleClip.DefaultSlopeScale)
{
    /// <summary>Other samples' total weight below which a cell is deposited unjudged.</summary>
    public const float DefaultMinOtherWeight = 4f;

    /// <summary>Multiples of the local slope a sample may deviate by beyond the sigma band; measured,
    /// see the remarks.</summary>
    public const float DefaultSlopeScale = 2f;

    /// <summary>
    /// The clip that matches <paramref name="rejector"/>'s thresholds, or null for none (no
    /// rejector, which the pipeline hands over below five frames). A rejector that is not
    /// sigma-based (min/max, percentile) has no per-sample sigma to borrow, so it maps onto the
    /// stack's standard pair, 3 low and 5 high.
    /// </summary>
    public static DrizzleClip? From(IPixelRejector? rejector) => rejector switch
    {
        null => null,
        SigmaClipRejector s => new DrizzleClip(s.LowSigma, s.HighSigma),
        WinsorizedSigmaClipRejector w => new DrizzleClip(w.LowSigma, w.HighSigma),
        LinearFitClipRejector l => new DrizzleClip(l.LowSigma, l.HighSigma),
        _ => new DrizzleClip(3f, 5f),
    };

    /// <summary>
    /// Whether a contribution of <paramref name="value"/> at <paramref name="area"/> survives against
    /// a cell whose running sums, INCLUDING this contribution, are <paramref name="sum"/>,
    /// <paramref name="weight"/> and <paramref name="squares"/>, and whose local slope is
    /// <paramref name="slope"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Keeps(float value, float area, float sum, float weight, float squares, float slope)
    {
        // In double: a raw-ADU sky of a few thousand squares to 1e7 per sample, and the variance is
        // the difference of two such numbers.
        var otherWeight = (double)weight - area;
        if (otherWeight < MinOtherWeight)
        {
            return true;
        }

        var otherSum = sum - (double)value * area;
        var otherSquares = squares - (double)value * value * area;
        var mean = otherSum / otherWeight;
        var variance = otherSquares / otherWeight - mean * mean;
        if (variance <= 0d)
        {
            // Every other sample identical (a saturated core, a synthetic flat): nothing to measure
            // a deviation against, so keep rather than reject on a zero spread.
            return true;
        }

        var sd = Math.Sqrt(variance);
        var deviation = value - mean;
        var allowance = (double)SlopeScale * slope;
        return deviation <= HighSigma * sd + allowance && -deviation <= LowSigma * sd + allowance;
    }
}
