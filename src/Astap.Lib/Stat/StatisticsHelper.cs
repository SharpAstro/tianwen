using System;
using System.Runtime.CompilerServices;

namespace Astap.Lib.Stat;

public static class StatisticsHelper
{
    /// <summary>
    /// Sorts the array in place and returns the median value.
    /// returns <see cref="float.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>median value if any or NaN</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static float Median(in Span<float> values)
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
    /// Calculates the GCD of the concatenated list first + rest (rest is copied).
    /// </summary>
    /// <param name="first">first item</param>
    /// <param name="rest">rest items</param>
    /// <returns>GCD of all values</returns>
    public static uint GCD(int first, params int[] rest)
    {
        var len = rest.Length + 1;
        Span<int> values = len < 128 ? stackalloc int[len] : new int[len];

        values[0] = first;
        rest.AsSpan().CopyTo(values[1..]);

        return GCD(values);
    }

    public static uint GCD(in Span<int> values)
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

        return (uint)Math.Abs(values[0]);
    }
}
