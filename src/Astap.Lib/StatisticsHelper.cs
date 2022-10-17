using System;
using System.Collections.Generic;

namespace Astap.Lib;

public static class StatisticsHelper
{
    /// <summary>
    /// Sorts the array in place and returns the median value.
    /// returns <see cref="double.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>median value if any or NaN</returns>
    public static float Median(float[] values) => Median(values.AsSpan());

    /// <summary>
    /// Sorts the array in place and returns the median value.
    /// returns <see cref="double.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>median value if any or NaN</returns>
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
    /// Sorts the array in place and returns the median value.
    /// returns <see cref="double.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>median value if any or NaN</returns>
    public static double Median(in Span<uint> values)
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
        return values.Length % 2 != 0 ? values[mid] : (values[mid] + values[mid - 1]) / 2;
    }

    public static double Median(List<double> values)
    {
        if (values == null || values.Count == 0)
        {
            return double.NaN;
        }
        else if (values.Count == 1)
        {
            return values[0];
        }

        values.Sort();

        int mid = values.Count / 2;
        return values.Count % 2 != 0 ? values[mid] : (values[mid] + values[mid - 1]) / 2;
    }
}
