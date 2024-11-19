using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Stat;

public static class StatisticsHelper
{
    /// <summary>
    /// Sorts the array in place and returns the median value.
    /// returns <see cref="float.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>median value if any or NaN</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static float Median(Span<float> values)
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
        else if (values.Length == 1)
        {
            return values[0];
        }

        int i = 0;
        var sum = 0d;

        if (Vector<float>.IsSupported)
        {
            int vectorSize = Vector<float>.Count;

            // Sum using Vector<float>
            if (values.Length >= vectorSize)
            {
                for (; i <= values.Length - vectorSize; i += vectorSize)
                {
                    var vector = new Vector<float>(values.Slice(i, vectorSize));
                    sum += Vector.Sum(vector);
                }
            }
        }

        // Sum remaining elements
        for (; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum;
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