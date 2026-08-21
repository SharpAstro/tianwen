using System;
using System.Numerics;
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
    /// Returns the value at fractional rank <paramref name="p"/> (0..1) via
    /// quickselect -- expected <c>O(n)</c>, no full sort. The span is permuted
    /// in place (not sorted), so successive calls for different percentiles on
    /// the same buffer are fine. <paramref name="p"/> is clamped to [0, 1];
    /// the rank index is <c>(int)(p * (n - 1))</c> (truncated, matching a
    /// <c>sorted[(int)(p * (len - 1))]</c> lookup). Returns NaN for an empty
    /// span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static float PercentileFast(Span<float> values, double p)
    {
        var n = values.Length;
        if (n == 0) return float.NaN;
        if (n == 1) return values[0];

        var k = (int)Math.Clamp(p * (n - 1), 0, n - 1);
        QuickSelect(values, k);
        return values[k];
    }

    /// <summary>
    /// Returns the <paramref name="k"/>-th smallest value (0-based) via quickselect -- expected
    /// <c>O(n)</c>, no full sort. Exactly equivalent to <c>sorted[k]</c>: the k-th order statistic
    /// is a property of the multiset, so this is BIT-IDENTICAL to sorting and indexing, which is
    /// what makes it a safe replacement for an <see cref="System.Array.Sort(System.Array)"/>-then-index
    /// pair rather than merely a close one.
    /// <para>Prefer <see cref="MedianFast(Span{float})"/> for a median. This overload exists for
    /// callers that must preserve an existing <c>sorted[n / 2]</c> convention: for even <c>n</c> that
    /// is the UPPER median, whereas <c>MedianFast</c> averages the two middle values, so the two are
    /// not interchangeable. <see cref="Image.GetStarMaskedMedianAndMADScaledToUnit"/> is such a
    /// caller -- its median and MAD feed the stretch, so changing the convention would move every
    /// rendered pixel slightly for no stated reason.</para>
    /// <para>The span is permuted in place but not sorted.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static float NthSmallest(Span<float> values, int k)
    {
        var n = values.Length;
        if (n == 0) return float.NaN;
        if (n == 1) return values[0];

        k = Math.Clamp(k, 0, n - 1);
        QuickSelect(values, k);
        return values[k];
    }

    /// <summary>
    /// Median and median absolute deviation over ONE buffer, in place: selects the median,
    /// overwrites each element with its absolute deviation from it, then selects again.
    /// </summary>
    /// <remarks>
    /// <para>This shape was written out longhand in four places, and the thing worth
    /// centralising is not the six lines -- it is the CONTRACT that makes them correct. A
    /// selection permutes the buffer but loses no values, and an absolute deviation is computed
    /// per element independently of order, so the second selection sees the right multiset
    /// without a second buffer. Two of the four call sites carried a comment explaining that,
    /// which is a good sign it is not obvious; a third
    /// (<see cref="Image.GetStarMaskedMedianAndMADScaledToUnit"/>) had missed it and allocated a
    /// second 6 MB array per channel, and a fourth (the plate solver's residual clip) had missed
    /// it differently and recomputed every residual from source in a second pass.</para>
    /// <para>The deviation pass is vectorised here, so sharing it is faster than the scalar
    /// copies it replaces rather than merely tidier. <c>Vector.Abs</c> clears a sign bit and
    /// rounds nothing, so the result is bit-identical to the scalar form.</para>
    /// <para>The buffer is left holding deviations, not the input values.</para>
    /// </remarks>
    public static (float Median, float Mad) MedianAndMad(Span<float> values)
    {
        var median = MedianFast(values);
        AbsoluteDeviationsInPlace(values, median);
        return (median, MedianFast(values));
    }

    /// <summary>
    /// As <see cref="MedianAndMad(Span{float})"/> but using the <c>sorted[n / 2]</c> convention
    /// for both selections -- the UPPER of the two middle values when <paramref name="values"/>
    /// has an even length, where <see cref="MedianFast(Span{float})"/> averages them.
    /// </summary>
    /// <remarks>
    /// Exists because <see cref="Image.GetStarMaskedMedianAndMADScaledToUnit"/> feeds the stretch
    /// and must not silently change convention. The two are a named pair rather than one method
    /// with a flag because the difference is a statistical definition, not a mode: it is exactly
    /// the distinction that no real image can reveal (a quantised background ties the two middle
    /// samples, so swapping them leaves every fixture assertion green -- measured, see
    /// <c>NthSmallestTests</c>), which is why it has to be legible at the call site.
    /// </remarks>
    public static (float Median, float Mad) UpperMedianAndMad(Span<float> values)
    {
        var k = values.Length / 2;
        var median = NthSmallest(values, k);
        AbsoluteDeviationsInPlace(values, median);
        return (median, NthSmallest(values, k));
    }

    /// <summary>Double-precision counterpart to <see cref="MedianAndMad(Span{float})"/>.</summary>
    public static (double Median, double Mad) MedianAndMad(Span<double> values)
    {
        var median = MedianFast(values);
        AbsoluteDeviationsInPlace(values, median);
        return (median, MedianFast(values));
    }


    /// <summary>
    /// Copies the non-NaN values of <paramref name="source"/> into <paramref name="destination"/>,
    /// packed from index 0, and returns how many were written.
    /// </summary>
    /// <remarks>
    /// <para>Compacting before a selection is not optional, it is a precondition: quickselect
    /// partitions with <c>&lt;</c> and <c>&gt;</c>, both of which are FALSE against NaN, so a NaN
    /// left in the buffer lands in an unpredictable partition position and the answer depends on
    /// the input permutation. Warped frames carry large NaN edge regions by construction, so this
    /// is the normal case on the stacking path rather than a defensive check.</para>
    /// <para>Scalar on purpose. <c>Vector&lt;float&gt;.Min</c> returns NaN-poisoned results if any
    /// lane is NaN, and a vectorised COMPACTION needs a compress/shuffle per block, which is more
    /// machinery than the surrounding selection cost justifies.</para>
    /// </remarks>
    public static int CompactFinite(ReadOnlySpan<float> source, Span<float> destination)
    {
        var n = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var v = source[i];
            if (!float.IsNaN(v))
            {
                destination[n++] = v;
            }
        }
        return n;
    }

    /// <summary>
    /// As <see cref="CompactFinite(ReadOnlySpan{float}, Span{float})"/>, and additionally reports
    /// the smallest value written. <paramref name="min"/> is
    /// <see cref="float.PositiveInfinity"/> when nothing was -- the caller chooses what an
    /// all-NaN input should mean, rather than having a sentinel baked in here.
    /// </summary>
    /// <remarks>
    /// The pairing is the point: a min and a compaction each need one <c>IsNaN</c> test per
    /// element, so taking them together halves the traversals. <c>Normalizer.ComputeStats</c> ran
    /// them as separate passes over the same pixels, once per channel per warped frame.
    /// </remarks>
    public static int CompactFinite(ReadOnlySpan<float> source, Span<float> destination, out float min)
    {
        var m = float.PositiveInfinity;
        var n = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var v = source[i];
            if (!float.IsNaN(v))
            {
                destination[n++] = v;
                if (v < m) { m = v; }
            }
        }
        min = m;
        return n;
    }

    /// <summary>
    /// Overwrites each element with its absolute deviation from <paramref name="centre"/>. One
    /// pass, vectorised in the same shape as <c>Normalizer.NormalizeVec</c>.
    /// </summary>
    private static void AbsoluteDeviationsInPlace(Span<float> values, float centre)
    {
        var width = Vector<float>.Count;
        var centreVec = new Vector<float>(centre);
        var i = 0;
        for (; i <= values.Length - width; i += width)
        {
            Vector.Abs(new Vector<float>(values[i..]) - centreVec).CopyTo(values[i..]);
        }
        for (; i < values.Length; i++)
        {
            values[i] = MathF.Abs(values[i] - centre);
        }
    }

    /// <summary>Double-precision counterpart to the above.</summary>
    private static void AbsoluteDeviationsInPlace(Span<double> values, double centre)
    {
        var width = Vector<double>.Count;
        var centreVec = new Vector<double>(centre);
        var i = 0;
        for (; i <= values.Length - width; i += width)
        {
            Vector.Abs(new Vector<double>(values[i..]) - centreVec).CopyTo(values[i..]);
        }
        for (; i < values.Length; i++)
        {
            values[i] = Math.Abs(values[i] - centre);
        }
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