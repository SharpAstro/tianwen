using System;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Stat;

public static class StatisticsHelper
{
    // Conversion from MAD to SD for a normal distribution. See https://en.wikipedia.org/wiki/Median_absolute_deviation */
    internal const float MAD_TO_SD = 1.4826f;

    /// <summary>
    /// Sorts the array in place and returns the median value. Use this only
    /// when the caller needs the sorted side-effect (e.g. to derive
    /// percentiles afterwards). For pure median computation, prefer
    /// <see cref="MedianFast(Span{float})"/> -- it's O(n) vs O(n log n) and
    /// won't waste cycles fully sorting the array.
    /// returns <see cref="float.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>median value if any or NaN</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static float MedianSorted(Span<float> values)
    {
        if (values.Length == 0)
        {
            return float.NaN;
        }
        else if (values.Length == 1)
        {
            return values[0];
        }

        values.Sort();

        int mid = values.Length / 2;
        return values.Length % 2 != 0 ? values[mid] : (values[mid] + values[mid - 1]) / 2;
    }

    /// <summary>
    /// Returns the median without producing a fully-sorted span. Uses
    /// quickselect (nth_element style) with median-of-three pivoting: expected
    /// <c>O(n)</c> vs the <c>O(n log n)</c> of <see cref="MedianSorted(Span{float})"/>.
    /// The span is permuted in place but not sorted; callers that need a sorted
    /// span afterwards must use <see cref="MedianSorted(Span{float})"/> instead.
    /// <para>Used in <see cref="Image.AnalyseStar"/> where the median is wanted
    /// twice per call (background + MAD) on annulus buffers up to ~328 floats,
    /// without callers caring about the post-call order. Around 8x faster per
    /// median at that size based on trace samples.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static float MedianFast(Span<float> values)
    {
        var n = values.Length;
        if (n == 0) return float.NaN;
        if (n == 1) return values[0];

        var mid = n / 2;
        QuickSelect(values, mid);

        // After QuickSelect(mid): values[mid] is the kth smallest (the upper
        // median for even n). For odd n that's the answer directly. For even n
        // we need the lower median too -- the max of values[0..mid), which is
        // *now* guaranteed to be <= values[mid] but unordered among themselves,
        // so a single linear scan picks the max.
        var upper = values[mid];
        if ((n & 1) == 1) return upper;

        var lower = values[0];
        for (var i = 1; i < mid; i++)
        {
            if (values[i] > lower) lower = values[i];
        }
        return (lower + upper) * 0.5f;
    }

    /// <summary>
    /// Double-precision counterpart to <see cref="MedianFast(Span{float})"/>.
    /// Same algorithm, same trade-offs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static double MedianFast(Span<double> values)
    {
        var n = values.Length;
        if (n == 0) return double.NaN;
        if (n == 1) return values[0];

        var mid = n / 2;
        QuickSelect(values, mid);

        var upper = values[mid];
        if ((n & 1) == 1) return upper;

        var lower = values[0];
        for (var i = 1; i < mid; i++)
        {
            if (values[i] > lower) lower = values[i];
        }
        return (lower + upper) * 0.5;
    }

    /// <summary>
    /// In-place partial sort: after this call, <c>values[k]</c> holds the
    /// (k+1)-th smallest element and all elements at indices &lt; k are &lt;=
    /// it (in arbitrary order), all at indices &gt; k are &gt;= it.
    /// Median-of-three pivot keeps the expected-case bound tight; the
    /// iterative shape avoids stack growth on adversarial inputs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void QuickSelect(Span<float> v, int k)
    {
        int lo = 0, hi = v.Length - 1;
        while (lo < hi)
        {
            // Median-of-three pivot: sort lo/mid/hi, then use mid as pivot.
            // Robust against already-sorted / reverse-sorted / many-equal
            // inputs that would otherwise give O(n^2) with a fixed pivot.
            int m = lo + ((hi - lo) >> 1);
            if (v[lo] > v[hi]) (v[lo], v[hi]) = (v[hi], v[lo]);
            if (v[m] > v[hi]) (v[m], v[hi]) = (v[hi], v[m]);
            if (v[lo] > v[m]) (v[lo], v[m]) = (v[m], v[lo]);
            var pivot = v[m];

            // Hoare partition. Sentinel guards: v[lo] <= pivot <= v[hi] after
            // median-of-three so the inner while loops can't run off either end.
            int i = lo - 1, j = hi + 1;
            while (true)
            {
                while (v[++i] < pivot) { }
                while (v[--j] > pivot) { }
                if (i >= j) break;
                (v[i], v[j]) = (v[j], v[i]);
            }
            // After partition: v[lo..j] <= pivot, v[j+1..hi] >= pivot.
            if (k <= j) hi = j;
            else lo = j + 1;
        }
    }

