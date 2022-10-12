using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using static Astap.Lib.StatisticsHelper;

namespace Astap.Lib.Astrometry.Focus;

public class HFDSamples
{
    private readonly ConcurrentDictionary<int, ConcurrentBag<double>> _samples = new();

    public ConcurrentBag<double> HFDValues(int focusPos) => _samples.TryGetValue(focusPos, out var hfdValues)
        ? hfdValues
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

            var hfd = Aggregate(focusPos, method);
            if (!hfd.HasValue)
            {
                solution = null;
                return false;
            }

            data[i, 0] = focusPos;
            data[i, 1] = hfd.Value;
        }

        solution = Hyperbola.FindBestHyperbolaFit(data);
        return true;
    }

    public double? Aggregate(int focusPos, AggregationMethod method)
    {
        if (_samples.TryGetValue(focusPos, out var hfdValues))
        {
            switch (method)
            {
                case AggregationMethod.Median:
                    var median = Median(hfdValues.ToArray());
                    return double.IsNaN(median) ? null : median;

                case AggregationMethod.Best:
                    return !hfdValues.IsEmpty ? hfdValues.Min() : null;

                case AggregationMethod.Average:
                    return !hfdValues.IsEmpty ? hfdValues.Average() : null;

                default:
                    return null;
            }
        }
        else
        {
            return null;
        }
    }
}
