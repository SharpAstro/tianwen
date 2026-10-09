using System;
using System.Buffers;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Threading;

namespace TianWen.Lib.Imaging;

/// <summary>
/// <c>scipy.ndimage.zoom(plane, factor, order=3)</c> on a 2-D float32 plane, every other argument at its default
/// (<c>mode='constant'</c>, <c>cval=0</c>, <c>prefilter=True</c>, <c>grid_mode=False</c>), reproduced to scipy's numbers
/// everywhere but one rounding artefact, which it does not reproduce: what it computes is scipy's <c>mode='mirror'</c>,
/// to the bit the same call off that artefact (item 3 below).
/// The in-house deconvolver (#844) runs its operator on a frame resampled up by a factor and resamples the result back
/// down onto the native grid; the training and every published readout did both resamples with that call
/// (<c>training/denoise/n2n_operator_master.py</c>), so the model's measured behaviour includes this resample, and a
/// merely similar cubic would be a different input.
/// </summary>
/// <remarks>
/// <para><b>The shape is the API.</b> With <c>grid_mode=False</c> scipy maps output index <c>i</c> to input coordinate
/// <c>i * (n_in - 1) / (n_out - 1)</c> on each axis, the ratio taken in double from the two sizes alone, so the factor
/// only decides the output size (<see cref="OutputSize"/>: <c>int(round(n * factor))</c>, Python's round half to even)
/// and two factors that round to one size give one result. The runner's way back down, a tuple factor
/// <c>(h / hz, w / wz)</c>, is therefore <see cref="Zoom(float[,], int, int, CancellationToken)"/> at <c>(h, w)</c>.</para>
///
/// <para><b>What scipy does at the edges</b>, read from scipy 1.18's <c>_interpolation.py</c>
/// (<c>zoom</c>, <c>_prepad_for_spline_filter</c>, <c>spline_filter</c>), <c>ni_splines.c</c> (<c>apply_filter</c> and its
/// boundary initialisers) and <c>ni_interpolation.c</c> (<c>map_coordinate</c>, <c>_get_spline_boundary_mode</c>,
/// <c>NI_ZoomShift</c>), and checked against scipy itself by <c>tools/spline-zoom-fixture.py</c>'s cases:</para>
/// <list type="number">
/// <item><description><b>The prefilter extends the plane by MIRROR, not by zeros, despite <c>mode='constant'</c>.</b>
/// <c>_prepad_for_spline_filter</c> pads only for <c>'nearest'</c> and <c>'grid-constant'</c>, and <c>apply_filter</c>
/// gives <c>NI_EXTEND_CONSTANT</c> the mirror initialisers (<c>_init_causal_mirror</c>, <c>_init_anticausal_mirror</c>):
/// the cubic B-spline coefficients are those of the whole-sample symmetric extension, the causal initial value summed
/// over the whole line. Each axis is filtered in turn (the gain <c>(1 - z)(1 - 1/z)</c>, then the causal and anticausal
/// passes, pole <c>z = sqrt(3) - 2</c>) in double; an axis of length 1 is not filtered at all.</description></item>
/// <item><description><b>A tap past the edge reads the mirrored coefficient.</b> <c>_get_spline_boundary_mode</c> maps
/// <c>'constant'</c> to mirror for the four taps of a sample near the edge, so <c>c[-1] = c[1]</c> and
/// <c>c[n] = c[n - 2]</c> (an axis of length 1 reads its one coefficient for every tap).</description></item>
/// <item><description><b><c>cval</c> is written only where the output coordinate itself falls outside
/// <c>[0, n_in - 1]</c></b> (<c>map_coordinate</c> answers -1 there, and <c>NI_ZoomShift</c> flags the whole output line).
/// With <c>grid_mode=False</c> the only way out is rounding: <c>(n_out - 1) * ((n_in - 1) / (n_out - 1))</c> can land
/// one ulp past <c>n_in - 1</c>, and then scipy writes 0 over the WHOLE last output row or column. Through 1.28125 it
/// hits 185 of the axis sizes from 1000 to 8000 on the way up and 481 on the way down, 3000 and 3008 among them
/// (3844 back to 3000, 3854 back to 3008), though not 4000 or 6000. <b>This does NOT reproduce it</b>: the coordinate
/// is clamped onto the last sample, which is <c>mode='mirror'</c>'s answer there (its prefilter and taps are the ones
/// above, so it IS this call everywhere else). A zeroed line is no part of what the deconvolver's prior learned, and
/// on the way back down it would draw a black line along the edge of a user's image.</description></item>
/// <item><description><b>One output sample</b>: the ratio's divisor is 0, scipy takes the ratio as 1, and the sample is
/// the spline at coordinate 0. <b>One input sample</b>: the ratio is 0 and every output sample is that input sample.
/// <b>A factor of 1 on every axis</b> returns the input; an output the shape of the input is returned as a copy here,
/// which is also what scipy's interpolation at integer coordinates gives, to round-off, for any other factor that
/// keeps the shape.</description></item>
/// </list>
///
/// <para><b>How it is evaluated.</b> scipy filters axis 0, then axis 1, then sums the 4 x 4 tensor-product taps. Every one
/// of those is linear and acts along one axis, so the same numbers come from a separable walk: each input row is
/// filtered and interpolated along x into a double intermediate (input rows by output columns), then each band of its
/// columns is filtered along y and interpolated into the float32 output. The two orders differ only in double
/// round-off; the fixture's cases agree with scipy to within float32 rounding of its result.</para>
///
/// <para><b>Cost.</b> The intermediate is <c>n_in_rows * n_out_columns</c> doubles, about 250 MB for a 6000 x 4000 frame
/// either way through 1.28125, allocated once per call and uninitialised (every element is written before it is read).
/// It is deliberately not rented: a frame-sized buffer returned to the shared pool would stay held after the call. Each
/// band's line scratch is rented. Both stages run in bands over every core through <see cref="ParallelFor.RunBands"/>,
/// and the token is honoured at the start of every band. Measured in Release on a 16-core x64 (2026-10-09): a
/// 4000 x 6000 plane up to 5125 x 7688 in 170 to 220 ms and back down in 180 to 210 ms, where scipy takes 3.1 s each
/// way on one core.</para>
/// </remarks>
public static class SplineZoom
{
    /// <summary>The cubic B-spline prefilter's one pole, <c>sqrt(3) - 2</c>, as scipy's <c>get_filter_poles</c> writes it.</summary>
    private const double Pole = -0.267949192431122706472553658494127633;

