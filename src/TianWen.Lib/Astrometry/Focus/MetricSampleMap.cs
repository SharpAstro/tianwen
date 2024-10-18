using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using TianWen.Lib.Stat;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Astrometry.Focus;

public class MetricSampleMap(SampleKind kind, AggregationMethod aggregationMethod)
{
    private readonly ConcurrentDictionary<int, ConcurrentBag<float>> _samples = new();

    public SampleKind Kind { get; } = kind;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ConcurrentBag<float> Samples(int focusPos) => _samples.GetOrAdd(focusPos, pFocusPos => []);

    public bool TryGetBestFocusSolution([NotNullWhen(true)] out FocusSolution? solution, out int min, out int max, int maxIterations = 20)
    {
        var keys = _samples.Keys.ToArray();
        Array.Sort(keys);

        var data = new float[keys.Length, 2];
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

            var aggregated = Aggregate(focusPos);
            if (!aggregated.HasValue)
            {
                solution = null;
                return false;
            }

            data[i, 0] = focusPos;
            data[i, 1] = aggregated.Value;
        }

        solution = Hyperbola.FindBestHyperbolaFit(data, max_iterations: maxIterations);
        return true;
    }

    public float? Aggregate(int focusPos)
    {
        if (_samples.TryGetValue(focusPos, out var samples))
        {
            switch (aggregationMethod)
            {
                case AggregationMethod.Median:
                    var median = Median(samples.ToArray());
                    return !float.IsNaN(median) ? median : default;

                case AggregationMethod.Mininum:
                    return !samples.IsEmpty ? samples.Min() : default;

                case AggregationMethod.Average:
                    return !samples.IsEmpty ? samples.Average() : default;

                default:
                    return default;
            }
        }
        else
        {
            return default;
        }
    }
}
