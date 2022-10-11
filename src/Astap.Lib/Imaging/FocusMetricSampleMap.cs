using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Astap.Lib.Imaging;

public enum AggregationMethod
{
    Median,
    Best,
    Average
}

public enum SampleKind
{
    HFD,
    FWHM
}

public class FocusMetricSampleMap
{
    private readonly ConcurrentDictionary<int, ConcurrentBag<double>> _samples = new();

    public FocusMetricSampleMap(SampleKind kind)
    {
        Kind = kind;
    }

    public (FocusSolution? solution, int? minPos, int? maxPos) SampleStarsAtFocusPosition(double sample, int currentPos)
    {
        if (!double.IsNaN(sample) && sample > 0.0)
        {
            // add the sample
            Samples(currentPos).Add(sample);

            if (TryGetBestFocusSolution(AggregationMethod.Average, out var solution, out var minPos, out var maxPos))
            {
                return (solution.Value, minPos, maxPos);
            }
            else
            {
                return default;
            }
        }
        else
        {
            return default;
        }
    }

    public SampleKind Kind { get; }

    public ConcurrentBag<double> Samples(int focusPos) =>
        _samples.TryGetValue(focusPos, out var samples)
            ? samples
            : _samples.GetOrAdd(focusPos, new ConcurrentBag<double>());

    public bool TryGetBestFocusSolution(AggregationMethod method, [NotNullWhen(true)] out FocusSolution? solution, out int min, out int max)
    {
        var keys = _samples.Keys.ToArray();
        Array.Sort(keys);

        var data = new double[keys.Length, 2];
        if (keys.Length > 2)
        {
            min = keys[0];
            max = keys[^1];
        }
        else
        {
            min = -1;
            max = -1;

            solution = null;
            return false;
        }

        for (int i = 0; i < keys.Length; i++)
        {
            var focusPos = keys[i];

            var aggregated = Aggregate(focusPos, method);
            if (!aggregated.HasValue)
            {
                solution = null;
                return false;
            }

            data[i, 0] = focusPos;
            data[i, 1] = aggregated.Value;
        }

        solution = Hyperbola.FindBestHyperbolaFit(data);
        return true;
    }

    public double? Aggregate(int focusPos, AggregationMethod method)
    {
        if (_samples.TryGetValue(focusPos, out var samples))
        {
            switch (method)
            {
                case AggregationMethod.Median:
                    var median = Median(samples.ToArray());
                    return double.IsNaN(median) ? null as double? : median;

                case AggregationMethod.Best:
                    return !samples.IsEmpty ? samples.Min() : null as double?;

                case AggregationMethod.Average:
                    return !samples.IsEmpty ? samples.Average() : null as double?;

                default:
                    return null;
            }
        }
        else
        {
            return null;
        }
    }

    /// <summary>
    /// Sorts the array in place and returns the median value.
    /// returns <see cref="double.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>median value if any or NaN</returns>
    public static double Median(double[] values)
    {
        if (values == null || values.Length == 0)
        {
            return double.NaN;
        }
        else if (values.Length == 1)
        {
            return values[0];
        }

        Array.Sort(values);

        int mid = values.Length / 2;
        return values.Length % 2 != 0 ? values[mid] : (values[mid] + values[mid - 1]) / 2;
    }

    /// <summary>
    /// Sorts the array in place and returns the median value.
    /// returns <see cref="double.NaN" /> if array is empty or null.
    /// </summary>
    /// <param name="values">values</param>
    /// <returns>median value if any or NaN</returns>
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