    /// <summary>Double-precision partial sort; see <see cref="QuickSelect(Span{float}, int)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void QuickSelect(Span<double> v, int k)
    {
        int lo = 0, hi = v.Length - 1;
        while (lo < hi)
        {
            int m = lo + ((hi - lo) >> 1);
            if (v[lo] > v[hi]) (v[lo], v[hi]) = (v[hi], v[lo]);
            if (v[m] > v[hi]) (v[m], v[hi]) = (v[hi], v[m]);
            if (v[lo] > v[m]) (v[lo], v[m]) = (v[m], v[lo]);
            var pivot = v[m];

            int i = lo - 1, j = hi + 1;
            while (true)
            {
                while (v[++i] < pivot) { }
                while (v[--j] > pivot) { }
                if (i >= j) break;
                (v[i], v[j]) = (v[j], v[i]);
            }
            if (k <= j) hi = j;
            else lo = j + 1;
        }
    }

    /// <summary>
    /// Double-precision <see cref="MedianSorted(Span{float})"/>. Sorts in place;
    /// returns <see cref="double.NaN"/> for an empty span. Used where samples
    /// are radians and float quantisation would coarsen the readout below an
    /// arcmin target. Pure median callers should prefer
    /// <see cref="MedianFast(Span{double})"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static double MedianSorted(Span<double> values)
    {
        if (values.Length == 0)
        {
            return double.NaN;
        }
        else if (values.Length == 1)
        {
            return values[0];
        }

        values.Sort();

        int mid = values.Length / 2;
        return (values.Length & 1) != 0 ? values[mid] : 0.5 * (values[mid] + values[mid - 1]);
    }

    /// <summary>
    /// Calculates the average of <paramref name="values"/>, using <see cref="SumD(Span{float})"/> for summation.
    /// returns <see cref="float.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>average value if any or NaN</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static float Average(ReadOnlySpan<float> values) => (float)(SumD(values) / values.Length);

    /// <summary>
    /// Calculates the sum of <paramref name="values"/>, using <see langword="double"/> to preserve precision.
    /// returns <see cref="float.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>average value if any or NaN</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static double SumD(ReadOnlySpan<float> values)
    {
        if (values.Length == 0)
        {
            return float.NaN;
        }

        return TensorPrimitives.Sum(values);
    }

    /// <summary>
    /// Calculates the GCD of the concatenated list first + rest (rest is copied).
    /// </summary>
    /// <param name="first">first item</param>
    /// <param name="rest">rest items</param>
    /// <returns>GCD of all values</returns>
    public static uint GCD(int first, params int[] rest) => GCDNoCopy([first, .. rest]);

    /// <summary>
    /// Makes a copy of values and calculates the GCD.
    /// </summary>
    /// <param name="values">Values to calculate GCD from.</param>
    /// <returns>GCD</returns>
    /// <exception cref="ArgumentException">if <paramref name="values"/> span is empty</exception>
    public static uint GCD(in ReadOnlySpan<int> values)
    {
        var len = values.Length;
        Span<int> copy = len < 128 ? stackalloc int[len] : new int[len];
        values.CopyTo(copy);

        return GCDNoCopy(copy);
    }

    /// <summary>
    /// Warning: Overwrites values so input values are lost on exit.
    /// </summary>
    /// <param name="values">Values to calculate GCD from.</param>
    /// <returns>GCD</returns>
    /// <exception cref="ArgumentException">if <paramref name="values"/> span is empty</exception>
    internal static uint GCDNoCopy(Span<int> values)
    {
        if (values.Length > 1)
        {
            do
            {
                values.Sort((a, b) => Math.Abs(b).CompareTo(Math.Abs(a)));

                if (values[1] != 0)
                {
                    values[0] %= values[1];
                }
                else
                {
                    return (uint)Math.Abs(values[0]);
                }
            }
            while (true);
        }
        else if (values.Length == 1)
        {
            return (uint)Math.Abs(values[0]);
        }
        else
        {
            throw new ArgumentException("Must provide at least one value", nameof(values));
        }
    }

    public static ulong LCM(int first, params int[] rest) => LCM([first, .. rest]);

    public static ulong LCM(Span<int> values) => LCM(GCD(values), values);

    internal static ulong LCM(uint gcd, in Span<int> values)
    {
        if (gcd == 0)
        {
            foreach (var value in values)
            {
                if (value == 0)
                {
                    return 0;
                }
            }
            throw new ArgumentException("A GCD of 0 was provided but no value 0", nameof(gcd));
        }
        else if (values.Length >= 1)
        {
            // TODO: there must be a faster way to multiply all values in an array/span?
            var prod = 1L;
            for (var i = 0; i < values.Length; i++)
            {
                prod *= values[i];
            }
            return (ulong)Math.Abs(prod) / gcd;
        }
        else
        {
            throw new ArgumentException("Must provide at least one value", nameof(values));
        }
    }
}