    /// <summary>scipy's <c>_apply_filter_gain</c> for the one pole: <c>(1 - z) * (1 - 1 / z)</c>, which is 6.</summary>
    private const double Gain = (1.0 - Pole) * (1.0 - 1.0 / Pole);

    /// <summary>
    /// The size scipy gives an axis of <paramref name="inputSize"/> samples zoomed by <paramref name="factor"/>:
    /// <c>int(round(n * factor))</c>, the product in double and Python's round half to even
    /// (6000 x 1.28125 = 7687.5 is 7688).
    /// </summary>
    public static int OutputSize(int inputSize, double factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputSize);
        if (!double.IsFinite(factor) || factor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "A zoom factor is finite and not negative.");
        }

        var size = Math.Round(inputSize * factor, MidpointRounding.ToEven);
        if (size > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, $"{inputSize} samples zoomed by {factor} is {size}, past the largest array.");
        }

        return (int)size;
    }

    /// <summary>
    /// <c>scipy.ndimage.zoom(plane, factor, order=3)</c>: the plane zoomed by one <paramref name="factor"/> on both axes,
    /// at the shape <see cref="OutputSize"/> gives each axis.
    /// </summary>
    public static float[,] Zoom(float[,] plane, double factor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plane);
        return Zoom(plane, OutputSize(plane.GetLength(0), factor), OutputSize(plane.GetLength(1), factor), cancellationToken);
    }

    /// <summary>
    /// <c>scipy.ndimage.zoom(plane, factor, order=3)</c> for whichever factor, scalar or per axis, gives an
    /// <paramref name="outHeight"/> x <paramref name="outWidth"/> output: the cubic B-spline through the plane, sampled
    /// at <c>i * (n_in - 1) / (n_out - 1)</c> on each axis, computed in double and returned as float32. See the class
    /// remarks for the edges.
    /// </summary>
    public static float[,] Zoom(float[,] plane, int outHeight, int outWidth, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plane);
        ArgumentOutOfRangeException.ThrowIfNegative(outHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(outWidth);
        cancellationToken.ThrowIfCancellationRequested();

        var inHeight = plane.GetLength(0);
        var inWidth = plane.GetLength(1);
        if (outHeight == 0 || outWidth == 0)
        {
            return new float[outHeight, outWidth];
        }

        if (inHeight == 0 || inWidth == 0)
        {
            throw new ArgumentException($"An empty {inHeight} x {inWidth} plane has no samples for a {outHeight} x {outWidth} output.", nameof(plane));
        }

        if (outHeight == inHeight && outWidth == inWidth)
        {
            return plane.Copy();
        }

        var intermediateLength = (long)inHeight * outWidth;
        if (intermediateLength > Array.MaxLength)
        {
            throw new ArgumentException($"A {inHeight} x {inWidth} plane zoomed to {outHeight} x {outWidth} needs a {inHeight} x {outWidth} intermediate, past the largest array.", nameof(plane));
        }

        var rows = AxisPlan.Create(inHeight, outHeight);
        var columns = AxisPlan.Create(inWidth, outWidth);
        var intermediate = GC.AllocateUninitializedArray<double>((int)intermediateLength);
        var output = new float[outHeight, outWidth];

        // The column filter's gain is folded into the row pass's store, which saves a whole pass over the
        // intermediate; an axis of one sample is not filtered, so it has no gain either.
        var columnGain = inHeight > 1 ? Gain : 1.0;
        ParallelFor.RunBands(inHeight, (first, end) => ZoomRows(plane, intermediate, columns, columnGain, first, end, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        ParallelFor.RunBands(outWidth, (first, end) => ZoomColumns(intermediate, inHeight, rows, columns, output, first, end, cancellationToken));

        return output;
    }

    /// <summary>
    /// Input rows <paramref name="first"/> to <paramref name="end"/>: each converted to double, prefiltered along x and
    /// interpolated onto the output columns, times <paramref name="columnGain"/>, into its row of the intermediate.
    /// </summary>
    private static void ZoomRows(float[,] plane, double[] intermediate, AxisPlan columns, double columnGain, int first, int end, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var inWidth = plane.GetLength(1);
        var outWidth = columns.Count;
        var line = ArrayPool<double>.Shared.Rent(inWidth);
        try
        {
            var coefficients = line.AsSpan(0, inWidth);
            var source = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
            for (var y = first; y < end; y++)
            {
                var row = source.Slice(y * inWidth, inWidth);
                for (var x = 0; x < inWidth; x++)
                {
                    coefficients[x] = row[x];
                }

                if (inWidth > 1)
                {
                    PrefilterLine(coefficients);
                }

                columns.Interpolate(coefficients, intermediate.AsSpan(y * outWidth, outWidth), columnGain);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(line);
        }
    }

    /// <summary>
    /// Output columns <paramref name="first"/> to <paramref name="end"/>: the intermediate's band prefiltered along y
    /// (a row of the band at a time, so every step is a contiguous span), then interpolated onto the output rows.
    /// </summary>
    private static void ZoomColumns(double[] intermediate, int inHeight, AxisPlan rows, AxisPlan columns, float[,] output, int first, int end, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stride = columns.Count;
        var width = end - first;
        if (inHeight > 1)
        {
            PrefilterColumns(intermediate, inHeight, stride, first, width);
        }

        var outHeight = rows.Count;
        var target = MemoryMarshal.CreateSpan(ref output[0, 0], output.Length);
        var taps = rows.Taps;
        var weights = rows.Weights;
        for (var o = 0; o < outHeight; o++)
        {
            var destination = target.Slice(o * stride + first, width);
            var t = 4 * o;
            ReadOnlySpan<double> r0 = intermediate.AsSpan(taps[t] * stride + first, width);
            ReadOnlySpan<double> r1 = intermediate.AsSpan(taps[t + 1] * stride + first, width);
            ReadOnlySpan<double> r2 = intermediate.AsSpan(taps[t + 2] * stride + first, width);
            ReadOnlySpan<double> r3 = intermediate.AsSpan(taps[t + 3] * stride + first, width);
            var w0 = weights[t];
            var w1 = weights[t + 1];
            var w2 = weights[t + 2];
            var w3 = weights[t + 3];
            for (var k = 0; k < width; k++)
            {
                destination[k] = (float)(w0 * r0[k] + w1 * r1[k] + w2 * r2[k] + w3 * r3[k]);
            }
        }

    }

    /// <summary>
    /// scipy's <c>apply_filter</c> for the cubic spline in <c>'constant'</c> mode, on one line in place: the gain, the
    /// causal pass from <c>_init_causal_mirror</c>'s initial value, the anticausal pass from
    /// <c>_init_anticausal_mirror</c>'s. The initial value's sum stops once the pole's power underflows to zero, past
    /// which every term scipy adds is exactly zero.
    /// </summary>
    private static void PrefilterLine(Span<double> c)
    {
        const double z = Pole;
        var n = c.Length;
        for (var i = 0; i < n; i++)
        {
            c[i] *= Gain;
        }

        var zn1 = Math.Pow(z, n - 1);
        var c0 = c[0] + zn1 * c[n - 1];
        var zi = z;
        for (var i = 1; i < n - 1 && zi != 0.0; i++)
        {
            c0 += zi * (c[i] + zn1 * c[n - 1 - i]);
            zi *= z;
        }

        c[0] = c0 / (1 - zn1 * zn1);
        for (var i = 1; i < n; i++)
        {
            c[i] += z * c[i - 1];
        }

        c[n - 1] = (z * c[n - 2] + c[n - 1]) * z / (z * z - 1);
        for (var i = n - 2; i >= 0; i--)
        {
            c[i] = z * (c[i + 1] - c[i]);
        }
    }

    /// <summary>
    /// <see cref="PrefilterLine"/> down every column of a band of the intermediate at once, without its gain (the row
    /// pass stored it): each step is the same operation on a row's span of the band.
    /// </summary>
    private static void PrefilterColumns(double[] intermediate, int n, int stride, int first, int width)
    {
        const double z = Pole;
        var scratch = ArrayPool<double>.Shared.Rent(width);
        try
        {
            var sum = scratch.AsSpan(0, width);
            var head = Row(0);
            var zn1 = Math.Pow(z, n - 1);
            TensorPrimitives.MultiplyAdd(Row(n - 1), zn1, head, head);
            var zi = z;
            for (var i = 1; i < n - 1 && zi != 0.0; i++)
            {
                if (zn1 != 0.0)
                {
                    TensorPrimitives.MultiplyAdd(Row(n - 1 - i), zn1, Row(i), sum);
                    TensorPrimitives.MultiplyAdd(sum, zi, head, head);
                }
                else
                {
                    TensorPrimitives.MultiplyAdd(Row(i), zi, head, head);
                }

                zi *= z;
            }

            if (zn1 != 0.0)
            {
                TensorPrimitives.Divide(head, 1 - zn1 * zn1, head);
            }

            for (var i = 1; i < n; i++)
            {
                var row = Row(i);
                TensorPrimitives.MultiplyAdd(Row(i - 1), z, row, row);
            }

            var last = Row(n - 1);
            TensorPrimitives.MultiplyAdd(Row(n - 2), z, last, last);
            TensorPrimitives.Multiply(last, z, last);
            TensorPrimitives.Divide(last, z * z - 1, last);
            for (var i = n - 2; i >= 0; i--)
            {
                var row = Row(i);
                TensorPrimitives.Subtract(Row(i + 1), row, row);
                TensorPrimitives.Multiply(row, z, row);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(scratch);
        }

        Span<double> Row(int y)
        {
            return intermediate.AsSpan(y * stride + first, width);
        }
    }

    /// <summary>
    /// One axis of <c>NI_ZoomShift</c>: for each output sample, its four coefficient taps (mirrored at the edges) and their
    /// cubic B-spline weights.
    /// </summary>
    private sealed class AxisPlan
    {
        private AxisPlan(int count)
        {
            Count = count;
            Taps = new int[4 * count];
            Weights = new double[4 * count];
        }

        public int Count { get; }

        /// <summary>Four per output sample: the coefficient index each tap reads, already mirrored into range.</summary>
        public int[] Taps { get; }

        /// <summary>Four per output sample, in <see cref="Taps"/>' order.</summary>
        public double[] Weights { get; }

        public static AxisPlan Create(int inSize, int outSize)
        {
            var plan = new AxisPlan(outSize);

            // numpy's divide of the two integer sizes, where a zero divisor leaves the ratio at 1.
            var ratio = outSize > 1 ? (double)(inSize - 1) / (outSize - 1) : 1.0;
            for (var k = 0; k < outSize; k++)
            {
                // A last coordinate a rounding error past the last sample is clamped onto it (mode='mirror'), where
                // scipy's 'constant' would zero the whole line (the class remarks, item 3).
                var cc = Math.Min(k * ratio, inSize - 1);

                // get_spline_interpolation_weights for order 3, its arithmetic in its order.
                var floor = Math.Floor(cc);
                var x = cc - floor;
                var y = x;
                var z = 1.0 - x;
                var w1 = (y * y * (y - 2.0) * 3.0 + 4.0) / 6.0;
                var w2 = (z * z * (z - 2.0) * 3.0 + 4.0) / 6.0;
                var w0 = z * z * z / 6.0;
                var w3 = 1.0 - w0 - w1 - w2;

                var t = 4 * k;
                var start = (int)floor - 1;
                for (var h = 0; h < 4; h++)
                {
                    plan.Taps[t + h] = Mirror(start + h, inSize);
                }

                plan.Weights[t] = w0;
                plan.Weights[t + 1] = w1;
                plan.Weights[t + 2] = w2;
                plan.Weights[t + 3] = w3;
            }

            return plan;
        }

        /// <summary>One line's interpolation onto this axis: the four weighted taps per sample, times <paramref name="gain"/>.</summary>
        public void Interpolate(ReadOnlySpan<double> coefficients, Span<double> destination, double gain)
        {
            var taps = Taps;
            var weights = Weights;
            for (var k = 0; k < Count; k++)
            {
                var t = 4 * k;
                destination[k] = gain * (weights[t] * coefficients[taps[t]]
                    + weights[t + 1] * coefficients[taps[t + 1]]
                    + weights[t + 2] * coefficients[taps[t + 2]]
                    + weights[t + 3] * coefficients[taps[t + 3]]);
            }
        }

        /// <summary>
        /// <c>map_coordinate</c> in <c>NI_EXTEND_MIRROR</c> on an integral index: whole-sample symmetric about the first
        /// and the last sample (<c>-1</c> is 1, <c>n</c> is <c>n - 2</c>), every index 0 on an axis of one sample.
        /// </summary>
        private static int Mirror(int index, int length)
        {
            if (index >= 0 && index <= length - 1)
            {
                return index;
            }

            if (length <= 1)
            {
                return 0;
            }

            var period = 2 * length - 2;
            if (index < 0)
            {
                var folded = period * (-index / period) + index;
                return folded <= 1 - length ? folded + period : -folded;
            }

            var wrapped = index - period * (index / period);
            return wrapped >= length ? period - wrapped : wrapped;
        }
    }
}
